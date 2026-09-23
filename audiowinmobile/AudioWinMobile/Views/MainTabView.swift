import SwiftUI

public struct MainTabView: View {
    @ObservedObject var playback = PlaybackManager.shared
    @ObservedObject var theme = ThemeManager.shared
    
    @State private var selectedTab: Int = 0
    
    public init() {
        // Configure iOS Navigation Bar and Tab Bar appearances
        let navBarAppearance = UINavigationBarAppearance()
        navBarAppearance.configureWithTransparentBackground()
        navBarAppearance.titleTextAttributes = [.foregroundColor: UIColor.white]
        navBarAppearance.largeTitleTextAttributes = [.foregroundColor: UIColor.white]
        UINavigationBar.appearance().standardAppearance = navBarAppearance
        UINavigationBar.appearance().scrollEdgeAppearance = navBarAppearance
    }
    
    public var body: some View {
        ZStack(alignment: .bottom) {
            TabView(selection: $selectedTab) {
                LibraryView()
                    .tabItem {
                        Label("Library", systemImage: "music.note.house.fill")
                    }
                    .tag(0)
                
                PlaylistsView()
                    .tabItem {
                        Label("Playlists", systemImage: "music.note.list")
                    }
                    .tag(1)
                
                LinkImportView()
                    .tabItem {
                        Label("Link Import", systemImage: "link.badge.plus")
                    }
                    .tag(2)
                
                SettingsView()
                    .tabItem {
                        Label("Settings", systemImage: "gearshape.fill")
                    }
                    .tag(3)
            }
            .accentColor(theme.accentPrimary)
            
            // Floating MiniPlayer docked above the Tab Bar
            MiniPlayerView()
                .padding(.bottom, 50)
        }
        .preferredColorScheme(theme.isDarkMode ? .dark : .light)
        .fullScreenCover(isPresented: $playback.showNowPlayingSheet) {
            NowPlayingSheet()
        }
    }
}
