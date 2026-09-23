import SwiftUI
import UniformTypeIdentifiers

public struct TrackRowView: View {
    let track: Track
    let isCurrent: Bool
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var showPlaylistPicker = false
    
    public var body: some View {
        HStack(spacing: 12) {
            // Thumbnail
            Group {
                if let imgPath = track.imagePath, let img = UIImage(contentsOfFile: imgPath) {
                    Image(uiImage: img)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                } else if let imgPath = track.imagePath, imgPath.starts(with: "http"), let url = URL(string: imgPath) {
                    AsyncImage(url: url) { img in
                        img.resizable().aspectRatio(contentMode: .fill)
                    } placeholder: {
                        Color.gray.opacity(0.3)
                    }
                } else {
                    ZStack {
                        theme.accentGradient.opacity(0.7)
                        Image(systemName: "music.note")
                            .font(.system(size: 14, weight: .bold))
                            .foregroundColor(.white)
                    }
                }
            }
            .frame(width: 44, height: 44)
            .cornerRadius(8)
            .clipped()
            
            // Track Info
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(track.title)
                        .font(.system(size: 15, weight: isCurrent ? .bold : .medium))
                        .foregroundColor(isCurrent ? theme.accentPrimary : theme.textPrimary)
                        .lineLimit(1)
                    
                    if track.isLink {
                        Text(track.sourceLabel)
                            .font(.system(size: 9, weight: .bold))
                            .foregroundColor(theme.accentPrimary)
                            .padding(.horizontal, 5)
                            .padding(.vertical, 1)
                            .background(Capsule().fill(theme.accentPrimary.opacity(0.15)))
                    }
                }
                
                HStack(spacing: 6) {
                    Text(track.artist)
                        .font(.system(size: 13))
                        .foregroundColor(theme.textSecondary)
                        .lineLimit(1)
                    
                    if track.needsFile {
                        Text("• Needs local file")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundColor(.orange)
                    }
                }
            }
            
            Spacer()
            
            // Duration & Like Button
            Text(track.duration)
                .font(.system(size: 12, weight: .medium, design: .monospaced))
                .foregroundColor(theme.textDim)
            
            Button(action: {
                playback.toggleLike(track: track)
            }) {
                Image(systemName: track.isLiked ? "heart.fill" : "heart")
                    .font(.system(size: 16))
                    .foregroundColor(track.isLiked ? .pink : theme.textDim)
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, 4)
        .contextMenu {
            Button {
                playback.playTrack(track)
            } label: {
                Label("Play Now", systemImage: "play.fill")
            }
            
            Button {
                playback.playNext(track: track)
            } label: {
                Label("Play Next", systemImage: "text.line.first.and.arrowtriangle.forward")
            }
            
            Button {
                playback.addToQueue(track: track)
            } label: {
                Label("Add to Queue", systemImage: "text.append")
            }
            
            Button {
                showPlaylistPicker = true
            } label: {
                Label("Add to Playlist...", systemImage: "plus.rectangle.on.rectangle")
            }
            
            Button {
                playback.toggleLike(track: track)
            } label: {
                Label(track.isLiked ? "Unlike" : "Like", systemImage: track.isLiked ? "heart.slash" : "heart")
            }
            
            if let sourceUrl = track.sourceUrl, let url = URL(string: sourceUrl) {
                Button {
                    UIApplication.shared.open(url)
                } label: {
                    Label("Open in Browser", systemImage: "safari")
                }
            }
        }
        .sheet(isPresented: $showPlaylistPicker) {
            PlaylistPickerSheet(track: track)
        }
    }
}

// MARK: - Library View
public struct LibraryView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var searchText = ""
    @State private var sortOption: SortOption = .recentlyAdded
    @State private var isImportingFiles = false
    
    enum SortOption: String, CaseIterable, Identifiable {
        case recentlyAdded = "Recently Added"
        case title = "Title"
        case artist = "Artist"
        case duration = "Duration"
        case mostPlayed = "Most Played"
        
        var id: String { rawValue }
    }
    
    var filteredTracks: [Track] {
        var tracks = playback.library
        
        if !searchText.isEmpty {
            let query = searchText.lowercased()
            tracks = tracks.filter {
                $0.title.lowercased().contains(query) ||
                $0.artist.lowercased().contains(query) ||
                $0.album.lowercased().contains(query)
            }
        }
        
        switch sortOption {
        case .recentlyAdded:
            return tracks.sorted { $0.addedUtc > $1.addedUtc }
        case .title:
            return tracks.sorted { $0.title.localizedCaseInsensitiveCompare($1.title) == .orderedAscending }
        case .artist:
            return tracks.sorted { $0.artist.localizedCaseInsensitiveCompare($1.artist) == .orderedAscending }
        case .duration:
            return tracks.sorted { $0.durationSeconds > $1.durationSeconds }
        case .mostPlayed:
            return tracks.sorted { $0.playCount > $1.playCount }
        }
    }
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                VStack(spacing: 0) {
                    // Header Action Buttons
                    HStack(spacing: 12) {
                        // Import Audio Button
                        Button(action: {
                            isImportingFiles = true
                        }) {
                            HStack(spacing: 6) {
                                Image(systemName: "plus.circle.fill")
                                    .font(.system(size: 14, weight: .bold))
                                Text("Import Audio")
                                    .font(.system(size: 13, weight: .bold))
                            }
                            .foregroundColor(.white)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 8)
                            .background(theme.accentGradient)
                            .cornerRadius(20)
                            .shadow(color: theme.accentPrimary.opacity(0.3), radius: 6, x: 0, y: 3)
                        }
                        
                        // Shuffle All Button
                        if !playback.library.isEmpty {
                            Button(action: {
                                if let randomTrack = playback.library.randomElement() {
                                    playback.isShuffleOn = true
                                    playback.playTrack(randomTrack, from: playback.library)
                                }
                            }) {
                                HStack(spacing: 6) {
                                    Image(systemName: "shuffle")
                                        .font(.system(size: 13, weight: .bold))
                                    Text("Shuffle")
                                        .font(.system(size: 13, weight: .semibold))
                                }
                                .foregroundColor(theme.textPrimary)
                                .padding(.horizontal, 14)
                                .padding(.vertical, 8)
                                .background(theme.surfaceElevated)
                                .cornerRadius(20)
                            }
                        }
                        
                        Spacer()
                        
                        // Sort Menu
                        Menu {
                            ForEach(SortOption.allCases) { option in
                                Button {
                                    sortOption = option
                                } label: {
                                    HStack {
                                        Text(option.rawValue)
                                        if sortOption == option {
                                            Image(systemName: "checkmark")
                                        }
                                    }
                                }
                            }
                        } label: {
                            Image(systemName: "arrow.up.arrow.down.circle")
                                .font(.system(size: 20))
                                .foregroundColor(theme.textSecondary)
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    
                    // Track List
                    if filteredTracks.isEmpty {
                        VStack(spacing: 16) {
                            Spacer()
                            Image(systemName: "music.note.house.fill")
                                .font(.system(size: 56))
                                .foregroundColor(theme.textDim)
                            Text(searchText.isEmpty ? "Your Library is Empty" : "No Matching Tracks")
                                .font(.system(size: 18, weight: .bold))
                                .foregroundColor(theme.textPrimary)
                            Text(searchText.isEmpty ? "Tap 'Import Audio' to add tracks from Files or iCloud" : "Try searching with a different keyword")
                                .font(.system(size: 14))
                                .foregroundColor(theme.textSecondary)
                                .multilineTextAlignment(.center)
                                .padding(.horizontal, 32)
                            Spacer()
                        }
                    } else {
                        List {
                            ForEach(filteredTracks) { track in
                                let isCurrent = playback.currentTrack?.id == track.id
                                TrackRowView(track: track, isCurrent: isCurrent)
                                    .contentShape(Rectangle())
                                    .onTapGesture {
                                        playback.playTrack(track, from: filteredTracks)
                                    }
                                    .swipeActions(edge: .leading) {
                                        Button {
                                            playback.toggleLike(track: track)
                                        } label: {
                                            Label("Like", systemImage: track.isLiked ? "heart.slash.fill" : "heart.fill")
                                        }
                                        .tint(.pink)
                                    }
                                    .swipeActions(edge: .trailing) {
                                        Button {
                                            playback.addToQueue(track: track)
                                        } label: {
                                            Label("Queue", systemImage: "text.append")
                                        }
                                        .tint(theme.accentPrimary)
                                    }
                            }
                            .onDelete { indexSet in
                                for index in indexSet {
                                    let track = filteredTracks[index]
                                    playback.library.removeAll(where: { $0.id == track.id })
                                }
                                playback.saveState()
                            }
                        }
                        .listStyle(.plain)
                        .scrollContentBackground(.hidden)
                    }
                }
            }
            .navigationTitle("Library")
            .searchable(text: $searchText, prompt: "Search tracks, artists, albums...")
            .fileImporter(
                isPresented: $isImportingFiles,
                allowedContentTypes: [.audio],
                allowsMultipleSelection: true
            ) { result in
                Task {
                    switch result {
                    case .success(let urls):
                        for url in urls {
                            if let newTrack = await DocumentPickerManager.shared.processAudioFile(from: url) {
                                DispatchQueue.main.async {
                                    if !playback.library.contains(where: { $0.matches(other: newTrack) }) {
                                        playback.library.append(newTrack)
                                    }
                                }
                            }
                        }
                        DispatchQueue.main.async {
                            playback.saveState()
                        }
                    case .failure(let error):
                        print("File import failed: \(error.localizedDescription)")
                    }
                }
            }
        }
    }
}

// MARK: - Playlist Picker Modal Sheet
struct PlaylistPickerSheet: View {
    let track: Track
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    @Environment(\.dismiss) var dismiss
    
    var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                List {
                    ForEach(playback.playlists.filter { !$0.isSystem }) { playlist in
                        Button {
                            playback.addTrackToPlaylist(track: track, playlistId: playlist.id)
                            dismiss()
                        } label: {
                            HStack {
                                Image(systemName: playlist.icon)
                                    .foregroundColor(theme.accentPrimary)
                                Text(playlist.name)
                                    .foregroundColor(theme.textPrimary)
                                Spacer()
                                Text("\(playlist.tracks.count) tracks")
                                    .font(.system(size: 12))
                                    .foregroundColor(theme.textDim)
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
            }
            .navigationTitle("Add to Playlist")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Cancel") { dismiss() }
                        .foregroundColor(theme.accentPrimary)
                }
            }
        }
    }
}
