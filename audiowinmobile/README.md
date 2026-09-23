# AudioWin Mobile (iOS App)

A modern, offline-first music player for iPhone and iPad, designed to pair with the **AudioWin 2.0** desktop experience.

Built with **SwiftUI**, **AVFoundation (AVAudioEngine)**, and **MPNowPlayingInfoCenter**.

---

## Features

### 🎧 Audio & Playback Engine
- **Hardware-accelerated Audio Engine**: Powered by `AVAudioEngine`, `AVAudioPlayerNode`, and `AVAudioUnitEQ`.
- **10-Band Graphic Equalizer**: Precise frequency sliders at `32Hz`, `64Hz`, `125Hz`, `250Hz`, `500Hz`, `1kHz`, `2kHz`, `4kHz`, `8kHz`, and `16kHz` with real-time DSP and custom presets (*Flat, Bass Boost, Bass Reducer, Vocal Booster, Electronic, Rock, Acoustic, Treble Boost, Podcast*).
- **Background Audio Playback**: Configured with `AVAudioSessionCategoryPlayback` so playback never pauses when the screen locks or when switching apps.
- **Lock Screen & Control Center Integration**: Live scrubber, album art, volume, track info, and playback controls synchronized through `MPNowPlayingInfoCenter` and `MPRemoteCommandCenter`.
- **Real-Time Spectrum Visualizer**: Fast Fourier Transform (FFT) tap calculating live audio frequency magnitudes.

### 📱 User Interface & Design System
- **iOS Human Interface Guidelines**: Native gestures, fluid animations, swipe actions, and bottom tab navigation.
- **Floating Glassmorphic Mini-Player**: Docked mini-player with a live progress bar, quick play/pause, like toggle, and swipe-to-skip gestures.
- **Expandable Now Playing Sheet**: Full-screen Apple Music-style player with high-resolution artwork, live scrubber, repeat/shuffle modes, equalizer shortcut, and AirPlay picker.
- **6 Signature Accent Colors**:
  - 🟣 **Violet** (`#7C5CFF`)
  - 🔵 **Ocean** (`#2E9BFF`)
  - 🔥 **Ember** (`#FF7A45`)
  - 🟢 **Mint** (`#22C88A`)
  - 🌸 **Rose** (`#FF5C8A`)
  - 🟡 **Gold** (`#F5A524`)
- **OLED Dark Mode & Light Mode**: Tailored glassmorphism palettes.

### 🔗 Link Import & Local Matching
- **Zero-Setup Link Import**: Paste YouTube or Spotify links to pull real track titles, artists, and artwork via public oEmbed endpoints.
- **Playlist & Album Unlocking**: Add your free YouTube Data API key and Spotify Client credentials in Settings to import full playlists and albums in one paste.
- **Auto-Match to Local Files**: Automatically fuzzy-matches imported track metadata against your local music library using Levenshtein distance matching.

### 📁 File Management & Storage
- **iOS Files & iCloud Importer**: Import `.mp3`, `.wav`, `.flac`, `.m4a`, `.aac`, `.ogg` files directly from the Files app or iCloud Drive.
- **Sandboxed Storage**: Automatically reads ID3 metadata and embedded cover art, storing library state in `audiowin_data.json`, `audiowin_settings.json`, and `audiowin_stats.json`.

---

## Project Structure

```
audiowinmobile/
├── Package.swift                    # Swift Package Manifest
├── README.md                        # Documentation & Build Guide
└── AudioWinMobile/
    ├── AudioWinMobileApp.swift      # App entry point & AVAudioSession setup
    ├── Info.plist                   # iOS permissions (Audio background mode, File sharing)
    ├── Audio/
    │   ├── AudioEngine.swift        # AVAudioEngine + 10-band hardware EQ + FFT Tap
    │   └── NowPlayingManager.swift  # MPNowPlayingInfoCenter & MPRemoteCommandCenter
    ├── Models/
    │   └── Models.swift             # Codable Track, Playlist, Settings, Stats & EqPreset
    ├── Services/
    │   ├── StorageManager.swift     # JSON Sandboxed Document Persistence
    │   ├── DocumentPickerManager.swift # iOS Files & iCloud Audio/ID3 Importer
    │   ├── LinkImportService.swift  # YouTube & Spotify oEmbed/API Resolver
    │   └── LibraryMatcher.swift     # Levenshtein Fuzzy Matching Engine
    ├── Managers/
    │   ├── PlaybackManager.swift    # Central Observable Playback & Queue Controller
    │   └── ThemeManager.swift       # 6 Accent Color Themes & Glassmorphic Tokens
    ├── Views/
    │   ├── MainTabView.swift        # Main Tab container & docked MiniPlayer
    │   ├── MiniPlayerView.swift     # Floating Glass Mini-Player
    │   ├── NowPlayingSheet.swift    # Fullscreen Now Playing modal
    │   ├── EqualizerView.swift      # 10-Band Graphic EQ Sliders & Presets
    │   ├── QueueView.swift          # Reorderable Playback Queue
    │   ├── LibraryView.swift        # Searchable, sortable audio track library
    │   ├── PlaylistsView.swift      # Custom playlists & Liked Songs
    │   ├── LinkImportView.swift     # YouTube & Spotify Link Parser UI
    │   └── SettingsView.swift       # Accent picker, audio options, API keys & stats
    └── Resources/
        └── README.md
```

---

## How to Build and Run on iOS

### Option 1: Open in Xcode on macOS
1. Copy or clone the `audiowinmobile` directory to a Mac.
2. Launch **Xcode** and select **File → Open...**, then choose the `audiowinmobile` folder (or `Package.swift`).
3. Select your target device (e.g. **iPhone 16 Pro Simulator** or your connected physical iPhone).
4. Press **Cmd + R** to build and run.

### Option 2: Sideload onto a Physical iPhone (Without a Paid Developer Account)
1. Open the project in Xcode with your iPhone connected via USB/Wi-Fi.
2. In Xcode project settings, go to **Signing & Capabilities**.
3. Select your personal Apple ID under **Team**.
4. Click **Run** to install the app on your iPhone.
5. On your iPhone, go to **Settings → General → VPN & Device Management** and trust your developer certificate.

### Option 3: Automated CI/CD (GitHub Actions)
You can set up a GitHub Actions workflow with a `macos-latest` runner to compile the `.ipa` automatically on every commit.
