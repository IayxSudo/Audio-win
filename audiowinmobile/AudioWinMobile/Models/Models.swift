import Foundation
import SwiftUI

// MARK: - Track Source
public enum TrackSource: Int, Codable, CaseIterable {
    case local = 0
    case youTube = 1
    case spotify = 2
    case web = 3
    
    public var label: String {
        switch self {
        case .local: return "Local"
        case .youTube: return "YouTube"
        case .spotify: return "Spotify"
        case .web: return "Web Link"
        }
    }
    
    public var iconName: String {
        switch self {
        case .local: return "music.note"
        case .youTube: return "play.rectangle.fill"
        case .spotify: return "antenna.radiowaves.left.and.right"
        case .web: return "link"
        }
    }
}

// MARK: - Repeat State
public enum RepeatState: Int, Codable, CaseIterable {
    case off = 0
    case one = 1
    case all = 2
    
    public var iconName: String {
        switch self {
        case .off: return "repeat"
        case .one: return "repeat.1"
        case .all: return "repeat"
        }
    }
}

// MARK: - Track Model
public struct Track: Identifiable, Codable, Equatable, Hashable {
    public var id: String = UUID().uuidString
    public var index: Int = 0
    public var title: String = "Unknown Title"
    public var artist: String = "Unknown Artist"
    public var album: String = "Unknown Album"
    public var filePath: String? = nil
    public var duration: String = "0:00"
    public var durationSeconds: Double = 0
    public var imagePath: String? = nil
    public var source: TrackSource = .local
    public var sourceUrl: String? = nil
    public var isLiked: Bool = false
    public var playCount: Int = 0
    public var lastPlayed: Date? = nil
    public var addedUtc: Date = Date()
    
    // View-only properties
    public var isNowPlaying: Bool = false
    
    public var isLink: Bool {
        source != .local
    }
    
    public var hasSourceUrl: Bool {
        guard let url = sourceUrl else { return false }
        return !url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
    
    public var isPlayable: Bool {
        guard let path = filePath, !path.isEmpty else { return false }
        if path.starts(with: "http://") || path.starts(with: "https://") { return true }
        return FileManager.default.fileExists(atPath: path)
    }
    
    public var needsFile: Bool {
        return filePath == nil || filePath?.isEmpty == true
    }
    
    public var sourceLabel: String {
        switch source {
        case .youTube: return "YouTube"
        case .spotify: return "Spotify"
        case .web: return "Link"
        case .local: return ""
        }
    }
    
    enum CodingKeys: String, CodingKey {
        case id, index, title, artist, album, filePath, duration, durationSeconds,
             imagePath, source, sourceUrl, isLiked, playCount, lastPlayed, addedUtc
    }
    
    public func matches(other: Track?) -> Bool {
        guard let other = other else { return false }
        if id == other.id { return true }
        if let p1 = filePath, let p2 = other.filePath, !p1.isEmpty, p1.caseInsensitiveCompare(p2) == .orderedSame {
            return true
        }
        if let u1 = sourceUrl, let u2 = other.sourceUrl, !u1.isEmpty, u1.caseInsensitiveCompare(u2) == .orderedSame {
            return true
        }
        return false
    }
}

// MARK: - Playlist Model
public struct Playlist: Identifiable, Codable, Equatable {
    public var id: String = UUID().uuidString
    public var name: String = "New Playlist"
    public var icon: String = "music.note.list"
    public var description: String = "A custom playlist collection."
    public var creator: String = "You"
    public var imagePath: String? = nil
    public var tracks: [Track] = []
    public var playCount: Int = 0
    public var lastPlayed: Date? = nil
    public var createdUtc: Date = Date()
    public var isSystem: Bool = false
    
    public var hasImage: Bool {
        guard let img = imagePath else { return false }
        return !img.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
    
    public var initial: String {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "?" : String(trimmed.prefix(1)).uppercased()
    }
    
    public var subtitle: String {
        let count = tracks.count
        return count == 1 ? "1 track" : "\(count) tracks"
    }
    
    public var totalSeconds: Double {
        tracks.reduce(0) { $0 + $1.durationSeconds }
    }
}

// MARK: - App Stats
public struct AppStats: Codable {
    public var firstListenedSong: String = "None"
    public var totalListenedSeconds: Double = 0
    public var mostPlayedPlaylist: String = "None"
    public var totalSongsPlayed: Int = 0
    public var totalLikedSongs: Int = 0
    public var totalTracks: Int = 0
    public var topTrack: String = "None"
}

// MARK: - Equalizer Presets
public struct EqPreset: Identifiable, Hashable {
    public var id: String { name }
    public let name: String
    public let gains: [Float] // 10 bands: 32Hz, 64Hz, 125Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz, 8kHz, 16kHz
    
    public static let all: [EqPreset] = [
        EqPreset(name: "Flat", gains: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        EqPreset(name: "Super Bass", gains: [12.0, 11.0, 8.0, 4.0, 1.0, 0, 0, 1.0, 2.0, 2.0]),
        EqPreset(name: "Bass Boost", gains: [6.0, 5.0, 4.0, 2.5, 1.0, 0, 0, 0, 0, 0]),
        EqPreset(name: "Bass Reducer", gains: [-6.0, -5.0, -4.0, -2.5, -1.0, 0, 0, 0, 0, 0]),
        EqPreset(name: "Vocal Booster", gains: [-2.0, -1.0, 1.0, 3.5, 4.5, 4.0, 3.0, 1.5, 0, -1.0]),
        EqPreset(name: "Electronic / Dance", gains: [5.0, 4.5, 2.0, 0, -1.0, 2.0, 3.5, 4.0, 4.5, 5.0]),
        EqPreset(name: "Rock", gains: [4.5, 3.5, 2.0, 0.5, -1.0, -0.5, 2.0, 3.5, 4.0, 4.5]),
        EqPreset(name: "Acoustic", gains: [3.5, 3.0, 2.0, 1.0, 1.5, 2.0, 3.0, 3.5, 3.0, 2.0]),
        EqPreset(name: "Treble Boost", gains: [0, 0, 0, 0, 0, 1.0, 2.5, 4.0, 5.5, 6.5]),
        EqPreset(name: "Podcast / Spoken", gains: [-3.0, -1.0, 2.0, 3.5, 4.0, 3.5, 2.0, 0.5, -1.0, -2.0])
    ]
}

// MARK: - Playback & App Settings
public struct PlaybackSettings: Codable {
    // Audio
    public var volume: Double = 0.75
    public var isMuted: Bool = false
    public var monoAudio: Bool = false
    public var normalization: Bool = true
    public var eqPreset: String = "Flat"
    public var eqGains: [Float] = Array(repeating: 0.0, count: 10)
    
    // Playback
    public var isShuffleOn: Bool = false
    public var repeatState: RepeatState = .off
    
    // Appearance
    public var theme: String = "Dark"
    public var accent: String = "Violet"
    public var visualizerEnabled: Bool = true
    
    // Integrations
    public var youTubeApiKey: String = ""
    public var spotifyClientId: String = ""
    public var spotifyClientSecret: String = ""
    
    public mutating func normalize() {
        if eqGains.count != 10 {
            eqGains = Array(repeating: 0.0, count: 10)
        }
        volume = max(0, min(1.0, volume))
        if theme.isEmpty { theme = "Dark" }
        if accent.isEmpty { accent = "Violet" }
    }
}
