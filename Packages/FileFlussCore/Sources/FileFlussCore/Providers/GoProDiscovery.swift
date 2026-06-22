import Foundation
import Network
import os

private let goProDiscoveryLog = Logger(subsystem: "com.rana.FileFluss", category: "goProDiscovery")

/// How FileFluss reached a particular camera. Stored with the account so a
/// reconnect knows where to look first.
public enum GoProConnectionMode: String, Codable, Sendable {
    /// Camera plugged in over USB, exposing the HTTP API on a USB-Ethernet link.
    case wiredUSB
    /// Mac joined to the camera's own Wi-Fi access point (fixed IP 10.5.5.9).
    case wifiAP
    /// Camera on the user's home Wi-Fi (COHN): HTTPS + Basic auth at a
    /// user-entered IP, provisioned beforehand via the GoPro app.
    case cohn
}

/// A camera found on the network/USB, ready to connect to.
public struct GoProCamera: Sendable, Identifiable, Hashable {
    public let name: String
    public let ipAddress: String
    public let mode: GoProConnectionMode

    public var id: String { ipAddress }

    public init(name: String, ipAddress: String, mode: GoProConnectionMode) {
        self.name = name
        self.ipAddress = ipAddress
        self.mode = mode
    }
}

/// Locates GoPro cameras without any external dependency.
///
/// Two reliable, framework-only paths cover the Phase 1 modes:
/// - **Wired USB**: when plugged in, the camera hands the Mac a DHCP lease on a
///   random `/24` inside `172.16.0.0/12` and sits at host `.51`. We enumerate
///   the Mac's interfaces (`getifaddrs`) and probe the `.51` of each such net.
/// - **Wi-Fi AP**: the camera's own access point always answers at `10.5.5.9`.
///
/// A best-effort Bonjour (`_gopro-web._tcp`) browse is layered on top to also
/// catch cameras on the home network. Each candidate is confirmed with a real
/// `GET /gopro/camera/state` so we never surface a dead address.
public enum GoProDiscovery {
    static let bonjourServiceType = "_gopro-web._tcp"
    static let wifiAPAddress = "10.5.5.9"
    static let port = 8080

    /// Scans for reachable cameras. Returns confirmed cameras only.
    public static func scan(timeout: TimeInterval = 6) async -> [GoProCamera] {
        var candidates: [(ip: String, mode: GoProConnectionMode)] = []
        candidates.append((wifiAPAddress, .wifiAP))
        for ip in usbCandidateAddresses() {
            candidates.append((ip, .wiredUSB))
        }
        for ip in await bonjourAddresses(timeout: timeout) {
            candidates.append((ip, .wiredUSB))
        }

        // De-dup by IP, preferring the first (wired/AP before bonjour dupes).
        var seen = Set<String>()
        let unique = candidates.filter { seen.insert($0.ip).inserted }

        // Confirm each candidate concurrently.
        var confirmed: [GoProCamera] = []
        await withTaskGroup(of: GoProCamera?.self) { group in
            for candidate in unique {
                group.addTask {
                    let client = GoProAPIClient(ipAddress: candidate.ip, port: port)
                    guard await client.ping() else { return nil }
                    let label = candidate.mode == .wifiAP ? "GoPro Camera (Wi-Fi)" : "GoPro Camera (USB)"
                    return GoProCamera(name: label, ipAddress: candidate.ip, mode: candidate.mode)
                }
            }
            for await camera in group {
                if let camera { confirmed.append(camera) }
            }
        }
        goProDiscoveryLog.info("[GoPro] scan found \(confirmed.count) camera(s)")
        return confirmed
    }

    /// Re-resolves a reachable IP for a saved connection: tries the last-known
    /// address first, then a fresh scan matching the saved mode, then the
    /// fixed Wi-Fi AP. Returns nil if nothing answers.
    public static func resolve(lastKnownIP: String?, mode: GoProConnectionMode) async -> String? {
        if let lastKnownIP {
            let client = GoProAPIClient(ipAddress: lastKnownIP, port: port)
            if await client.ping() { return lastKnownIP }
        }
        let cameras = await scan()
        if let match = cameras.first(where: { $0.mode == mode }) { return match.ipAddress }
        return cameras.first?.ipAddress
    }

    // MARK: - USB interface enumeration

    /// IPv4 `.51` host addresses derived from every local interface whose
    /// address is in `172.16.0.0/12` — the range GoPro's USB-Ethernet gadget
    /// uses. The camera is always at host `.51` of that subnet.
    static func usbCandidateAddresses() -> [String] {
        var results: [String] = []
        var ifaddrPtr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddrPtr) == 0, let first = ifaddrPtr else { return [] }
        defer { freeifaddrs(ifaddrPtr) }

        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let current = ptr {
            defer { ptr = current.pointee.ifa_next }
            guard let addr = current.pointee.ifa_addr,
                  addr.pointee.sa_family == UInt8(AF_INET) else { continue }

            var storage = sockaddr_in()
            memcpy(&storage, addr, Int(MemoryLayout<sockaddr_in>.size))
            let ip = ipString(from: storage.sin_addr)
            guard isInGoProUSBRange(ip) else { continue }

            let octets = ip.split(separator: ".")
            guard octets.count == 4 else { continue }
            let cameraIP = "\(octets[0]).\(octets[1]).\(octets[2]).51"
            if cameraIP != ip { results.append(cameraIP) }
        }
        return results
    }

    private static func ipString(from addr: in_addr) -> String {
        var a = addr
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &a, &buffer, socklen_t(INET_ADDRSTRLEN))
        return String(cString: buffer)
    }

    /// True for `172.16.0.0` – `172.31.255.255`.
    static func isInGoProUSBRange(_ ip: String) -> Bool {
        let octets = ip.split(separator: ".").compactMap { Int($0) }
        guard octets.count == 4 else { return false }
        return octets[0] == 172 && (16...31).contains(octets[1])
    }

    // MARK: - Bonjour

    /// Best-effort Bonjour browse + resolve to IPv4 addresses. Never throws;
    /// returns whatever it resolved within `timeout`.
    private static func bonjourAddresses(timeout: TimeInterval) async -> [String] {
        await withCheckedContinuation { (continuation: CheckedContinuation<[String], Never>) in
            let coordinator = BonjourCoordinator(serviceType: bonjourServiceType, timeout: timeout) { ips in
                continuation.resume(returning: ips)
            }
            coordinator.start()
        }
    }
}

/// Drives an `NWBrowser` for the GoPro service type and resolves each result
/// to an IPv4 address via a short-lived `NWConnection`. Self-retains until it
/// has reported once (after `timeout` or when browsing settles).
private final class BonjourCoordinator: @unchecked Sendable {
    private let browser: NWBrowser
    private let timeout: TimeInterval
    private let onComplete: ([String]) -> Void
    private let queue = DispatchQueue(label: "com.rana.FileFluss.gopro.bonjour")

    private var resolved = Set<String>()
    private var pendingConnections: [NWConnection] = []
    private var finished = false
    private var selfRef: BonjourCoordinator?

    init(serviceType: String, timeout: TimeInterval, onComplete: @escaping ([String]) -> Void) {
        self.timeout = timeout
        self.onComplete = onComplete
        let params = NWParameters()
        params.includePeerToPeer = true
        self.browser = NWBrowser(for: .bonjour(type: serviceType, domain: nil), using: params)
    }

    func start() {
        selfRef = self
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            self?.queue.async { self?.handle(results: results) }
        }
        browser.stateUpdateHandler = { [weak self] state in
            if case .failed = state { self?.queue.async { self?.finish() } }
        }
        browser.start(queue: queue)
        queue.asyncAfter(deadline: .now() + timeout) { [weak self] in self?.finish() }
    }

    private func handle(results: Set<NWBrowser.Result>) {
        guard !finished else { return }
        for result in results {
            let connection = NWConnection(to: result.endpoint, using: .tcp)
            pendingConnections.append(connection)
            connection.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                self.queue.async {
                    switch state {
                    case .ready:
                        if let ip = Self.ipv4(from: connection.currentPath?.remoteEndpoint) {
                            self.resolved.insert(ip)
                        }
                        connection.cancel()
                    case .failed, .cancelled:
                        break
                    default:
                        break
                    }
                }
            }
            connection.start(queue: queue)
        }
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        browser.cancel()
        for c in pendingConnections { c.cancel() }
        pendingConnections.removeAll()
        onComplete(Array(resolved))
        selfRef = nil
    }

    private static func ipv4(from endpoint: NWEndpoint?) -> String? {
        guard case let .hostPort(host, _) = endpoint else { return nil }
        switch host {
        case .ipv4(let addr):
            return addr.debugDescription.split(separator: "%").first.map(String.init)
        default:
            return nil
        }
    }
}
