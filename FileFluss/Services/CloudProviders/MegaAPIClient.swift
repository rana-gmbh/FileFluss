import Foundation
import CommonCrypto
import os

private let megaLog = Logger(subsystem: "com.rana.FileFluss", category: "mega")

struct MegaCredentials: Codable, Sendable {
    let sessionId: String
    let masterKey: [UInt32] // 4 x UInt32 = 128-bit AES key
    let email: String
}

actor MegaAPIClient {
    private static let apiURL = "https://g.api.mega.co.nz/cs"

    private(set) var credentials: MegaCredentials
    private let session: URLSession
    private var sequenceNumber: Int = Int.random(in: 0..<0x100000000)

    /// Cached node tree: handle → node
    private var nodes: [String: MegaNode] = [:]
    /// Root folder handle
    private var rootHandle: String?
    /// Cached decrypted file keys: handle → full 8-word decrypted key (for files) or 4-word key (for folders)
    private var nodeKeys: [String: [UInt32]] = [:]

    init(credentials: MegaCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - Authentication

    static func login(email: String, password: String) async throws -> MegaCredentials {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        let session = URLSession(configuration: config)

        // Step 1: Pre-login to get version and salt
        let preLogin = try await apiRequest(
            session: session,
            sessionId: nil,
            commands: [["a": "us0", "user": email]]
        )

        guard let preLoginResult = preLogin.first as? [String: Any],
              let version = preLoginResult["v"] as? Int else {
            throw CloudProviderError.invalidResponse
        }

        // Step 2: Derive password key
        let passwordKey: [UInt32]
        if version == 2, let saltBase64 = preLoginResult["s"] as? String {
            // v2: PBKDF2 with salt
            guard let saltData = Data(base64MegaDecode: saltBase64) else {
                throw CloudProviderError.invalidResponse
            }
            let derivedKey = pbkdf2(password: password, salt: saltData, iterations: 100_000, keyLength: 32)
            // Use first 16 bytes as password key
            passwordKey = bytesToUInt32Array(Array(derivedKey.prefix(16)))
        } else {
            // v1: Legacy key derivation
            passwordKey = deriveKeyV1(password: password)
        }

        // Step 3: Compute user handle from password key
        let userHandle: String
        if version == 2 {
            // v2: PBKDF2-derived handle
            let derivedKey = pbkdf2(password: password, salt: Data(base64MegaDecode: preLoginResult["s"] as? String ?? "") ?? Data(), iterations: 100_000, keyLength: 32)
            let handleBytes = Array(derivedKey.suffix(16))
            userHandle = Data(handleBytes).base64MegaEncode()
        } else {
            // v1: AES-ECB encrypt email hash
            let emailHash = makeEmailHash(email: email, key: passwordKey)
            userHandle = emailHash
        }

        // Step 4: Login
        var loginCmd: [String: Any] = ["a": "us", "user": email, "uh": userHandle]
        if version == 2 {
            loginCmd["sek"] = NSNull() // Not using session encryption key
        }

        let loginResult = try await apiRequest(
            session: session,
            sessionId: nil,
            commands: [loginCmd]
        )

        guard let loginData = loginResult.first as? [String: Any] else {
            // Check for error code
            if let errorCode = loginResult.first as? Int {
                switch errorCode {
                case -2: throw CloudProviderError.invalidCredentials
                case -9: throw CloudProviderError.unauthorized
                case -16: throw CloudProviderError.rateLimited
                default: throw CloudProviderError.serverError(errorCode)
                }
            }
            throw CloudProviderError.invalidResponse
        }

        // Step 5: Decrypt master key
        guard let encryptedMasterKeyBase64 = loginData["k"] as? String,
              let encryptedMasterKeyData = Data(base64MegaDecode: encryptedMasterKeyBase64) else {
            throw CloudProviderError.invalidResponse
        }

        let encryptedMasterKey = bytesToUInt32Array(Array(encryptedMasterKeyData))
        let masterKey = decryptECB(data: encryptedMasterKey, key: passwordKey)

        // Step 6: Get session ID
        let sessionId: String
        if let tsidBase64 = loginData["tsid"] as? String,
           let tsidData = Data(base64MegaDecode: tsidBase64) {
            // Temporary session ID - decrypt and verify
            let tsidBytes = Array(tsidData)
            let firstHalf = Array(tsidBytes.prefix(16))
            let decrypted = decryptECB(data: bytesToUInt32Array(firstHalf), key: masterKey)
            let decryptedBytes = uint32ArrayToBytes(decrypted)
            let secondHalf = Array(tsidBytes.suffix(from: 16).prefix(16))
            if decryptedBytes == secondHalf {
                sessionId = tsidBase64
            } else {
                throw CloudProviderError.invalidCredentials
            }
        } else if let csidBase64 = loginData["csid"] as? String,
                  let privkBase64 = loginData["privk"] as? String {
            // RSA session - decrypt private key and then session ID
            guard let privkData = Data(base64MegaDecode: privkBase64) else {
                throw CloudProviderError.invalidResponse
            }
            let decryptedPrivKey = decryptECB(data: bytesToUInt32Array(Array(privkData)), key: masterKey)
            let privKeyBytes = uint32ArrayToBytes(decryptedPrivKey)

            let rsaPrivKey = try parseMPISequence(from: privKeyBytes, count: 4)
            guard let csidData = Data(base64MegaDecode: csidBase64) else {
                throw CloudProviderError.invalidResponse
            }
            let csidMPI = parseMPI(from: Array(csidData))
            let decryptedSid = rsaDecrypt(data: csidMPI, privKey: rsaPrivKey)
            sessionId = Data(decryptedSid.prefix(43)).base64MegaEncode()
        } else {
            throw CloudProviderError.invalidResponse
        }

        megaLog.info("[Mega] Successfully authenticated as \(email)")

        return MegaCredentials(
            sessionId: sessionId,
            masterKey: masterKey,
            email: email
        )
    }

    // MARK: - File Operations

    func fetchNodes() async throws {
        let result = try await apiCall([["a": "f", "c": 1]])

        guard let filesData = result.first as? [String: Any],
              let fileNodes = filesData["f"] as? [[String: Any]] else {
            throw CloudProviderError.invalidResponse
        }

        nodes.removeAll()
        rootHandle = nil

        for nodeData in fileNodes {
            guard let handle = nodeData["h"] as? String,
                  let type = nodeData["t"] as? Int else { continue }

            let parent = nodeData["p"] as? String ?? ""
            let size = nodeData["s"] as? Int64 ?? 0
            let timestamp = nodeData["ts"] as? TimeInterval ?? 0

            // Decrypt node name from attributes
            let name: String
            if type == 2 {
                name = "Cloud Drive"
                rootHandle = handle
            } else if type == 3 {
                name = "Inbox"
            } else if type == 4 {
                name = "Trash"
            } else if let attrBase64 = nodeData["a"] as? String,
                      let keyStr = nodeData["k"] as? String {
                name = decryptNodeName(attr: attrBase64, keyStr: keyStr, handle: handle) ?? "Unknown"
            } else {
                name = "Unknown"
            }

            let node = MegaNode(
                handle: handle,
                parentHandle: parent,
                name: name,
                type: type,
                size: size,
                timestamp: timestamp
            )
            nodes[handle] = node
        }

        megaLog.debug("[Mega] Loaded \(self.nodes.count) nodes, root=\(self.rootHandle ?? "nil")")
    }

    func listFolder(path: String) async throws -> [CloudFileItem] {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let parentHandle = try resolveHandle(for: path)

        return nodes.values
            .filter { $0.parentHandle == parentHandle && $0.type <= 1 }
            .map { node in
                let itemPath = path == "/" ? "/\(node.name)" : "\(path)/\(node.name)"
                return CloudFileItem(
                    id: node.handle,
                    name: node.name,
                    path: itemPath,
                    isDirectory: node.type == 1,
                    size: node.size,
                    modificationDate: Date(timeIntervalSince1970: node.timestamp),
                    checksum: nil
                )
            }
            .sorted { lhs, rhs in
                if lhs.isDirectory != rhs.isDirectory { return lhs.isDirectory }
                return lhs.name.localizedStandardCompare(rhs.name) == .orderedAscending
            }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let handle = try resolveHandle(for: remotePath)
        guard let node = nodes[handle], node.type == 0 else {
            throw CloudProviderError.notFound(remotePath)
        }

        // Re-fetch nodes if we don't have the decryption key cached
        if nodeKeys[handle] == nil {
            megaLog.info("[Mega] Key missing for \(node.name), re-fetching node tree")
            try await fetchNodes()
        }

        let result = try await apiCall([["a": "g", "g": 1, "n": handle]])

        guard let dlData = result.first as? [String: Any],
              let downloadURLStr = dlData["g"] as? String else {
            throw CloudProviderError.invalidResponse
        }

        // Mega may return HTTP URLs; force HTTPS for App Transport Security
        let secureURLStr = downloadURLStr.hasPrefix("http://")
            ? "https://" + downloadURLStr.dropFirst(7)
            : downloadURLStr
        guard let downloadURL = URL(string: secureURLStr) else {
            throw CloudProviderError.invalidResponse
        }

        let (data, response) = try await session.data(from: downloadURL)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        // Decrypt the downloaded file using the node key
        let decryptedData = try decryptFileData(data, handle: handle)
        try decryptedData.write(to: localURL)
    }

    func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String) async throws {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let parentHandle = try resolveHandle(for: folderPath)
        let fileData = try Data(contentsOf: localURL)
        let fileSize = fileData.count

        // Step 1: Request upload URL
        let uploadResult = try await apiCall([["a": "u", "s": fileSize]])
        guard let uploadData = uploadResult.first as? [String: Any],
              let uploadURLStr = uploadData["p"] as? String else {
            throw CloudProviderError.invalidResponse
        }

        // Mega may return HTTP URLs; force HTTPS for App Transport Security
        let secureUploadURLStr = uploadURLStr.hasPrefix("http://")
            ? "https://" + uploadURLStr.dropFirst(7)
            : uploadURLStr
        guard let uploadURL = URL(string: secureUploadURLStr) else {
            throw CloudProviderError.invalidResponse
        }

        // Step 2: Generate random file key and encrypt the file
        let fileKey = generateRandomKey()
        let iv = generateRandomIV()
        let encryptedData = encryptAESCTR(data: fileData, key: fileKey, iv: iv)

        // Step 3: Upload encrypted data
        var request = URLRequest(url: uploadURL)
        request.httpMethod = "POST"
        request.httpBody = encryptedData
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        let (responseData, uploadResponse) = try await session.data(for: request)
        guard let http = uploadResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        guard let completionHandle = String(data: responseData, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !completionHandle.isEmpty else {
            throw CloudProviderError.invalidResponse
        }

        // Step 4: Complete upload by creating the file node
        let encryptedAttrs = encryptAttributes(name: fileName, key: fileKey)
        let nodeKey = encryptNodeKey(fileKey: fileKey, iv: iv, masterKey: credentials.masterKey)

        let putResult = try await apiCall([[
            "a": "p",
            "t": parentHandle,
            "n": [[
                "h": completionHandle,
                "t": 0,
                "a": encryptedAttrs,
                "k": nodeKey
            ]]
        ]])

        // Refresh node tree and cache key for the uploaded file
        if let putData = putResult.first as? [String: Any],
           let newNodes = putData["f"] as? [[String: Any]] {
            for nodeData in newNodes {
                guard let handle = nodeData["h"] as? String,
                      let type = nodeData["t"] as? Int else { continue }
                let node = MegaNode(
                    handle: handle,
                    parentHandle: nodeData["p"] as? String ?? "",
                    name: fileName,
                    type: type,
                    size: Int64(fileSize),
                    timestamp: Date().timeIntervalSince1970
                )
                nodes[handle] = node
                // Cache the decryption key: [k0..k3, iv0, iv1, 0, 0]
                if type == 0 {
                    nodeKeys[handle] = fileKey + iv + [0, 0]
                }
            }
        }
    }

    func deleteItem(handle: String) async throws {
        let _ = try await apiCall([["a": "d", "n": handle]])
        nodes.removeValue(forKey: handle)
    }

    func deleteItem(at path: String) async throws {
        if nodes.isEmpty {
            try await fetchNodes()
        }
        let handle = try resolveHandle(for: path)
        try await deleteItem(handle: handle)
    }

    func createFolder(at path: String) async throws {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent
        let parentHandle = try resolveHandle(for: parentPath)

        let folderKey = generateRandomKey()
        let encryptedAttrs = encryptAttributes(name: folderName, key: folderKey)
        let nodeKey = encryptNodeKeyFolder(folderKey: folderKey, masterKey: credentials.masterKey)

        let result = try await apiCall([[
            "a": "p",
            "t": parentHandle,
            "n": [[
                "h": "xxxxxxxx",
                "t": 1,
                "a": encryptedAttrs,
                "k": nodeKey
            ]]
        ]])

        // Update cached nodes
        if let resultData = result.first as? [String: Any],
           let newNodes = resultData["f"] as? [[String: Any]] {
            for nodeData in newNodes {
                guard let handle = nodeData["h"] as? String,
                      let type = nodeData["t"] as? Int else { continue }
                let node = MegaNode(
                    handle: handle,
                    parentHandle: nodeData["p"] as? String ?? "",
                    name: folderName,
                    type: type,
                    size: 0,
                    timestamp: Date().timeIntervalSince1970
                )
                nodes[handle] = node
            }
        }
    }

    func renameItem(at path: String, to newName: String) async throws {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let handle = try resolveHandle(for: path)
        guard let node = nodes[handle] else {
            throw CloudProviderError.notFound(path)
        }

        // Re-encrypt attributes with new name using the node's key
        let nodeKey = try getNodeKey(handle: handle)
        let encryptedAttrs = encryptAttributes(name: newName, key: nodeKey)

        let _ = try await apiCall([["a": "a", "n": handle, "attr": encryptedAttrs]])

        // Update cache
        nodes[handle] = MegaNode(
            handle: node.handle,
            parentHandle: node.parentHandle,
            name: newName,
            type: node.type,
            size: node.size,
            timestamp: node.timestamp
        )
    }

    func stat(at path: String) async throws -> CloudFileItem {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let handle = try resolveHandle(for: path)
        guard let node = nodes[handle] else {
            throw CloudProviderError.notFound(path)
        }

        return CloudFileItem(
            id: node.handle,
            name: node.name,
            path: path,
            isDirectory: node.type == 1,
            size: node.size,
            modificationDate: Date(timeIntervalSince1970: node.timestamp),
            checksum: nil
        )
    }

    func folderSize(at path: String) async throws -> Int64 {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let handle = try resolveHandle(for: path)
        return computeFolderSize(handle: handle)
    }

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        if nodes.isEmpty {
            try await fetchNodes()
        }

        let lowercaseQuery = query.lowercased()
        let searchRoot: String?
        if let path, path != "/" {
            searchRoot = try? resolveHandle(for: path)
        } else {
            searchRoot = nil
        }

        return nodes.values
            .filter { node in
                guard node.type <= 1 else { return false }
                guard node.name.lowercased().contains(lowercaseQuery) else { return false }
                if let searchRoot {
                    return isDescendant(handle: node.handle, of: searchRoot)
                }
                return true
            }
            .map { node in
                let fullPath = buildPath(for: node.handle)
                return CloudFileItem(
                    id: node.handle,
                    name: node.name,
                    path: fullPath,
                    isDirectory: node.type == 1,
                    size: node.size,
                    modificationDate: Date(timeIntervalSince1970: node.timestamp),
                    checksum: nil
                )
            }
    }

    func userDisplayName() async throws -> String {
        credentials.email
    }

    // MARK: - Private Helpers

    private func apiCall(_ commands: [[String: Any]]) async throws -> [Any] {
        let body = try JSONSerialization.data(withJSONObject: commands)
        return try await Self.apiRequest(session: session, sessionId: credentials.sessionId, body: body)
    }

    private static func apiRequest(session: URLSession, sessionId: String?, commands: [[String: Any]]) async throws -> [Any] {
        let body = try JSONSerialization.data(withJSONObject: commands)
        return try await apiRequest(session: session, sessionId: sessionId, body: body)
    }

    private static func apiRequest(session: URLSession, sessionId: String?, body: Data) async throws -> [Any] {
        let seqNum = Int.random(in: 0..<0x100000000)
        var urlString = "\(apiURL)?id=\(seqNum)"
        if let sessionId {
            urlString += "&sid=\(sessionId)"
        }

        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let (data, response) = try await session.data(for: request)

        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Handle hashcash challenge (HTTP 402)
        if http.statusCode == 402 {
            guard let challenge = http.value(forHTTPHeaderField: "X-Hashcash") else {
                throw CloudProviderError.invalidResponse
            }

            megaLog.info("[Mega] Solving hashcash challenge…")
            let solution = try await solveHashcash(challenge: challenge)

            // Retry with hashcash solution
            var retryRequest = request
            retryRequest.setValue(solution, forHTTPHeaderField: "X-Hashcash")

            let (retryData, retryResponse) = try await session.data(for: retryRequest)
            guard let retryHttp = retryResponse as? HTTPURLResponse,
                  (200...299).contains(retryHttp.statusCode) else {
                throw CloudProviderError.invalidResponse
            }

            return try parseAPIResponse(retryData)
        }

        guard (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(http.statusCode)
        }

        return try parseAPIResponse(data)
    }

    private static func parseAPIResponse(_ data: Data) throws -> [Any] {
        guard let result = try JSONSerialization.jsonObject(with: data) as? [Any] else {
            if let errorCode = try JSONSerialization.jsonObject(with: data) as? Int {
                throw mapError(code: errorCode)
            }
            throw CloudProviderError.invalidResponse
        }

        if let firstResult = result.first as? Int, firstResult < 0 {
            throw mapError(code: firstResult)
        }

        return result
    }

    // MARK: - Hashcash Proof-of-Work

    private static func solveHashcash(challenge: String) async throws -> String {
        let parts = challenge.split(separator: ":")
        guard parts.count == 4,
              parts[0] == "1",
              let easiness = UInt8(parts[1]) else {
            throw CloudProviderError.invalidResponse
        }

        let token = String(parts[3])

        // Decode token from base64url
        var base64 = token
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while base64.count % 4 != 0 { base64 += "=" }
        guard let tokenData = Data(base64Encoded: base64), tokenData.count == 48 else {
            throw CloudProviderError.invalidResponse
        }

        // Compute threshold from easiness byte
        let threshold = hashcashThreshold(easiness)

        // Build the ~12MB buffer template: 4-byte nonce + 262144 repetitions of 48-byte token
        let kRepeat = 262_144
        let bufSize = 4 + kRepeat * 48
        var templateBuffer = Data(count: bufSize)
        templateBuffer.withUnsafeMutableBytes { buf in
            let ptr = buf.baseAddress!.assumingMemoryBound(to: UInt8.self)
            tokenData.withUnsafeBytes { tokenBuf in
                let tokenPtr = tokenBuf.baseAddress!.assumingMemoryBound(to: UInt8.self)
                for i in 0..<kRepeat {
                    memcpy(ptr + 4 + i * 48, tokenPtr, 48)
                }
            }
        }

        // Solve on background threads
        let solution = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
            let workerCount = ProcessInfo.processInfo.activeProcessorCount
            // Use pointers for shared mutable state (Swift 6 concurrency safe)
            let stop = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
            stop.initialize(to: false)
            let resolved = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
            resolved.initialize(to: false)
            let group = DispatchGroup()
            let resultLock = NSLock()

            for w in 0..<workerCount {
                group.enter()
                DispatchQueue.global(qos: .userInitiated).async {
                    defer { group.leave() }
                    var buf = templateBuffer
                    var n = UInt32(w)
                    let stride = UInt32(workerCount)

                    buf.withUnsafeMutableBytes { rawBuf in
                        let ptr = rawBuf.baseAddress!
                        var digest = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))

                        while !stop.pointee {
                            // Write nonce in big-endian
                            var nonceBE = n.bigEndian
                            memcpy(ptr, &nonceBE, 4)

                            // SHA-256 of entire buffer
                            CC_SHA256(ptr, CC_LONG(bufSize), &digest)

                            // First 4 bytes as big-endian uint32
                            let hashWord = UInt32(digest[0]) << 24
                                | UInt32(digest[1]) << 16
                                | UInt32(digest[2]) << 8
                                | UInt32(digest[3])

                            if hashWord <= threshold {
                                resultLock.lock()
                                if !stop.pointee {
                                    stop.pointee = true
                                    // Encode nonce as base64url
                                    var nonceBE = n.bigEndian
                                    let nonceData = Data(bytes: &nonceBE, count: 4)
                                    let encoded = nonceData.base64EncodedString()
                                        .replacingOccurrences(of: "+", with: "-")
                                        .replacingOccurrences(of: "/", with: "_")
                                        .replacingOccurrences(of: "=", with: "")

                                    let solution = "1:\(token):\(encoded)"
                                    resolved.pointee = true
                                    resultLock.unlock()
                                    continuation.resume(returning: solution)
                                } else {
                                    resultLock.unlock()
                                }
                                return
                            }

                            let (next, overflow) = n.addingReportingOverflow(stride)
                            if overflow { return }
                            n = next
                        }
                    }
                }
            }

            group.notify(queue: .global()) {
                resultLock.lock()
                let wasResolved = resolved.pointee
                resultLock.unlock()
                stop.deallocate()
                resolved.deallocate()
                if !wasResolved {
                    continuation.resume(throwing: CloudProviderError.serverError(402))
                }
            }
        }

        megaLog.info("[Mega] Hashcash solved")
        return solution
    }

    private static func hashcashThreshold(_ easiness: UInt8) -> UInt32 {
        let e = UInt32(easiness)
        let mantissa = ((e & 63) << 1) + 1
        let shift = (e >> 6) * 7 + 3
        return mantissa << shift
    }

    private func resolveHandle(for path: String) throws -> String {
        if path == "/" || path.isEmpty {
            guard let root = rootHandle else {
                throw CloudProviderError.notFound("Root folder not found")
            }
            return root
        }

        let components = path.split(separator: "/").map(String.init)
        guard let root = rootHandle else {
            throw CloudProviderError.notFound("Root folder not found")
        }

        var currentHandle = root
        for component in components {
            guard let child = nodes.values.first(where: {
                $0.parentHandle == currentHandle && $0.name == component
            }) else {
                throw CloudProviderError.notFound(path)
            }
            currentHandle = child.handle
        }

        return currentHandle
    }

    private func computeFolderSize(handle: String) -> Int64 {
        var total: Int64 = 0
        for node in nodes.values where node.parentHandle == handle {
            if node.type == 1 {
                total += computeFolderSize(handle: node.handle)
            } else {
                total += node.size
            }
        }
        return total
    }

    private func isDescendant(handle: String, of ancestor: String) -> Bool {
        var current = handle
        while let node = nodes[current] {
            if node.parentHandle == ancestor { return true }
            if node.parentHandle.isEmpty { return false }
            current = node.parentHandle
        }
        return false
    }

    private func buildPath(for handle: String) -> String {
        var components: [String] = []
        var current = handle
        while let node = nodes[current] {
            if node.handle == rootHandle { break }
            components.insert(node.name, at: 0)
            current = node.parentHandle
        }
        return "/" + components.joined(separator: "/")
    }

    // MARK: - Crypto Helpers

    private func decryptNodeName(attr: String, keyStr: String, handle: String) -> String? {
        // Key string format: "owner:base64key" or "handle:base64key/..."
        let keyParts = keyStr.split(separator: "/").compactMap { part -> Data? in
            let segments = part.split(separator: ":")
            guard segments.count >= 2 else { return nil }
            return Data(base64MegaDecode: String(segments.last!))
        }

        guard let encKeyData = keyParts.first else { return nil }
        let encKey = bytesToUInt32Array(Array(encKeyData))

        // Decrypt node key with master key
        let nodeKey: [UInt32]
        if encKey.count == 4 {
            // Folder key: 4 words
            let decrypted = decryptECB(data: encKey, key: credentials.masterKey)
            nodeKeys[handle] = decrypted
            nodeKey = decrypted
        } else if encKey.count >= 8 {
            // File key: 8 words → decrypt to get full key, then XOR for attribute key
            let decrypted = decryptECB(data: Array(encKey.prefix(8)), key: credentials.masterKey)
            nodeKeys[handle] = decrypted // Cache full 8-word key for file decryption
            nodeKey = [
                decrypted[0] ^ decrypted[4],
                decrypted[1] ^ decrypted[5],
                decrypted[2] ^ decrypted[6],
                decrypted[3] ^ decrypted[7]
            ]
        } else {
            return nil
        }

        // Decrypt attributes
        guard let attrData = Data(base64MegaDecode: attr) else { return nil }
        let decryptedAttr = decryptCBC(data: Array(attrData), key: nodeKey)

        // Attributes start with "MEGA" prefix
        guard decryptedAttr.count >= 4,
              decryptedAttr[0] == 0x4D, // M
              decryptedAttr[1] == 0x45, // E
              decryptedAttr[2] == 0x47, // G
              decryptedAttr[3] == 0x41  // A
        else { return nil }

        // Parse JSON after "MEGA" prefix, stripping trailing nulls
        let jsonBytes = Array(decryptedAttr[4...]).prefix(while: { $0 != 0 })
        guard let jsonData = String(bytes: jsonBytes, encoding: .utf8),
              let parsed = try? JSONSerialization.jsonObject(with: Data(jsonData.utf8)) as? [String: Any],
              let name = parsed["n"] as? String else {
            return nil
        }

        return name
    }

    private func getNodeKey(handle: String) throws -> [UInt32] {
        if let cachedKey = nodeKeys[handle] {
            // For files (8 words), return the XORed attribute key
            if cachedKey.count >= 8 {
                return [
                    cachedKey[0] ^ cachedKey[4],
                    cachedKey[1] ^ cachedKey[5],
                    cachedKey[2] ^ cachedKey[6],
                    cachedKey[3] ^ cachedKey[7]
                ]
            }
            return cachedKey
        }
        return credentials.masterKey
    }

    private func decryptFileData(_ data: Data, handle: String) throws -> Data {
        guard let fullKey = nodeKeys[handle], fullKey.count >= 8 else {
            megaLog.warning("[Mega] No cached key for handle \(handle), returning raw data")
            return data
        }

        // File content key: words 0-3
        let fileKey = Array(fullKey.prefix(4))

        // CTR nonce: words 4-5 in standard MEGA format
        // Standard MEGA key: [k0, k1, k2, k3, iv0, iv1, mac0, mac1]
        let iv = [fullKey[4], fullKey[5]]

        megaLog.info("[Mega] Decrypting \(data.count) bytes for handle \(handle)")

        // CTR decryption = encryption (XOR with keystream)
        let decrypted = aesCTR(data: data, key: fileKey, iv: iv)
        return decrypted
    }

    /// AES-128-CTR encrypt/decrypt (symmetric operation)
    private func aesCTR(data: Data, key: [UInt32], iv: [UInt32]) -> Data {
        let keyBytes = uint32ArrayToBytes(key)
        // CTR counter: [nonce0, nonce1, 0, 0] — 16 bytes total
        var ivBytes = uint32ArrayToBytes(iv)
        while ivBytes.count < 16 { ivBytes.append(0) }

        var output = [UInt8](repeating: 0, count: data.count)

        var cryptor: CCCryptorRef?
        let status = CCCryptorCreateWithMode(
            CCOperation(kCCEncrypt), // CTR: encrypt == decrypt
            CCMode(kCCModeCTR),
            CCAlgorithm(kCCAlgorithmAES128),
            CCPadding(ccNoPadding),
            ivBytes,
            keyBytes,
            keyBytes.count,
            nil, 0, 0,
            CCModeOptions(kCCModeOptionCTR_BE),
            &cryptor
        )

        guard status == kCCSuccess, let cryptor else {
            megaLog.error("[Mega] CCCryptorCreateWithMode failed: \(status)")
            return data
        }

        var dataOut = 0
        data.withUnsafeBytes { rawBuffer in
            guard let ptr = rawBuffer.baseAddress else { return }
            CCCryptorUpdate(cryptor, ptr, data.count, &output, output.count, &dataOut)
        }
        CCCryptorRelease(cryptor)

        return Data(output.prefix(dataOut))
    }

    private func encryptAttributes(name: String, key: [UInt32]) -> String {
        let json = "{\"n\":\"\(name.replacingOccurrences(of: "\"", with: "\\\""))\"}"
        var attrBytes = Array("MEGA".utf8) + Array(json.utf8)
        // Pad to 16-byte boundary
        while attrBytes.count % 16 != 0 {
            attrBytes.append(0)
        }
        let encrypted = encryptCBC(data: attrBytes, key: key)
        return Data(encrypted).base64MegaEncode()
    }

    private func encryptNodeKey(fileKey: [UInt32], iv: [UInt32], masterKey: [UInt32]) -> String {
        // MEGA file node key: [k0, k1, k2, k3, iv0, iv1, mac0, mac1]
        // mac is set to 0 for simplicity (integrity checked separately by MEGA)
        let compositeKey: [UInt32] = [
            fileKey[0], fileKey[1], fileKey[2], fileKey[3],
            iv[0], iv[1], 0, 0
        ]
        let encrypted = encryptECB(data: compositeKey, key: masterKey)
        return Data(uint32ArrayToBytes(encrypted)).base64MegaEncode()
    }

    private func encryptNodeKeyFolder(folderKey: [UInt32], masterKey: [UInt32]) -> String {
        let encrypted = encryptECB(data: folderKey, key: masterKey)
        return Data(uint32ArrayToBytes(encrypted)).base64MegaEncode()
    }

    private func generateRandomKey() -> [UInt32] {
        var key = [UInt32](repeating: 0, count: 4)
        for i in 0..<4 {
            key[i] = UInt32.random(in: 0...UInt32.max)
        }
        return key
    }

    private func generateRandomIV() -> [UInt32] {
        var iv = [UInt32](repeating: 0, count: 2)
        for i in 0..<2 {
            iv[i] = UInt32.random(in: 0...UInt32.max)
        }
        return iv
    }

    private func encryptAESCTR(data: Data, key: [UInt32], iv: [UInt32]) -> Data {
        aesCTR(data: data, key: key, iv: iv)
    }

    private static func mapError(code: Int) -> CloudProviderError {
        switch code {
        case -2: return .invalidCredentials
        case -4: return .quotaExceeded
        case -6: return .rateLimited
        case -9: return .notFound("Object not found")
        case -11: return .unauthorized
        case -14: return .notFound("Temporary not available")
        case -15: return .serverError(code) // Session expired
        case -16: return .rateLimited
        case -17: return .quotaExceeded // Over quota
        default: return .serverError(code)
        }
    }
}

// MARK: - Crypto Primitives

private func deriveKeyV1(password: String) -> [UInt32] {
    var key: [UInt32] = [0x93C467E3, 0x7DB0C7A4, 0xD1BE3F81, 0x0152CB56]
    let paddedPassword = Array(password.utf8)

    for i in stride(from: 0, to: paddedPassword.count, by: 16) {
        var block = [UInt32](repeating: 0, count: 4)
        for j in 0..<min(16, paddedPassword.count - i) {
            let wordIdx = j / 4
            let byteIdx = j % 4
            block[wordIdx] |= UInt32(paddedPassword[i + j]) << UInt32((3 - byteIdx) * 8)
        }
        key = encryptECB(data: key, key: block)
    }

    return key
}

private func makeEmailHash(email: String, key: [UInt32]) -> String {
    let emailLower = email.lowercased()
    var hash: [UInt32] = [0, 0, 0, 0]
    let emailBytes = Array(emailLower.utf8)

    for i in 0..<emailBytes.count {
        hash[i % 4] ^= UInt32(emailBytes[i]) << UInt32(((i / 4) % 4) * 8)
    }

    for _ in 0..<16384 {
        hash = encryptECB(data: hash, key: key)
    }

    let resultBytes = uint32ArrayToBytes([hash[0], hash[2]])
    return Data(resultBytes).base64MegaEncode()
}

private func encryptECB(data: [UInt32], key: [UInt32]) -> [UInt32] {
    let keyBytes = uint32ArrayToBytes(key)
    let dataBytes = uint32ArrayToBytes(data)
    var encrypted = [UInt8](repeating: 0, count: dataBytes.count)
    var dataOutMoved = 0

    CCCrypt(
        CCOperation(kCCEncrypt),
        CCAlgorithm(kCCAlgorithmAES128),
        CCOptions(kCCOptionECBMode),
        keyBytes, keyBytes.count,
        nil,
        dataBytes, dataBytes.count,
        &encrypted, encrypted.count,
        &dataOutMoved
    )

    return bytesToUInt32Array(Array(encrypted.prefix(dataBytes.count)))
}

private func decryptECB(data: [UInt32], key: [UInt32]) -> [UInt32] {
    let keyBytes = uint32ArrayToBytes(key)
    let dataBytes = uint32ArrayToBytes(data)
    var decrypted = [UInt8](repeating: 0, count: dataBytes.count + 16)
    var dataOutMoved = 0

    CCCrypt(
        CCOperation(kCCDecrypt),
        CCAlgorithm(kCCAlgorithmAES128),
        CCOptions(kCCOptionECBMode),
        keyBytes, keyBytes.count,
        nil,
        dataBytes, dataBytes.count,
        &decrypted, decrypted.count,
        &dataOutMoved
    )

    return bytesToUInt32Array(Array(decrypted.prefix(dataBytes.count)))
}

private func encryptCBC(data: [UInt8], key: [UInt32]) -> [UInt8] {
    let keyBytes = uint32ArrayToBytes(key)
    let iv = [UInt8](repeating: 0, count: 16)
    var encrypted = [UInt8](repeating: 0, count: data.count + 16)
    var dataOutMoved = 0

    CCCrypt(
        CCOperation(kCCEncrypt),
        CCAlgorithm(kCCAlgorithmAES128),
        0, // No padding
        keyBytes, keyBytes.count,
        iv,
        data, data.count,
        &encrypted, encrypted.count,
        &dataOutMoved
    )

    return Array(encrypted.prefix(dataOutMoved))
}

private func decryptCBC(data: [UInt8], key: [UInt32]) -> [UInt8] {
    let keyBytes = uint32ArrayToBytes(key)
    let iv = [UInt8](repeating: 0, count: 16)
    var decrypted = [UInt8](repeating: 0, count: data.count + 16)
    var dataOutMoved = 0

    CCCrypt(
        CCOperation(kCCDecrypt),
        CCAlgorithm(kCCAlgorithmAES128),
        0, // No padding
        keyBytes, keyBytes.count,
        iv,
        data, data.count,
        &decrypted, decrypted.count,
        &dataOutMoved
    )

    return Array(decrypted.prefix(dataOutMoved))
}

private func pbkdf2(password: String, salt: Data, iterations: Int, keyLength: Int) -> Data {
    var derivedKey = [UInt8](repeating: 0, count: keyLength)
    let passwordData = Array(password.utf8)

    salt.withUnsafeBytes { saltBuffer in
        let saltPtr = saltBuffer.baseAddress!.assumingMemoryBound(to: UInt8.self)
        CCKeyDerivationPBKDF(
            CCPBKDFAlgorithm(kCCPBKDF2),
            passwordData, passwordData.count,
            saltPtr, salt.count,
            CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA512),
            UInt32(iterations),
            &derivedKey, keyLength
        )
    }

    return Data(derivedKey)
}

// MARK: - Byte Conversion

private func bytesToUInt32Array(_ bytes: [UInt8]) -> [UInt32] {
    var result = [UInt32]()
    let count = bytes.count / 4
    for i in 0..<count {
        let value = UInt32(bytes[i * 4]) << 24
            | UInt32(bytes[i * 4 + 1]) << 16
            | UInt32(bytes[i * 4 + 2]) << 8
            | UInt32(bytes[i * 4 + 3])
        result.append(value)
    }
    return result
}

private func uint32ArrayToBytes(_ values: [UInt32]) -> [UInt8] {
    var result = [UInt8]()
    for value in values {
        result.append(UInt8((value >> 24) & 0xFF))
        result.append(UInt8((value >> 16) & 0xFF))
        result.append(UInt8((value >> 8) & 0xFF))
        result.append(UInt8(value & 0xFF))
    }
    return result
}

// MARK: - RSA Helpers

private func parseMPI(from bytes: [UInt8]) -> (value: [UInt8], bytesRead: Int) {
    guard bytes.count >= 2 else { return ([], 0) }
    let bitLength = Int(bytes[0]) << 8 | Int(bytes[1])
    let byteLength = (bitLength + 7) / 8
    guard bytes.count >= 2 + byteLength else { return ([], 0) }
    let value = Array(bytes[2..<(2 + byteLength)])
    return (value, 2 + byteLength)
}

private func parseMPISequence(from bytes: [UInt8], count: Int) throws -> [[UInt8]] {
    var result: [[UInt8]] = []
    var offset = 0
    for _ in 0..<count {
        let (value, bytesRead) = parseMPI(from: Array(bytes[offset...]))
        guard bytesRead > 0 else { throw CloudProviderError.invalidResponse }
        result.append(value)
        offset += bytesRead
    }
    return result
}

private func rsaDecrypt(data: (value: [UInt8], bytesRead: Int), privKey: [[UInt8]]) -> [UInt8] {
    guard privKey.count >= 4 else { return data.value }

    let p = BigUInt(data: privKey[0])
    let q = BigUInt(data: privKey[1])
    let d = BigUInt(data: privKey[2])
    let n = p * q

    let ciphertext = BigUInt(data: data.value)
    let plaintext = ciphertext.modPow(d, n)
    return plaintext.toBytes()
}

// MARK: - Big Integer (minimal, for RSA only)

private struct BigUInt {
    // Digits stored in little-endian order (least significant first), base 2^32
    var digits: [UInt32]

    init(data bytes: [UInt8]) {
        // Convert big-endian bytes to little-endian UInt32 digits
        var result: [UInt32] = []
        var i = bytes.count
        while i > 0 {
            var word: UInt32 = 0
            let start = max(0, i - 4)
            for j in start..<i {
                word = (word << 8) | UInt32(bytes[j])
            }
            result.append(word)
            i = start
        }
        // Trim leading zeros
        while result.count > 1 && result.last == 0 { result.removeLast() }
        self.digits = result
    }

    init(digits: [UInt32]) {
        var d = digits
        while d.count > 1 && d.last == 0 { d.removeLast() }
        self.digits = d
    }

    static let zero = BigUInt(digits: [0])
    static let one = BigUInt(digits: [1])

    var isZero: Bool { digits.count == 1 && digits[0] == 0 }

    func toBytes() -> [UInt8] {
        var result: [UInt8] = []
        for i in stride(from: digits.count - 1, through: 0, by: -1) {
            let word = digits[i]
            result.append(UInt8((word >> 24) & 0xFF))
            result.append(UInt8((word >> 16) & 0xFF))
            result.append(UInt8((word >> 8) & 0xFF))
            result.append(UInt8(word & 0xFF))
        }
        // Strip leading zeros
        while result.count > 1 && result.first == 0 { result.removeFirst() }
        return result
    }

    // Compare
    func compare(_ other: BigUInt) -> Int {
        if digits.count != other.digits.count {
            return digits.count < other.digits.count ? -1 : 1
        }
        for i in stride(from: digits.count - 1, through: 0, by: -1) {
            if digits[i] != other.digits[i] {
                return digits[i] < other.digits[i] ? -1 : 1
            }
        }
        return 0
    }

    // Addition
    static func + (lhs: BigUInt, rhs: BigUInt) -> BigUInt {
        let maxLen = max(lhs.digits.count, rhs.digits.count)
        var result = [UInt32](repeating: 0, count: maxLen + 1)
        var carry: UInt64 = 0
        for i in 0..<maxLen {
            let a = i < lhs.digits.count ? UInt64(lhs.digits[i]) : 0
            let b = i < rhs.digits.count ? UInt64(rhs.digits[i]) : 0
            let sum = a + b + carry
            result[i] = UInt32(sum & 0xFFFFFFFF)
            carry = sum >> 32
        }
        result[maxLen] = UInt32(carry)
        return BigUInt(digits: result)
    }

    // Subtraction (assumes lhs >= rhs)
    static func - (lhs: BigUInt, rhs: BigUInt) -> BigUInt {
        var result = [UInt32](repeating: 0, count: lhs.digits.count)
        var borrow: Int64 = 0
        for i in 0..<lhs.digits.count {
            let a = Int64(lhs.digits[i])
            let b = i < rhs.digits.count ? Int64(rhs.digits[i]) : 0
            var diff = a - b - borrow
            if diff < 0 {
                diff += 0x100000000
                borrow = 1
            } else {
                borrow = 0
            }
            result[i] = UInt32(diff)
        }
        return BigUInt(digits: result)
    }

    // Multiplication
    static func * (lhs: BigUInt, rhs: BigUInt) -> BigUInt {
        if lhs.isZero || rhs.isZero { return .zero }
        var result = [UInt32](repeating: 0, count: lhs.digits.count + rhs.digits.count)
        for i in 0..<lhs.digits.count {
            var carry: UInt64 = 0
            for j in 0..<rhs.digits.count {
                let prod = UInt64(lhs.digits[i]) * UInt64(rhs.digits[j]) + UInt64(result[i + j]) + carry
                result[i + j] = UInt32(prod & 0xFFFFFFFF)
                carry = prod >> 32
            }
            result[i + rhs.digits.count] += UInt32(carry)
        }
        return BigUInt(digits: result)
    }

    // Division and remainder
    static func divmod(_ lhs: BigUInt, _ rhs: BigUInt) -> (quotient: BigUInt, remainder: BigUInt) {
        if rhs.isZero { fatalError("Division by zero") }
        if lhs.compare(rhs) < 0 { return (.zero, lhs) }
        if rhs.digits.count == 1 {
            return divmodSingle(lhs, rhs.digits[0])
        }

        // Long division
        let shift = rhs.digits.count
        var remainder = BigUInt.zero
        var quotientDigits = [UInt32](repeating: 0, count: lhs.digits.count)

        for i in stride(from: lhs.digits.count - 1, through: 0, by: -1) {
            remainder = remainder.shiftLeft(32)
            remainder = remainder + BigUInt(digits: [lhs.digits[i]])

            // Binary search for quotient digit
            var lo: UInt64 = 0
            var hi: UInt64 = 0xFFFFFFFF
            while lo < hi {
                let mid = lo + (hi - lo + 1) / 2
                let product = rhs.multiplySingle(UInt32(mid))
                if product.compare(remainder) <= 0 {
                    lo = mid
                } else {
                    hi = mid - 1
                }
            }

            quotientDigits[i] = UInt32(lo)
            if lo > 0 {
                remainder = remainder - rhs.multiplySingle(UInt32(lo))
            }
        }

        return (BigUInt(digits: quotientDigits), remainder)
    }

    private static func divmodSingle(_ lhs: BigUInt, _ rhs: UInt32) -> (quotient: BigUInt, remainder: BigUInt) {
        var result = [UInt32](repeating: 0, count: lhs.digits.count)
        var carry: UInt64 = 0
        for i in stride(from: lhs.digits.count - 1, through: 0, by: -1) {
            let cur = carry << 32 | UInt64(lhs.digits[i])
            result[i] = UInt32(cur / UInt64(rhs))
            carry = cur % UInt64(rhs)
        }
        return (BigUInt(digits: result), BigUInt(digits: [UInt32(carry)]))
    }

    static func % (lhs: BigUInt, rhs: BigUInt) -> BigUInt {
        divmod(lhs, rhs).remainder
    }

    func multiplySingle(_ s: UInt32) -> BigUInt {
        if s == 0 { return .zero }
        var result = [UInt32](repeating: 0, count: digits.count + 1)
        var carry: UInt64 = 0
        for i in 0..<digits.count {
            let prod = UInt64(digits[i]) * UInt64(s) + carry
            result[i] = UInt32(prod & 0xFFFFFFFF)
            carry = prod >> 32
        }
        result[digits.count] = UInt32(carry)
        return BigUInt(digits: result)
    }

    func shiftLeft(_ bits: Int) -> BigUInt {
        guard bits > 0 else { return self }
        let wordShift = bits / 32
        let bitShift = bits % 32
        var result = [UInt32](repeating: 0, count: digits.count + wordShift + 1)
        var carry: UInt32 = 0
        for i in 0..<digits.count {
            let shifted = UInt64(digits[i]) << bitShift
            result[i + wordShift] = UInt32(shifted & 0xFFFFFFFF) | carry
            carry = UInt32(shifted >> 32)
        }
        result[digits.count + wordShift] = carry
        return BigUInt(digits: result)
    }

    // Modular exponentiation (square-and-multiply)
    func modPow(_ exp: BigUInt, _ mod: BigUInt) -> BigUInt {
        if mod.digits == [1] { return .zero }
        var result = BigUInt.one
        var base = BigUInt.divmod(self, mod).remainder

        // Iterate over each bit of the exponent
        for i in 0..<(exp.digits.count * 32) {
            let wordIndex = i / 32
            let bitIndex = i % 32
            if wordIndex < exp.digits.count && (exp.digits[wordIndex] >> bitIndex) & 1 == 1 {
                result = (result * base) % mod
            }
            base = (base * base) % mod
        }

        return result
    }
}

// MARK: - Mega Node

struct MegaNode: Sendable {
    let handle: String
    let parentHandle: String
    let name: String
    let type: Int // 0=file, 1=folder, 2=root, 3=inbox, 4=trash
    let size: Int64
    let timestamp: TimeInterval
}

// MARK: - Base64 Mega Encoding

extension Data {
    /// Mega uses URL-safe base64 without padding
    init?(base64MegaDecode string: String) {
        var base64 = string
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        // Add padding
        while base64.count % 4 != 0 {
            base64 += "="
        }
        self.init(base64Encoded: base64)
    }

    func base64MegaEncode() -> String {
        self.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
