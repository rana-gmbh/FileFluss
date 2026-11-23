#if os(macOS)
import Darwin
import Foundation

/// Owns the sockets, helper, and command files for an OpenSSH invocation.
/// No credential material is stored in this directory.
final class SFTPTemporaryDirectory: Sendable {
    let url: URL

    init() throws {
        // UNIX-domain socket paths are limited to 103 bytes on macOS.
        var template = Array("/tmp/filefluss-sftp.XXXXXXXX".utf8CString)
        guard let path = template.withUnsafeMutableBufferPointer({ mkdtemp($0.baseAddress!) }) else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        url = URL(fileURLWithPath: String(cString: path), isDirectory: true)
    }

    deinit {
        try? FileManager.default.removeItem(at: url)
    }
}

/// Gives OpenSSH's askpass child one response over a local socket. Only the
/// helper and socket paths go in its environment, and the script is constant.
final class SFTPAskPass: Sendable {
    private let source: any DispatchSourceRead

    init(secret: String, directory: SFTPTemporaryDirectory) throws {
        let payload = Data(secret.utf8)
        guard !payload.isEmpty, payload.count <= 1023,
              !payload.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) else {
            throw CloudProviderError.commandFailed(L10n.text("The SSH password or passphrase cannot be passed to OpenSSH."))
        }

        let socketPath = directory.url.appendingPathComponent("askpass.sock").path
        let descriptor = try Self.listen(at: socketPath)
        let source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: .global(qos: .utility))
        source.setCancelHandler { close(descriptor) }
        source.setEventHandler {
            let peer = accept(descriptor, nil, nil)
            guard peer >= 0 else { return }
            defer { close(peer) }
            var uid: uid_t = 0
            var gid: gid_t = 0
            var enabled: Int32 = 1
            guard getpeereid(peer, &uid, &gid) == 0, uid == geteuid(),
                  setsockopt(peer, SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size)) == 0 else { return }
            source.cancel()
            payload.withUnsafeBytes { bytes in
                guard let start = bytes.baseAddress else { return }
                var offset = 0
                while offset < bytes.count {
                    let count = Darwin.send(peer, start.advanced(by: offset), bytes.count - offset, 0)
                    if count < 0, errno == EINTR { continue }
                    guard count > 0 else { return }
                    offset += count
                }
            }
        }
        self.source = source
        source.activate()
    }

    deinit {
        source.cancel()
    }

    static func environment(directory: SFTPTemporaryDirectory) throws -> [String: String] {
        let helper = directory.url.appendingPathComponent("askpass")
        let script = """
        #!/bin/sh
        case "${SSH_ASKPASS_PROMPT-}" in
            confirm|none) exit 1 ;;
        esac
        exec /usr/bin/nc -dU -w 5 "$FILEFLUSS_ASKPASS_SOCKET" </dev/null
        """
        guard FileManager.default.createFile(
            atPath: helper.path,
            contents: Data((script + "\n").utf8),
            attributes: [.posixPermissions: 0o700]
        ) else { throw CloudProviderError.invalidResponse }

        return [
            "SSH_ASKPASS": helper.path,
            "SSH_ASKPASS_REQUIRE": "force",
            "FILEFLUSS_ASKPASS_SOCKET": directory.url.appendingPathComponent("askpass.sock").path,
        ]
    }

    private static func listen(at path: String) throws -> Int32 {
        var address = sockaddr_un()
        let pathBytes = path.utf8CString
        guard pathBytes.count <= MemoryLayout.size(ofValue: address.sun_path) else {
            throw POSIXError(.ENAMETOOLONG)
        }
        address.sun_family = sa_family_t(AF_UNIX)
        let addressLength = socklen_t(MemoryLayout<sockaddr_un>.size)
        address.sun_len = UInt8(addressLength)
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: pathBytes.map { UInt8(bitPattern: $0) }) }

        let descriptor = socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(descriptor, $0, addressLength)
            }
        }
        guard fcntl(descriptor, F_SETFD, FD_CLOEXEC) == 0,
              bound == 0, Darwin.listen(descriptor, 1) == 0 else {
            let error = POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            close(descriptor)
            throw error
        }
        return descriptor
    }
}
#endif
