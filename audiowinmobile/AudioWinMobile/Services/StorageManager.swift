import Foundation

public struct AppData: Codable {
    public var library: [Track] = []
    public var playlists: [Playlist] = []
}

public final class StorageManager {
    public static let shared = StorageManager()
    
    private let fileManager = FileManager.default
    private let jsonEncoder = JSONEncoder()
    private let jsonDecoder = JSONDecoder()
    
    private var documentsDirectory: URL {
        fileManager.urls(for: .documentDirectory, in: .userDomainMask)[0]
    }
    
    private var dataFileUrl: URL {
        documentsDirectory.appendingPathComponent("audiowin_data.json")
    }
    
    private var settingsFileUrl: URL {
        documentsDirectory.appendingPathComponent("audiowin_settings.json")
    }
    
    private var statsFileUrl: URL {
        documentsDirectory.appendingPathComponent("audiowin_stats.json")
    }
    
    public init() {
        jsonEncoder.outputFormatting = .prettyPrinted
        jsonEncoder.dateEncodingStrategy = .iso8601
        jsonDecoder.dateDecodingStrategy = .iso8601
    }
    
    // MARK: - Data (Library & Playlists)
    public func loadData() -> (library: [Track], playlists: [Playlist]) {
        guard fileManager.fileExists(atPath: dataFileUrl.path) else {
            return ([], createDefaultPlaylists())
        }
        
        do {
            let data = try Data(contentsOf: dataFileUrl)
            let appData = try jsonDecoder.decode(AppData.self, from: data)
            var playlists = appData.playlists
            if !playlists.contains(where: { $0.isSystem }) {
                playlists.insert(createLikedSongsPlaylist(), at: 0)
            }
            return (appData.library, playlists)
        } catch {
            print("StorageManager: Failed to decode app data: \(error.localizedDescription)")
            return ([], createDefaultPlaylists())
        }
    }
    
    public func saveData(library: [Track], playlists: [Playlist]) {
        let appData = AppData(library: library, playlists: playlists)
        do {
            let data = try jsonEncoder.encode(appData)
            try data.write(to: dataFileUrl, options: .atomic)
        } catch {
            print("StorageManager: Failed to save app data: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Settings
    public func loadSettings() -> PlaybackSettings {
        guard fileManager.fileExists(atPath: settingsFileUrl.path) else {
            return PlaybackSettings()
        }
        
        do {
            let data = try Data(contentsOf: settingsFileUrl)
            var settings = try jsonDecoder.decode(PlaybackSettings.self, from: data)
            settings.normalize()
            return settings
        } catch {
            print("StorageManager: Failed to decode settings: \(error.localizedDescription)")
            return PlaybackSettings()
        }
    }
    
    public func saveSettings(_ settings: PlaybackSettings) {
        do {
            let data = try jsonEncoder.encode(settings)
            try data.write(to: settingsFileUrl, options: .atomic)
        } catch {
            print("StorageManager: Failed to save settings: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Stats
    public func loadStats() -> AppStats {
        guard fileManager.fileExists(atPath: statsFileUrl.path) else {
            return AppStats()
        }
        
        do {
            let data = try Data(contentsOf: statsFileUrl)
            return try jsonDecoder.decode(AppStats.self, from: data)
        } catch {
            return AppStats()
        }
    }
    
    public func saveStats(_ stats: AppStats) {
        do {
            let data = try jsonEncoder.encode(stats)
            try data.write(to: statsFileUrl, options: .atomic)
        } catch {
            print("StorageManager: Failed to save stats: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Defaults
    private func createLikedSongsPlaylist() -> Playlist {
        var p = Playlist()
        p.id = "system_liked_songs"
        p.name = "Liked Songs"
        p.icon = "heart.fill"
        p.description = "Your favorite tracks, all in one place."
        p.isSystem = true
        return p
    }
    
    private func createDefaultPlaylists() -> [Playlist] {
        return [
            createLikedSongsPlaylist(),
            Playlist(name: "Favorites", icon: "star.fill", description: "Top favorite tracks"),
            Playlist(name: "Road Trip", icon: "car.fill", description: "High energy music for driving")
        ]
    }
}
