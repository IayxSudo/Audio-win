using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using NAudio.Wave;

namespace AudioWin
{
    public partial class MainWindow : Window
    {
        private AudioEngine audioEngine;
        private DispatcherTimer timer;
        private ObservableCollection<Playlist> playlists = new ObservableCollection<Playlist>();
        private List<Track> queueList = new List<Track>();
        private Playlist selectedPlaylist;
        private Track currentTrack;
        private AppStats stats;
        private PlaybackSettings settings;
        private RepeatState repeatState = RepeatState.Off;
        private bool isPlaying = false;
        private bool isUpdatingSliderInternally = false;
        private bool isUserSeeking = false;
        private bool isShuffleOn = false;
        private float[] lastHeights = new float[20];

        public MainWindow()
        {
            InitializeComponent();
            InitializeAudio();
            LoadData();
            ApplySettingsToUI();
            
            PlaylistsList.ItemsSource = playlists;
            
            // Fix for covering taskbar in borderless mode
            this.SourceInitialized += MainWindow_SourceInitialized;
            
            // Draw a beautiful soft inactive waveform curve once laid out
            this.Loaded += (s, e) => DrawPlaceholderVisualizer();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(new HwndSourceHook(WindowProc));
        }

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0024) // WM_GETMINMAXINFO
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                MONITORINFO monitorInfo = new MONITORINFO();
                GetMonitorInfo(monitor, monitorInfo);
                RECT rcWorkArea = monitorInfo.rcWork;
                RECT rcMonitorArea = monitorInfo.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(rcWorkArea.left - rcMonitorArea.left);
                mmi.ptMaxPosition.y = Math.Abs(rcWorkArea.top - rcMonitorArea.top);
                mmi.ptMaxSize.x = Math.Abs(rcWorkArea.right - rcWorkArea.left);
                mmi.ptMaxSize.y = Math.Abs(rcWorkArea.bottom - rcWorkArea.top);
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO { public POINT ptReserved; public POINT ptMaxSize; public POINT ptMaxPosition; public POINT ptMinTrackSize; public POINT ptMaxTrackSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MONITORINFO { public int cbSize = Marshal.SizeOf(typeof(MONITORINFO)); public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left; public int top; public int right; public int bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        private const int MONITOR_DEFAULTTONEAREST = 2;

        private void InitializeAudio()
        {
            try {
                audioEngine = new AudioEngine();
                audioEngine.FftDataAvailable += AudioEngine_FftDataAvailable;
            } catch (Exception ex) { MessageBox.Show("Audio Engine failed: " + ex.Message); }
            
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += Timer_Tick;
            timer.Start();
        }

        private void LoadData()
        {
            var loaded = StorageManager.Load();
            if (loaded != null) { foreach (var p in loaded) playlists.Add(p); }
            stats = StorageManager.LoadStats() ?? new AppStats();
            settings = StorageManager.LoadSettings() ?? new PlaybackSettings();
        }

        private void SaveData() 
        { 
            StorageManager.Save(playlists); 
            StorageManager.SaveStats(stats); 
            StorageManager.SaveSettings(settings); 
        }

        private void ApplySettingsToUI()
        {
            if (settings == null) return;
            try {
                VolumeSlider.Value = settings.Volume;
                if (audioEngine != null) audioEngine.Volume = (float)(settings.Volume / 100.0);
                
                isShuffleOn = settings.IsShuffleOn;
                var shuffleBrush = isShuffleOn ? Brushes.White : (SolidColorBrush)FindResource("GrayBrush");
                BtnShuffleDetail.Foreground = shuffleBrush;
                BtnShufflePlayer.Foreground = shuffleBrush;
                
                repeatState = settings.RepeatState;
                switch (repeatState)
                {
                    case RepeatState.Off:
                        TxtRepeatState.Text = "";
                        IconRepeat.Fill = (SolidColorBrush)FindResource("GrayBrush");
                        break;
                    case RepeatState.One:
                        TxtRepeatState.Text = "1";
                        IconRepeat.Fill = Brushes.White;
                        break;
                    case RepeatState.All:
                        TxtRepeatState.Text = "∞";
                        IconRepeat.Fill = Brushes.White;
                        break;
                }
            } catch { }
        }

        private int GetCurrentTrackIndex()
        {
            if (selectedPlaylist == null || currentTrack == null) return -1;
            for (int i = 0; i < selectedPlaylist.Tracks.Count; i++) {
                if (selectedPlaylist.Tracks[i].FilePath == currentTrack.FilePath) {
                    return i;
                }
            }
            return -1;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (isPlaying && audioEngine != null && !isUserSeeking)
            {
                try
                {
                    double current = audioEngine.CurrentTime;
                    double total = audioEngine.TotalTime;
                    
                    TxtCurrentTime.Text = FormatTime(current);
                    if (total > 0)
                    {
                        isUpdatingSliderInternally = true;
                        PlaybackSlider.Value = (current / total) * 100;
                        isUpdatingSliderInternally = false;

                        if (current >= total - 0.2) PlayNext();
                        stats.TotalListenedSeconds += 0.1; 
                    }
                }
                catch { }
            }
        }

        private void AudioEngine_FftDataAvailable(float[] fftData)
        {
            try {
                Dispatcher.Invoke(() =>
                {
                    try {
                        if (WaveformCanvas == null) return;
                        WaveformCanvas.Children.Clear();
                        double width = WaveformCanvas.ActualWidth;
                        double height = WaveformCanvas.ActualHeight;
                        if (width == 0 || height == 0) return;
                        
                        float volumeFactor = (float)(VolumeSlider.Value / 100.0);
                        int barCount = 20;
                        double barWidth = width / barCount;
                        
                        if (lastHeights == null || lastHeights.Length != barCount)
                        {
                            lastHeights = new float[barCount];
                        }

                        for (int i = 0; i < barCount; i++)
                        {
                            float frequencyBoost = 1.0f + (i * 0.15f);
                            float targetVal = fftData[i] * 650.0f * frequencyBoost * volumeFactor;
                            
                            if (targetVal > height) targetVal = (float)height;
                            
                            float currentVal = lastHeights[i];
                            if (targetVal > currentVal)
                            {
                                currentVal = targetVal;
                            }
                            else
                            {
                                currentVal = Math.Max(currentVal - 2.5f, targetVal);
                            }
                            
                            if (currentVal < 3) currentVal = 3; 
                            lastHeights[i] = currentVal;
                            
                            Rectangle rect = new Rectangle { 
                                Width = Math.Max(2, barWidth - 6), 
                                Height = currentVal, 
                                Fill = Brushes.White, 
                                RadiusX = 1, 
                                RadiusY = 1
                            };
                            Canvas.SetLeft(rect, i * barWidth);
                            Canvas.SetBottom(rect, 0);
                            WaveformCanvas.Children.Add(rect);
                        }
                    } catch { }
                }, DispatcherPriority.Render);
            } catch { }
        }

        private string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "0:00";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void SidebarPlaylistItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Playlist playlist) {
                SelectPlaylist(playlist);
                if (e.ClickCount == 2 && playlist.Tracks.Count > 0) {
                    TogglePlay(playlist.Tracks[0]);
                }
            }
        }

        private void PlaylistCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is Playlist playlist) {
                SelectPlaylist(playlist);
                if (e.ClickCount == 2 && playlist.Tracks.Count > 0) {
                    TogglePlay(playlist.Tracks[0]);
                }
            }
        }

        private void SelectPlaylist(Playlist playlist)
        {
            selectedPlaylist = playlist;
            DetailTitle.Text = playlist.Name;
            UpdatePlaylistCoverUI(playlist);
            UpdateStats();
            UpdateSongsViewVisibility();
            AnimateFadeOutIn(CurrentVisiblePanel(), SongsView);
            ResetSidebarSelection();
        }

        private void UpdatePlaylistCoverUI(Playlist playlist)
        {
            if (DetailDefaultText != null) DetailDefaultText.Text = playlist.Icon;
            if (!string.IsNullOrEmpty(playlist.ImagePath)) {
                try { DetailCoverImage.Source = new BitmapImage(new Uri(playlist.ImagePath)); DetailDefaultText.Visibility = Visibility.Collapsed; }
                catch { DetailCoverImage.Source = null; DetailDefaultText.Visibility = Visibility.Visible; }
            } else { DetailCoverImage.Source = null; DetailDefaultText.Visibility = Visibility.Visible; }
        }

        private void BtnChangeCover_Click(object sender, MouseButtonEventArgs e)
        {
            if (selectedPlaylist == null) return;
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
            if (ofd.ShowDialog() == true) {
                selectedPlaylist.ImagePath = ofd.FileName;
                UpdatePlaylistCoverUI(selectedPlaylist);
                SaveData();
                RefreshPlaylistsDisplay();
            }
        }

        private void RefreshPlaylistsDisplay() { var temp = PlaylistsList.ItemsSource; PlaylistsList.ItemsSource = null; PlaylistsList.ItemsSource = temp; }

        private FrameworkElement CurrentVisiblePanel()
        {
            if (PlaylistsGrid.Visibility == Visibility.Visible) return PlaylistsGrid;
            if (StatsGrid.Visibility == Visibility.Visible) return StatsGrid;
            if (SongsView.Visibility == Visibility.Visible) return SongsView;
            return PlaylistsGrid;
        }

        private void ResetSidebarSelection()
        {
            BtnSidebarHome.Style = (Style)FindResource("SidebarButton");
            BtnSidebarStats.Style = (Style)FindResource("SidebarButton");
            BtnSidebarLiked.Style = (Style)FindResource("SidebarButton");
        }

        private void BtnLikedSongs_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarSelection();
            BtnSidebarLiked.Style = (Style)FindResource("SidebarButtonActive");
            
            var likedTracks = new List<Track>();
            foreach (var p in playlists) {
                foreach (var t in p.Tracks) {
                    if (t.IsLiked && !likedTracks.Any(lt => lt.FilePath == t.FilePath)) {
                        likedTracks.Add(t);
                    }
                }
            }
            
            var likedPlaylist = new Playlist { Name = "Liked Songs", Icon = "♥" };
            int index = 1;
            foreach (var t in likedTracks) {
                likedPlaylist.Tracks.Add(new Track {
                    Index = index++,
                    Title = t.Title,
                    Artist = t.Artist,
                    FilePath = t.FilePath,
                    Duration = t.Duration,
                    IsLiked = t.IsLiked,
                    ImagePath = t.ImagePath,
                    PlayCount = t.PlayCount
                });
            }
            
            selectedPlaylist = likedPlaylist;
            DetailTitle.Text = "Liked Songs";
            
            DetailCoverImage.Source = null;
            if (DetailDefaultText != null) {
                DetailDefaultText.Text = "L";
                DetailDefaultText.Visibility = Visibility.Visible;
            }
            
            UpdateStats();
            UpdateSongsViewVisibility();
            AnimateFadeOutIn(CurrentVisiblePanel(), SongsView);
        }

        private void UpdateStats()
        {
            if (selectedPlaylist == null) return;
            int count = selectedPlaylist.Tracks.Count;
            double totalSeconds = 0;
            foreach (var t in selectedPlaylist.Tracks) {
                try { string[] parts = t.Duration.Split(':'); totalSeconds += int.Parse(parts[0]) * 60 + int.Parse(parts[1]); } catch { }
            }
            DetailStats.Text = $"{count} songs, about {Math.Round(totalSeconds / 60)} min";
        }

        private void UpdateSongsViewVisibility()
        {
            if (selectedPlaylist == null) return;
            
            if (selectedPlaylist.Name == "Liked Songs")
            {
                BtnAddAudio.Visibility = Visibility.Collapsed;
                if (selectedPlaylist.Tracks.Count == 0)
                {
                    EmptyPlaylistState.Visibility = Visibility.Visible;
                    SongsGrid.Visibility = Visibility.Collapsed;
                    try {
                        var stackPanel = (StackPanel)EmptyPlaylistState.Child;
                        var textBlock1 = (TextBlock)stackPanel.Children[1];
                        var textBlock2 = (TextBlock)stackPanel.Children[2];
                        textBlock1.Text = "No Liked Songs Yet";
                        textBlock2.Text = "Click the heart icon on any song to see it here!";
                    } catch { }
                }
                else
                {
                    EmptyPlaylistState.Visibility = Visibility.Collapsed;
                    SongsGrid.Visibility = Visibility.Visible;
                    RefreshSongsGrid();
                }
            }
            else
            {
                BtnAddAudio.Visibility = Visibility.Visible;
                if (selectedPlaylist.Tracks.Count == 0)
                {
                    EmptyPlaylistState.Visibility = Visibility.Visible;
                    SongsGrid.Visibility = Visibility.Collapsed;
                    try {
                        var stackPanel = (StackPanel)EmptyPlaylistState.Child;
                        var textBlock1 = (TextBlock)stackPanel.Children[1];
                        var textBlock2 = (TextBlock)stackPanel.Children[2];
                        textBlock1.Text = "Add Audio Files";
                        textBlock2.Text = "Click to browse your music library";
                    } catch { }
                }
                else
                {
                    EmptyPlaylistState.Visibility = Visibility.Collapsed;
                    SongsGrid.Visibility = Visibility.Visible;
                    RefreshSongsGrid();
                }
            }
        }

        private void AnimateFadeOutIn(FrameworkElement oldView, FrameworkElement newView)
        {
            if (oldView == null || newView == null || oldView == newView) { if (newView != null) { newView.Visibility = Visibility.Visible; newView.Opacity = 1; } return; }
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s, e) => {
                oldView.Visibility = Visibility.Collapsed;
                newView.Visibility = Visibility.Visible;
                newView.Opacity = 0;
                DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
                newView.BeginAnimation(OpacityProperty, fadeIn);
            };
            oldView.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void BtnAddAudio_Click(object sender, RoutedEventArgs e) { if (selectedPlaylist != null && selectedPlaylist.Name != "Liked Songs") PromptAddSongs(selectedPlaylist); }
        private void EmptyStateAdd_Click(object sender, MouseButtonEventArgs e) { if (selectedPlaylist != null && selectedPlaylist.Name != "Liked Songs") PromptAddSongs(selectedPlaylist); }

        private async void PromptAddSongs(Playlist playlist)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*", Multiselect = true };
            if (openFileDialog.ShowDialog() == true) {
                string[] files = openFileDialog.FileNames;
                int startCount = playlist.Tracks.Count + 1;
                
                var loadedTracks = await Task.Run(() => {
                    var list = new List<Track>();
                    int count = startCount;
                    foreach (string filename in files) {
                        string duration = "0:00";
                        try { using (var reader = new AudioFileReader(filename)) duration = FormatTime(reader.TotalTime.TotalSeconds); } catch { }
                        list.Add(new Track { 
                            Index = count++, 
                            Title = System.IO.Path.GetFileNameWithoutExtension(filename), 
                            Artist = "Unknown Artist", 
                            FilePath = filename, 
                            Duration = duration 
                        });
                    }
                    return list;
                });
                
                foreach (var track in loadedTracks) {
                    playlist.Tracks.Add(track);
                }
                
                SaveData(); 
                UpdateStats(); 
                UpdateSongsViewVisibility();
            }
        }

        private void RefreshSongsGrid() { SongsGrid.ItemsSource = null; SongsGrid.ItemsSource = selectedPlaylist?.Tracks; }

        private void SongsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (SongsGrid.SelectedItem is Track track) TogglePlay(track); }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (SongsGrid.SelectedItem is Track track) TogglePlay(track);
            else if (selectedPlaylist != null && selectedPlaylist.Tracks.Count > 0) TogglePlay(selectedPlaylist.Tracks[0]);
        }

        private void TogglePlay(Track track)
        {
            try {
                if (currentTrack != track) {
                    audioEngine.Play(track.FilePath);
                    currentTrack = track;
                    UpdatePlayPauseIcon(true);
                    isPlaying = true;
                    TxtNowPlayingTitle.Text = track.Title;
                    TxtNowPlayingArtist.Text = track.Artist;
                    TxtTotalTime.Text = track.Duration;
                    BtnLikeCurrent.IsChecked = track.IsLiked;
                    
                    // Strictly use song cover if available, otherwise default. NEVER playlist icon as requested.
                    if (!string.IsNullOrEmpty(track.ImagePath)) {
                        PlayerCoverImage.Source = new BitmapImage(new Uri(track.ImagePath));
                        PlayerDefaultIcon.Visibility = Visibility.Collapsed;
                    } else { PlayerCoverImage.Source = null; PlayerDefaultIcon.Visibility = Visibility.Visible; }


                    track.PlayCount++;
                    stats.TotalSongsPlayed++;
                    if (string.IsNullOrEmpty(stats.FirstListenedSong)) stats.FirstListenedSong = track.Title;
                } else {
                    if (isPlaying) { 
                        audioEngine.Pause(); 
                        UpdatePlayPauseIcon(false); 
                        isPlaying = false; 
                        DrawPlaceholderVisualizer(); 
                    }
                    else { 
                        if (audioEngine.PlaybackState == PlaybackState.Paused) audioEngine.Resume();
                        else audioEngine.Play(track.FilePath);
                        UpdatePlayPauseIcon(true); 
                        isPlaying = true; 
                    }
                }
            } catch (Exception ex) { MessageBox.Show("Playback Error: " + ex.Message); }
        }

        private void PlayNext()
        {
            // Proper Queue Logic: If queue exists, play from it first
            if (queueList.Count > 0) { 
                var nextFromQueue = queueList[0]; 
                queueList.RemoveAt(0); 
                TogglePlay(nextFromQueue); 
                return; 
            }

            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0) return;
            
            // Loop Logic: Seamlessly restart current track infinitely when loop on "1"
            if (repeatState == RepeatState.One && currentTrack != null) { 
                try {
                    audioEngine.SetPosition(0);
                    audioEngine.Play(currentTrack.FilePath);
                    isPlaying = true;
                    UpdatePlayPauseIcon(true);
                } catch { }
                return; 
            }

            
            int nextIndex;
            if (isShuffleOn) {
                Random rnd = new Random();
                nextIndex = rnd.Next(0, selectedPlaylist.Tracks.Count);
                if (selectedPlaylist.Tracks.Count > 1) {
                    while (nextIndex == GetCurrentTrackIndex()) nextIndex = rnd.Next(0, selectedPlaylist.Tracks.Count);
                }
            }
            else {
                int currentIndex = GetCurrentTrackIndex();
                nextIndex = currentIndex + 1;
                if (nextIndex >= selectedPlaylist.Tracks.Count) {
                    if (repeatState == RepeatState.All) nextIndex = 0;
                    else { isPlaying = false; UpdatePlayPauseIcon(false); DrawPlaceholderVisualizer(); return; }
                }
            }
            
            var nextTrack = selectedPlaylist.Tracks[nextIndex];
            TogglePlay(nextTrack);
            SongsGrid.SelectedItem = nextTrack;
            SongsGrid.ScrollIntoView(nextTrack);
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e) 
        {
            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0) return;
            int currentIndex = GetCurrentTrackIndex();
            int nextIndex = currentIndex + 1;
            if (nextIndex >= selectedPlaylist.Tracks.Count) nextIndex = 0;
            
            var nextTrack = selectedPlaylist.Tracks[nextIndex];
            TogglePlay(nextTrack);
            SongsGrid.SelectedItem = nextTrack;
            SongsGrid.ScrollIntoView(nextTrack);
        }
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0) return;
            int currentIndex = GetCurrentTrackIndex();
            int prevIndex = currentIndex - 1;
            if (prevIndex < 0) prevIndex = selectedPlaylist.Tracks.Count - 1;
            
            var prevTrack = selectedPlaylist.Tracks[prevIndex];
            TogglePlay(prevTrack);
            SongsGrid.SelectedItem = prevTrack;
            SongsGrid.ScrollIntoView(prevTrack);
        }

        private void BtnShuffle_Click(object sender, RoutedEventArgs e)
        {
            isShuffleOn = !isShuffleOn;
            var brush = isShuffleOn ? Brushes.White : (SolidColorBrush)FindResource("GrayBrush");
            BtnShuffleDetail.Foreground = brush;
            BtnShufflePlayer.Foreground = brush;
            if (settings != null) { settings.IsShuffleOn = isShuffleOn; SaveData(); }
        }

        private void BtnRepeat_Click(object sender, RoutedEventArgs e)
        {
            switch (repeatState)
            {
                case RepeatState.Off:
                    repeatState = RepeatState.One;
                    TxtRepeatState.Text = "1";
                    IconRepeat.Fill = Brushes.White;
                    break;
                case RepeatState.One:
                    repeatState = RepeatState.All;
                    TxtRepeatState.Text = "∞";
                    IconRepeat.Fill = Brushes.White;
                    break;
                case RepeatState.All:
                    repeatState = RepeatState.Off;
                    TxtRepeatState.Text = "";
                    IconRepeat.Fill = (SolidColorBrush)FindResource("GrayBrush");
                    break;
            }
            if (settings != null) { settings.RepeatState = repeatState; SaveData(); }
        }

        private void UpdatePlayPauseIcon(bool playing)
        {
            if (playing)
            {
                BtnPlayPauseIcon.Data = Geometry.Parse("M 0,0 L 6,0 L 6,20 L 0,20 Z M 10,0 L 16,0 L 16,20 L 10,20 Z");
                BtnPlayPauseIcon.Margin = new Thickness(0);
            }
            else
            {
                BtnPlayPauseIcon.Data = Geometry.Parse("M 0,0 L 16,10 L 0,20 Z");
                BtnPlayPauseIcon.Margin = new Thickness(2, 0, 0, 0);
            }
        }

        private void PlaybackSlider_MouseDown(object sender, MouseButtonEventArgs e) => isUserSeeking = true;
        private void PlaybackSlider_MouseUp(object sender, MouseButtonEventArgs e) 
        { 
            isUserSeeking = false; 
            if (audioEngine != null) audioEngine.SetPosition(PlaybackSlider.Value); 
        }

        private void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!isUpdatingSliderInternally && !isUserSeeking && audioEngine != null) { audioEngine.SetPosition(PlaybackSlider.Value); }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) 
        { 
            if (audioEngine != null) audioEngine.Volume = (float)(e.NewValue / 100.0); 
            if (settings != null) { settings.Volume = e.NewValue; SaveData(); }
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e) 
        { 
            ResetSidebarSelection(); 
            BtnSidebarHome.Style = (Style)FindResource("SidebarButtonActive");
            AnimateFadeOutIn(CurrentVisiblePanel(), PlaylistsGrid); 
        }

        private void BtnStats_Click(object sender, RoutedEventArgs e)
        {
            ResetSidebarSelection(); 
            BtnSidebarStats.Style = (Style)FindResource("SidebarButtonActive");
            StatTotalTime.Text = $"{Math.Round(stats.TotalListenedSeconds / 3600, 1)} hours";
            StatTotalPlayed.Text = stats.TotalSongsPlayed.ToString();
            StatMostPlayedPlaylist.Text = stats.MostPlayedPlaylist ?? "None";
            StatFirstSong.Text = stats.FirstListenedSong ?? "None";
            AnimateFadeOutIn(CurrentVisiblePanel(), StatsGrid);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = ((TextBox)sender).Text.ToLower();
            if (string.IsNullOrWhiteSpace(query)) PlaylistsList.ItemsSource = playlists;
            else PlaylistsList.ItemsSource = playlists.Where(p => p.Name.ToLower().Contains(query)).ToList();
        }

        private void TxtSongSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = ((TextBox)sender).Text.ToLower();
            if (selectedPlaylist == null) return;
            if (string.IsNullOrWhiteSpace(query)) RefreshSongsGrid();
            else SongsGrid.ItemsSource = selectedPlaylist.Tracks.Where(t => t.Title.ToLower().Contains(query) || t.Artist.ToLower().Contains(query)).ToList();
        }

        private void ComboSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (selectedPlaylist == null) return;
            var item = (ComboBoxItem)ComboSort.SelectedItem;
            if (item == null) return;
            IEnumerable<Track> sorted;
            switch (item.Content.ToString()) {
                case "Title": sorted = selectedPlaylist.Tracks.OrderBy(t => t.Title); break;
                case "Artist": sorted = selectedPlaylist.Tracks.OrderBy(t => t.Artist); break;
                case "Duration": sorted = selectedPlaylist.Tracks.OrderBy(t => t.Duration); break;
                default: sorted = selectedPlaylist.Tracks; break;
            }
            SongsGrid.ItemsSource = sorted.ToList();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) { WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized; BtnMaximize.Content = (WindowState == WindowState.Maximized) ? "󰨙" : "☐"; }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { try { SaveData(); timer?.Stop(); audioEngine?.Dispose(); Application.Current.Shutdown(); } catch { Application.Current.Shutdown(); } }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try { SaveData(); timer?.Stop(); audioEngine?.Dispose(); } catch { }
            base.OnClosing(e);
        }

        private void BtnCreatePlaylist_Click(object sender, RoutedEventArgs e) { ModalOverlay.Visibility = Visibility.Visible; ModalOverlay.Opacity = 0; ModalOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))); }
        private void BtnCancelModal_Click(object sender, RoutedEventArgs e) { DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)); fadeOut.Completed += (s, arg) => ModalOverlay.Visibility = Visibility.Collapsed; ModalOverlay.BeginAnimation(OpacityProperty, fadeOut); }
        private void BtnConfirmCreatePlaylist_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(TxtPlaylistName.Text)) { var np = new Playlist { Name = TxtPlaylistName.Text, Icon = TxtPlaylistName.Text.Substring(0, 1).ToUpper() }; playlists.Add(np); SaveData(); BtnCancelModal_Click(null, null); TxtPlaylistName.Text = ""; SelectPlaylist(np); } }
        private void RemoveTrack_Click(object sender, RoutedEventArgs e) { if (SongsGrid.SelectedItem is Track track && selectedPlaylist != null) { selectedPlaylist.Tracks.Remove(track); UpdateSongsViewVisibility(); UpdateStats(); SaveData(); } }
        private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistsList.SelectedItem is Playlist playlist)
            {
                if (currentTrack != null && playlist.Tracks.Any(t => t.FilePath == currentTrack.FilePath))
                {
                    try { audioEngine?.Stop(); } catch { }
                    currentTrack = null;
                    isPlaying = false;
                    TxtNowPlayingTitle.Text = "Not Playing";
                    TxtNowPlayingArtist.Text = "Select a track";
                    TxtCurrentTime.Text = "0:00";
                    TxtTotalTime.Text = "0:00";
                    PlaybackSlider.Value = 0;
                    PlayerCoverImage.Source = null;
                    PlayerDefaultIcon.Visibility = Visibility.Visible;
                    UpdatePlayPauseIcon(false);
                    DrawPlaceholderVisualizer();
                }

                if (selectedPlaylist == playlist)
                {
                    selectedPlaylist = null;
                    BtnHome_Click(null, null);
                }

                playlists.Remove(playlist);
                SaveData();
            }
        }

        private void ShowFeaturePopup(string title, string text) { if (FeaturePopup == null || PopupTitle == null || PopupText == null) return; PopupTitle.Text = title; PopupText.Text = text; FeaturePopup.Visibility = Visibility.Visible; FeaturePopup.Opacity = 0; FeaturePopup.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))); }
        private void BtnClosePopup_Click(object sender, RoutedEventArgs e) { if (FeaturePopup == null) return; DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)); fadeOut.Completed += (s, a) => FeaturePopup.Visibility = Visibility.Collapsed; FeaturePopup.BeginAnimation(OpacityProperty, fadeOut); }
        private void PopupOverlay_MouseDown(object sender, MouseButtonEventArgs e) => BtnClosePopup_Click(null, null);
        private Track GetNextTrackToPlay()
        {
            if (queueList.Count > 0) return queueList[0];
            if (selectedPlaylist == null || selectedPlaylist.Tracks.Count == 0 || currentTrack == null) return null;
            
            if (repeatState == RepeatState.One) return currentTrack;
            
            if (isShuffleOn && selectedPlaylist.Tracks.Count > 1) {
                var list = selectedPlaylist.Tracks.Where(t => t.FilePath != currentTrack.FilePath).ToList();
                if (list.Count > 0) return list[0];
            }
            
            int currentIndex = GetCurrentTrackIndex();
            int nextIndex = currentIndex + 1;
            if (nextIndex >= selectedPlaylist.Tracks.Count) {
                if (repeatState == RepeatState.All) nextIndex = 0;
                else return null;
            }
            return selectedPlaylist.Tracks[nextIndex];
        }

        private void BtnQueue_Click(object sender, RoutedEventArgs e)
        {
            List<string> lines = new List<string>();
            if (currentTrack != null) {
                lines.Add($"🎵 Now Playing:\n  • {currentTrack.Title} - {currentTrack.Artist}\n");
            }
            
            lines.Add("⏭️ Up Next:");
            if (queueList.Count > 0) {
                foreach (var t in queueList) {
                    lines.Add($"  • {t.Title} - {t.Artist} (From Queue)");
                }
            } else {
                var next = GetNextTrackToPlay();
                if (next != null) {
                    lines.Add($"  • {next.Title} - {next.Artist}");
                } else {
                    lines.Add("  • End of playback (Add more tracks or enable loop!)");
                }
            }
            
            ShowFeaturePopup("Current Queue", string.Join("\n", lines));
        }

        private void BtnLikeCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (currentTrack == null) return;
            currentTrack.IsLiked = BtnLikeCurrent.IsChecked == true;
            
            foreach (var p in playlists) {
                var match = p.Tracks.FirstOrDefault(t => t.FilePath == currentTrack.FilePath);
                if (match != null) match.IsLiked = currentTrack.IsLiked;
            }
            
            if (selectedPlaylist != null && selectedPlaylist.Name == "Liked Songs") {
                BtnLikedSongs_Click(null, null);
            } else {
                RefreshSongsGrid();
            }
            SaveData();
        }

        private void BtnTrackLike_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.DataContext is Track track) {
                track.IsLiked = tb.IsChecked == true;
                
                if (currentTrack != null && currentTrack.FilePath == track.FilePath) {
                    currentTrack.IsLiked = track.IsLiked;
                    BtnLikeCurrent.IsChecked = track.IsLiked;
                }
                
                foreach (var p in playlists) {
                    var match = p.Tracks.FirstOrDefault(t => t.FilePath == track.FilePath);
                    if (match != null && match != track) match.IsLiked = track.IsLiked;
                }
                
                if (selectedPlaylist != null && selectedPlaylist.Name == "Liked Songs") {
                    BtnLikedSongs_Click(null, null);
                } else {
                    RefreshSongsGrid();
                }
                SaveData();
            }
        }

        private void AddToQueue_Click(object sender, RoutedEventArgs e) { if (SongsGrid.SelectedItem is Track track) { queueList.Add(track); ShowFeaturePopup("Queue", $"{track.Title} added to queue."); } }

        private void DrawPlaceholderVisualizer()
        {
            if (WaveformCanvas == null) return;
            WaveformCanvas.Children.Clear();
            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width == 0) width = 120;
            if (height == 0) height = 60;
            
            int barCount = 20;
            double barWidth = width / barCount;
            
            for (int i = 0; i < barCount; i++)
            {
                double angle = (i / (double)barCount) * Math.PI;
                float val = (float)(Math.Sin(angle) * 15 + 4);
                
                Rectangle rect = new Rectangle { 
                    Width = Math.Max(2, barWidth - 6), 
                    Height = val, 
                    Fill = (SolidColorBrush)FindResource("SelectionBrush"),
                    RadiusX = 1, 
                    RadiusY = 1
                };
                Canvas.SetLeft(rect, i * barWidth);
                Canvas.SetBottom(rect, 0);
                WaveformCanvas.Children.Add(rect);
            }
        }
    }
}