import Foundation

public extension URLSessionConfiguration {

    /// Configuration for a session that moves files.
    ///
    /// The two URLSession timeouts are easy to confuse, and getting them the
    /// wrong way round is what made large transfers fail:
    ///
    /// - `timeoutIntervalForRequest` is an **inactivity** guard — it fires
    ///   when no data has arrived for that long. This is the one worth
    ///   setting: it catches a connection that has genuinely stalled.
    /// - `timeoutIntervalForResource` is a **wall-clock cap on the whole
    ///   request**, however well it is going. Every provider here set one,
    ///   between five and thirty minutes, so a transfer bigger than the link
    ///   could carry in that time was killed in the middle — at roughly the
    ///   same percentage every attempt. A user reported a 1.5 GB upload to
    ///   Nextcloud stopping at about half, every time, while the same file
    ///   through Finder was fine. Five minutes at his upload speed is about
    ///   half the file.
    ///
    /// So this leaves the resource timeout at the system default (seven
    /// days) and keeps the inactivity guard. A long transfer is not a
    /// failing one; a silent one is.
    static func fileTransfer(inactivityTimeout: TimeInterval = 30) -> URLSessionConfiguration {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = inactivityTimeout
        return config
    }
}
