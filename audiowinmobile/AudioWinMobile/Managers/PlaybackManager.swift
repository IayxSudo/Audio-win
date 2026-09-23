import Foundation
import SwiftUI
import Combine

public final class PlaybackManager: ObservableObject {
    public static let shared = PlaybackManager()
    
    // Dependencies
    private let audioEngine = AudioEngine.shared
    private let nowPlayingManager = NowPlayingManager.shared
    private let storageManager = StorageManager.shared
    private let matcher = LibraryMatcher.shared
    
    // Core State
    @Published public var library: [Track] = []
    @Published public var playlists: [Playlist] = []
    @Published public var currentTrack: Track? = nil
    @Published public var queue: [Track] = []
    @Published public var queueHistory: [Track] = []
    @Published public var settings: PlaybackSettings = PlaybackSettings()
    @Published public var stats: AppStats = AppStats()
    
    // Playback state mirrors
    @Published public var isPlaying: Bool = false
    @Published public var currentTime: Double = 0.0
    @Published public var duration: Double = 0.0
    @Published public var isShuffleOn: Bool = false
    @Published public var repeatState: RepeatState = .off
    
    // UI Sheets State
    @Published public var showNowPlayingSheet: Bool = false
    @Published public var showEqualizerSheet: Bool = false
    @Published public var showQueueSheet: Bool = false
    
    // Visualizer data from AudioEngine
    @Published public var fftMagnitudes: [Float] = Array(repeating: 0.0, count: 32)
    
    private var cancellables = Set<AnyCancellable>()
    private var unShuffledQueue: [Track] = []
    
    public init() {
        loadData()
        bindAudioEngine()
        bindNowPlayingCommands()
    }
    
    // MARK: - Initial State Loading
    private func loadData() {
        let (loadedLib, loadedPlaylists) = storageManager.loadData()
        library = loadedLib
        playlists = loadedPlaylists
        settings = storageManager.loadSettings()
        stats = storageManager.loadStats()
        
        isShuffleOn = settings.isShuffleOn
        repeatState = settings.repeatState
        
        // Sync theme
        ThemeManager.shared.currentAccentName = settings.accent
        ThemeManager.shared.isDarkMode = (settings.theme == "Dark")
        
        // Apply EQ
        audioEngine.setEqGains(settings.eqGains)
        audioEngine.setVolume(Float(settings.volume))
        audioEngine.setMono(settings.monoAudio)
    }
    
    // MARK: - Audio Engine Binding
    private func bindAudioEngine() {
        audioEngine.$isPlaying
            .receive(on: DispatchQueue.main)
            .assign(to: \.isPlaying, on: self)
            .store(in: &cancellables)
        
        audioEngine.$currentTime
            .receive(on: DispatchQueue.main)
            .sink { [weak self] time in
                guard let self = self else { return }
                self.currentTime = time
                self.nowPlayingManager.updateNowPlaying(
                    track: self.currentTrack,
                    isPlaying: self.isPlaying,
                    currentTime: time,
                    duration: self.duration
                )
            }
            .store(in: &cancellables)
        
        audioEngine.$duration
            .receive(on: DispatchQueue.main)
            .assign(to: \.duration, on: self)
            .store(in: &cancellables)
        
        audioEngine.$fftMagnitudes
            .receive(on: DispatchQueue.main)
            .assign(to: \.fftMagnitudes, on: self)
            .store(in: &cancellables)
        
        audioEngine.onPlaybackEnded = { [weak self] in
            self?.handleTrackEnded()
        }
    }
    
    // MARK: - Remote Command Bindings
    private func bindNowPlayingCommands() {
        nowPlayingManager.onPlay = { [weak self] in self?.play() }
        nowPlayingManager.onPause = { [weak self] in self?.pause() }
        nowPlayingManager.onTogglePlayPause = { [weak self] in self?.togglePlayPause() }
        nowPlayingManager.onNextTrack = { [weak self] in self?.nextTrack() }
        nowPlayingManager.onPreviousTrack = { [weak self] in self?.previousTrack() }
        nowPlayingManager.onSeek = { [weak self] targetTime in self?.seek(to: targetTime) }
        nowPlayingManager.onToggleLike = { [weak self] in
            if let current = self?.currentTrack {
                self?.toggleLike(track: current)
            }
        }
    }
    
    // MARK: - Playback Control Methods
    public func playTrack(_ track: Track, from trackList: [Track]? = nil) {
        if let current = currentTrack, current.matches(other: track) {
            togglePlayPause()
            return
        }
        
        currentTrack = track
        
        // If an explicit track list is provided, populate queue
        if let list = trackList, !list.isEmpty {
            unShuffledQueue = list
            if isShuffleOn {
                queue = list.shuffled().filter { !$0.matches(other: track) }
            } else if let currentIndex = list.firstIndex(where: { $0.matches(other: track) }) {
                queue = Array(list.suffix(from: currentIndex + 1))
            } else {
                queue = list.filter { !$0.matches(other: track) }
            }
        }
        
        // Update stats
        recordTrackPlayed(track)
        
        audioEngine.load(track: track)
        audioEngine.play()
        
        nowPlayingManager.updateNowPlaying(
            track: track,
            isPlaying: true,
            currentTime: 0,
            duration: track.durationSeconds
        )
        
        saveState()
    }
    
    public func play() {
        audioEngine.play()
        nowPlayingManager.updateNowPlaying(
            track: currentTrack,
            isPlaying: true,
            currentTime: currentTime,
            duration: duration
        )
    }
    
    public func pause() {
        audioEngine.pause()
        nowPlayingManager.updateNowPlaying(
            track: currentTrack,
            isPlaying: false,
            currentTime: currentTime,
            duration: duration
        )
    }
    
    public func togglePlayPause() {
        if isPlaying {
            pause()
        } else {
            play()
        }
    }
    
    public func nextTrack() {
        guard !queue.isEmpty else {
            if repeatState == .all, !unShuffledQueue.isEmpty {
                queue = isShuffleOn ? unShuffledQueue.shuffled() : unShuffledQueue
                if let next = queue.first {
                    queue.removeFirst()
                    playTrack(next)
                }
            } else {
                audioEngine.stop()
                currentTrack = nil
            }
            return
        }
        
        if let current = currentTrack {
            queueHistory.append(current)
            if queueHistory.count > 50 {
                queueHistory.removeFirst()
            }
        }
        
        let next = queue.removeFirst()
        playTrack(next)
    }
    
    public func previousTrack() {
        // If elapsed time > 3 seconds, restart current song
        if currentTime > 3.0 {
            seek(to: 0)
            return
        }
        
        if let previous = queueHistory.popLast() {
            if let current = currentTrack {
                queue.insert(current, at: 0)
            }
            playTrack(previous)
        } else {
            seek(to: 0)
        }
    }
    
    public func seek(to timeInSeconds: Double) {
        audioEngine.seek(to: timeInSeconds)
    }
    
    private func handleTrackEnded() {
        if repeatState == .one, let current = currentTrack {
            audioEngine.seek(to: 0)
            audioEngine.play()
            return
        }
        nextTrack()
    }
    
    // MARK: - Queue Controls
    public func addToQueue(track: Track) {
        queue.append(track)
    }
    
    public func playNext(track: Track) {
        queue.insert(track, at: 0)
    }
    
    public func removeFromQueue(at index: Int) {
        guard index >= 0 && index < queue.count else { return }
        queue.remove(at: index)
    }
    
    public func moveQueueItem(fromOffsets source: IndexSet, toOffset destination: Int) {
        queue.move(fromOffsets: source, toOffset: destination)
    }
    
    public func clearQueue() {
        queue.removeAll()
    }
    
    // MARK: - Shuffle & Repeat
    public func toggleShuffle() {
        isShuffleOn.toggle()
        settings.isShuffleOn = isShuffleOn
        if isShuffleOn {
            queue.shuffle()
        } else {
            // Restore natural order
            if let current = currentTrack,
               let currentIndex = unShuffledQueue.firstIndex(where: { $0.matches(other: current) }) {
                queue = Array(unShuffledQueue.suffix(from: currentIndex + 1))
            }
        }
        saveState()
    }
    
    public func toggleRepeat() {
        switch repeatState {
        case .off: repeatState = .all
        case .all: repeatState = .one
        case .one: repeatState = .off
        }
        settings.repeatState = repeatState
        saveState()
    }
    
    // MARK: - Likes & Playlists
    public func toggleLike(track: Track) {
        let newLikeState = !track.isLiked
        
        // Update in library
        if let idx = library.firstIndex(where: { $0.id == track.id }) {
            library[idx].isLiked = newLikeState
        }
        
        // Update in playlists
        for pIdx in 0..<playlists.count {
            for tIdx in 0..<playlists[pIdx].tracks.count {
                if playlists[pIdx].tracks[tIdx].id == track.id {
                    playlists[pIdx].tracks[tIdx].isLiked = newLikeState
                }
            }
        }
        
        // Update Liked Songs system playlist
        if let likedPlaylistIdx = playlists.firstIndex(where: { $0.isSystem }) {
            if newLikeState {
                if !playlists[likedPlaylistIdx].tracks.contains(where: { $0.id == track.id }) {
                    var likedCopy = track
                    likedCopy.isLiked = true
                    playlists[likedPlaylistIdx].tracks.insert(likedCopy, at: 0)
                }
            } else {
                playlists[likedPlaylistIdx].tracks.removeAll(where: { $0.id == track.id })
            }
        }
        
        if currentTrack?.id == track.id {
            currentTrack?.isLiked = newLikeState
        }
        
        saveState()
    }
    
    public func createPlaylist(name: String, description: String = "A custom playlist") -> Playlist {
        var playlist = Playlist()
        playlist.name = name
        playlist.description = description
        playlists.append(playlist)
        saveState()
        return playlist
    }
    
    public func addTrackToPlaylist(track: Track, playlistId: String) {
        guard let idx = playlists.firstIndex(where: { $0.id == playlistId }) else { return }
        if !playlists[idx].tracks.contains(where: { $0.id == track.id }) {
            playlists[idx].tracks.append(track)
            saveState()
        }
    }
    
    public func removeTrackFromPlaylist(trackId: String, playlistId: String) {
        guard let idx = playlists.firstIndex(where: { $0.id == playlistId }) else { return }
        playlists[idx].tracks.removeAll(where: { $0.id == trackId })
        saveState()
    }
    
    public func deletePlaylist(id: String) {
        playlists.removeAll(where: { $0.id == id && !$0.isSystem })
        saveState()
    }
    
    public func matchPlaylistToLocalFiles(playlistId: String) -> Int {
        guard let idx = playlists.firstIndex(where: { $0.id == playlistId }) else { return 0 }
        var matchedCount = 0
        
        for tIdx in 0..<playlists[idx].tracks.count {
            let track = playlists[idx].tracks[tIdx]
            if track.needsFile {
                if let localMatch = matcher.findBestMatch(for: track, in: library) {
                    playlists[idx].tracks[tIdx].filePath = localMatch.filePath
                    playlists[idx].tracks[tIdx].duration = localMatch.duration
                    playlists[idx].tracks[tIdx].durationSeconds = localMatch.durationSeconds
                    matchedCount += 1
                }
            }
        }
        
        saveState()
        return matchedCount
    }
    
    // MARK: - Import Tracks
    public func addImportedTracks(_ newTracks: [Track], toPlaylistNamed name: String? = nil) {
        for track in newTracks {
            if !library.contains(where: { $0.matches(other: track) }) {
                library.append(track)
            }
        }
        
        if let playlistName = name, !playlistName.isEmpty {
            var playlist = createPlaylist(name: playlistName, description: "Imported via link")
            playlist.tracks = newTracks
            if let idx = playlists.firstIndex(where: { $0.id == playlist.id }) {
                playlists[idx] = playlist
            }
        }
        
        saveState()
    }
    
    // MARK: - Equalizer
    public func setEqGain(bandIndex: Int, gain: Float) {
        guard bandIndex >= 0 && bandIndex < 10 else { return }
        settings.eqGains[bandIndex] = gain
        settings.eqPreset = "Custom"
        audioEngine.setEqGains(settings.eqGains)
        saveState()
    }
    
    public func applyEqPreset(_ preset: EqPreset) {
        settings.eqPreset = preset.name
        settings.eqGains = preset.gains
        audioEngine.setEqPreset(preset)
        saveState()
    }
    
    // MARK: - Stats Tracking
    private func recordTrackPlayed(_ track: Track) {
        stats.totalSongsPlayed += 1
        stats.totalTracks = library.count
        stats.totalLikedSongs = library.filter { $0.isLiked }.count
        
        if stats.firstListenedSong == "None" {
            stats.firstListenedSong = "\(track.title) - \(track.artist)"
        }
        
        stats.topTrack = "\(track.title) - \(track.artist)"
        stats.totalListenedSeconds += track.durationSeconds
    }
    
    // MARK: - Persistence
    public func saveState() {
        storageManager.saveData(library: library, playlists: playlists)
        storageManager.saveSettings(settings)
        storageManager.saveStats(stats)
    }
}
