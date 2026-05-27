import Foundation
import CommonCrypto
import CryptoKit

/// All of Internxt's client-side crypto, transliterated from the official
/// `@internxt/cli` (crypto.service.ts), `@internxt/lib` and
/// `@internxt/inxt-js`. Internxt is end-to-end encrypted: the login password
/// never leaves the device in cleartext, the account mnemonic is decrypted
/// locally, and per-file keys are derived from that mnemonic to encrypt/decrypt
/// file *content* via the separate Network service.
///
/// The two layers use different schemes, faithfully reproduced here:
///   • Account-secret text (the login salt, the password hash, the mnemonic):
///     CryptoJS-compatible AES-256-CBC with an OpenSSL "Salted__" envelope and
///     an MD5-based EVP_BytesToKey, hex-encoded.
///   • File content: AES-256-CTR with a key derived from the BIP39 seed of the
///     mnemonic, the bucket id, and a per-file random index.
enum InternxtCrypto {
    /// `APP_CRYPTO_SECRET` from the Internxt CLI's committed `.env.template` —
    /// the production secret used to decrypt the login salt and wrap the
    /// password hash. Shared across all Internxt clients.
    static let appCryptoSecret = "6KYQBP847D4ATSFA"

    // MARK: - Hashing

    static func sha256(_ data: Data) -> Data { Data(SHA256.hash(data: data)) }
    static func sha512(_ data: Data) -> Data { Data(SHA512.hash(data: data)) }
    static func md5(_ data: Data) -> Data { Data(Insecure.MD5.hash(data: data)) }

    // MARK: - Hex

    static func hexDecode(_ hex: String) -> Data? {
        let chars = Array(hex)
        guard chars.count % 2 == 0 else { return nil }
        var out = Data(capacity: chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = chars[i].hexDigitValue, let lo = chars[i + 1].hexDigitValue else { return nil }
            out.append(UInt8(hi << 4 | lo))
            i += 2
        }
        return out
    }

    static func hexEncode(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - CryptoJS-compatible AES-256-CBC (account-secret text)

    /// OpenSSL EVP_BytesToKey with MD5 and a single salt round-trip, exactly as
    /// CryptoJS derives key+iv from a passphrase: three chained MD5 hashes of
    /// (secret || salt), the first two forming the 32-byte key and the third the
    /// 16-byte IV.
    private static func keyAndIV(secret: String, salt: Data) -> (key: Data, iv: Data) {
        let password = Data(secret.utf8) + salt
        let d0 = md5(password)
        let d1 = md5(d0 + password)
        let d2 = md5(d1 + password)
        return (key: d0 + d1, iv: d2)
    }

    /// Decrypts a hex string produced by CryptoJS `AES.encrypt(text, secret)`:
    /// bytes are `"Salted__"`(8) + salt(8) + AES-256-CBC ciphertext.
    static func decryptText(_ hex: String, secret: String) -> String? {
        guard let raw = hexDecode(hex), raw.count > 16 else { return nil }
        let salt = raw.subdata(in: 8..<16)
        let ciphertext = raw.subdata(in: 16..<raw.count)
        let (key, iv) = keyAndIV(secret: secret, salt: salt)
        guard let plain = aesCBC(ciphertext, key: key, iv: iv, encrypt: false) else { return nil }
        return String(data: plain, encoding: .utf8)
    }

    /// Encrypts text into the same CryptoJS hex envelope.
    static func encryptText(_ text: String, secret: String) -> String? {
        var salt = Data(count: 8)
        salt.withUnsafeMutableBytes { _ = SecRandomCopyBytes(kSecRandomDefault, 8, $0.baseAddress!) }
        let (key, iv) = keyAndIV(secret: secret, salt: salt)
        guard let ct = aesCBC(Data(text.utf8), key: key, iv: iv, encrypt: true) else { return nil }
        return hexEncode(Data("Salted__".utf8) + salt + ct)
    }

    // MARK: - Password hashing (login)

    /// PBKDF2-HMAC-SHA1(password, hexDecode(saltHex), 10000, 32) → hex. Matches
    /// CryptoJS `PBKDF2(password, Hex.parse(salt), {keySize: 256/32, iterations: 10000})`.
    static func passToHash(password: String, saltHex: String) -> String? {
        guard let salt = hexDecode(saltHex) else { return nil }
        guard let derived = pbkdf2(password: password, salt: salt, rounds: 10000, keyLength: 32, prf: CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1)) else { return nil }
        return hexEncode(derived)
    }

    /// Reproduces the CLI's `encryptPasswordHash`: decrypt the server salt,
    /// PBKDF2-hash the password with it, then wrap the hash back in the secret.
    static func encryptPasswordHash(password: String, encryptedSalt: String) -> String? {
        guard let saltHex = decryptText(encryptedSalt, secret: appCryptoSecret),
              let hash = passToHash(password: password, saltHex: saltHex),
              let wrapped = encryptText(hash, secret: appCryptoSecret) else { return nil }
        return wrapped
    }

    /// The account mnemonic is stored CryptoJS-encrypted under the user's
    /// plaintext password (not the app secret).
    static func decryptMnemonic(_ encryptedMnemonic: String, password: String) -> String? {
        decryptText(encryptedMnemonic, secret: password)
    }

    // MARK: - File content keys (Network)

    /// BIP39 mnemonic → 64-byte seed: PBKDF2-HMAC-SHA512(NFKD(mnemonic),
    /// "mnemonic", 2048, 64). No passphrase, matching `bip39.mnemonicToSeed`.
    static func mnemonicToSeed(_ mnemonic: String) -> Data? {
        let normalized = mnemonic.decomposedStringWithCompatibilityMapping // BIP39 mandates NFKD
        return pbkdf2(password: normalized, salt: Data("mnemonic".utf8), rounds: 2048, keyLength: 64, prf: CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA512))
    }

    /// `GenerateFileKey(mnemonic, bucketId, index)` from inxt-js: a SHA-512 chain
    /// over (seed ‖ bucketId) then (bucketKey[0..32] ‖ index), truncated to 32 bytes.
    static func generateFileKey(mnemonic: String, bucketIdHex: String, index: Data) -> Data? {
        guard let seed = mnemonicToSeed(mnemonic), let bucketId = hexDecode(bucketIdHex) else { return nil }
        let bucketKey = sha512(seed + bucketId)
        let fileKey = sha512(bucketKey.prefix(32) + index)
        return fileKey.prefix(32)
    }

    // MARK: - AES helpers

    private static func aesCBC(_ data: Data, key: Data, iv: Data, encrypt: Bool) -> Data? {
        let op = CCOperation(encrypt ? kCCEncrypt : kCCDecrypt)
        var outLength = 0
        let outCapacity = data.count + kCCBlockSizeAES128
        var out = Data(count: outCapacity)
        let status = out.withUnsafeMutableBytes { outBuf in
            data.withUnsafeBytes { dataBuf in
                key.withUnsafeBytes { keyBuf in
                    iv.withUnsafeBytes { ivBuf in
                        CCCrypt(op, CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                keyBuf.baseAddress, key.count, ivBuf.baseAddress,
                                dataBuf.baseAddress, data.count,
                                outBuf.baseAddress, outCapacity, &outLength)
                    }
                }
            }
        }
        guard status == kCCSuccess else { return nil }
        out.removeSubrange(outLength..<out.count)
        return out
    }

    /// AES-256-CTR. Encryption and decryption are the same operation (XOR with
    /// the keystream), so callers use this for both directions.
    static func aesCTR(_ data: Data, key: Data, iv: Data) -> Data? {
        var cryptor: CCCryptorRef?
        let createStatus = key.withUnsafeBytes { keyBuf in
            iv.withUnsafeBytes { ivBuf in
                CCCryptorCreateWithMode(CCOperation(kCCEncrypt), CCMode(kCCModeCTR), CCAlgorithm(kCCAlgorithmAES),
                                        CCPadding(ccNoPadding), ivBuf.baseAddress, keyBuf.baseAddress, key.count,
                                        nil, 0, 0, CCModeOptions(kCCModeOptionCTR_BE), &cryptor)
            }
        }
        guard createStatus == kCCSuccess, let cryptor else { return nil }
        defer { CCCryptorRelease(cryptor) }

        let outLen = CCCryptorGetOutputLength(cryptor, data.count, true)
        var out = Data(count: outLen)
        var moved = 0
        var total = 0
        let updateStatus = out.withUnsafeMutableBytes { outBuf in
            data.withUnsafeBytes { dataBuf in
                CCCryptorUpdate(cryptor, dataBuf.baseAddress, data.count, outBuf.baseAddress, outLen, &moved)
            }
        }
        guard updateStatus == kCCSuccess else { return nil }
        total += moved
        let finalStatus = out.withUnsafeMutableBytes { outBuf in
            CCCryptorFinal(cryptor, outBuf.baseAddress?.advanced(by: total), outLen - total, &moved)
        }
        guard finalStatus == kCCSuccess else { return nil }
        total += moved
        out.removeSubrange(total..<out.count)
        return out
    }

    // MARK: - Shard hash (upload)

    /// The network shard hash Internxt's bridge expects: RIPEMD-160 of the
    /// SHA-256 of the *encrypted* content (Bitcoin-style "hash160"), hex-encoded.
    static func shardHashHex(_ ciphertext: Data) -> String {
        hexEncode(ripemd160(sha256(ciphertext)))
    }

    /// RIPEMD-160. Not provided by CommonCrypto/CryptoKit, so implemented here
    /// from the reference spec (two parallel 80-step lines over 512-bit blocks,
    /// little-endian length and output).
    static func ripemd160(_ message: Data) -> Data {
        func rol(_ x: UInt32, _ n: UInt32) -> UInt32 { (x << n) | (x >> (32 - n)) }
        func f(_ round: Int, _ x: UInt32, _ y: UInt32, _ z: UInt32) -> UInt32 {
            switch round {
            case 0: return x ^ y ^ z
            case 1: return (x & y) | (~x & z)
            case 2: return (x | ~y) ^ z
            case 3: return (x & z) | (y & ~z)
            default: return x ^ (y | ~z)
            }
        }
        let rL: [Int] = [0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15, 7,4,13,1,10,6,15,3,12,0,9,5,2,14,11,8, 3,10,14,4,9,15,8,1,2,7,0,6,13,11,5,12, 1,9,11,10,0,8,12,4,13,3,7,15,14,5,6,2, 4,0,5,9,7,12,2,10,14,1,3,8,11,6,15,13]
        let rR: [Int] = [5,14,7,0,9,2,11,4,13,6,15,8,1,10,3,12, 6,11,3,7,0,13,5,10,14,15,8,12,4,9,1,2, 15,5,1,3,7,14,6,9,11,8,12,2,10,0,4,13, 8,6,4,1,3,11,15,0,5,12,2,13,9,7,10,14, 12,15,10,4,1,5,8,7,6,2,13,14,0,3,9,11]
        let sL: [UInt32] = [11,14,15,12,5,8,7,9,11,13,14,15,6,7,9,8, 7,6,8,13,11,9,7,15,7,12,15,9,11,7,13,12, 11,13,6,7,14,9,13,15,14,8,13,6,5,12,7,5, 11,12,14,15,14,15,9,8,9,14,5,6,8,6,5,12, 9,15,5,11,6,8,13,12,5,12,13,14,11,8,5,6]
        let sR: [UInt32] = [8,9,9,11,13,15,15,5,7,7,8,11,14,14,12,6, 9,13,15,7,12,8,9,11,7,7,12,7,6,15,13,11, 9,7,15,11,8,6,6,14,12,13,5,14,13,13,7,5, 15,5,8,11,14,14,6,14,6,9,12,9,12,5,15,8, 8,5,12,9,12,5,14,6,8,13,6,5,15,13,11,11]
        let kL: [UInt32] = [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E]
        let kR: [UInt32] = [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000]

        var h: [UInt32] = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0]

        var msg = message
        let bitLen = UInt64(message.count) &* 8
        msg.append(0x80)
        while msg.count % 64 != 56 { msg.append(0) }
        for i in 0..<8 { msg.append(UInt8((bitLen >> (8 * UInt64(i))) & 0xff)) }

        var blockStart = 0
        while blockStart < msg.count {
            var x = [UInt32](repeating: 0, count: 16)
            for i in 0..<16 {
                let o = blockStart + i * 4
                x[i] = UInt32(msg[o]) | UInt32(msg[o + 1]) << 8 | UInt32(msg[o + 2]) << 16 | UInt32(msg[o + 3]) << 24
            }
            var (al, bl, cl, dl, el) = (h[0], h[1], h[2], h[3], h[4])
            var (ar, br, cr, dr, er) = (h[0], h[1], h[2], h[3], h[4])
            for j in 0..<80 {
                let round = j / 16
                var t = rol(al &+ f(round, bl, cl, dl) &+ x[rL[j]] &+ kL[round], sL[j]) &+ el
                al = el; el = dl; dl = rol(cl, 10); cl = bl; bl = t
                t = rol(ar &+ f(4 - round, br, cr, dr) &+ x[rR[j]] &+ kR[round], sR[j]) &+ er
                ar = er; er = dr; dr = rol(cr, 10); cr = br; br = t
            }
            let t = h[1] &+ cl &+ dr
            h[1] = h[2] &+ dl &+ er
            h[2] = h[3] &+ el &+ ar
            h[3] = h[4] &+ al &+ br
            h[4] = h[0] &+ bl &+ cr
            h[0] = t
            blockStart += 64
        }

        var out = Data(capacity: 20)
        for word in h {
            out.append(UInt8(word & 0xff))
            out.append(UInt8((word >> 8) & 0xff))
            out.append(UInt8((word >> 16) & 0xff))
            out.append(UInt8((word >> 24) & 0xff))
        }
        return out
    }

    private static func pbkdf2(password: String, salt: Data, rounds: Int, keyLength: Int, prf: CCPseudoRandomAlgorithm) -> Data? {
        var derived = Data(count: keyLength)
        let pw = Array(password.utf8)
        let status = derived.withUnsafeMutableBytes { derivedBuf in
            salt.withUnsafeBytes { saltBuf in
                CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                     pw.map { Int8(bitPattern: $0) }, pw.count,
                                     saltBuf.bindMemory(to: UInt8.self).baseAddress, salt.count,
                                     prf, UInt32(rounds),
                                     derivedBuf.bindMemory(to: UInt8.self).baseAddress, keyLength)
            }
        }
        return status == kCCSuccess ? derived : nil
    }
}
