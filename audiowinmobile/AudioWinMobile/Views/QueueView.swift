import SwiftUI

public struct QueueView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    @Environment(\.dismiss) var dismiss
    
    public var body: some View {
        NavigationView {
            ZStack {
                theme.background.ignoresSafeArea()
                
                VStack(spacing: 0) {
                    if playback.currentTrack == nil && playback.queue.isEmpty {
                        VStack(spacing: 16) {
                            Image(systemName: "list.bullet.rectangle.portrait")
                                .font(.system(size: 48))
                                .foregroundColor(theme.textDim)
                            Text("Queue is Empty")
                                .font(.system(size: 18, weight: .bold))
                                .foregroundColor(theme.textPrimary)
                            Text("Play a track or add songs to Up Next")
                                .font(.system(size: 14))
                                .foregroundColor(theme.textSecondary)
                        }
                        .frame(maxHeight: .infinity)
                    } else {
                        List {
                            // Current Playing Track
                            if let current = playback.currentTrack {
                                Section(header: Text("NOW PLAYING").font(.system(size: 11, weight: .bold)).foregroundColor(theme.textDim)) {
                                    TrackRowView(track: current, isCurrent: true)
                                }
                                .listRowBackground(theme.surfaceElevated.opacity(0.8))
                            }
                            
                            // Up Next Queue
                            if !playback.queue.isEmpty {
                                Section(header:
                                    HStack {
                                        Text("UP NEXT (\(playback.queue.count))")
                                            .font(.system(size: 11, weight: .bold))
                                            .foregroundColor(theme.textDim)
                                        Spacer()
                                        Button("Clear") {
                                            playback.clearQueue()
                                        }
                                        .font(.system(size: 12, weight: .bold))
                                        .foregroundColor(.red)
                                    }
                                ) {
                                    ForEach(Array(playback.queue.enumerated()), id: \.element.id) { index, track in
                                        TrackRowView(track: track, isCurrent: false)
                                            .contentShape(Rectangle())
                                            .onTapGesture {
                                                playback.removeFromQueue(at: index)
                                                playback.playTrack(track)
                                            }
                                    }
                                    .onMove { source, destination in
                                        playback.moveQueueItem(fromOffsets: source, toOffset: destination)
                                    }
                                    .onDelete { indexSet in
                                        for index in indexSet {
                                            playback.removeFromQueue(at: index)
                                        }
                                    }
                                }
                                .listRowBackground(theme.surface.opacity(0.7))
                            }
                        }
                        .listStyle(.insetGrouped)
                        .scrollContentBackground(.hidden)
                    }
                }
            }
            .navigationTitle("Playback Queue")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    EditButton()
                        .foregroundColor(theme.accentPrimary)
                }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Done") {
                        dismiss()
                    }
                    .font(.system(size: 16, weight: .bold))
                    .foregroundColor(theme.accentPrimary)
                }
            }
        }
    }
}
