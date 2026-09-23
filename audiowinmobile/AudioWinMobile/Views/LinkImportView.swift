import SwiftUI

public struct LinkImportView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var urlString = ""
    @State private var isLoading = false
    @State private var importResult: ImportResult? = nil
    @State private var errorMessage: String? = nil
    @State private var successBanner: String? = nil
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                ScrollView {
                    VStack(spacing: 24) {
                        // URL Input Box
                        VStack(alignment: .leading, spacing: 10) {
                            Text("PASTE A YOUTUBE OR SPOTIFY LINK")
                                .font(.system(size: 11, weight: .bold))
                                .tracking(1.5)
                                .foregroundColor(theme.textDim)
                            
                            HStack(spacing: 8) {
                                Image(systemName: "link")
                                    .foregroundColor(theme.accentPrimary)
                                
                                TextField("https://youtube.com/... or https://open.spotify.com/...", text: $urlString)
                                    .font(.system(size: 14))
                                    .foregroundColor(theme.textPrimary)
                                    .autocapitalization(.none)
                                    .disableAutocorrection(true)
                                
                                if !urlString.isEmpty {
                                    Button(action: { urlString = "" }) {
                                        Image(systemName: "xmark.circle.fill")
                                            .foregroundColor(theme.textDim)
                                    }
                                }
                                
                                Button(action: {
                                    if let clip = UIPasteboard.general.string {
                                        urlString = clip
                                    }
                                }) {
                                    Text("Paste")
                                        .font(.system(size: 12, weight: .bold))
                                        .foregroundColor(theme.accentPrimary)
                                        .padding(.horizontal, 10)
                                        .padding(.vertical, 6)
                                        .background(theme.surfaceElevated)
                                        .cornerRadius(8)
                                }
                            }
                            .padding(14)
                            .glassCard(cornerRadius: 16)
                            
                            // Import Action Button
                            Button(action: {
                                runImport()
                            }) {
                                HStack(spacing: 8) {
                                    if isLoading {
                                        ProgressView()
                                            .progressViewStyle(CircularProgressViewStyle(tint: .white))
                                    } else {
                                        Image(systemName: "arrow.down.circle.fill")
                                            .font(.system(size: 16, weight: .bold))
                                        Text("Fetch Link Info")
                                            .font(.system(size: 15, weight: .bold))
                                    }
                                }
                                .foregroundColor(.white)
                                .frame(maxWidth: .infinity)
                                .frame(height: 48)
                                .background(theme.accentGradient)
                                .cornerRadius(24)
                                .shadow(color: theme.accentPrimary.opacity(0.4), radius: 8, x: 0, y: 4)
                            }
                            .disabled(urlString.trimmingCharacters(in: .whitespaces).isEmpty || isLoading)
                        }
                        .padding(.horizontal, 16)
                        .padding(.top, 12)
                        
                        // Error or Success Banner
                        if let error = errorMessage {
                            HStack {
                                Image(systemName: "exclamationmark.triangle.fill")
                                    .foregroundColor(.red)
                                Text(error)
                                    .font(.system(size: 13, weight: .medium))
                                    .foregroundColor(.red)
                            }
                            .padding(12)
                            .background(Color.red.opacity(0.12))
                            .cornerRadius(12)
                            .padding(.horizontal, 16)
                        }
                        
                        if let banner = successBanner {
                            HStack {
                                Image(systemName: "checkmark.circle.fill")
                                    .foregroundColor(.green)
                                Text(banner)
                                    .font(.system(size: 13, weight: .medium))
                                    .foregroundColor(.green)
                            }
                            .padding(12)
                            .background(Color.green.opacity(0.12))
                            .cornerRadius(12)
                            .padding(.horizontal, 16)
                        }
                        
                        // Preview Card
                        if let result = importResult, result.success {
                            VStack(alignment: .leading, spacing: 16) {
                                HStack {
                                    Text("FETCHED PREVIEW")
                                        .font(.system(size: 11, weight: .bold))
                                        .tracking(1.5)
                                        .foregroundColor(theme.textDim)
                                    Spacer()
                                    Text("\(result.tracks.count) track(s)")
                                        .font(.system(size: 12, weight: .bold))
                                        .foregroundColor(theme.accentPrimary)
                                }
                                
                                VStack(spacing: 8) {
                                    ForEach(result.tracks.prefix(10)) { track in
                                        HStack(spacing: 12) {
                                            if let img = track.imagePath, let url = URL(string: img) {
                                                AsyncImage(url: url) { image in
                                                    image.resizable().aspectRatio(contentMode: .fill)
                                                } placeholder: {
                                                    Color.gray.opacity(0.3)
                                                }
                                                .frame(width: 40, height: 40)
                                                .cornerRadius(8)
                                                .clipped()
                                            }
                                            
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(track.title)
                                                    .font(.system(size: 14, weight: .semibold))
                                                    .foregroundColor(theme.textPrimary)
                                                    .lineLimit(1)
                                                Text(track.artist)
                                                    .font(.system(size: 12))
                                                    .foregroundColor(theme.textSecondary)
                                                    .lineLimit(1)
                                            }
                                            Spacer()
                                        }
                                        .padding(.vertical, 2)
                                    }
                                }
                                
                                // Save Buttons
                                HStack(spacing: 12) {
                                    Button(action: {
                                        playback.addImportedTracks(result.tracks, toPlaylistNamed: nil)
                                        successBanner = "Added \(result.tracks.count) track(s) to your Library!"
                                        importResult = nil
                                        urlString = ""
                                    }) {
                                        Text("Add to Library")
                                            .font(.system(size: 14, weight: .bold))
                                            .foregroundColor(.white)
                                            .frame(maxWidth: .infinity)
                                            .frame(height: 44)
                                            .background(theme.accentGradient)
                                            .cornerRadius(22)
                                    }
                                    
                                    Button(action: {
                                        playback.addImportedTracks(result.tracks, toPlaylistNamed: result.collectionName)
                                        successBanner = "Created playlist '\(result.collectionName)' with \(result.tracks.count) track(s)!"
                                        importResult = nil
                                        urlString = ""
                                    }) {
                                        Text("Save as Playlist")
                                            .font(.system(size: 14, weight: .bold))
                                            .foregroundColor(theme.textPrimary)
                                            .frame(maxWidth: .infinity)
                                            .frame(height: 44)
                                            .background(theme.surfaceElevated)
                                            .cornerRadius(22)
                                    }
                                }
                            }
                            .padding(16)
                            .glassCard(cornerRadius: 20)
                            .padding(.horizontal, 16)
                        }
                        
                        // Information Guide Card
                        VStack(alignment: .leading, spacing: 12) {
                            HStack(spacing: 8) {
                                Image(systemName: "info.circle.fill")
                                    .foregroundColor(theme.accentPrimary)
                                Text("How Link Importing Works")
                                    .font(.system(size: 15, weight: .bold))
                                    .foregroundColor(theme.textPrimary)
                            }
                            
                            Text("• Single YouTube videos & Spotify tracks work instantly with zero setup using public oEmbed endpoints.")
                                .font(.system(size: 13))
                                .foregroundColor(theme.textSecondary)
                            
                            Text("• To unlock entire YouTube Playlists or Spotify Albums in one paste, enter your free API keys in Settings.")
                                .font(.system(size: 13))
                                .foregroundColor(theme.textSecondary)
                            
                            Text("• Imported entries are references with artwork & title. Match them to local audio files using 'Auto Match' inside any playlist.")
                                .font(.system(size: 13))
                                .foregroundColor(theme.textSecondary)
                        }
                        .padding(16)
                        .glassCard(cornerRadius: 20)
                        .padding(.horizontal, 16)
                        .padding(.bottom, 80)
                    }
                }
            }
            .navigationTitle("Link Import")
        }
    }
    
    private func runImport() {
        isLoading = true
        errorMessage = nil
        successBanner = nil
        importResult = nil
        
        Task {
            let res = await LinkImportService.shared.importUrl(urlString, settings: playback.settings)
            DispatchQueue.main.async {
                isLoading = false
                if res.success {
                    importResult = res
                } else {
                    errorMessage = res.error ?? "Failed to import link."
                }
            }
        }
    }
}
