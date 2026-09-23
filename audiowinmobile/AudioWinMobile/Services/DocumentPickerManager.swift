import Foundation
import AVFoundation
import UIKit

public final class DocumentPickerManager {
    public static let shared = DocumentPickerManager()
    
    private let fileManager = FileManager.default
    
    private var musicDirectory: URL {
        let docs = fileManager.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let musicDir = docs.appendingPathComponent("Music", isDirectory: true)
        if !fileManager.fileExists(atPath: musicDir.path) {
            try? fileManager.createDirectory(at: musicDir, withIntermediateDirectories: true)
        }
        return musicDir
    }
    
    private var artworkDirectory: URL {
        let docs = fileManager.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let artDir = docs.appendingPathComponent("Artwork", isDirectory: true)
        if !fileManager.fileExists(atPath: artDir.path) {
            try? fileManager.createDirectory(at: artDir, withIntermediateDirectories: true)
        }
        return artDir
    }
    
    // MARK: - Process Audio File from URL
    public func processAudioFile(from sourceUrl: URL) async -> Track? {
        // Start accessing security scoped resource if needed
        let isSecScoped = sourceUrl.startAccessingSecurityScopedResource()
        defer {
            if isSecScoped {
                sourceUrl.stopAccessingSecurityScopedResource()
            }
        }
        
        let destinationUrl = musicDirectory.appendingPathComponent(sourceUrl.lastPathComponent)
        
        // Copy to local app sandbox
        do {
            if fileManager.fileExists(atPath: destinationUrl.path) {
                try fileManager.removeItem(at: destinationUrl)
            }
            try fileManager.copyItem(at: sourceUrl, to: destinationUrl)
        } catch {
            print("DocumentPickerManager: Failed to copy audio file: \(error.localizedDescription)")
            return nil
        }
        
        // Read Metadata via AVAsset
        return await readMetadata(from: destinationUrl)
    }
    
    // MARK: - Read Audio Metadata
    public func readMetadata(from fileUrl: URL) async -> Track {
        var track = Track()
        track.filePath = fileUrl.path
        track.source = .local
        
        let filenameWithoutExtension = fileUrl.deletingPathExtension().lastPathComponent
        track.title = filenameWithoutExtension
        
        let asset = AVURLAsset(url: fileUrl)
        
        // Duration
        if let duration = try? await asset.load(.duration) {
            let seconds = duration.seconds
            if !seconds.isNaN && seconds > 0 {
                track.durationSeconds = seconds
                let mins = Int(seconds) / 60
                let secs = Int(seconds) % 60
                track.duration = String(format: "%d:%02d", mins, secs)
            }
        }
        
        // Metadata items
        if let metadata = try? await asset.load(.commonMetadata) {
            for item in metadata {
                guard let commonKey = item.commonKey?.rawValue else { continue }
                
                switch commonKey {
                case AVMetadataKey.commonKeyTitle.rawValue:
                    if let val = try? await item.load(.stringValue), !val.trimmingCharacters(in: .whitespaces).isEmpty {
                        track.title = val
                    }
                case AVMetadataKey.commonKeyArtist.rawValue:
                    if let val = try? await item.load(.stringValue), !val.trimmingCharacters(in: .whitespaces).isEmpty {
                        track.artist = val
                    }
                case AVMetadataKey.commonKeyAlbumName.rawValue:
                    if let val = try? await item.load(.stringValue), !val.trimmingCharacters(in: .whitespaces).isEmpty {
                        track.album = val
                    }
                case AVMetadataKey.commonKeyArtwork.rawValue:
                    if let data = try? await item.load(.dataValue) {
                        let artName = "\(UUID().uuidString).jpg"
                        let artUrl = artworkDirectory.appendingPathComponent(artName)
                        try? data.write(to: artUrl)
                        track.imagePath = artUrl.path
                    }
                default:
                    break
                }
            }
        }
        
        return track
    }
}
