import Foundation

public final class LibraryMatcher {
    public static let shared = LibraryMatcher()
    
    // MARK: - Fuzzy Match Imported Track to Local Library
    public func findBestMatch(for importedTrack: Track, in localLibrary: [Track]) -> Track? {
        let playableTracks = localLibrary.filter { $0.isPlayable }
        guard !playableTracks.isEmpty else { return nil }
        
        var bestScore: Double = 0.0
        var bestMatch: Track? = nil
        
        let targetTitle = normalize(importedTrack.title)
        let targetArtist = normalize(importedTrack.artist)
        
        for candidate in playableTracks {
            let candidateTitle = normalize(candidate.title)
            let candidateArtist = normalize(candidate.artist)
            
            // 1. Direct match
            if candidateTitle == targetTitle && (candidateArtist == targetArtist || targetArtist.isEmpty || candidateArtist.isEmpty) {
                return candidate
            }
            
            // 2. Score calculation
            let titleSim = similarity(targetTitle, candidateTitle)
            let artistSim = (targetArtist.isEmpty || candidateArtist.isEmpty) ? 0.8 : similarity(targetArtist, candidateArtist)
            
            let totalScore = (titleSim * 0.7) + (artistSim * 0.3)
            
            if totalScore > bestScore && totalScore >= 0.65 {
                bestScore = totalScore
                bestMatch = candidate
            }
        }
        
        return bestMatch
    }
    
    // MARK: - String Similarity Helpers
    private func normalize(_ string: String) -> String {
        return string
            .lowercased()
            .replacingOccurrences(of: "(official music video)", with: "")
            .replacingOccurrences(of: "(official video)", with: "")
            .replacingOccurrences(of: "(audio)", with: "")
            .replacingOccurrences(of: "[official video]", with: "")
            .replacingOccurrences(of: "[lyrics]", with: "")
            .replacingOccurrences(of: "(lyrics)", with: "")
            .components(separatedBy: CharacterSet.alphanumerics.inverted)
            .joined(separator: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
    
    private func similarity(_ s1: String, _ s2: String) -> Double {
        if s1 == s2 { return 1.0 }
        if s1.isEmpty || s2.isEmpty { return 0.0 }
        
        let distance = levenshteinDistance(s1, s2)
        let maxLen = max(s1.count, s2.count)
        return 1.0 - (Double(distance) / Double(maxLen))
    }
    
    private func levenshteinDistance(_ s1: String, _ s2: String) -> Int {
        let empty = [Int](repeating: 0, count: s2.count + 1)
        var last = [Int](0...s2.count)
        
        for (i, c1) in s1.enumerated() {
            var cur = [i + 1] + empty.dropFirst()
            for (j, c2) in s2.enumerated() {
                cur[j + 1] = c1 == c2 ? last[j] : min(last[j], last[j + 1], cur[j]) + 1
            }
            last = cur
        }
        return last.last!
    }
}
