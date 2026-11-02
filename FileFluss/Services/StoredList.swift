import Foundation

/// Reads and writes a stored JSON list one element at a time.
///
/// Decoding a whole `[T]` at once fails on a single element this version
/// doesn't understand, such as an account for a provider added in a newer
/// version. That left the list looking empty, and the next save destroyed
/// everything in it. Here an unreadable element is set aside and written back
/// untouched, so a newer version finds it again and an older one can't lose it.
enum StoredList {

    /// The readable elements and, as raw JSON, the others. Nil when `data`
    /// isn't a JSON array at all.
    static func decode<T: Decodable>(_ data: Data, as type: T.Type) -> (items: [T], unreadable: [Data])? {
        guard let elements = try? JSONSerialization.jsonObject(with: data) as? [Any] else { return nil }
        let decoder = JSONDecoder()
        var items: [T] = []
        var unreadable: [Data] = []
        for element in elements {
            guard let raw = try? JSONSerialization.data(withJSONObject: element, options: .fragmentsAllowed) else { continue }
            if let item = try? decoder.decode(T.self, from: raw) {
                items.append(item)
            } else {
                unreadable.append(raw)
            }
        }
        return (items, unreadable)
    }

    static func encode<T: Encodable>(_ items: [T], unreadable: [Data]) throws -> Data {
        let known = try JSONEncoder().encode(items)
        guard !unreadable.isEmpty else { return known }
        var elements = try JSONSerialization.jsonObject(with: known) as? [Any] ?? []
        elements += unreadable.compactMap { try? JSONSerialization.jsonObject(with: $0, options: .fragmentsAllowed) }
        return try JSONSerialization.data(withJSONObject: elements)
    }
}
