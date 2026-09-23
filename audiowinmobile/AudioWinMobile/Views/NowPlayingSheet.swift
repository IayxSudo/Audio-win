import SwiftUI
import AVKit

public struct NowPlayingSheet: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    @Environment(\.dismiss) var dismiss
    
    @State private var sliderValue: Double = 0.0
    @State private var isDraggingSlider: Bool = false
    
    public var body: some View {
        ZStack {
            // Background Dynamic Gradient
            theme.background.ignoresSafeArea()
            
            // Ambient colored background blur
            Circle()
                .fill(theme.accentPrimary.opacity(0.18))
                .blur(radius: 80)
                .offset(y: -100)
            
            VStack(spacing: 20) {
                // Top Grabber Bar & Close Button
                HStack {
                    Button(action: { dismiss() }) {
                        Image(systemName: "chevron.down")
                            .font(.system(size: 18, weight: .bold))
                            .foregroundColor(theme.textSecondary)
                            .padding(8)
                            .background(Circle().fill(theme.surfaceElevated.opacity(0.6)))
                    }
                    
                    Spacer()
                    
                    Text("NOW PLAYING")
                        .font(.system(size: 11, weight: .bold))
                        .tracking(2)
                        .foregroundColor(theme.textDim)
                    
                    Spacer()
                    
                    // Equalizer shortcut
                    Button(action: {
                        playback.showEqualizerSheet = true
                    }) {
                        Image(systemName: "slider.vertical.3")
                            .font(.system(size: 17, weight: .medium))
                            .foregroundColor(theme.textSecondary)
                            .padding(8)
                            .background(Circle().fill(theme.surfaceElevated.opacity(0.6)))
                    }
                }
                .padding(.horizontal, 24)
                .padding(.top, 16)
                
                Spacer(minLength: 10)
                
                // Big Album Artwork
                Group {
                    if let imgPath = playback.currentTrack?.imagePath, let img = UIImage(contentsOfFile: imgPath) {
                        Image(uiImage: img)
                            .resizable()
                            .aspectRatio(contentMode: .fill)
                    } else if let imgPath = playback.currentTrack?.imagePath, imgPath.starts(with: "http"), let url = URL(string: imgPath) {
                        AsyncImage(url: url) { image in
                            image.resizable().aspectRatio(contentMode: .fill)
                        } placeholder: {
                            Color.gray.opacity(0.3)
                        }
                    } else {
                        ZStack {
                            RoundedRectangle(cornerRadius: 24, style: .continuous)
                                .fill(theme.accentGradient.opacity(0.85))
                            
                            Image(systemName: "music.note")
                                .font(.system(size: 72, weight: .bold))
                                .foregroundColor(.white.opacity(0.9))
                        }
                    }
                }
                .frame(maxWidth: 320, maxHeight: 320)
                .aspectRatio(1, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 24, style: .continuous)
                        .stroke(theme.stroke.opacity(0.3), lineWidth: 1)
                )
                .shadow(color: theme.accentPrimary.opacity(0.4), radius: 24, x: 0, y: 12)
                .padding(.horizontal, 28)
                .scaleEffect(playback.isPlaying ? 1.0 : 0.94)
                .animation(.spring(response: 0.4, dampingFraction: 0.7), value: playback.isPlaying)
                
                Spacer(minLength: 10)
                
                // Track Info & Like Button
                HStack(alignment: .center, spacing: 16) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(playback.currentTrack?.title ?? "Not Playing")
                            .font(.system(size: 22, weight: .bold))
                            .foregroundColor(theme.textPrimary)
                            .lineLimit(1)
                        
                        HStack(spacing: 8) {
                            Text(playback.currentTrack?.artist ?? "")
                                .font(.system(size: 16, weight: .medium))
                                .foregroundColor(theme.textSecondary)
                                .lineLimit(1)
                            
                            if let track = playback.currentTrack, track.isLink {
                                Text(track.sourceLabel)
                                    .font(.system(size: 10, weight: .bold))
                                    .foregroundColor(theme.accentPrimary)
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 2)
                                    .background(Capsule().fill(theme.accentPrimary.opacity(0.15)))
                            }
                        }
                    }
                    
                    Spacer()
                    
                    if let track = playback.currentTrack {
                        Button(action: {
                            playback.toggleLike(track: track)
                        }) {
                            Image(systemName: track.isLiked ? "heart.fill" : "heart")
                                .font(.system(size: 24, weight: .semibold))
                                .foregroundColor(track.isLiked ? .pink : theme.textSecondary)
                                .scaleEffect(track.isLiked ? 1.15 : 1.0)
                                .animation(.spring(response: 0.25, dampingFraction: 0.6), value: track.isLiked)
                        }
                    }
                }
                .padding(.horizontal, 28)
                
                // Live Scrubber Bar
                VStack(spacing: 6) {
                    Slider(
                        value: Binding(
                            get: { isDraggingSlider ? sliderValue : playback.currentTime },
                            set: { newValue in
                                sliderValue = newValue
                                isDraggingSlider = true
                            }
                        ),
                        in: 0...max(1, playback.duration),
                        onEditingChanged: { editing in
                            if !editing {
                                playback.seek(to: sliderValue)
                                isDraggingSlider = false
                            }
                        }
                    )
                    .accentColor(theme.accentPrimary)
                    
                    HStack {
                        Text(formatTime(isDraggingSlider ? sliderValue : playback.currentTime))
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .foregroundColor(theme.textDim)
                        
                        Spacer()
                        
                        Text(formatTime(playback.duration))
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .foregroundColor(theme.textDim)
                    }
                }
                .padding(.horizontal, 28)
                
                // Big Playback Controls
                HStack(spacing: 28) {
                    // Shuffle
                    Button(action: { playback.toggleShuffle() }) {
                        Image(systemName: "shuffle")
                            .font(.system(size: 20, weight: .semibold))
                            .foregroundColor(playback.isShuffleOn ? theme.accentPrimary : theme.textDim)
                    }
                    
                    // Previous
                    Button(action: { playback.previousTrack() }) {
                        Image(systemName: "backward.fill")
                            .font(.system(size: 26, weight: .semibold))
                            .foregroundColor(theme.textPrimary)
                    }
                    
                    // Main Play/Pause Button
                    Button(action: { playback.togglePlayPause() }) {
                        ZStack {
                            Circle()
                                .fill(theme.accentGradient)
                                .frame(width: 72, height: 72)
                            
                            Image(systemName: playback.isPlaying ? "pause.fill" : "play.fill")
                                .font(.system(size: 28, weight: .bold))
                                .foregroundColor(.white)
                                .offset(x: playback.isPlaying ? 0 : 2)
                        }
                        .shadow(color: theme.accentPrimary.opacity(0.5), radius: 16, x: 0, y: 6)
                    }
                    
                    // Next
                    Button(action: { playback.nextTrack() }) {
                        Image(systemName: "forward.fill")
                            .font(.system(size: 26, weight: .semibold))
                            .foregroundColor(theme.textPrimary)
                    }
                    
                    // Repeat
                    Button(action: { playback.toggleRepeat() }) {
                        Image(systemName: playback.repeatState.iconName)
                            .font(.system(size: 20, weight: .semibold))
                            .foregroundColor(playback.repeatState != .off ? theme.accentPrimary : theme.textDim)
                    }
                }
                .padding(.horizontal, 24)
                
                // Spectrum Visualizer
                if playback.settings.visualizerEnabled {
                    HStack(alignment: .bottom, spacing: 3) {
                        ForEach(0..<playback.fftMagnitudes.count, id: \.self) { i in
                            let mag = playback.isPlaying ? playback.fftMagnitudes[i] : 0.05
                            RoundedRectangle(cornerRadius: 2)
                                .fill(theme.accentGradient)
                                .frame(width: 4, height: CGFloat(mag * 32.0 + 3.0))
                                .animation(.easeOut(duration: 0.08), value: mag)
                        }
                    }
                    .frame(height: 36)
                    .padding(.horizontal, 28)
                }
                
                // Bottom Tools (AirPlay / Queue)
                HStack(spacing: 36) {
                    AirPlayRoutePicker()
                        .frame(width: 32, height: 32)
                    
                    Button(action: {
                        playback.showQueueSheet = true
                    }) {
                        HStack(spacing: 6) {
                            Image(systemName: "list.bullet")
                                .font(.system(size: 16, weight: .semibold))
                            if !playback.queue.isEmpty {
                                Text("\(playback.queue.count)")
                                    .font(.system(size: 12, weight: .bold))
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 1)
                                    .background(Capsule().fill(theme.accentPrimary))
                                    .foregroundColor(.white)
                            }
                        }
                        .foregroundColor(theme.textSecondary)
                    }
                }
                .padding(.bottom, 24)
            }
        }
        .sheet(isPresented: $playback.showEqualizerSheet) {
            EqualizerView()
        }
        .sheet(isPresented: $playback.showQueueSheet) {
            QueueView()
        }
    }
    
    private func formatTime(_ seconds: Double) -> String {
        guard !seconds.isNaN && seconds >= 0 else { return "0:00" }
        let mins = Int(seconds) / 60
        let secs = Int(seconds) % 60
        return String(format: "%d:%02d", mins, secs)
    }
}

// MARK: - AirPlay Route Picker Representable
public struct AirPlayRoutePicker: UIViewRepresentable {
    public func makeUIView(context: Context) -> AVRoutePickerView {
        let picker = AVRoutePickerView()
        picker.tintColor = .systemGray
        picker.activeTintColor = .white
        return picker
    }
    
    public func updateUIView(_ uiView: AVRoutePickerView, context: Context) {}
}
