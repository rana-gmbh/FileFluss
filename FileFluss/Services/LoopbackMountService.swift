import AppKit
import Foundation
import FileFlussCore
import NetFS
import os

private let mountLog = Logger(subsystem: "com.rana.FileFluss", category: "mount")

/// Spins up a per-account `WebDAVServer` and asks macOS's built-in
/// `mount_webdav` to mount it under `/Volumes`. The mount lives only as
/// long as FileFluss does — quitting tears every active mount down.
@MainActor
@Observable
public final class LoopbackMountService {
    public struct ActiveMount: Identifiable, Hashable {
        public let id = UUID()
        public let accountId: UUID
        public let providerRoot: String
        public let displayName: String
        public let mountPoint: URL
        public let port: UInt16
        nonisolated public static func == (lhs: ActiveMount, rhs: ActiveMount) -> Bool { lhs.id == rhs.id }
        nonisolated public func hash(into hasher: inout Hasher) { hasher.combine(id) }
    }

    public private(set) var mounts: [ActiveMount] = []

    /// Backing server + sidecar per active mount. Kept out of the public
    /// `ActiveMount` value type so SwiftUI can observe `mounts` without
    /// having to think about Sendable on the server actor.
    private var servers: [UUID: WebDAVServer] = [:]

    public init() {}

    public func mount(
        provider: any CloudProvider,
        accountId: UUID,
        providerRoot: String = "/",
        displayName: String
    ) async throws -> ActiveMount {
        let safeName = sanitiseVolumeName(displayName)
        let mountPoint = URL(fileURLWithPath: "/Volumes/\(safeName)")

        let sidecarRoot = sidecarBaseURL.appendingPathComponent(accountId.uuidString)
        let sidecar = WebDAVSidecarStore(root: sidecarRoot)

        let server = WebDAVServer(provider: provider, mountRoot: providerRoot, sidecar: sidecar)
        let port = try await server.start()

        do {
            try await runMountWebDAV(port: port, mountPoint: mountPoint)
        } catch {
            await server.stop()
            throw error
        }

        let mount = ActiveMount(
            accountId: accountId,
            providerRoot: providerRoot,
            displayName: safeName,
            mountPoint: mountPoint,
            port: port
        )
        servers[mount.id] = server
        mounts.append(mount)
        mountLog.info("[mount] mounted \(safeName, privacy: .public) at \(mountPoint.path, privacy: .public) on port \(port)")
        return mount
    }

    @discardableResult
    public func unmount(_ mount: ActiveMount) async -> Bool {
        await runUmount(mountPoint: mount.mountPoint)
        if let server = servers.removeValue(forKey: mount.id) {
            await server.stop()
        }
        mounts.removeAll { $0.id == mount.id }
        mountLog.info("[mount] unmounted \(mount.displayName, privacy: .public)")
        return true
    }

    /// Find an active mount by (account, providerRoot) — what the UI keys
    /// off when toggling the per-account "Mount" button.
    public func mount(for accountId: UUID, providerRoot: String = "/") -> ActiveMount? {
        mounts.first { $0.accountId == accountId && $0.providerRoot == providerRoot }
    }

    public func isMounted(accountId: UUID, providerRoot: String = "/") -> Bool {
        mount(for: accountId, providerRoot: providerRoot) != nil
    }

    /// Called on app quit so we don't leave orphan Volumes entries pointing
    /// at a server that's about to disappear.
    public func unmountAll() async {
        for mount in mounts {
            _ = await unmount(mount)
        }
    }

    // MARK: - Process plumbing

    private func runMountWebDAV(port: UInt16, mountPoint: URL) async throws {
        // Pre-create the mount point — NetFS expects it to exist when we
        // ask it to mount at a specific location.
        try? FileManager.default.createDirectory(at: mountPoint, withIntermediateDirectories: true)

        // Self-probe: confirm our own server responds before handing the URL
        // to NetFS. If this fails the bug is on our side and the diagnostic
        // is much clearer than NetFS's status code would be.
        try await probeServer(port: port)

        // Modern macOS shipped /sbin/mount_webdav without its setuid bit, so
        // a regular app process can't call it directly — the mount(2) call
        // inside fails with EPERM. NetFS does the same job via a launchd-
        // launched privileged helper, which is the supported path.
        try await mountViaNetFS(port: port, mountPoint: mountPoint)
    }

    private func mountViaNetFS(port: UInt16, mountPoint: URL) async throws {
        // Use `localhost` rather than `127.0.0.1`. NetFS treats raw IPs as
        // "unknown server" which goes through a stricter validation path
        // that mid-handshakes around the OpenSession/Mount call. The
        // hostname route lands on the same address (loopback) but uses the
        // friendly-name code path.
        let urlString = "http://localhost:\(port)/"
        guard let url = URL(string: urlString) else {
            throw NSError(domain: "FileFluss.Mount", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "Couldn't build the loopback URL."])
        }
        // `kNetFSUseGuestKey=true` short-circuits the auth prompt.
        // `AllowLoopback=true` separately suppresses the unsecured-HTTP
        // warning. We deliberately do NOT set `kNetFSMountAtMountDirKey`
        // here — letting NetFS pick its own mount point under /Volumes
        // sidesteps "errno 2 (mount point bad)" failures we were hitting
        // with a parenthesised user-friendly name.
        let openOptions: NSMutableDictionary = [
            kNetFSNoUserPreferencesKey as String: true,
            kNetFSUseGuestKey as String: true,
            "AllowLoopback": true,
            "NoUI": true,
        ]
        let mountOptions: NSMutableDictionary = [:]

        // NetFSMountURLSync blocks the calling thread; run it off the main
        // actor so the UI's `isWorking` spinner stays responsive.
        let status: Int32 = await Task.detached {
            var mountedPaths: Unmanaged<CFArray>?
            return NetFSMountURLSync(
                url as CFURL,
                nil,                    // mountpath — let NetFS pick
                nil,                    // username — guest mode supplies it
                nil,                    // password — guest mode supplies it
                openOptions,
                mountOptions,
                &mountedPaths
            )
        }.value

        if status != 0 {
            let message = Self.netfsErrorMessage(status: status)
            mountLog.error("[mount] NetFS failed: status=\(status, privacy: .public) message=\(message, privacy: .public)")
            try? FileManager.default.removeItem(at: mountPoint)
            throw NSError(
                domain: "FileFluss.Mount", code: Int(status),
                userInfo: [NSLocalizedDescriptionKey: "Could not mount in Finder: \(message)"]
            )
        }
    }

    private static func netfsErrorMessage(status: Int32) -> String {
        // Most NetFS errors come back as POSIX errno values. Translate via
        // strerror so the user gets something readable instead of a code.
        if status > 0, let str = String(validatingUTF8: strerror(status)) {
            return "\(str) (errno \(status))"
        }
        return "NetFS status \(status)"
    }

    /// HTTP probe against the just-started WebDAV server. Throws on failure
    /// with a message the user can act on — almost always "the listener
    /// didn't actually bind", which mount_webdav would have reported as an
    /// opaque exit code.
    private func probeServer(port: UInt16) async throws {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/")!)
        request.httpMethod = "OPTIONS"
        request.timeoutInterval = 3
        do {
            let (_, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                throw NSError(domain: "FileFluss.Mount", code: -1,
                              userInfo: [NSLocalizedDescriptionKey: "The internal WebDAV server didn't respond with 200 OK to OPTIONS."])
            }
        } catch {
            throw NSError(domain: "FileFluss.Mount", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "Could not reach the internal WebDAV server on 127.0.0.1:\(port): \(error.localizedDescription)"])
        }
    }

    private func runUmount(mountPoint: URL) async {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/sbin/umount")
        process.arguments = [mountPoint.path]
        process.standardOutput = Pipe()
        process.standardError = Pipe()
        do {
            try process.run()
            await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
                process.terminationHandler = { _ in continuation.resume() }
            }
        } catch {
            mountLog.error("[mount] umount failed to launch: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: - Helpers

    /// `/Volumes/<name>` is fussy about characters; whitelist a generous
    /// but predictable set so the mount point and the volume label both
    /// behave. Anything outside the whitelist becomes `-`.
    private func sanitiseVolumeName(_ raw: String) -> String {
        let allowed = CharacterSet.alphanumerics
            .union(CharacterSet(charactersIn: " ._-()"))
        let mapped = String(raw.unicodeScalars.map { scalar -> Character in
            allowed.contains(scalar) ? Character(scalar) : "-"
        })
        let trimmed = mapped.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "FileFluss" : trimmed
    }

    private var sidecarBaseURL: URL {
        let base = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let dir = base.appendingPathComponent("FileFluss/WebDAV-Mounts", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
}
