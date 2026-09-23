import SwiftUI
import AVFoundation

@main
struct AudioWinMobileApp: App {
    @StateObject private var playbackManager = PlaybackManager.shared
    @StateObject private var themeManager = ThemeManager.shared
    
    init() {
        // Activate background audio session on launch
        do {
            try AVAudioSession.sharedInstance().setCategory(
                .playback,
                mode: .default,
                options: [.allowBluetooth, .allowBluetoothA2DP, .defaultToSpeaker]
            )
            try AVAudioSession.sharedInstance().setActive(true)
        } catch {
            print("AudioWinMobileApp: Failed to initialize AVAudioSession: \(error.localizedDescription)")
        }
    }
    
    var body: some Scene {
        WindowGroup {
            MainTabView()
                .environmentObject(playbackManager)
                .environmentObject(themeManager)
        }
    }
}
