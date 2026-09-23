using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using Windows.Media;
using Windows.Storage.Streams;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace AudioWin
{
    public partial class MainWindow : Window
    {
        // ---- core services ------------------------------------------------
        private AudioEngine audioEngine;
        private DiscordRpcService discordRpc;
        private SaveScheduler saver;
        private DispatcherTimer uiTimer;

        // ---- data ---------------------------------------------------------
        private readonly ObservableCollection<Playlist> playlists = new ObservableCollection<Playlist>();
        private readonly ObservableCollection<Track> queue = new ObservableCollection<Track>();
        private AppStats stats = new AppStats();
        private PlaybackSettings settings = new PlaybackSettings();

        /// <summary>The playlist currently on screen. Browsing does not change playback.</summary>
        private Playlist selectedPlaylist;

        /// <summary>The playlist the current track is being played from.</summary>
        private Playlist playbackPlaylist;

        private Track currentTrack;

        // ---- transport state ----------------------------------------------
        private bool isPlaying;
        private bool isUserSeeking;
        private bool suppressSliderEvent;
        private bool suppressToggleEvents;
        private RepeatState repeatState = RepeatState.Off;
        private bool isShuffleOn;
        private readonly List<Track> shuffleOrder = new List<Track>();
        private int shufflePos = -1;
        private readonly Random rng = new Random();

        // ---- visualiser ---------------------------------------------------
        private readonly Rectangle[] visualizerBars = new Rectangle[22];
        private readonly float[] barHeights = new float[22];
        private float[] latestFft;
        private bool renderHooked;

        // ---- system integration --------------------------------------------
        private Windows.Media.Playback.MediaPlayer backgroundMediaPlayer;
        private SystemMediaTransportControls smtc;

        // ---- misc ----------------------------------------------------------
        private FrameworkElement currentView;
        private Playlist contextPlaylist;   // right-clicked playlist
        private Track contextTrack;         // right-clicked track
        private Track renameTrackTarget;
        private Playlist renamePlaylistTarget;
        private int dragDepth;
        private bool openingPlaylist;
        private bool shuttingDown;

        public MainWindow()
        {
            InitializeComponent();

            LoadData();
            InitializeAudio();
            InitializeVisualizer();
            ApplySettingsToUi();
            RefreshPlaylistBindings();
            SetupSystemMediaControls();
            InitializeDiscord();

            saver = new SaveScheduler(SaveAll);

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;

            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            currentView = HomeView;
            UpdateHomeGreeting();
            DrawIdleVisualizer();

            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                Width = Math.Max(MinWidth, settings.WindowWidth);
                Height = Math.Max(MinHeight, settings.WindowHeight);
            }
            UpdateMaximizeIcon();
        }

        // ==================================================================
        // Startup
        // ==================================================================

        private void LoadData()
        {
            var loaded = StorageManager.Load();
            if (loaded != null)
                foreach (var p in loaded) playlists.Add(p);

            stats = StorageManager.LoadStats();
            settings = StorageManager.LoadSettings();

            repeatState = settings.RepeatState;
            isShuffleOn = settings.IsShuffleOn;
        }

        private void InitializeAudio()
        {
            try
            {
                audioEngine = new AudioEngine();
                audioEngine.FftDataAvailable += OnFftData;
                audioEngine.PlaybackEnded += OnPlaybackEnded;
                audioEngine.Volume = (float)(settings.Volume / 100.0);
                audioEngine.IsMuted = settings.IsMuted;
                audioEngine.IsMono = settings.MonoAudio;
                audioEngine.IsNormalized = settings.Normalization;
                for (int i = 0; i < 10; i++) audioEngine.SetEqBand(i, settings.EqGains[i]);
            }
            catch (Exception ex)
            {
                StorageManager.Log("Audio engine failed: " + ex);
                MessageBox.Show("The audio engine could not start:\n\n" + ex.Message,
                                "AudioWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            uiTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            uiTimer.Tick += UiTimer_Tick;
            uiTimer.Start();
        }

        private void InitializeDiscord()
        {
            discordRpc = new DiscordRpcService
            {
                Enabled = settings.DiscordEnabled,
                ShowSourceButton = settings.DiscordShowButton
            };
            if (settings.DiscordEnabled)
            {
                discordRpc.Initialize();
                discordRpc.SetIdle();
            }
        }

        // ==================================================================
        // Window chrome
        // ==================================================================

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                // Without this a borderless maximised window covers the taskbar.
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO();
                    GetMonitorInfo(monitor, info);
                    var work = info.rcWork;
                    var full = info.rcMonitor;
                    mmi.ptMaxPosition.x = Math.Abs(work.left - full.left);
                    mmi.ptMaxPosition.y = Math.Abs(work.top - full.top);
                    mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                    mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                    mmi.ptMinTrackSize.x = 1040;
                    mmi.ptMinTrackSize.y = 640;
                }
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MONITORINFO
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);
        private const int MONITOR_DEFAULTTONEAREST = 2;

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Window_StateChanged(object sender, EventArgs e)
        {
            // Aero Snap and the double-click caption gesture also change the state,
            // so the icon is refreshed here rather than inside the button handler.
            UpdateMaximizeIcon();
            if (settings != null && WindowState != WindowState.Minimized)
            {
                settings.WindowMaximized = WindowState == WindowState.Maximized;
                saver?.Request();
            }
        }

        private void UpdateMaximizeIcon()
        {
            if (MaximizeIcon == null) return;
            string key = WindowState == WindowState.Maximized ? "I.WinRestore" : "I.WinMaximize";
            if (TryFindResource(key) is Geometry g) MaximizeIcon.Data = g;
            if (BtnMaximize != null)
                BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void OnThemeChanged()
        {
            if (!isPlaying) DrawIdleVisualizer(); else RecolourVisualizer();
        }

        // ==================================================================
        // View switching
        // ==================================================================

        private void ShowView(FrameworkElement view)
        {
            if (view == null) return;

            HomeView.Visibility = Visibility.Collapsed;
            StatsView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            DetailView.Visibility = Visibility.Collapsed;

            currentView = view;
            view.Opacity = 1;
            view.RenderTransform = null;
            view.Visibility = Visibility.Visible;
        }

        private void NavHome_Click(object sender, RoutedEventArgs e)
        {
            UpdateHomeGreeting();
            ShowView(HomeView);
        }

        private void NavStats_Click(object sender, RoutedEventArgs e)
        {
            RefreshStats();
            ShowView(StatsView);
        }

        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView(SettingsView);

        private void NavLiked_Click(object sender, RoutedEventArgs e) => OpenLikedSongs();

        private void ClearNavSelection()
        {
            // A playlist is not one of the nav destinations, so nothing should stay lit.
            suppressToggleEvents = true;
            NavHome.IsChecked = false;
            NavLiked.IsChecked = false;
            NavStats.IsChecked = false;
            NavSettings.IsChecked = false;
            suppressToggleEvents = false;
        }

        // ==================================================================
        // Toasts
        // ==================================================================

        private void Toast(string message, bool isError = false)
        {
            if (ToastHost == null || string.IsNullOrWhiteSpace(message)) return;

            var card = new Border
            {
                Background = ThemeManager.Brush("B.Elevated"),
                BorderBrush = isError ? ThemeManager.Brush("B.Danger") : ThemeManager.Brush("B.Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 12, 18, 12),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 1,
                Child = new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    MaxWidth = 460,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeManager.Brush("B.Text")
                }
            };

            ToastHost.Children.Add(card);

            var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 3.4) };
            life.Tick += (s, e) =>
            {
                life.Stop();
                ToastHost.Children.Remove(card);
            };
            life.Start();

            // Never let toasts stack off the top of the window.
            while (ToastHost.Children.Count > 4) ToastHost.Children.RemoveAt(0);
        }

        // ==================================================================
        // Playlists
        // ==================================================================

        private void RefreshPlaylistBindings()
        {
            string query = TxtSearch?.Text?.Trim();
            IEnumerable<Playlist> source = playlists;

            if (!string.IsNullOrWhiteSpace(query))
                source = playlists.Where(p => (p.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = source.ToList();
            PlaylistsList.ItemsSource = list;
            PlaylistsItems.ItemsSource = list;

            if (EmptyLibraryState != null)
                EmptyLibraryState.Visibility = playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var p in playlists) p.NotifySubtitle();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshPlaylistBindings();

        private void UpdateHomeGreeting()
        {
            int hour = DateTime.Now.Hour;
            string greeting = hour < 5 ? "Still up?"
                            : hour < 12 ? "Good morning"
                            : hour < 18 ? "Good afternoon"
                            : "Good evening";

            TxtHomeGreeting.Text = greeting;

            int tracks = playlists.Sum(p => p.Tracks.Count);
            TxtHomeSub.Text = playlists.Count == 0
                ? "Let's get some music in here."
                : $"{playlists.Count} playlist{(playlists.Count == 1 ? "" : "s")} · {tracks} track{(tracks == 1 ? "" : "s")}";
        }

        private void PlaylistCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Playlist p)
            {
                OpenPlaylist(p);
                if (e.ClickCount == 2 && p.Tracks.Count > 0) StartPlaylist(p);
            }
        }

        private void PlaylistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaylistsList.SelectedItem is Playlist p) OpenPlaylist(p);
        }

        private void PlaylistsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Right-click has to select the row too, otherwise the context menu acts
            // on whatever was left-clicked last. That is how the old build could
            // delete a playlist you were not even pointing at.
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            contextPlaylist = item?.DataContext as Playlist;
            if (item != null) item.IsSelected = true;
        }

        private void OpenPlaylist(Playlist playlist)
        {
            if (playlist == null || openingPlaylist) return;
            openingPlaylist = true;
            try
            {
                OpenPlaylistCore(playlist);
            }
            finally
            {
                openingPlaylist = false;
            }
        }

        private void OpenPlaylistCore(Playlist playlist)
        {
            selectedPlaylist = playlist;
            ClearNavSelection();

            // Keep the sidebar highlight in step when a playlist is opened from a
            // home-screen card rather than from the list itself.
            if (!ReferenceEquals(PlaylistsList.SelectedItem, playlist))
                PlaylistsList.SelectedItem = playlist;

            DetailKind.Text = playlist.IsSystem ? "COLLECTION" : "PLAYLIST";
            DetailTitle.Text = playlist.Name;
            DetailDefaultText.Text = playlist.Initial;

            LoadCoverImage(DetailCoverImage, playlist.ImagePath);
            DetailDefaultText.Visibility = DetailCoverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

            BtnAddAudioTop.Visibility = playlist.IsSystem ? Visibility.Collapsed : Visibility.Visible;
            BtnImportLinkDetail.Visibility = playlist.IsSystem ? Visibility.Collapsed : Visibility.Visible;
            BtnMatchFiles.Visibility = playlist.IsSystem ? Visibility.Collapsed : Visibility.Visible;
            BtnDownloadPlaylist.Visibility = Visibility.Visible;

            if (TxtSongSearch.Text.Length > 0) TxtSongSearch.Text = "";
            if (ComboSort.SelectedIndex != 0) ComboSort.SelectedIndex = 0;

            RefreshSongList();
            UpdateDetailSummary();
            ShowView(DetailView);
            ScrollSongsToTop();
        }

        private void OpenLikedSongs()
        {
            // Holds the real Track objects rather than copies, so liking or editing
            // here updates the underlying playlists immediately.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liked = new Playlist { Name = "Liked Songs", Icon = "♥", IsSystem = true };

            foreach (var p in playlists)
                foreach (var t in p.Tracks)
                {
                    if (!t.IsLiked) continue;
                    string key = t.FilePath ?? t.SourceUrl ?? t.Id;
                    if (!seen.Add(key)) continue;
                    liked.Tracks.Add(t);
                }

            int i = 1;
            foreach (var t in liked.Tracks) t.Index = i++;

            selectedPlaylist = liked;

            DetailKind.Text = "COLLECTION";
            DetailTitle.Text = "Liked Songs";
            DetailDefaultText.Text = "♥";
            DetailCoverImage.Source = null;
            DetailDefaultText.Visibility = Visibility.Visible;

            BtnAddAudioTop.Visibility = Visibility.Collapsed;
            BtnImportLinkDetail.Visibility = Visibility.Collapsed;
            BtnMatchFiles.Visibility = Visibility.Collapsed;
            BtnDownloadPlaylist.Visibility = Visibility.Visible;

            TxtSongSearch.Text = "";
            if (ComboSort.SelectedIndex != 0) ComboSort.SelectedIndex = 0;

            PlaylistsList.SelectedItem = null;
            RefreshSongList();
            UpdateDetailSummary();
            ShowView(DetailView);
            ScrollSongsToTop();
        }

        private void UpdateDetailSummary()
        {
            if (selectedPlaylist == null) return;

            int count = selectedPlaylist.Tracks.Count;
            double seconds = selectedPlaylist.Tracks.Sum(t => t.DurationSeconds);
            int missing = selectedPlaylist.Tracks.Count(t => t.NeedsFile);

            var parts = new List<string> { count == 1 ? "1 track" : count + " tracks" };
            if (seconds > 0) parts.Add(DescribeLength(seconds));
            if (missing > 0) parts.Add(missing + " waiting for a local file");

            DetailStats.Text = string.Join("  ·  ", parts);
        }

        private static string DescribeLength(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return $"about {(int)t.TotalHours} hr {t.Minutes} min";
            return $"about {Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
        }

        private void RefreshSongList()
        {
            if (selectedPlaylist == null)
            {
                SongsGrid.ItemsSource = null;
                return;
            }

            int i = 1;
            foreach (var t in selectedPlaylist.Tracks) t.Index = i++;

            IEnumerable<Track> view = selectedPlaylist.Tracks;

            string query = TxtSongSearch?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(query))
                view = view.Where(t =>
                    (t.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (t.Artist ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (t.Album ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            view = ApplySort(view);

            SongsGrid.ItemsSource = view.ToList();

            bool empty = selectedPlaylist.Tracks.Count == 0;
            EmptyPlaylistState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            SongsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            if (empty)
            {
                if (selectedPlaylist.IsSystem)
                {
                    EmptyStateTitle.Text = "No liked songs yet";
                    EmptyStateSub.Text = "Tap the heart on any track and it shows up here";
                    EmptyPlaylistState.Cursor = Cursors.Arrow;
                }
                else
                {
                    EmptyStateTitle.Text = "Add some music";
                    EmptyStateSub.Text = "Click to browse, drag files in, or paste a link";
                    EmptyPlaylistState.Cursor = Cursors.Hand;
                }
            }

            MarkNowPlaying();
        }

        private IEnumerable<Track> ApplySort(IEnumerable<Track> view)
        {
            string mode = (ComboSort?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Custom order";
            return mode switch
            {
                "Title" => view.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
                "Artist" => view.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
                                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
                // Sorting the display string put "10:00" before "9:00"; sort the number.
                "Duration" => view.OrderBy(t => t.DurationSeconds),
                "Recently added" => view.OrderByDescending(t => t.AddedUtc),
                "Most played" => view.OrderByDescending(t => t.PlayCount).ThenBy(t => t.Index),
                _ => view
            };
        }

        private void TxtSongSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshSongList();
        private void ComboSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            RefreshSongList();
        }

        private void ScrollSongsToTop()
        {
            try
            {
                var sv = FindDescendant<ScrollViewer>(SongsGrid);
                sv?.ScrollToTop();
            }
            catch { }
        }

        private void LoadCoverImage(Image target, string path)
        {
            if (target == null) return;
            target.Source = null;
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    target.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
                    return;
                }

                if (!File.Exists(path)) return;

                // Loaded through a stream with OnLoad caching so the file is not
                // left locked - the old code held a handle on every cover it showed.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                target.Source = bmp;
            }
            catch
            {
                target.Source = null;
            }
        }

        // ---- playlist commands -------------------------------------------

        private void BtnCreatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            TxtPlaylistName.Text = "";
            ShowModal(ModalCreate);
            TxtPlaylistName.Focus();
        }

        private void TxtPlaylistName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { BtnConfirmCreatePlaylist_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideModals(); e.Handled = true; }
        }

        private void BtnConfirmCreatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtPlaylistName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                Toast("Give the playlist a name first.", true);
                return;
            }

            var p = new Playlist { Name = name, Icon = name.Substring(0, 1).ToUpperInvariant() };
            playlists.Add(p);
            SaveAll();
            RefreshPlaylistBindings();
            UpdateHomeGreeting();
            HideModals();
            OpenPlaylist(p);
            Toast("Created \"" + name + "\"");
        }

        private void MenuRenamePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var target = contextPlaylist ?? PlaylistsList.SelectedItem as Playlist;
            if (target == null) return;

            renamePlaylistTarget = target;
            renameTrackTarget = null;
            TxtRenameHeading.Text = "Rename playlist";
            TxtRenameSub.Text = "Choose a new name for this playlist.";
            TxtRenameValue.Text = target.Name;
            Ui.SetWatermark(TxtRenameValue, "Playlist name");
            TxtRenameValue2.Visibility = Visibility.Collapsed;
            ShowModal(ModalRename);
            TxtRenameValue.SelectAll();
            TxtRenameValue.Focus();
        }

        private void TxtRenameValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { BtnConfirmRename_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideModals(); e.Handled = true; }
        }

        private void BtnConfirmRename_Click(object sender, RoutedEventArgs e)
        {
            string value = TxtRenameValue.Text?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                Toast("That can't be blank.", true);
                return;
            }

            if (renamePlaylistTarget != null)
            {
                renamePlaylistTarget.Name = value;
                renamePlaylistTarget.Icon = value.Substring(0, 1).ToUpperInvariant();
                if (selectedPlaylist == renamePlaylistTarget) DetailTitle.Text = value;
                RefreshPlaylistBindings();
            }
            else if (renameTrackTarget != null)
            {
                renameTrackTarget.Title = value;
                string artist = TxtRenameValue2.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(artist)) renameTrackTarget.Artist = artist;
                if (currentTrack != null && currentTrack.Matches(renameTrackTarget)) UpdateNowPlayingText(renameTrackTarget);
                RefreshSongList();
            }

            renamePlaylistTarget = null;
            renameTrackTarget = null;
            SaveAll();
            HideModals();
        }

        private void MenuChangeCover_Click(object sender, RoutedEventArgs e)
        {
            var target = contextPlaylist ?? PlaylistsList.SelectedItem as Playlist;
            PickCoverFor(target);
        }

        private void DetailCover_Click(object sender, MouseButtonEventArgs e)
        {
            if (selectedPlaylist == null || selectedPlaylist.IsSystem) return;
            PickCoverFor(selectedPlaylist);
        }

        private void PickCoverFor(Playlist target)
        {
            if (target == null || target.IsSystem) return;

            var dlg = new OpenFileDialog
            {
                Title = "Choose a cover image",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            target.ImagePath = dlg.FileName;
            if (selectedPlaylist == target)
            {
                LoadCoverImage(DetailCoverImage, target.ImagePath);
                DetailDefaultText.Visibility = DetailCoverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
            }
            SaveAll();
            RefreshPlaylistBindings();
        }

        private void MenuDeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var target = contextPlaylist ?? PlaylistsList.SelectedItem as Playlist;
            if (target == null) return;

            var confirm = MessageBox.Show(
                $"Delete \"{target.Name}\"?\n\nThis removes the playlist from AudioWin. Your audio files are not touched.",
                "AudioWin", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            if (playbackPlaylist == target)
            {
                StopPlayback();
                playbackPlaylist = null;
            }

            playlists.Remove(target);
            contextPlaylist = null;

            if (selectedPlaylist == target)
            {
                selectedPlaylist = null;
                NavHome.IsChecked = true;
                UpdateHomeGreeting();
                ShowView(HomeView);
            }

            SaveAll();
            RefreshPlaylistBindings();
            UpdateHomeGreeting();
            Toast("Deleted \"" + target.Name + "\"");
        }

        private void MenuPlayPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var target = contextPlaylist ?? PlaylistsList.SelectedItem as Playlist;
            if (target != null) StartPlaylist(target);
        }

        private void BtnDownloadPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (selectedPlaylist == null) return;
            _ = DownloadPlaylistTracksAsync(selectedPlaylist);
        }

        private void MenuDownloadPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var target = contextPlaylist ?? PlaylistsList.SelectedItem as Playlist;
            if (target != null) _ = DownloadPlaylistTracksAsync(target);
        }

        private async Task DownloadPlaylistTracksAsync(Playlist playlist)
        {
            if (playlist == null || playlist.Tracks.Count == 0)
            {
                Toast("This playlist has no tracks to download.");
                return;
            }

            var targets = playlist.Tracks.Where(t => t.IsLink || !t.IsPlayable || t.NeedsFile).ToList();
            if (targets.Count == 0)
            {
                Toast($"All tracks in \"{playlist.Name}\" are already downloaded.");
                return;
            }

            SetBusy(true, $"Preparing to download {targets.Count} tracks from \"{playlist.Name}\"…");
            int total = targets.Count;
            int succeeded = 0;
            int failed = 0;

            await Task.Run(async () =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var track = targets[i];
                    int currentNumber = i + 1;

                    Dispatcher.Invoke(() => SetBusy(true, $"[{currentNumber}/{total}] Downloading: {track.Title}…"));

                    try
                    {
                        string downloadedPath = await AudioStreamResolver.ResolveAndDownloadAsync(
                            track,
                            status => Dispatcher.Invoke(() => SetBusy(true, $"[{currentNumber}/{total}] {status}"))
                        );

                        if (!string.IsNullOrEmpty(downloadedPath) && File.Exists(downloadedPath))
                        {
                            track.FilePath = downloadedPath;
                            if (track.DurationSeconds <= 0)
                            {
                                try
                                {
                                    using var reader = new AudioFileReader(downloadedPath);
                                    track.DurationSeconds = reader.TotalTime.TotalSeconds;
                                    track.Duration = LinkImport.Format(track.DurationSeconds);
                                }
                                catch { }
                            }
                            track.RefreshComputed();
                            succeeded++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }

                    if (currentNumber % 2 == 0 || currentNumber == total)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (selectedPlaylist == playlist)
                            {
                                RefreshSongList();
                                UpdateDetailSummary();
                            }
                        });
                    }
                }
            });

            SetBusy(false);
            SaveAll();
            if (selectedPlaylist == playlist)
            {
                RefreshSongList();
                UpdateDetailSummary();
            }

            if (failed == 0)
            {
                Toast($"Downloaded all {succeeded} track{(succeeded == 1 ? "" : "s")} in \"{playlist.Name}\"!");
            }
            else
            {
                Toast($"Downloaded {succeeded} of {total} tracks ({failed} failed).");
            }
        }

        // ==================================================================
        // Adding audio files
        // ==================================================================

        private void BtnAddAudio_Click(object sender, RoutedEventArgs e) => PromptAddFiles();
        private void EmptyStateAdd_Click(object sender, MouseButtonEventArgs e) => PromptAddFiles();

        private void PromptAddFiles()
        {
            if (selectedPlaylist == null || selectedPlaylist.IsSystem)
            {
                Toast("Open a playlist first, then add tracks to it.", true);
                return;
            }

            string filter = "Audio files|" +
                            string.Join(";", LibraryMatcher.AudioExtensions.Select(x => "*" + x)) +
                            "|All files (*.*)|*.*";

            var dlg = new OpenFileDialog { Filter = filter, Multiselect = true, Title = "Add audio files" };
            if (dlg.ShowDialog() == true) _ = AddFilesAsync(selectedPlaylist, dlg.FileNames);
        }

        private async Task AddFilesAsync(Playlist playlist, string[] paths)
        {
            if (playlist == null || playlist.IsSystem || paths == null || paths.Length == 0) return;

            // Expand any dropped folders.
            var files = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                        files.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories));
                    else
                        files.Add(p);
                }
                catch { }
            }

            var audio = files.Where(LibraryMatcher.IsAudioFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (audio.Count == 0)
            {
                Toast("None of those were audio files.", true);
                return;
            }

            var existing = new HashSet<string>(
                playlist.Tracks.Where(t => !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath),
                StringComparer.OrdinalIgnoreCase);

            audio = audio.Where(f => !existing.Contains(f)).ToList();
            if (audio.Count == 0)
            {
                Toast("Those tracks are already in this playlist.");
                return;
            }

            SetBusy(true, $"Reading {audio.Count} file{(audio.Count == 1 ? "" : "s")}…");

            // Tag reading and duration probing are slow, so they happen off the UI
            // thread; the old build blocked the dispatcher while it did this.
            var loaded = await Task.Run(() =>
            {
                var list = new List<Track>();
                foreach (var file in audio)
                {
                    try
                    {
                        if (!File.Exists(file)) continue;

                        var tags = TagReader.ReadWithArtwork(file, out string art);

                        double seconds = tags.LengthSeconds;
                        if (seconds <= 0)
                        {
                            try { using var reader = new AudioFileReader(file); seconds = reader.TotalTime.TotalSeconds; }
                            catch { }
                        }

                        list.Add(new Track
                        {
                            Title = !string.IsNullOrWhiteSpace(tags.Title)
                                        ? tags.Title
                                        : Path.GetFileNameWithoutExtension(file),
                            Artist = tags.Artist ?? "Unknown Artist",
                            Album = tags.Album,
                            FilePath = file,
                            ImagePath = art,
                            DurationSeconds = seconds,
                            Duration = LinkImport.Format(seconds),
                            Source = TrackSource.Local
                        });
                    }
                    catch { }
                }
                return list;
            });

            foreach (var t in loaded) playlist.Tracks.Add(t);

            SetBusy(false);
            SaveAll();
            RefreshPlaylistBindings();
            UpdateHomeGreeting();

            if (selectedPlaylist == playlist)
            {
                RefreshSongList();
                UpdateDetailSummary();
            }

            Toast($"Added {loaded.Count} track{(loaded.Count == 1 ? "" : "s")}");
        }

        private void SetBusy(bool busy, string message = null)
        {
            Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
            if (busy && message != null) Toast(message);
        }

        // ==================================================================
        // Playback
        // ==================================================================

        private void SongsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Track t)
                PlayFrom(selectedPlaylist, t);
        }

        private void SongsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            contextTrack = item?.DataContext as Track;
            if (item != null) item.IsSelected = true;
        }

        private void BtnDetailPlay_Click(object sender, RoutedEventArgs e)
        {
            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0) return;

            // If this playlist is already the one playing, act as play/pause.
            if (playbackPlaylist == selectedPlaylist && currentTrack != null)
            {
                TogglePlayPause();
                return;
            }

            StartPlaylist(selectedPlaylist);
        }

        private void BtnDetailShuffle_Click(object sender, RoutedEventArgs e)
        {
            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0) return;

            if (!isShuffleOn)
            {
                isShuffleOn = true;
                settings.IsShuffleOn = true;
                BtnShuffle.IsChecked = true;
                saver?.Request();
            }

            var pick = selectedPlaylist.Tracks[rng.Next(selectedPlaylist.Tracks.Count)];
            PlayFrom(selectedPlaylist, pick);
        }

        private void StartPlaylist(Playlist playlist)
        {
            if (playlist == null || playlist.Tracks.Count == 0) return;

            var first = isShuffleOn
                ? playlist.Tracks[rng.Next(playlist.Tracks.Count)]
                : playlist.Tracks[0];

            PlayFrom(playlist, first);
        }

        private void PlayFrom(Playlist playlist, Track track)
        {
            if (track == null) return;

            playbackPlaylist = playlist;
            BuildShuffleOrder(playlist, track);
            PlayTrack(track);
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (currentTrack != null) { TogglePlayPause(); return; }

            var list = selectedPlaylist ?? playlists.FirstOrDefault(p => p.Tracks.Count > 0);
            if (list != null && list.Tracks.Count > 0) StartPlaylist(list);
            else Toast("Nothing to play yet — add some music first.");
        }

        private void TogglePlayPause()
        {
            if (audioEngine == null || currentTrack == null) return;

            if (isPlaying)
            {
                audioEngine.Pause();
                SetPlayingState(false);
            }
            else
            {
                if (audioEngine.PlaybackState == PlaybackState.Paused && audioEngine.HasTrack)
                    audioEngine.Resume();
                else
                    PlayTrack(currentTrack, restart: true);

                SetPlayingState(true);
            }

            PushDiscordPresence();
        }

        private void PlayTrack(Track track, bool restart = false)
        {
            if (track == null || audioEngine == null) return;

            if (!track.IsPlayable)
            {
                HandleUnplayableTrack(track);
                return;
            }

            try
            {
                audioEngine.Play(track.FilePath);
            }
            catch (Exception ex)
            {
                StorageManager.Log("Playback failed for " + track.FilePath + ": " + ex);
                Toast("Couldn't play \"" + track.Title + "\": " + ex.Message, true);
                SetPlayingState(false);
                return;
            }

            currentTrack = track;
            SetPlayingState(true);

            track.PlayCount++;
            track.LastPlayed = DateTime.Now;
            stats.TotalSongsPlayed++;
            if (string.IsNullOrWhiteSpace(stats.FirstListenedSong)) stats.FirstListenedSong = track.Title;

            if (playbackPlaylist != null && !playbackPlaylist.IsSystem)
            {
                playbackPlaylist.PlayCount++;
                playbackPlaylist.LastPlayed = DateTime.Now;
            }

            // The reader knows the true length; the stored duration can be wrong for
            // files that were edited after being added.
            double total = audioEngine.TotalTime;
            if (total > 0 && Math.Abs(total - track.DurationSeconds) > 1.5)
            {
                track.DurationSeconds = total;
                track.Duration = LinkImport.Format(total);
            }

            UpdateNowPlayingText(track);
            LoadCoverImage(PlayerCoverImage, track.ImagePath);
            PlayerDefaultIcon.Visibility = PlayerCoverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

            BtnLikeCurrent.IsChecked = track.IsLiked;

            suppressSliderEvent = true;
            PlaybackSlider.Value = 0;
            suppressSliderEvent = false;
            TxtCurrentTime.Text = "0:00";
            TxtTotalTime.Text = track.Duration;

            MarkNowPlaying();
            UpdateSmtcMetadata(track);
            UpdateSmtcPlaybackStatus(true);
            PushDiscordPresence();
            UpdateQueuePanel();
            saver?.Request();
        }

        private async void HandleUnplayableTrack(Track track)
        {
            if (track.IsLink)
            {
                await ResolveAndPlayLinkAsync(track);
                return;
            }

            Toast("That file is missing: " + (track.FilePath ?? "unknown path"), true);
        }

        private async Task ResolveAndPlayLinkAsync(Track track)
        {
            SetBusy(true, "Buffering stream...");
            try
            {
                string downloadedPath = await AudioStreamResolver.ResolveAndDownloadAsync(
                    track,
                    status => Dispatcher.Invoke(() => SetBusy(true, status))
                );

                if (string.IsNullOrEmpty(downloadedPath) || !File.Exists(downloadedPath))
                {
                    Toast("Could not download audio stream.", true);
                    return;
                }

                track.FilePath = downloadedPath;
                track.RefreshComputed();
                
                PlayTrack(track, true);
            }
            catch (Exception ex)
            {
                Toast("Failed to stream track: " + ex.Message, true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetPlayingState(bool playing)
        {
            isPlaying = playing;
            UpdatePlayPauseIcons(playing);
            UpdateSmtcPlaybackStatus(playing);

            if (playing) HookRendering();
            else { UnhookRendering(); DrawIdleVisualizer(); }
        }

        private void UpdateNowPlayingText(Track track)
        {
            TxtNowPlayingTitle.Text = track?.Title ?? "Not playing";
            TxtNowPlayingArtist.Text = track?.Artist ?? "Choose a track";
            TxtTitleBarNowPlaying.Text = track == null ? "" : $"— {track.Title} · {track.Artist}";
        }

        private void UpdatePlayPauseIcons(bool playing)
        {
            var geo = TryFindResource(playing ? "I.Pause" : "I.Play") as Geometry;
            if (geo == null) return;

            BtnPlayPauseIcon.Data = geo;
            BtnPlayPauseIcon.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0);

            if (BtnDetailPlayIcon != null)
            {
                bool detailPlaying = playing && playbackPlaylist == selectedPlaylist;
                var detailGeo = TryFindResource(detailPlaying ? "I.Pause" : "I.Play") as Geometry;
                if (detailGeo != null)
                {
                    BtnDetailPlayIcon.Data = detailGeo;
                    BtnDetailPlayIcon.Margin = new Thickness(detailPlaying ? 0 : 2, 0, 0, 0);
                }
            }

            if (ThumbPlayPause != null)
            {
                ThumbPlayPause.ImageSource = TryFindResource(playing ? "TaskbarPauseIcon" : "TaskbarPlayIcon") as ImageSource;
                ThumbPlayPause.Description = playing ? "Pause" : "Play";
            }
        }

        private void MarkNowPlaying()
        {
            foreach (var p in playlists)
                foreach (var t in p.Tracks)
                {
                    bool live = currentTrack != null && t.Matches(currentTrack);
                    if (t.IsNowPlaying != live) t.IsNowPlaying = live;
                }

            // The Liked Songs view holds the same objects, so nothing extra is needed.
        }

        private void StopPlayback()
        {
            try { audioEngine?.Stop(); } catch { }
            currentTrack = null;
            SetPlayingState(false);

            UpdateNowPlayingText(null);
            PlayerCoverImage.Source = null;
            PlayerDefaultIcon.Visibility = Visibility.Visible;
            BtnLikeCurrent.IsChecked = false;

            suppressSliderEvent = true;
            PlaybackSlider.Value = 0;
            suppressSliderEvent = false;
            TxtCurrentTime.Text = "0:00";
            TxtTotalTime.Text = "0:00";

            MarkNowPlaying();
            UpdateSmtcPlaybackStatus(false);
            discordRpc?.SetIdle();
            UpdateQueuePanel();
        }

        // ---- track ordering ------------------------------------------------

        private void BuildShuffleOrder(Playlist playlist, Track startingWith)
        {
            shuffleOrder.Clear();
            shufflePos = -1;
            if (playlist == null) return;

            shuffleOrder.AddRange(playlist.Tracks);

            // Fisher-Yates. The old code picked a fresh random index every time,
            // so tracks repeated constantly and some never played at all.
            for (int i = shuffleOrder.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffleOrder[i], shuffleOrder[j]) = (shuffleOrder[j], shuffleOrder[i]);
            }

            if (startingWith != null)
            {
                int idx = shuffleOrder.FindIndex(t => t.Matches(startingWith));
                if (idx > 0)
                {
                    shuffleOrder.RemoveAt(idx);
                    shuffleOrder.Insert(0, startingWith);
                }
                shufflePos = 0;
            }
        }

        private int IndexOfCurrent()
        {
            if (playbackPlaylist == null || currentTrack == null) return -1;
            for (int i = 0; i < playbackPlaylist.Tracks.Count; i++)
                if (playbackPlaylist.Tracks[i].Matches(currentTrack)) return i;
            return -1;
        }

        /// <summary>
        /// Works out what plays next. <paramref name="userInitiated"/> separates
        /// pressing Next (which always moves on and wraps) from a track ending on
        /// its own (which must respect Repeat Off and stop at the end).
        /// </summary>
        private Track GetNextTrack(bool userInitiated)
        {
            if (queue.Count > 0) return queue[0];
            if (playbackPlaylist == null || playbackPlaylist.Tracks.Count == 0) return null;

            if (!userInitiated && repeatState == RepeatState.One) return currentTrack;

            if (isShuffleOn)
            {
                if (shuffleOrder.Count != playbackPlaylist.Tracks.Count)
                    BuildShuffleOrder(playbackPlaylist, currentTrack);

                if (shufflePos + 1 < shuffleOrder.Count) return shuffleOrder[shufflePos + 1];

                if (repeatState == RepeatState.All || userInitiated)
                {
                    BuildShuffleOrder(playbackPlaylist, null);
                    return shuffleOrder.FirstOrDefault();
                }
                return null; // end of the shuffled run
            }

            int index = IndexOfCurrent();
            int next = index + 1;

            if (next >= playbackPlaylist.Tracks.Count)
            {
                if (repeatState == RepeatState.All || userInitiated) next = 0;
                else return null; // Repeat Off means stop, not loop forever
            }

            return playbackPlaylist.Tracks[next];
        }

        private Track GetPreviousTrack()
        {
            if (playbackPlaylist == null || playbackPlaylist.Tracks.Count == 0) return null;

            if (isShuffleOn && shuffleOrder.Count > 0)
                return shufflePos > 0 ? shuffleOrder[shufflePos - 1] : shuffleOrder[shuffleOrder.Count - 1];

            int index = IndexOfCurrent();
            int prev = index - 1;
            if (prev < 0) prev = playbackPlaylist.Tracks.Count - 1;
            return playbackPlaylist.Tracks[prev];
        }

        private void AdvanceTo(Track track, bool fromQueue)
        {
            if (track == null) return;

            if (fromQueue && queue.Count > 0 && queue[0].Matches(track))
            {
                queue.RemoveAt(0);
                UpdateQueuePanel();
            }
            else if (isShuffleOn)
            {
                int idx = shuffleOrder.FindIndex(t => t.Matches(track));
                if (idx >= 0) shufflePos = idx;
            }

            PlayTrack(track);
            HighlightPlayingRow(track);
        }

        private void HighlightPlayingRow(Track track)
        {
            if (selectedPlaylist == null || playbackPlaylist != selectedPlaylist) return;
            try
            {
                var shown = SongsGrid.ItemsSource as IEnumerable<Track>;
                var match = shown?.FirstOrDefault(t => t.Matches(track));
                if (match != null) SongsGrid.ScrollIntoView(match);
            }
            catch { }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            var next = GetNextTrack(userInitiated: true);
            if (next == null) { Toast("That's the end of the playlist."); return; }
            AdvanceTo(next, queue.Count > 0 && queue[0].Matches(next));
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            // Standard behaviour: more than three seconds in, restart the track first.
            if (audioEngine != null && audioEngine.CurrentTime > 3)
            {
                audioEngine.SetPosition(0);
                PushDiscordPresence();
                return;
            }

            var prev = GetPreviousTrack();
            if (prev == null) return;
            if (isShuffleOn && shufflePos > 0) shufflePos--;
            PlayTrack(prev);
            HighlightPlayingRow(prev);
        }

        private void OnPlaybackEnded()
        {
            // Raised on an audio thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (shuttingDown) return;

                if (repeatState == RepeatState.One && currentTrack != null)
                {
                    PlayTrack(currentTrack, restart: true);
                    return;
                }

                // Walk forward past anything that has no audio on this PC. A playlist
                // imported from a link is full of references, and popping a dialog for
                // each one as the album auto-advances would be unbearable.
                int skipped = 0;
                var next = GetNextTrack(userInitiated: false);

                while (next != null && !next.IsPlayable && skipped < 500)
                {
                    bool fromQueue = queue.Count > 0 && queue[0].Matches(next);
                    if (fromQueue) queue.RemoveAt(0);
                    else if (isShuffleOn)
                    {
                        int idx = shuffleOrder.FindIndex(t => t.Matches(next));
                        if (idx >= 0) shufflePos = idx;
                    }

                    var previous = currentTrack;
                    currentTrack = next;
                    skipped++;

                    next = GetNextTrack(userInitiated: false);
                    if (next != null && next.Matches(currentTrack)) { next = null; break; }
                    if (next == null) currentTrack = previous;
                }

                if (next == null)
                {
                    SetPlayingState(false);
                    suppressSliderEvent = true;
                    PlaybackSlider.Value = 0;
                    suppressSliderEvent = false;
                    TxtCurrentTime.Text = "0:00";
                    PushDiscordPresence();
                    if (skipped > 0)
                        Toast($"Reached the end. Skipped {skipped} track{(skipped == 1 ? "" : "s")} with no local file.");
                    return;
                }

                AdvanceTo(next, queue.Count > 0 && queue[0].Matches(next));
                if (skipped > 0)
                    Toast($"Skipped {skipped} track{(skipped == 1 ? "" : "s")} with no local file.");
            }));
        }

        private void ThumbPrev_Click(object sender, EventArgs e) => BtnPrev_Click(null, null);
        private void ThumbNext_Click(object sender, EventArgs e) => BtnNext_Click(null, null);
        private void ThumbPlayPause_Click(object sender, EventArgs e) => BtnPlayPause_Click(null, null);

        // ---- shuffle / repeat ---------------------------------------------

        private void BtnShuffle_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;

            isShuffleOn = BtnShuffle.IsChecked == true;
            settings.IsShuffleOn = isShuffleOn;

            if (isShuffleOn && playbackPlaylist != null)
                BuildShuffleOrder(playbackPlaylist, currentTrack);

            saver?.Request();
            Toast(isShuffleOn ? "Shuffle on" : "Shuffle off");
        }

        private void BtnRepeat_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;

            repeatState = repeatState switch
            {
                RepeatState.Off => RepeatState.All,
                RepeatState.All => RepeatState.One,
                _ => RepeatState.Off
            };

            settings.RepeatState = repeatState;
            ApplyRepeatVisual();
            saver?.Request();

            Toast(repeatState switch
            {
                RepeatState.All => "Repeat playlist",
                RepeatState.One => "Repeat one track",
                _ => "Repeat off"
            });
        }

        private void ApplyRepeatVisual()
        {
            suppressToggleEvents = true;
            BtnRepeat.IsChecked = repeatState != RepeatState.Off;
            TxtRepeatBadge.Text = repeatState == RepeatState.One ? "1" : "";
            suppressToggleEvents = false;
        }

        // ==================================================================
        // Progress, volume, timer
        // ==================================================================

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (audioEngine == null || currentTrack == null) return;

            if (isPlaying)
            {
                stats.TotalListenedSeconds += uiTimer.Interval.TotalSeconds;

                double current = audioEngine.CurrentTime;
                double total = audioEngine.TotalTime;

                if (!isUserSeeking)
                {
                    TxtCurrentTime.Text = LinkImport.Format(current);
                    if (total > 0)
                    {
                        suppressSliderEvent = true;
                        PlaybackSlider.Value = Math.Clamp(current / total * 1000.0, 0, 1000);
                        suppressSliderEvent = false;
                    }
                }

                if (total > 0 && TxtTotalTime.Text != LinkImport.Format(total))
                    TxtTotalTime.Text = LinkImport.Format(total);

                PushDiscordPresence();
            }
        }

        private void PlaybackSlider_MouseDown(object sender, MouseButtonEventArgs e) => isUserSeeking = true;

        private void PlaybackSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!isUserSeeking) return;
            isUserSeeking = false;

            if (audioEngine == null || currentTrack == null) return;
            audioEngine.SetPosition(PlaybackSlider.Value / 10.0);
            TxtCurrentTime.Text = LinkImport.Format(audioEngine.CurrentTime);
            PushDiscordPresence(force: true);
        }

        private void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderEvent) return;

            if (isUserSeeking)
            {
                // Preview the time while dragging; the actual seek happens on release
                // so we are not thrashing the decoder on every pixel.
                double total = audioEngine?.TotalTime ?? 0;
                if (total > 0) TxtCurrentTime.Text = LinkImport.Format(total * e.NewValue / 1000.0);
                return;
            }

            // Click-to-seek on the bar (IsMoveToPointEnabled) lands here.
            if (audioEngine != null && currentTrack != null)
            {
                audioEngine.SetPosition(e.NewValue / 10.0);
                PushDiscordPresence(force: true);
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (audioEngine != null)
            {
                audioEngine.Volume = (float)(e.NewValue / 100.0);
                if (e.NewValue > 0 && audioEngine.IsMuted)
                {
                    audioEngine.IsMuted = false;
                    settings.IsMuted = false;
                }
            }

            if (settings != null)
            {
                settings.Volume = e.NewValue;
                // Debounced: the old build wrote three JSON files per pixel of drag.
                saver?.Request();
            }

            UpdateVolumeIcon();
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            if (audioEngine == null) return;
            audioEngine.IsMuted = !audioEngine.IsMuted;
            settings.IsMuted = audioEngine.IsMuted;
            saver?.Request();
            UpdateVolumeIcon();
            Toast(audioEngine.IsMuted ? "Muted" : "Unmuted");
        }

        private void UpdateVolumeIcon()
        {
            if (IconVolume == null) return;

            bool muted = audioEngine?.IsMuted == true || VolumeSlider.Value <= 0.5;
            string key = muted ? "I.VolumeMute"
                       : VolumeSlider.Value < 45 ? "I.VolumeLow"
                       : "I.VolumeHigh";

            if (TryFindResource(key) is Geometry g) IconVolume.Data = g;
            if (BtnMute != null) BtnMute.ToolTip = audioEngine?.IsMuted == true ? "Unmute" : "Mute";
        }

        // ==================================================================
        // Likes
        // ==================================================================

        private void BtnLikeCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (currentTrack == null) { BtnLikeCurrent.IsChecked = false; return; }
            SetLiked(currentTrack, BtnLikeCurrent.IsChecked == true);
        }

        private void TrackLike_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.DataContext is Track t)
                SetLiked(t, tb.IsChecked == true);
        }

        private void SetLiked(Track track, bool liked)
        {
            if (track == null) return;
            track.IsLiked = liked;

            // Mirror onto every copy of the same file across all playlists.
            foreach (var p in playlists)
                foreach (var other in p.Tracks)
                    if (!ReferenceEquals(other, track) && other.Matches(track))
                        other.IsLiked = liked;

            if (currentTrack != null && currentTrack.Matches(track))
                BtnLikeCurrent.IsChecked = liked;

            stats.TotalLikedSongs = CountLiked();

            if (selectedPlaylist != null && selectedPlaylist.IsSystem)
                OpenLikedSongs();

            SaveAll();
        }

        private int CountLiked()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var p in playlists)
                foreach (var t in p.Tracks)
                    if (t.IsLiked && seen.Add(t.FilePath ?? t.SourceUrl ?? t.Id)) n++;
            return n;
        }

        // ==================================================================
        // Track context menu
        // ==================================================================

        private Track MenuTrack(object sender)
            => (sender as FrameworkElement)?.DataContext as Track ?? contextTrack;

        private void MenuPlayTrack_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t != null) PlayFrom(selectedPlaylist, t);
        }

        private void MenuQueueTrack_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null) return;
            queue.Add(t);
            UpdateQueuePanel();
            Toast("\"" + t.Title + "\" added to the queue");
        }

        private async void MenuDownloadTrack_Click(object sender, RoutedEventArgs e)
        {
            var track = MenuTrack(sender);
            if (track == null) return;

            if (track.IsPlayable && !string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            {
                Toast($"\"{track.Title}\" is already downloaded.");
                return;
            }

            SetBusy(true, $"Downloading \"{track.Title}\"…");
            try
            {
                string path = await AudioStreamResolver.ResolveAndDownloadAsync(
                    track,
                    status => Dispatcher.Invoke(() => SetBusy(true, status))
                );

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    track.FilePath = path;
                    if (track.DurationSeconds <= 0)
                    {
                        try
                        {
                            using var reader = new AudioFileReader(path);
                            track.DurationSeconds = reader.TotalTime.TotalSeconds;
                            track.Duration = LinkImport.Format(track.DurationSeconds);
                        }
                        catch { }
                    }
                    track.RefreshComputed();
                    SaveAll();
                    RefreshSongList();
                    UpdateDetailSummary();
                    Toast($"Downloaded \"{track.Title}\"!");
                }
                else
                {
                    Toast($"Failed to download \"{track.Title}\".", true);
                }
            }
            catch (Exception ex)
            {
                Toast($"Download error: {ex.Message}", true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void MenuEditTrack_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null) return;

            renameTrackTarget = t;
            renamePlaylistTarget = null;
            TxtRenameHeading.Text = "Edit track";
            TxtRenameSub.Text = "Fix up the title and artist.";
            TxtRenameValue.Text = t.Title ?? "";
            Ui.SetWatermark(TxtRenameValue, "Title");
            TxtRenameValue2.Text = t.Artist ?? "";
            TxtRenameValue2.Visibility = Visibility.Visible;
            ShowModal(ModalRename);
            TxtRenameValue.SelectAll();
            TxtRenameValue.Focus();
        }

        private void MenuLinkFile_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null) return;

            string filter = "Audio files|" +
                            string.Join(";", LibraryMatcher.AudioExtensions.Select(x => "*" + x)) +
                            "|All files (*.*)|*.*";

            var dlg = new OpenFileDialog
            {
                Filter = filter,
                Title = "Choose the audio file for \"" + t.Title + "\""
            };
            if (dlg.ShowDialog() != true) return;

            t.FilePath = dlg.FileName;
            try
            {
                using var reader = new AudioFileReader(dlg.FileName);
                t.DurationSeconds = reader.TotalTime.TotalSeconds;
                t.Duration = LinkImport.Format(t.DurationSeconds);
            }
            catch { }

            t.RefreshComputed();
            RefreshSongList();
            UpdateDetailSummary();
            SaveAll();
            Toast("Linked. \"" + t.Title + "\" will play from your PC now.");
        }

        private void MenuOpenSource_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null) return;
            if (!t.HasSourceUrl) { Toast("That track didn't come from a link.", true); return; }
            OpenExternal(t.SourceUrl);
        }

        private void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null || string.IsNullOrWhiteSpace(t.FilePath) || !File.Exists(t.FilePath))
            {
                Toast("There's no local file for that track.", true);
                return;
            }

            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + t.FilePath + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Toast("Couldn't open Explorer: " + ex.Message, true); }
        }

        private void MenuRemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            var t = MenuTrack(sender);
            if (t == null || selectedPlaylist == null) return;

            if (selectedPlaylist.IsSystem)
            {
                // Removing from Liked Songs means unliking, not deleting the track.
                SetLiked(t, false);
                Toast("Removed from Liked Songs");
                return;
            }

            selectedPlaylist.Tracks.Remove(t);
            if (queue.Contains(t)) queue.Remove(t);

            RefreshSongList();
            UpdateDetailSummary();
            RefreshPlaylistBindings();
            UpdateHomeGreeting();
            UpdateQueuePanel();
            SaveAll();
            Toast("Removed \"" + t.Title + "\"");
        }

        private void OpenExternal(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Toast("Couldn't open that link: " + ex.Message, true); }
        }

        // ==================================================================
        // Queue flyout
        // ==================================================================

        private void BtnQueue_Click(object sender, RoutedEventArgs e)
        {
            if (QueueFlyout.Visibility == Visibility.Visible) CloseQueue();
            else OpenQueue();
        }

        private void OpenQueue()
        {
            UpdateQueuePanel();
            QueueScrim.Opacity = 0.55;
            QueueScrim.Visibility = Visibility.Visible;
            QueueFlyout.Visibility = Visibility.Visible;
            QueueSlide.X = 0;
        }

        private void CloseQueue()
        {
            QueueFlyout.Visibility = Visibility.Collapsed;
            QueueScrim.Visibility = Visibility.Collapsed;
        }

        private void QueueScrim_Click(object sender, RoutedEventArgs e) => CloseQueue();

        private void QueueScrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseQueue();

        private void UpdateQueuePanel()
        {
            QueueNowTitle.Text = currentTrack?.Title ?? "Nothing playing";
            QueueNowArtist.Text = currentTrack?.Artist ?? "";
            LoadCoverImage(QueueNowArt, currentTrack?.ImagePath);

            var upcoming = new List<Track>(queue);

            if (upcoming.Count == 0)
            {
                // Show where playback is heading even with an empty manual queue.
                var next = GetNextTrack(userInitiated: false);
                if (next != null && !next.Matches(currentTrack)) upcoming.Add(next);
            }

            QueueList.ItemsSource = upcoming;
            TxtQueueEmpty.Visibility = upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueUpNextLabel.Text = queue.Count > 0 ? "QUEUED (" + queue.Count + ")" : "UP NEXT";
        }

        private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Track t)
            {
                int idx = queue.IndexOf(t);
                if (idx >= 0) for (int i = 0; i <= idx; i++) queue.RemoveAt(0);
                PlayTrack(t);
                UpdateQueuePanel();
            }
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            queue.Clear();
            UpdateQueuePanel();
            Toast("Queue cleared");
        }

        // ==================================================================
        // Visualiser
        // ==================================================================

        private void InitializeVisualizer()
        {
            WaveformCanvas.Children.Clear();
            double width = 104;
            double slot = width / visualizerBars.Length;

            for (int i = 0; i < visualizerBars.Length; i++)
            {
                var bar = new Rectangle
                {
                    Width = Math.Max(2, slot - 2.2),
                    Height = 3,
                    RadiusX = 1.4,
                    RadiusY = 1.4,
                    Fill = ThemeManager.Brush("B.Overlay")
                };
                Canvas.SetLeft(bar, i * slot);
                Canvas.SetBottom(bar, 0);
                WaveformCanvas.Children.Add(bar);
                visualizerBars[i] = bar;
                barHeights[i] = 3;
            }
        }

        private void OnFftData(float[] fft)
        {
            // Called from the audio thread. Just stash the latest frame; the render
            // loop picks it up. The old code marshalled every FFT frame to the
            // dispatcher and rebuilt 20 Rectangles each time, ~90 times a second.
            latestFft = fft;
        }

        private void HookRendering()
        {
            if (renderHooked || !settings.VisualizerEnabled) return;
            CompositionTarget.Rendering += OnRendering;
            renderHooked = true;
        }

        private void UnhookRendering()
        {
            if (!renderHooked) return;
            CompositionTarget.Rendering -= OnRendering;
            renderHooked = false;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var fft = latestFft;
            if (fft == null || WaveformCanvas == null) return;

            double maxHeight = 34;
            var brush = ThemeManager.Brush("B.AccentGradient");
            float volume = (float)(VolumeSlider.Value / 100.0);
            if (audioEngine?.IsMuted == true) volume = 0;

            int barCount = visualizerBars.Length;
            for (int i = 0; i < barCount; i++)
            {
                // Multi-octave logarithmic frequency distribution (20Hz -> 18kHz)
                double frac = (double)i / barCount;
                int binStart = (int)(Math.Pow(frac, 2.2) * 230) + 1;
                int binEnd = (int)(Math.Pow((double)(i + 1) / barCount, 2.2) * 230) + 2;
                binStart = Math.Clamp(binStart, 0, fft.Length - 1);
                binEnd = Math.Clamp(binEnd, binStart, fft.Length - 1);

                float maxMag = 0f;
                for (int b = binStart; b <= binEnd; b++)
                    if (fft[b] > maxMag) maxMag = fft[b];

                // Convert raw linear FFT magnitude to human-perceived decibels (dBFS)
                // -48dB (noise floor) to 0dB (peak)
                double db = 20.0 * Math.Log10(Math.Max(maxMag, 1e-5f));
                double normalized = Math.Clamp((db + 48.0) / 48.0, 0.0, 1.0);

                // Equal-loudness tilt compensation across spectrum
                double trebleTilt = 1.0 + Math.Pow(frac, 1.3) * 0.45;
                float target = (float)(normalized * maxHeight * trebleTilt * Math.Sqrt(volume));
                if (target > maxHeight) target = (float)maxHeight;
                if (target < 3f) target = 3f;

                // Physics ballistics: instantaneous fast attack on peaks, smooth acoustic gravity falloff
                float current = barHeights[i];
                if (target > current)
                {
                    current = current + (target - current) * 0.75f; // fast transient attack
                }
                else
                {
                    current = Math.Max(target, current * 0.91f - 0.4f); // exponential gravity decay
                }

                barHeights[i] = current;

                var bar = visualizerBars[i];
                if (bar != null)
                {
                    bar.Height = current;
                    if (!ReferenceEquals(bar.Fill, brush)) bar.Fill = brush;
                }
            }
        }

        private void DrawIdleVisualizer()
        {
            var brush = ThemeManager.Brush("B.Overlay");
            for (int i = 0; i < visualizerBars.Length; i++)
            {
                var bar = visualizerBars[i];
                if (bar == null) continue;
                double angle = (double)i / visualizerBars.Length * Math.PI;
                bar.Height = Math.Sin(angle) * 9 + 3;
                bar.Fill = brush;
                barHeights[i] = (float)bar.Height;
            }
        }

        private void RecolourVisualizer()
        {
            var brush = ThemeManager.Brush("B.AccentGradient");
            foreach (var bar in visualizerBars) if (bar != null) bar.Fill = brush;
        }

        private void ToggleVisualizer_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            settings.VisualizerEnabled = ToggleVisualizer.IsChecked == true;
            saver?.Request();

            if (!settings.VisualizerEnabled) { UnhookRendering(); DrawIdleVisualizer(); }
            else if (isPlaying) HookRendering();
        }

        // ==================================================================
        // System media transport controls (media keys, Windows overlay)
        // ==================================================================

        private void SetupSystemMediaControls()
        {
            try
            {
                backgroundMediaPlayer = new Windows.Media.Playback.MediaPlayer();
                backgroundMediaPlayer.CommandManager.IsEnabled = false;
                smtc = backgroundMediaPlayer.SystemMediaTransportControls;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.IsEnabled = true;
                smtc.ButtonPressed += Smtc_ButtonPressed;
            }
            catch (Exception ex)
            {
                StorageManager.Log("SMTC unavailable: " + ex.Message);
            }
        }

        private void Smtc_ButtonPressed(SystemMediaTransportControls sender,
                                        SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            var button = args.Button;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause:
                        BtnPlayPause_Click(null, null);
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        BtnNext_Click(null, null);
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        BtnPrev_Click(null, null);
                        break;
                }
            }));
        }

        private void UpdateSmtcMetadata(Track track)
        {
            if (smtc == null || track == null) return;
            try
            {
                var updater = smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = track.Title ?? "Unknown Title";
                updater.MusicProperties.Artist = track.Artist ?? "Unknown Artist";
                if (!string.IsNullOrWhiteSpace(track.Album)) updater.MusicProperties.AlbumTitle = track.Album;

                string art = track.ImagePath;
                if (!string.IsNullOrWhiteSpace(art) && !art.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(art))
                {
                    string path = art;
                    Task.Run(async () =>
                    {
                        try
                        {
                            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                            updater.Update();
                        }
                        catch { try { updater.Update(); } catch { } }
                    });
                }
                else
                {
                    updater.Thumbnail = null;
                    updater.Update();
                }
            }
            catch { }
        }

        private void UpdateSmtcPlaybackStatus(bool playing)
        {
            if (smtc == null) return;
            try
            {
                smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing
                                     : currentTrack == null ? MediaPlaybackStatus.Stopped
                                     : MediaPlaybackStatus.Paused;
            }
            catch { }
        }

        // ==================================================================
        // Discord
        // ==================================================================

        private void PushDiscordPresence(bool force = false)
        {
            if (discordRpc == null || !settings.DiscordEnabled) return;

            if (currentTrack == null)
            {
                discordRpc.SetIdle();
                return;
            }

            discordRpc.Update(new PresenceSnapshot
            {
                Title = currentTrack.Title,
                Artist = currentTrack.Artist,
                Album = currentTrack.Album,
                ArtworkUrl = currentTrack.ImagePath,
                SourceUrl = currentTrack.SourceUrl,
                Elapsed = audioEngine?.CurrentTime ?? 0,
                Total = audioEngine?.TotalTime ?? currentTrack.DurationSeconds,
                IsPlaying = isPlaying
            });
        }

        private void ToggleDiscord_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;

            settings.DiscordEnabled = ToggleDiscord.IsChecked == true;
            discordRpc?.SetEnabled(settings.DiscordEnabled);
            if (settings.DiscordEnabled) PushDiscordPresence(true);

            saver?.Request();
            Toast(settings.DiscordEnabled ? "Discord presence on" : "Discord presence off");
        }

        private void ToggleDiscordButton_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            settings.DiscordShowButton = ToggleDiscordButton.IsChecked == true;
            if (discordRpc != null) discordRpc.ShowSourceButton = settings.DiscordShowButton;
            PushDiscordPresence(true);
            saver?.Request();
        }

        // ==================================================================
        // Settings: audio and EQ
        // ==================================================================

        private void ApplySettingsToUi()
        {
            suppressToggleEvents = true;
            try
            {
                VolumeSlider.Value = settings.Volume;
                UpdateVolumeIcon();

                BtnShuffle.IsChecked = isShuffleOn;
                ApplyRepeatVisual();

                ToggleNormalization.IsChecked = settings.Normalization;
                ToggleMono.IsChecked = settings.MonoAudio;
                ToggleVisualizer.IsChecked = settings.VisualizerEnabled;
                ToggleDiscord.IsChecked = settings.DiscordEnabled;
                ToggleDiscordButton.IsChecked = settings.DiscordShowButton;

                TxtYouTubeKey.Text = settings.YouTubeApiKey ?? "";
                TxtSpotifyId.Text = settings.SpotifyClientId ?? "";
                TxtSpotifySecret.Password = settings.SpotifyClientSecret ?? "";

                for (int i = 0; i < 10; i++)
                {
                    var slider = EqSliderAt(i);
                    if (slider != null) slider.Value = settings.EqGains[i];
                    UpdateEqLabel(i, settings.EqGains[i]);
                }

                SelectComboItem(ComboEQ, settings.EqPreset);
            }
            catch (Exception ex)
            {
                StorageManager.Log("ApplySettingsToUi: " + ex);
            }
            finally
            {
                suppressToggleEvents = false;
            }
        }

        private static void SelectComboItem(ComboBox combo, string value)
        {
            if (combo == null) return;
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private Slider EqSliderAt(int i) => i switch
        {
            0 => EqSlider0, 1 => EqSlider1, 2 => EqSlider2, 3 => EqSlider3, 4 => EqSlider4,
            5 => EqSlider5, 6 => EqSlider6, 7 => EqSlider7, 8 => EqSlider8, 9 => EqSlider9,
            _ => null
        };

        private TextBlock EqLabelAt(int i) => i switch
        {
            0 => EqVal0, 1 => EqVal1, 2 => EqVal2, 3 => EqVal3, 4 => EqVal4,
            5 => EqVal5, 6 => EqVal6, 7 => EqVal7, 8 => EqVal8, 9 => EqVal9,
            _ => null
        };

        private void UpdateEqLabel(int i, double value)
        {
            var label = EqLabelAt(i);
            if (label == null) return;
            label.Text = value > 0.05 ? "+" + value.ToString("0") : value.ToString("0");
        }

        private void ToggleNormalization_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            settings.Normalization = ToggleNormalization.IsChecked == true;
            if (audioEngine != null) audioEngine.IsNormalized = settings.Normalization;
            saver?.Request();
        }

        private void ToggleMono_Click(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            settings.MonoAudio = ToggleMono.IsChecked == true;
            if (audioEngine != null) audioEngine.IsMono = settings.MonoAudio;
            saver?.Request();
        }

        private void ComboEQ_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressToggleEvents || !IsLoaded || audioEngine == null) return;

            string preset = (ComboEQ.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrEmpty(preset) || preset == "Custom")
            {
                settings.EqPreset = "Custom";
                saver?.Request();
                return;
            }

            float[] gains = preset switch
            {
                "Super Bass"   => new float[] { 12, 11,  8,  4,  1,  0,  0,  1,  2,  2 },
                "Bass Boost"   => new float[] {  6,  6,  4,  2,  0,  0,  0,  0,  0,  0 },
                "Treble Boost" => new float[] {  0,  0,  0,  0,  0,  0,  2,  4,  6,  6 },
                "Electronic"   => new float[] {  5,  4,  2,  0, -2, -2,  0,  2,  4,  5 },
                "Acoustic"     => new float[] {  2,  2,  3,  3,  2,  2,  1,  1,  0,  0 },
                "Vocal Focus"  => new float[] { -8, -8, -6, -3,  4,  7,  7,  3, -4, -8 },
                "Beat Focus"   => new float[] {  8,  6,  4,  0, -6, -9, -9, -6,  3,  5 },
                _              => new float[10]
            };

            suppressToggleEvents = true;
            for (int i = 0; i < 10; i++)
            {
                var slider = EqSliderAt(i);
                if (slider != null) slider.Value = gains[i];
                settings.EqGains[i] = gains[i];
                audioEngine.SetEqBand(i, gains[i]);
                UpdateEqLabel(i, gains[i]);
            }
            suppressToggleEvents = false;

            settings.EqPreset = preset;
            saver?.Request();
        }

        private void EqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (audioEngine == null || sender is not Slider slider) return;
            if (!int.TryParse(slider.Tag?.ToString(), out int index)) return;

            audioEngine.SetEqBand(index, (float)e.NewValue);
            UpdateEqLabel(index, e.NewValue);

            if (suppressToggleEvents) return;

            if (settings.EqGains != null && index < settings.EqGains.Length)
                settings.EqGains[index] = (float)e.NewValue;

            // Dragging a band means the preset no longer applies. "Custom" is a real
            // item in the list now, so it survives a restart instead of silently
            // reverting the dropdown to Flat.
            if (settings.EqPreset != "Custom")
            {
                settings.EqPreset = "Custom";
                suppressToggleEvents = true;
                SelectComboItem(ComboEQ, "Custom");
                suppressToggleEvents = false;
            }

            saver?.Request();
        }

        private void BtnResetEq_Click(object sender, RoutedEventArgs e)
        {
            suppressToggleEvents = true;
            SelectComboItem(ComboEQ, "Flat");
            suppressToggleEvents = false;
            ComboEQ_SelectionChanged(ComboEQ, null);
            Toast("Equaliser reset");
        }

        // ==================================================================
        // Settings: API keys
        // ==================================================================

        private void BtnSaveKeys_Click(object sender, RoutedEventArgs e)
        {
            settings.YouTubeApiKey = TxtYouTubeKey.Text?.Trim() ?? "";
            settings.SpotifyClientId = TxtSpotifyId.Text?.Trim() ?? "";
            settings.SpotifyClientSecret = TxtSpotifySecret.Password?.Trim() ?? "";
            SaveAll();

            var have = new List<string>();
            if (!string.IsNullOrEmpty(settings.YouTubeApiKey)) have.Add("YouTube");
            if (!string.IsNullOrEmpty(settings.SpotifyClientId) &&
                !string.IsNullOrEmpty(settings.SpotifyClientSecret)) have.Add("Spotify");

            Toast(have.Count > 0
                ? "Saved. Playlist import unlocked for " + string.Join(" and ", have) + "."
                : "Saved.");
        }

        private void BtnClearKeys_Click(object sender, RoutedEventArgs e)
        {
            TxtYouTubeKey.Text = "";
            TxtSpotifyId.Text = "";
            TxtSpotifySecret.Password = "";
            settings.YouTubeApiKey = settings.SpotifyClientId = settings.SpotifyClientSecret = "";
            SaveAll();
            Toast("Keys cleared");
        }

        // ==================================================================
        // Statistics
        // ==================================================================

        private void RefreshStats()
        {
            var hours = stats.TotalListenedSeconds / 3600.0;
            StatTotalTime.Text = hours >= 1
                ? Math.Round(hours, 1) + " hours"
                : Math.Round(stats.TotalListenedSeconds / 60.0) + " min";

            StatTotalPlayed.Text = stats.TotalSongsPlayed.ToString("N0");
            StatTotalTracks.Text = playlists.Sum(p => p.Tracks.Count).ToString("N0");
            StatLiked.Text = CountLiked().ToString("N0");
            StatFirstSong.Text = string.IsNullOrWhiteSpace(stats.FirstListenedSong) ? "None yet" : stats.FirstListenedSong;

            // These two were declared in the model but never actually computed, so
            // the stats page always said "None".
            var topPlaylist = playlists.Where(p => p.PlayCount > 0)
                                       .OrderByDescending(p => p.PlayCount)
                                       .FirstOrDefault();
            stats.MostPlayedPlaylist = topPlaylist?.Name;
            StatMostPlayedPlaylist.Text = topPlaylist?.Name ?? "None yet";

            var topTrack = playlists.SelectMany(p => p.Tracks)
                                    .Where(t => t.PlayCount > 0)
                                    .OrderByDescending(t => t.PlayCount)
                                    .FirstOrDefault();
            stats.TopTrack = topTrack?.Title;
            StatTopTrack.Text = topTrack == null ? "None yet" : $"{topTrack.Title} ({topTrack.PlayCount} plays)";

            stats.TotalLikedSongs = CountLiked();
            stats.TotalTracks = playlists.Sum(p => p.Tracks.Count);
        }

        // ==================================================================
        // Import from a YouTube / Spotify link
        // ==================================================================

        private void BtnImportLink_Click(object sender, RoutedEventArgs e)
        {
            TxtImportUrl.Text = "";
            TxtImportStatus.Text = "";
            BtnDoImport.IsEnabled = true;
            ShowSpinner(false);
            BuildImportTargets();
            ShowModal(ModalImport);

            // Paste straight from the clipboard if a link is already sitting there.
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText()?.Trim();
                    if (LinkImport.LooksLikeUrl(text) && text.Length < 500) TxtImportUrl.Text = text;
                }
            }
            catch { }

            TxtImportUrl.Focus();
            TxtImportUrl.SelectAll();
        }

        private void BuildImportTargets()
        {
            ComboImportTarget.Items.Clear();

            var createNew = new ComboBoxItem { Content = "＋  New playlist from this link", Tag = null };
            ComboImportTarget.Items.Add(createNew);

            foreach (var p in playlists)
                ComboImportTarget.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });

            // Default to whatever the user is looking at, if it can take tracks.
            var preferred = ComboImportTarget.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is Playlist p && selectedPlaylist != null
                                     && !selectedPlaylist.IsSystem && ReferenceEquals(p, selectedPlaylist));

            ComboImportTarget.SelectedItem = preferred ?? createNew;
        }

        private void TxtImportUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { BtnDoImport_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideModals(); e.Handled = true; }
        }

        private async void BtnDoImport_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtImportUrl.Text?.Trim();

            if (!LinkImport.LooksLikeUrl(url))
            {
                TxtImportStatus.Foreground = ThemeManager.Brush("B.Danger");
                TxtImportStatus.Text = "Paste a YouTube, Spotify, or SoundCloud link.";
                return;
            }

            BtnDoImport.IsEnabled = false;
            ShowSpinner(true);
            TxtImportStatus.Foreground = ThemeManager.Brush("B.TextMuted");
            TxtImportStatus.Text = "Looking that up…";

            ImportResult result;
            try
            {
                result = await LinkImport.ImportAsync(url, settings);
            }
            catch (Exception ex)
            {
                StorageManager.Log("Import crashed: " + ex);
                result = new ImportResult { Error = "Something went wrong: " + ex.Message };
            }

            ShowSpinner(false);
            BtnDoImport.IsEnabled = true;

            if (!result.Success || result.Tracks.Count == 0)
            {
                TxtImportStatus.Foreground = ThemeManager.Brush("B.Danger");
                TxtImportStatus.Text = result.Error ?? "Nothing could be imported from that link.";
                return;
            }

            var target = (ComboImportTarget.SelectedItem as ComboBoxItem)?.Tag as Playlist;

            if (target == null)
            {
                string name = result.CollectionName;
                if (string.IsNullOrWhiteSpace(name))
                    name = result.Tracks.Count == 1 ? result.Tracks[0].Title : "Imported playlist";

                target = new Playlist
                {
                    Name = name,
                    Icon = string.IsNullOrWhiteSpace(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(),
                    Description = "Imported from a link",
                    ImagePath = result.Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ImagePath))?.ImagePath
                };
                playlists.Add(target);
            }

            // Skip anything already in the target playlist.
            var known = new HashSet<string>(
                target.Tracks.Where(t => !string.IsNullOrEmpty(t.SourceUrl)).Select(t => t.SourceUrl),
                StringComparer.OrdinalIgnoreCase);

            int added = 0, skipped = 0;
            foreach (var t in result.Tracks)
            {
                if (!string.IsNullOrEmpty(t.SourceUrl) && !known.Add(t.SourceUrl)) { skipped++; continue; }
                target.Tracks.Add(t);
                added++;
            }

            SaveAll();
            RefreshPlaylistBindings();
            UpdateHomeGreeting();
            HideModals();
            OpenPlaylist(target);

            string message = added == 1
                ? $"Added \"{result.Tracks[0].Title}\" to {target.Name}"
                : $"Added {added} tracks to {target.Name}";
            if (skipped > 0) message += $" ({skipped} already there)";
            Toast(message);

            if (!string.IsNullOrWhiteSpace(result.Note)) Toast(result.Note);

            if (result.Tracks.Any(t => t.NeedsFile))
                Toast("These are link references. Use the wand button to match them to audio files on this PC.");
        }

        private void ShowSpinner(bool visible)
        {
            if (ImportSpinner == null) return;
            ImportSpinner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            if (visible)
            {
                var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
                { RepeatBehavior = RepeatBehavior.Forever };
                ImportSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            else
            {
                ImportSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        // ==================================================================
        // Matching link tracks to local files
        // ==================================================================

        private async void BtnMatchFiles_Click(object sender, RoutedEventArgs e)
        {
            if (selectedPlaylist == null || selectedPlaylist.IsSystem) return;

            var pending = selectedPlaylist.Tracks.Where(t => t.NeedsFile).ToList();
            if (pending.Count == 0)
            {
                Toast("Every track in this playlist already has a file.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Pick any file inside your music folder",
                CheckFileExists = false,
                FileName = "Select this folder",
                Filter = "All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            string folder;
            try { folder = Path.GetDirectoryName(dialog.FileName); }
            catch { return; }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            SetBusy(true, "Scanning " + folder + "…");

            int matched = 0;
            int scanned = 0;
            try
            {
                await Task.Run(() =>
                {
                    var candidates = LibraryMatcher.ScanFolder(folder);
                    scanned = candidates.Count;
                    matched = LibraryMatcher.MatchTracks(pending, candidates);
                });
            }
            catch (Exception ex)
            {
                StorageManager.Log("Match failed: " + ex);
                Toast("Couldn't scan that folder: " + ex.Message, true);
                SetBusy(false);
                return;
            }

            // Fill in real durations for whatever got linked.
            await Task.Run(() =>
            {
                foreach (var t in pending.Where(t => t.IsPlayable && t.DurationSeconds <= 0))
                {
                    try
                    {
                        using var reader = new AudioFileReader(t.FilePath);
                        t.DurationSeconds = reader.TotalTime.TotalSeconds;
                        t.Duration = LinkImport.Format(t.DurationSeconds);
                    }
                    catch { }
                }
            });

            SetBusy(false);
            RefreshSongList();
            UpdateDetailSummary();
            SaveAll();

            Toast(matched == 0
                ? $"Scanned {scanned} files but found no confident matches."
                : $"Matched {matched} of {pending.Count} tracks from {scanned} files.");
        }

        // ==================================================================
        // Backup
        // ==================================================================

        private void BtnExportBackup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export backup",
                FileName = "audiowin_data.json",
                Filter = "AudioWin backup (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                SaveAll();

                string src = Path.Combine(StorageManager.AppDataFolder, "audiowin_data.json");
                if (!File.Exists(src)) { Toast("Nothing to back up yet.", true); return; }

                File.Copy(src, dlg.FileName, true);

                string destDir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(destDir))
                {
                    foreach (var name in new[] { "audiowin_stats.json", "audiowin_settings.json" })
                    {
                        var from = Path.Combine(StorageManager.AppDataFolder, name);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(destDir, name), true);
                    }
                }

                Toast("Backup exported");
            }
            catch (Exception ex)
            {
                Toast("Export failed: " + ex.Message, true);
            }
        }

        private void BtnImportBackup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose your audiowin_data.json backup",
                Filter = "AudioWin backup (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var confirm = MessageBox.Show(
                "Importing replaces the playlists currently in AudioWin.\n\nContinue?",
                "AudioWin", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            try
            {
                var json = File.ReadAllText(dlg.FileName);
                var imported = System.Text.Json.JsonSerializer.Deserialize<ObservableCollection<Playlist>>(json);
                if (imported == null) { Toast("That file isn't an AudioWin backup.", true); return; }

                StopPlayback();
                playbackPlaylist = null;
                selectedPlaylist = null;
                queue.Clear();

                playlists.Clear();
                foreach (var p in imported)
                {
                    if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                    p.Tracks ??= new ObservableCollection<Track>();
                    foreach (var t in p.Tracks)
                    {
                        if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N");
                        if (t.DurationSeconds <= 0) t.DurationSeconds = StorageManager.ParseDuration(t.Duration);
                    }
                    playlists.Add(p);
                }

                string dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(dir))
                {
                    var statsFile = Path.Combine(dir, "audiowin_stats.json");
                    if (File.Exists(statsFile))
                        try
                        {
                            var s = System.Text.Json.JsonSerializer.Deserialize<AppStats>(File.ReadAllText(statsFile));
                            if (s != null) stats = s;
                        }
                        catch { }

                    var settingsFile = Path.Combine(dir, "audiowin_settings.json");
                    if (File.Exists(settingsFile))
                        try
                        {
                            var s = System.Text.Json.JsonSerializer.Deserialize<PlaybackSettings>(File.ReadAllText(settingsFile));
                            if (s != null)
                            {
                                s.Normalize();
                                settings = s;
                                repeatState = settings.RepeatState;
                                isShuffleOn = settings.IsShuffleOn;
                                ApplySettingsToUi();
                                ThemeManager.Apply(settings.Theme, settings.Accent);
                                if (audioEngine != null)
                                {
                                    audioEngine.Volume = (float)(settings.Volume / 100.0);
                                    audioEngine.IsMono = settings.MonoAudio;
                                    audioEngine.IsNormalized = settings.Normalization;
                                    for (int i = 0; i < 10; i++) audioEngine.SetEqBand(i, settings.EqGains[i]);
                                }
                            }
                        }
                        catch { }
                }

                SaveAll();
                RefreshPlaylistBindings();
                UpdateHomeGreeting();
                NavHome.IsChecked = true;
                ShowView(HomeView);
                Toast("Imported " + playlists.Count + " playlists");
            }
            catch (Exception ex)
            {
                Toast("Import failed: " + ex.Message, true);
            }
        }

        private void BtnOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(StorageManager.AppDataFolder);
                Process.Start(new ProcessStartInfo(StorageManager.AppDataFolder) { UseShellExecute = true });
            }
            catch (Exception ex) { Toast("Couldn't open the folder: " + ex.Message, true); }
        }

        // ==================================================================
        // Modals
        // ==================================================================

        private void ShowModal(Border modal)
        {
            HideModals(instant: true);
            if (modal == null) return;

            modal.Opacity = 1;
            modal.Visibility = Visibility.Visible;
            if (modal.Child is FrameworkElement card)
            {
                card.RenderTransform = null;
            }
        }

        private void HideModals(bool instant = false)
        {
            foreach (var modal in new[] { ModalCreate, ModalRename, ModalImport })
            {
                if (modal == null || modal.Visibility != Visibility.Visible) continue;
                modal.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnCloseModals_Click(object sender, RoutedEventArgs e) => HideModals();

        private void ModalScrim_Click(object sender, MouseButtonEventArgs e) => HideModals();

        /// <summary>Stops a click inside the dialog card from bubbling up and closing it.</summary>
        private void ModalCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // ==================================================================
        // Drag and drop
        // ==================================================================

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            dragDepth++;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            bool canAccept = selectedPlaylist != null && !selectedPlaylist.IsSystem;
            TxtDropTitle.Text = canAccept ? "Drop to add" : "Open a playlist first";
            TxtDropSub.Text = canAccept
                ? "Files will be added to \"" + selectedPlaylist.Name + "\""
                : "Pick a playlist on the left, then drop your files";

            DropOverlay.Visibility = Visibility.Visible;
            DropOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(DropOverlay.Opacity, 1, TimeSpan.FromMilliseconds(140)));
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            bool canAccept = e.Data.GetDataPresent(DataFormats.FileDrop)
                             && selectedPlaylist != null && !selectedPlaylist.IsSystem;
            e.Effects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            // DragLeave fires for every child element the cursor crosses, so the
            // overlay is only dismissed once the outermost enter is unwound.
            dragDepth = Math.Max(0, dragDepth - 1);
            if (dragDepth == 0) HideDropOverlay();
        }

        private void HideDropOverlay()
        {
            var fade = new DoubleAnimation(DropOverlay.Opacity, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
            DropOverlay.BeginAnimation(OpacityProperty, fade);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            dragDepth = 0;
            HideDropOverlay();

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            if (selectedPlaylist == null || selectedPlaylist.IsSystem)
            {
                Toast("Open a playlist first, then drop files into it.", true);
                return;
            }

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            _ = AddFilesAsync(selectedPlaylist, files);
        }

        // ==================================================================
        // Keyboard
        // ==================================================================

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Media keys work everywhere, including while typing.
            switch (e.Key)
            {
                case Key.MediaPlayPause: BtnPlayPause_Click(null, null); e.Handled = true; return;
                case Key.MediaNextTrack: BtnNext_Click(null, null); e.Handled = true; return;
                case Key.MediaPreviousTrack: BtnPrev_Click(null, null); e.Handled = true; return;
                case Key.MediaStop: StopPlayback(); e.Handled = true; return;
            }

            if (e.Key == Key.Escape)
            {
                if (ModalCreate.Visibility == Visibility.Visible ||
                    ModalRename.Visibility == Visibility.Visible ||
                    ModalImport.Visibility == Visibility.Visible)
                {
                    HideModals();
                    e.Handled = true;
                    return;
                }
                if (QueueFlyout.Visibility == Visibility.Visible) { CloseQueue(); e.Handled = true; return; }
            }

            // Everything below would fight with typing, so leave text boxes alone.
            if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.N: BtnCreatePlaylist_Click(null, null); e.Handled = true; return;
                    case Key.L: BtnImportLink_Click(null, null); e.Handled = true; return;
                    case Key.F: TxtSearch.Focus(); e.Handled = true; return;
                    case Key.Q: BtnQueue_Click(null, null); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    BtnPlayPause_Click(null, null);
                    e.Handled = true;
                    break;

                case Key.Right when currentTrack != null:
                    SeekBy(5);
                    e.Handled = true;
                    break;

                case Key.Left when currentTrack != null:
                    SeekBy(-5);
                    e.Handled = true;
                    break;

                case Key.Up:
                    VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                    e.Handled = true;
                    break;

                case Key.Down:
                    VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                    e.Handled = true;
                    break;

                case Key.M:
                    BtnMute_Click(null, null);
                    e.Handled = true;
                    break;
            }
        }

        private void SeekBy(double seconds)
        {
            if (audioEngine == null || audioEngine.TotalTime <= 0) return;
            audioEngine.SetPositionSeconds(audioEngine.CurrentTime + seconds);
            TxtCurrentTime.Text = LinkImport.Format(audioEngine.CurrentTime);
            PushDiscordPresence(true);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            var node = start;
            while (node != null)
            {
                if (node is T hit) return hit;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private void SaveAll()
        {
            if (shuttingDown && !Dispatcher.CheckAccess()) return;

            if (!Dispatcher.CheckAccess())
            {
                // The save scheduler ticks on a background thread; the collections it
                // serialises are owned by the UI thread.
                Dispatcher.Invoke(SaveAll);
                return;
            }

            try
            {
                stats.TotalLikedSongs = CountLiked();
                stats.TotalTracks = playlists.Sum(p => p.Tracks.Count);

                StorageManager.Save(playlists);
                StorageManager.SaveStats(stats);
                StorageManager.SaveSettings(settings);
            }
            catch (Exception ex)
            {
                StorageManager.Log("SaveAll failed: " + ex);
            }
        }

        // ==================================================================
        // Shutdown
        // ==================================================================

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (shuttingDown) return;
            shuttingDown = true;

            try
            {
                if (WindowState == WindowState.Normal)
                {
                    settings.WindowWidth = Width;
                    settings.WindowHeight = Height;
                }
                settings.WindowMaximized = WindowState == WindowState.Maximized;
            }
            catch { }

            try { uiTimer?.Stop(); } catch { }
            UnhookRendering();

            try { saver?.FlushNow(); } catch { }
            try { saver?.Dispose(); } catch { }

            try { ThemeManager.ThemeChanged -= OnThemeChanged; } catch { }

            try
            {
                if (audioEngine != null)
                {
                    audioEngine.FftDataAvailable -= OnFftData;
                    audioEngine.PlaybackEnded -= OnPlaybackEnded;
                    audioEngine.Dispose();
                }
            }
            catch { }

            try { discordRpc?.Dispose(); } catch { }

            try
            {
                if (smtc != null) smtc.ButtonPressed -= Smtc_ButtonPressed;
                backgroundMediaPlayer?.Dispose();
            }
            catch { }
        }
    }
}
