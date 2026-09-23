import SwiftUI

public struct MiniPlayerView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    public var body: some View {
        if let track = playback.currentTrack {
            Button(action: {
                playback.showNowPlayingSheet = true
            }) {
                VStack(spacing: 0) {
                    // Thin live progress line at top
                    GeometryReader { geo in
                        let progress = playback.duration > 0 ? playback.currentTime / playback.duration : 0
                        Rectangle()
                            .fill(theme.accentGradient)
                            .frame(width: geo.size.width * CGFloat(max(0, min(1, progress))), height: 2.5)
                    }
                    .frame(height: 2.5)
                    
                    HStack(spacing: 12) {
                        // Artwork
                        Group {
                            if let imgPath = track.imagePath, let img = UIImage(contentsOfFile: imgPath) {
                                Image(uiImage: img)
                                    .resizable()
                                    .aspectRatio(contentMode: .fill)
                            } else if let imgPath = track.imagePath, imgPath.starts(with: "http"), let url = URL(string: imgPath) {
                                AsyncImage(url: url) { image in
                                    image.resizable().aspectRatio(contentMode: .fill)
                                } placeholder: {
                                    Color.gray.opacity(0.3)
                                }
                            } else {
                                ZStack {
                                    theme.accentGradient.opacity(0.8)
                                    Image(systemName: "music.note")
                                        .font(.system(size: 16, weight: .bold))
                                        .foregroundColor(.white)
                                }
                            }
                        }
                        .frame(width: 44, height: 44)
                        .cornerRadius(10)
                        .clipped()
                        .shadow(color: theme.accentPrimary.opacity(0.3), radius: 6, x: 0, y: 3)
                        
                        // Title & Artist
                        VStack(alignment: .leading, spacing: 2) {
                            Text(track.title)
                                .font(.system(size: 14, weight: .semibold))
                                .foregroundColor(theme.textPrimary)
                                .lineLimit(1)
                            
                            Text(track.artist)
                                .font(.system(size: 12, weight: .regular))
                                .foregroundColor(theme.textSecondary)
                                .lineLimit(1)
                        }
                        
                        Spacer()
                        
                        // Like Button
                        Button(action: {
                            playback.toggleLike(track: track)
                        }) {
                            Image(systemName: track.isLiked ? "heart.fill" : "heart")
                                .font(.system(size: 18))
                                .foregroundColor(track.isLiked ? .pink : theme.textSecondary)
                        }
                        .buttonStyle(.plain)
                        .padding(.trailing, 4)
                        
                        // Play/Pause Button
                        Button(action: {
                            playback.togglePlayPause()
                        }) {
                            ZStack {
                                Circle()
                                    .fill(theme.accentGradient)
                                    .frame(width: 36, height: 36)
                                
                                Image(systemName: playback.isPlaying ? "pause.fill" : "play.fill")
                                    .font(.system(size: 14, weight: .bold))
                                    .foregroundColor(.white)
                                    .offset(x: playback.isPlaying ? 0 : 1)
                            }
                            .shadow(color: theme.accentPrimary.opacity(0.4), radius: 8, x: 0, y: 3)
                        }
                        .buttonStyle(.plain)
                        
                        // Next Track Button
                        Button(action: {
                            playback.nextTrack()
                        }) {
                            Image(systemName: "forward.fill")
                                .font(.system(size: 16))
                                .foregroundColor(theme.textPrimary)
                        }
                        .buttonStyle(.plain)
                        .padding(.trailing, 2)
                    }
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                }
                .glassCard(cornerRadius: 18, isElevated: true)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .gesture(
                DragGesture(minimumDistance: 25)
                    .onEnded { value in
                        if value.translation.width < -50 {
                            playback.nextTrack()
                        } else if value.translation.width > 50 {
                            playback.previousTrack()
                        }
                    }
            )
            .padding(.horizontal, 12)
            .padding(.bottom, 6)
            .transition(.move(edge: .bottom).combined(with: .opacity))
            .animation(.spring(response: 0.35, dampingFraction: 0.8), value: playback.currentTrack != nil)
        }
    }
}
