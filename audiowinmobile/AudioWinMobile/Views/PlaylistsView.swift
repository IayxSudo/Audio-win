import SwiftUI

public struct PlaylistsView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var showCreateSheet = false
    @State private var newPlaylistName = ""
    @State private var newPlaylistDesc = ""
    
    private let columns = [
        GridItem(.flexible(), spacing: 16),
        GridItem(.flexible(), spacing: 16)
    ]
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                ScrollView {
                    VStack(alignment: .leading, spacing: 20) {
                        // Liked Songs Feature Card
                        if let likedPlaylist = playback.playlists.first(where: { $0.isSystem }) {
                            NavigationLink(destination: PlaylistDetailView(playlist: likedPlaylist)) {
                                HStack(spacing: 16) {
                                    ZStack {
                                        LinearGradient(
                                            colors: [Color(hex: "#FF416C"), Color(hex: "#8A2387")],
                                            startPoint: .topLeading,
                                            endPoint: .bottomTrailing
                                        )
                                        Image(systemName: "heart.fill")
                                            .font(.system(size: 32))
                                            .foregroundColor(.white)
                                    }
                                    .frame(width: 80, height: 80)
                                    .cornerRadius(16)
                                    .shadow(color: Color(hex: "#FF416C").opacity(0.4), radius: 10, x: 0, y: 5)
                                    
                                    VStack(alignment: .leading, spacing: 4) {
                                        Text("Liked Songs")
                                            .font(.system(size: 20, weight: .bold))
                                            .foregroundColor(theme.textPrimary)
                                        
                                        Text(likedPlaylist.subtitle)
                                            .font(.system(size: 14, weight: .medium))
                                            .foregroundColor(theme.textSecondary)
                                    }
                                    
                                    Spacer()
                                    
                                    Image(systemName: "chevron.right")
                                        .font(.system(size: 16, weight: .semibold))
                                        .foregroundColor(theme.textDim)
                                }
                                .padding(16)
                                .glassCard(cornerRadius: 20)
                            }
                            .buttonStyle(.plain)
                        }
                        
                        // Custom Playlists Section
                        HStack {
                            Text("YOUR PLAYLISTS")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            Spacer()
                            
                            Button(action: { showCreateSheet = true }) {
                                HStack(spacing: 4) {
                                    Image(systemName: "plus")
                                    Text("New")
                                }
                                .font(.system(size: 13, weight: .bold))
                                .foregroundColor(theme.accentPrimary)
                            }
                        }
                        .padding(.top, 8)
                        
                        // Grid of custom playlists
                        let customPlaylists = playback.playlists.filter { !$0.isSystem }
                        if customPlaylists.isEmpty {
                            VStack(spacing: 12) {
                                Image(systemName: "music.note.list")
                                    .font(.system(size: 40))
                                    .foregroundColor(theme.textDim)
                                Text("No Custom Playlists")
                                    .font(.system(size: 16, weight: .semibold))
                                    .foregroundColor(theme.textPrimary)
                                Text("Tap '+ New' to create your first playlist.")
                                    .font(.system(size: 13))
                                    .foregroundColor(theme.textSecondary)
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 40)
                        } else {
                            LazyVGrid(columns: columns, spacing: 16) {
                                ForEach(customPlaylists) { playlist in
                                    NavigationLink(destination: PlaylistDetailView(playlist: playlist)) {
                                        PlaylistCardView(playlist: playlist)
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.top, 12)
                    .padding(.bottom, 80)
                }
            }
            .navigationTitle("Playlists")
            .sheet(isPresented: $showCreateSheet) {
                CreatePlaylistSheet()
            }
        }
    }
}

// MARK: - Playlist Grid Card
struct PlaylistCardView: View {
    let playlist: Playlist
    @ObservedObject var theme = ThemeManager.shared
    
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ZStack {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(theme.surfaceElevated)
                
                Image(systemName: playlist.icon)
                    .font(.system(size: 36, weight: .semibold))
                    .foregroundColor(theme.accentPrimary)
            }
            .aspectRatio(1, contentMode: .fit)
            .overlay(
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .stroke(theme.stroke.opacity(0.3), lineWidth: 1)
            )
            
            VStack(alignment: .leading, spacing: 2) {
                Text(playlist.name)
                    .font(.system(size: 15, weight: .bold))
                    .foregroundColor(theme.textPrimary)
                    .lineLimit(1)
                
                Text(playlist.subtitle)
                    .font(.system(size: 12))
                    .foregroundColor(theme.textSecondary)
            }
        }
    }
}

// MARK: - Playlist Detail View
public struct PlaylistDetailView: View {
    let playlist: Playlist
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var matchBannerText: String? = nil
    
    private var currentPlaylist: Playlist {
        playback.playlists.first(where: { $0.id == playlist.id }) ?? playlist
    }
    
    public var body: some View {
        ZStack {
            theme.background.ignoresSafeArea()
            
            ScrollView {
                VStack(spacing: 20) {
                    // Header Artwork & Info
                    VStack(spacing: 12) {
                        ZStack {
                            if currentPlaylist.isSystem {
                                LinearGradient(
                                    colors: [Color(hex: "#FF416C"), Color(hex: "#8A2387")],
                                    startPoint: .topLeading,
                                    endPoint: .bottomTrailing
                                )
                            } else {
                                theme.accentGradient
                            }
                            
                            Image(systemName: currentPlaylist.icon)
                                .font(.system(size: 56, weight: .bold))
                                .foregroundColor(.white)
                        }
                        .frame(width: 140, height: 140)
                        .cornerRadius(24)
                        .shadow(color: theme.accentPrimary.opacity(0.35), radius: 16, x: 0, y: 8)
                        
                        Text(currentPlaylist.name)
                            .font(.system(size: 24, weight: .bold))
                            .foregroundColor(theme.textPrimary)
                        
                        Text(currentPlaylist.description)
                            .font(.system(size: 14))
                            .foregroundColor(theme.textSecondary)
                            .multilineTextAlignment(.center)
                            .padding(.horizontal, 24)
                        
                        Text("\(currentPlaylist.subtitle) • Created by \(currentPlaylist.creator)")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(theme.textDim)
                    }
                    .padding(.top, 16)
                    
                    // Match to Local Files Banner if needed
                    let unmatchedCount = currentPlaylist.tracks.filter { $0.needsFile }.count
                    if unmatchedCount > 0 {
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text("\(unmatchedCount) tracks need local files")
                                    .font(.system(size: 13, weight: .bold))
                                    .foregroundColor(.orange)
                                Text("Tap to auto-match with your library")
                                    .font(.system(size: 11))
                                    .foregroundColor(theme.textSecondary)
                            }
                            Spacer()
                            Button("Auto Match") {
                                let matched = playback.matchPlaylistToLocalFiles(playlistId: currentPlaylist.id)
                                matchBannerText = "Matched \(matched) track(s)!"
                            }
                            .font(.system(size: 12, weight: .bold))
                            .foregroundColor(.white)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 6)
                            .background(Color.orange)
                            .cornerRadius(12)
                        }
                        .padding(12)
                        .background(Color.orange.opacity(0.15))
                        .cornerRadius(14)
                        .padding(.horizontal, 16)
                    }
                    
                    if let banner = matchBannerText {
                        Text(banner)
                            .font(.system(size: 12, weight: .bold))
                            .foregroundColor(theme.accentPrimary)
                    }
                    
                    // Action Buttons (Play All & Shuffle)
                    if !currentPlaylist.tracks.isEmpty {
                        HStack(spacing: 16) {
                            Button(action: {
                                if let first = currentPlaylist.tracks.first {
                                    playback.isShuffleOn = false
                                    playback.playTrack(first, from: currentPlaylist.tracks)
                                }
                            }) {
                                HStack {
                                    Image(systemName: "play.fill")
                                    Text("Play")
                                }
                                .font(.system(size: 16, weight: .bold))
                                .foregroundColor(.white)
                                .frame(maxWidth: .infinity)
                                .frame(height: 48)
                                .background(theme.accentGradient)
                                .cornerRadius(24)
                                .shadow(color: theme.accentPrimary.opacity(0.4), radius: 8, x: 0, y: 4)
                            }
                            
                            Button(action: {
                                if let randomTrack = currentPlaylist.tracks.randomElement() {
                                    playback.isShuffleOn = true
                                    playback.playTrack(randomTrack, from: currentPlaylist.tracks)
                                }
                            }) {
                                HStack {
                                    Image(systemName: "shuffle")
                                    Text("Shuffle")
                                }
                                .font(.system(size: 16, weight: .bold))
                                .foregroundColor(theme.textPrimary)
                                .frame(maxWidth: .infinity)
                                .frame(height: 48)
                                .background(theme.surfaceElevated)
                                .cornerRadius(24)
                            }
                        }
                        .padding(.horizontal, 20)
                    }
                    
                    // Track List
                    LazyVStack(spacing: 4) {
                        ForEach(currentPlaylist.tracks) { track in
                            let isCurrent = playback.currentTrack?.id == track.id
                            TrackRowView(track: track, isCurrent: isCurrent)
                                .contentShape(Rectangle())
                                .onTapGesture {
                                    playback.playTrack(track, from: currentPlaylist.tracks)
                                }
                                .padding(.horizontal, 16)
                        }
                    }
                    .padding(.bottom, 80)
                }
            }
        }
        .navigationBarTitleDisplayMode(.inline)
    }
}

// MARK: - Create Playlist Sheet
struct CreatePlaylistSheet: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    @Environment(\.dismiss) var dismiss
    
    @State private var name = ""
    @State private var description = ""
    
    var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                VStack(spacing: 20) {
                    TextField("Playlist Name", text: $name)
                        .font(.system(size: 16, weight: .semibold))
                        .padding(14)
                        .background(theme.surfaceElevated)
                        .cornerRadius(12)
                        .foregroundColor(theme.textPrimary)
                    
                    TextField("Description (optional)", text: $description)
                        .font(.system(size: 14))
                        .padding(14)
                        .background(theme.surfaceElevated)
                        .cornerRadius(12)
                        .foregroundColor(theme.textPrimary)
                    
                    Spacer()
                }
                .padding(20)
            }
            .navigationTitle("New Playlist")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button("Cancel") { dismiss() }
                        .foregroundColor(theme.textSecondary)
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Create") {
                        if !name.trimmingCharacters(in: .whitespaces).isEmpty {
                            _ = playback.createPlaylist(name: name, description: description.isEmpty ? "A custom playlist" : description)
                            dismiss()
                        }
                    }
                    .font(.system(size: 16, weight: .bold))
                    .foregroundColor(theme.accentPrimary)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }
}
