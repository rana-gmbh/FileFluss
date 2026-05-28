import AppKit
import Foundation
import FileFlussCore
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
        // Embed dummy credentials in the URL so mount_webdav doesn't fall
        // back to a Keychain lookup (or worse, a Finder prompt) — our server
        // ignores the Authorization header either way.
        let url = "http://filefluss:filefluss@127.0.0.1:\(port)/"
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/sbin/mount_webdav")
        process.arguments = ["-S", "-o", "nobrowse", url, mountPoint.path]
        let stderr = Pipe()
        process.standardError = stderr
        process.standardOutput = Pipe()

        try process.run()
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            process.terminationHandler = { _ in continuation.resume() }
        }

        if process.terminationStatus != 0 {
            let raw = String(data: stderr.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            let message = raw.isEmpty ? "mount_webdav exited \(process.terminationStatus)" : raw.trimmingCharacters(in: .whitespacesAndNewlines)
            mountLog.error("[mount] mount_webdav failed: \(message, privacy: .public)")
            throw NSError(
                domain: "FileFluss.Mount",
                code: Int(process.terminationStatus),
                userInfo: [NSLocalizedDescriptionKey: "Could not mount in Finder: \(message)"]
            )
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

    /// `/Volumes/<name>` is fussy about characters; strip anything that
    /// would break the path or confuse Finder.
    private func sanitiseVolumeName(_ raw: String) -> String {
        let stripped = raw.replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
            .replacingOccurrences(of: "\u{0}", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return stripped.isEmpty ? "FileFluss" : stripped
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
