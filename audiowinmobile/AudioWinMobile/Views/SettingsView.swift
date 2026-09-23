import SwiftUI

public struct SettingsView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var showResetAlert = false
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                ScrollView {
                    VStack(spacing: 24) {
                        // Section: Appearance & Accent
                        VStack(alignment: .leading, spacing: 14) {
                            Text("APPEARANCE")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            VStack(spacing: 16) {
                                // Dark / Light Mode Toggle
                                Toggle(isOn: $theme.isDarkMode) {
                                    HStack(spacing: 12) {
                                        Image(systemName: theme.isDarkMode ? "moon.stars.fill" : "sun.max.fill")
                                            .foregroundColor(theme.accentPrimary)
                                        Text("Dark Theme")
                                            .font(.system(size: 15, weight: .semibold))
                                            .foregroundColor(theme.textPrimary)
                                    }
                                }
                                .tint(theme.accentPrimary)
                                .onChange(of: theme.isDarkMode) { newVal in
                                    playback.settings.theme = newVal ? "Dark" : "Light"
                                    playback.saveState()
                                }
                                
                                Divider().background(theme.stroke.opacity(0.4))
                                
                                // Accent Color Selector
                                VStack(alignment: .leading, spacing: 10) {
                                    Text("Accent Color")
                                        .font(.system(size: 14, weight: .semibold))
                                        .foregroundColor(theme.textPrimary)
                                    
                                    HStack(spacing: 12) {
                                        ForEach(ThemeManager.accents) { preset in
                                            let isSelected = theme.currentAccentName == preset.name
                                            Button(action: {
                                                theme.setAccent(name: preset.name)
                                                playback.settings.accent = preset.name
                                                playback.saveState()
                                            }) {
                                                Circle()
                                                    .fill(preset.gradient)
                                                    .frame(width: 38, height: 38)
                                                    .overlay(
                                                        Circle()
                                                            .stroke(Color.white, lineWidth: isSelected ? 3 : 0)
                                                    )
                                                    .shadow(color: preset.primaryColor.opacity(isSelected ? 0.6 : 0.2), radius: 6, x: 0, y: 3)
                                            }
                                        }
                                    }
                                }
                                
                                Divider().background(theme.stroke.opacity(0.4))
                                
                                // Spectrum Visualizer Toggle
                                Toggle(isOn: $playback.settings.visualizerEnabled) {
                                    HStack(spacing: 12) {
                                        Image(systemName: "waveform.path.ecg")
                                            .foregroundColor(theme.accentPrimary)
                                        Text("Spectrum Visualizer")
                                            .font(.system(size: 15, weight: .semibold))
                                            .foregroundColor(theme.textPrimary)
                                    }
                                }
                                .tint(theme.accentPrimary)
                                .onChange(of: playback.settings.visualizerEnabled) { _ in
                                    playback.saveState()
                                }
                            }
                            .padding(16)
                            .glassCard(cornerRadius: 20)
                        }
                        .padding(.horizontal, 16)
                        .padding(.top, 12)
                        
                        // Section: Audio Settings
                        VStack(alignment: .leading, spacing: 14) {
                            Text("AUDIO ENGINE")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            VStack(spacing: 16) {
                                Toggle(isOn: $playback.settings.normalization) {
                                    HStack(spacing: 12) {
                                        Image(systemName: "dial.medium.fill")
                                            .foregroundColor(theme.accentPrimary)
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text("Volume Normalization")
                                                .font(.system(size: 15, weight: .semibold))
                                                .foregroundColor(theme.textPrimary)
                                            Text("Balances loudness between songs")
                                                .font(.system(size: 12))
                                                .foregroundColor(theme.textSecondary)
                                        }
                                    }
                                }
                                .tint(theme.accentPrimary)
                                
                                Divider().background(theme.stroke.opacity(0.4))
                                
                                Toggle(isOn: $playback.settings.monoAudio) {
                                    HStack(spacing: 12) {
                                        Image(systemName: "speaker.wave.2.fill")
                                            .foregroundColor(theme.accentPrimary)
                                        Text("Mono Audio")
                                            .font(.system(size: 15, weight: .semibold))
                                            .foregroundColor(theme.textPrimary)
                                    }
                                }
                                .tint(theme.accentPrimary)
                                .onChange(of: playback.settings.monoAudio) { newVal in
                                    AudioEngine.shared.setMono(newVal)
                                    playback.saveState()
                                }
                            }
                            .padding(16)
                            .glassCard(cornerRadius: 20)
                        }
                        .padding(.horizontal, 16)
                        
                        // Section: API Keys for Link Importer
                        VStack(alignment: .leading, spacing: 14) {
                            Text("API KEYS (OPTIONAL FOR PLAYLIST IMPORTS)")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            VStack(spacing: 14) {
                                VStack(alignment: .leading, spacing: 6) {
                                    Text("YouTube Data API Key")
                                        .font(.system(size: 13, weight: .semibold))
                                        .foregroundColor(theme.textSecondary)
                                    TextField("AIzaSy...", text: $playback.settings.youTubeApiKey)
                                        .font(.system(size: 14))
                                        .padding(10)
                                        .background(theme.surfaceElevated)
                                        .cornerRadius(10)
                                        .foregroundColor(theme.textPrimary)
                                        .autocapitalization(.none)
                                        .onChange(of: playback.settings.youTubeApiKey) { _ in playback.saveState() }
                                }
                                
                                VStack(alignment: .leading, spacing: 6) {
                                    Text("Spotify Client ID")
                                        .font(.system(size: 13, weight: .semibold))
                                        .foregroundColor(theme.textSecondary)
                                    TextField("Client ID", text: $playback.settings.spotifyClientId)
                                        .font(.system(size: 14))
                                        .padding(10)
                                        .background(theme.surfaceElevated)
                                        .cornerRadius(10)
                                        .foregroundColor(theme.textPrimary)
                                        .autocapitalization(.none)
                                        .onChange(of: playback.settings.spotifyClientId) { _ in playback.saveState() }
                                }
                            }
                            .padding(16)
                            .glassCard(cornerRadius: 20)
                        }
                        .padding(.horizontal, 16)
                        
                        // Section: App Stats
                        VStack(alignment: .leading, spacing: 14) {
                            Text("LISTENING STATS")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            VStack(spacing: 12) {
                                StatRow(title: "Total Songs Played", value: "\(playback.stats.totalSongsPlayed)")
                                StatRow(title: "Total Library Tracks", value: "\(playback.library.count)")
                                StatRow(title: "Liked Songs", value: "\(playback.library.filter { $0.isLiked }.count)")
                                StatRow(title: "Top Track", value: playback.stats.topTrack)
                                StatRow(title: "First Listened", value: playback.stats.firstListenedSong)
                            }
                            .padding(16)
                            .glassCard(cornerRadius: 20)
                        }
                        .padding(.horizontal, 16)
                        
                        // About & Reset
                        VStack(spacing: 16) {
                            Text("AudioWin Mobile v2.0.0")
                                .font(.system(size: 13, weight: .semibold))
                                .foregroundColor(theme.textDim)
                            
                            Button(action: {
                                showResetAlert = true
                            }) {
                                Text("Clear All App Data")
                                    .font(.system(size: 14, weight: .bold))
                                    .foregroundColor(.red)
                            }
                        }
                        .padding(.top, 10)
                        .padding(.bottom, 80)
                    }
                }
            }
            .navigationTitle("Settings")
            .alert("Reset All Data?", isPresented: $showResetAlert) {
                Button("Cancel", role: .cancel) { }
                Button("Reset Everything", role: .destructive) {
                    playback.library.removeAll()
                    playback.playlists = [
                        Playlist(id: "system_liked_songs", name: "Liked Songs", icon: "heart.fill", description: "Your favorite tracks", isSystem: true)
                    ]
                    playback.stats = AppStats()
                    playback.saveState()
                }
            } message: {
                Text("This will remove all imported songs, custom playlists, and listening statistics from your device.")
            }
        }
    }
}

struct StatRow: View {
    let title: String
    let value: String
    @ObservedObject var theme = ThemeManager.shared
    
    var body: some View {
        HStack {
            Text(title)
                .font(.system(size: 14))
                .foregroundColor(theme.textSecondary)
            Spacer()
            Text(value)
                .font(.system(size: 14, weight: .semibold))
                .foregroundColor(theme.textPrimary)
                .lineLimit(1)
        }
    }
}
