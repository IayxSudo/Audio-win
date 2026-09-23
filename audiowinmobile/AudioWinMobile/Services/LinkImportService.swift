import Foundation

public struct ImportResult {
    public var success: Bool = false
    public var error: String? = nil
    public var note: String? = nil
    public var collectionName: String = "Imported Link"
    public var tracks: [Track] = []
}

public final class LinkImportService {
    public static let shared = LinkImportService()
    
    private let session = URLSession(configuration: .default)
    
    public func looksLikeUrl(_ string: String) -> Bool {
        let s = string.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return s.starts(with: "http://") || s.starts(with: "https://") || s.starts(with: "spotify:")
    }
    
    public func detectSource(_ url: String) -> TrackSource {
        let u = url.lowercased()
        if u.contains("youtube.com") || u.contains("youtu.be") {
            return .youTube
        }
        if u.contains("spotify.com") || u.starts(with: "spotify:") {
            return .spotify
        }
        return .web
    }
    
    // MARK: - Main Import Function
    public func importUrl(_ urlString: String, settings: PlaybackSettings) async -> ImportResult {
        let trimmed = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard looksLikeUrl(trimmed) else {
            return ImportResult(success: false, error: "Please enter a valid web URL or Spotify URI.")
        }
        
        let source = detectSource(trimmed)
        switch source {
        case .youTube:
            return await importYouTube(url: trimmed, apiKey: settings.youTubeApiKey)
        case .spotify:
            return await importSpotify(url: trimmed, clientId: settings.spotifyClientId, clientSecret: settings.spotifyClientSecret)
        case .web, .local:
            return await importGenericOembed(url: trimmed)
        }
    }
    
    // MARK: - YouTube Import (oEmbed fallback / Data API)
    private func importYouTube(url: String, apiKey: String) async -> ImportResult {
        // If playlist and API key provided, use YouTube Data API
        if url.contains("list=") && !apiKey.isEmpty {
            return await importYouTubePlaylist(url: url, apiKey: apiKey)
        }
        
        // Single track oEmbed
        guard let encodedUrl = url.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let oembedUrl = URL(string: "https://www.youtube.com/oembed?url=\(encodedUrl)&format=json") else {
            return ImportResult(success: false, error: "Invalid YouTube URL.")
        }
        
        do {
            let (data, response) = try await session.data(from: oembedUrl)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                return ImportResult(success: false, error: "Could not fetch YouTube metadata. Video may be private or unavailable.")
            }
            
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return ImportResult(success: false, error: "Invalid response from YouTube oEmbed.")
            }
            
            let rawTitle = (json["title"] as? String) ?? "YouTube Audio"
            let authorName = (json["author_name"] as? String) ?? "Unknown Artist"
            let thumbnailUrl = json["thumbnail_url"] as? String
            
            // Parse "Artist - Title" format if present in the video title
            var parsedArtist = authorName
            var parsedTitle = rawTitle
            if rawTitle.contains(" - ") {
                let parts = rawTitle.components(separatedBy: " - ")
                if parts.count >= 2 {
                    parsedArtist = parts[0].trimmingCharacters(in: .whitespaces)
                    parsedTitle = parts.dropFirst().joined(separator: " - ").trimmingCharacters(in: .whitespaces)
                }
            }
            
            var track = Track()
            track.title = parsedTitle
            track.artist = parsedArtist
            track.album = "YouTube"
            track.source = .youTube
            track.sourceUrl = url
            track.imagePath = thumbnailUrl
            track.duration = "3:30"
            track.durationSeconds = 210
            
            var res = ImportResult()
            res.success = true
            res.collectionName = parsedTitle
            res.tracks = [track]
            return res
        } catch {
            return ImportResult(success: false, error: "Network error: \(error.localizedDescription)")
        }
    }
    
    private func importYouTubePlaylist(url: String, apiKey: String) async -> ImportResult {
        guard let components = URLComponents(string: url),
              let playlistId = components.queryItems?.first(where: { $0.name == "list" })?.value else {
            return await importYouTube(url: url, apiKey: "")
        }
        
        let endpoint = "https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&maxResults=50&playlistId=\(playlistId)&key=\(apiKey)"
        guard let requestUrl = URL(string: endpoint) else {
            return ImportResult(success: false, error: "Invalid YouTube API URL.")
        }
        
        do {
            let (data, response) = try await session.data(from: requestUrl)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                return await importYouTube(url: url, apiKey: "") // Fallback to oEmbed
            }
            
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let items = json["items"] as? [[String: Any]] else {
                return ImportResult(success: false, error: "Failed to parse YouTube playlist.")
            }
            
            var tracks: [Track] = []
            for item in items {
                guard let snippet = item["snippet"] as? [String: Any],
                      let title = snippet["title"] as? String,
                      let resourceId = snippet["resourceId"] as? [String: Any],
                      let videoId = resourceId["videoId"] as? String else { continue }
                
                let author = (snippet["videoOwnerChannelTitle"] as? String) ?? "YouTube"
                let thumbs = snippet["thumbnails"] as? [String: Any]
                let highThumb = (thumbs?["high"] as? [String: Any])?["url"] as? String
                
                var track = Track()
                track.title = title
                track.artist = author
                track.album = "YouTube Playlist"
                track.source = .youTube
                track.sourceUrl = "https://www.youtube.com/watch?v=\(videoId)"
                track.imagePath = highThumb
                track.duration = "3:30"
                track.durationSeconds = 210
                tracks.append(track)
            }
            
            var res = ImportResult()
            res.success = true
            res.collectionName = "YouTube Playlist (\(tracks.count) tracks)"
            res.tracks = tracks
            return res
        } catch {
            return await importYouTube(url: url, apiKey: "")
        }
    }
    
    // MARK: - Spotify Import (oEmbed fallback)
    private func importSpotify(url: String, clientId: String, clientSecret: String) async -> ImportResult {
        guard let encodedUrl = url.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let oembedUrl = URL(string: "https://open.spotify.com/oembed?url=\(encodedUrl)") else {
            return ImportResult(success: false, error: "Invalid Spotify URL.")
        }
        
        do {
            let (data, response) = try await session.data(from: oembedUrl)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                return ImportResult(success: false, error: "Could not fetch Spotify track. Make sure the link is public.")
            }
            
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return ImportResult(success: false, error: "Invalid response from Spotify.")
            }
            
            let title = (json["title"] as? String) ?? "Spotify Track"
            let thumbnailUrl = json["thumbnail_url"] as? String
            
            var track = Track()
            track.title = title
            track.artist = "Spotify"
            track.album = "Spotify Music"
            track.source = .spotify
            track.sourceUrl = url
            track.imagePath = thumbnailUrl
            track.duration = "3:30"
            track.durationSeconds = 210
            
            var res = ImportResult()
            res.success = true
            res.collectionName = title
            res.tracks = [track]
            return res
        } catch {
            return ImportResult(success: false, error: "Network error: \(error.localizedDescription)")
        }
    }
    
    // MARK: - Generic oEmbed
    private func importGenericOembed(url: String) async -> ImportResult {
        var track = Track()
        track.title = "Web Audio"
        track.artist = "Web Link"
        track.source = .web
        track.sourceUrl = url
        track.filePath = url
        
        var res = ImportResult()
        res.success = true
        res.collectionName = "Web Audio"
        res.tracks = [track]
        return res
    }
}
