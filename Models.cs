using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AudioWin
{
    /// <summary>Where a track came from. Local files play in-app; the rest are references.</summary>
    public enum TrackSource
    {
        Local = 0,
        YouTube = 1,
        Spotify = 2,
        Web = 3,
        SoundCloud = 4
    }

    public enum RepeatState
    {
        Off = 0,
        One = 1,
        All = 2
    }

    public class ObservableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public class Track : ObservableBase
    {
        /// <summary>
        /// Stable identity. The previous build compared tracks by object reference,
        /// which broke as soon as a view handed out copies (Liked Songs did exactly
        /// that), so "play the song that is already playing" restarted it instead of
        /// pausing. Everything now matches on Id, falling back to FilePath/SourceUrl.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        private int index;
        public int Index { get => index; set => Set(ref index, value); }

        private string title;
        public string Title { get => title; set => Set(ref title, value); }

        private string artist;
        public string Artist { get => artist; set => Set(ref artist, value); }

        private string album;
        public string Album { get => album; set => Set(ref album, value); }

        private string filePath;
        public string FilePath
        {
            get => filePath;
            set { if (Set(ref filePath, value)) { OnPropertyChanged(nameof(IsPlayable)); OnPropertyChanged(nameof(NeedsFile)); } }
        }

        private string duration = "0:00";
        public string Duration { get => duration; set => Set(ref duration, value); }

        /// <summary>Numeric length, so sorting by duration doesn't compare "10:00" &lt; "9:00" as strings.</summary>
        private double durationSeconds;
        public double DurationSeconds { get => durationSeconds; set => Set(ref durationSeconds, value); }

        private string imagePath;
        public string ImagePath { get => imagePath; set => Set(ref imagePath, value); }

        private TrackSource source = TrackSource.Local;
        public TrackSource Source
        {
            get => source;
            set { if (Set(ref source, value)) { OnPropertyChanged(nameof(IsLink)); OnPropertyChanged(nameof(SourceLabel)); OnPropertyChanged(nameof(NeedsFile)); } }
        }

        private string sourceUrl;
        public string SourceUrl
        {
            get => sourceUrl;
            set { if (Set(ref sourceUrl, value)) OnPropertyChanged(nameof(HasSourceUrl)); }
        }

        private bool isLiked;
        public bool IsLiked { get => isLiked; set => Set(ref isLiked, value); }

        public int PlayCount { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        // ---- view-only state, never persisted -------------------------------

        [JsonIgnore]
        private bool isNowPlaying;
        [JsonIgnore]
        public bool IsNowPlaying { get => isNowPlaying; set => Set(ref isNowPlaying, value); }

        [JsonIgnore]
        public bool IsLink => Source != TrackSource.Local;

        [JsonIgnore]
        public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);

        /// <summary>True when the audio can actually be decoded locally right now.</summary>
        [JsonIgnore]
        public bool IsPlayable
        {
            get
            {
                if (string.IsNullOrWhiteSpace(FilePath)) return false;
                try { return File.Exists(FilePath); } catch { return false; }
            }
        }

        /// <summary>An imported reference that has not been matched to a local file yet.</summary>
        [JsonIgnore]
        public bool NeedsFile => string.IsNullOrWhiteSpace(FilePath);

        [JsonIgnore]
        public string SourceLabel => Source switch
        {
            TrackSource.YouTube => "YouTube",
            TrackSource.Spotify => "Spotify",
            TrackSource.SoundCloud => "SoundCloud",
            TrackSource.Web => "Link",
            _ => string.Empty
        };

        /// <summary>Tells the UI to re-query the computed properties after a file is linked.</summary>
        public void RefreshComputed()
        {
            OnPropertyChanged(nameof(IsPlayable));
            OnPropertyChanged(nameof(NeedsFile));
            OnPropertyChanged(nameof(IsLink));
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(HasSourceUrl));
        }

        public bool Matches(Track other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (!string.IsNullOrEmpty(Id) && Id == other.Id) return true;
            if (!string.IsNullOrEmpty(FilePath) && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(SourceUrl) && string.Equals(SourceUrl, other.SourceUrl, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public Track Clone() => new Track
        {
            Id = Id,
            Index = Index,
            Title = Title,
            Artist = Artist,
            Album = Album,
            FilePath = FilePath,
            Duration = Duration,
            DurationSeconds = DurationSeconds,
            ImagePath = ImagePath,
            Source = Source,
            SourceUrl = SourceUrl,
            IsLiked = IsLiked,
            PlayCount = PlayCount,
            LastPlayed = LastPlayed,
            AddedUtc = AddedUtc
        };
    }

    public class Playlist : ObservableBase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        private string name;
        public string Name
        {
            get => name;
            set { if (Set(ref name, value)) OnPropertyChanged(nameof(Initial)); }
        }

        private string icon;
        public string Icon { get => icon; set => Set(ref icon, value); }

        private string description = "A custom playlist collection.";
        public string Description { get => description; set => Set(ref description, value); }

        public string Creator { get; set; } = "You";

        private string imagePath;
        public string ImagePath
        {
            get => imagePath;
            set { if (Set(ref imagePath, value)) OnPropertyChanged(nameof(HasImage)); }
        }

        public ObservableCollection<Track> Tracks { get; set; } = new ObservableCollection<Track>();

        public int PlayCount { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Marks the synthesised "Liked Songs" view. The old build detected it by
        /// comparing Name == "Liked Songs", so creating a real playlist with that
        /// name silently broke adding tracks to it.
        /// </summary>
        [JsonIgnore]
        public bool IsSystem { get; set; }

        [JsonIgnore]
        public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

        [JsonIgnore]
        public string Initial =>
            string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim().Substring(0, 1).ToUpperInvariant();

        [JsonIgnore]
        public string Subtitle
        {
            get
            {
                int n = Tracks?.Count ?? 0;
                return n == 1 ? "1 track" : n + " tracks";
            }
        }

        public void NotifySubtitle() => OnPropertyChanged(nameof(Subtitle));

        [JsonIgnore]
        public double TotalSeconds => Tracks?.Sum(t => t.DurationSeconds) ?? 0;
    }

    public class AppStats
    {
        public string FirstListenedSong { get; set; }
        public double TotalListenedSeconds { get; set; }
        public string MostPlayedPlaylist { get; set; }
        public int TotalSongsPlayed { get; set; }
        public int TotalLikedSongs { get; set; }
        public int TotalTracks { get; set; }
        public string TopTrack { get; set; }
    }

    public class PlaybackSettings
    {
        // --- audio ---
        public double Volume { get; set; } = 70;
        public bool IsMuted { get; set; }
        public bool MonoAudio { get; set; }
        public bool Normalization { get; set; }
        public string EqPreset { get; set; } = "Flat";
        public float[] EqGains { get; set; } = new float[10];

        // --- playback ---
        public bool IsShuffleOn { get; set; }
        public RepeatState RepeatState { get; set; } = RepeatState.Off;

        // --- appearance ---
        public string Theme { get; set; } = "Dark";
        public string Accent { get; set; } = "Violet";
        public bool VisualizerEnabled { get; set; } = true;

        // --- integrations ---
        public bool DiscordEnabled { get; set; } = true;
        public bool DiscordShowButton { get; set; } = true;
        public string DiscordDetailFormat { get; set; } = "{title}";
        public string YouTubeApiKey { get; set; } = "";
        public string SpotifyClientId { get; set; } = "";
        public string SpotifyClientSecret { get; set; } = "";

        // --- window ---
        public double WindowWidth { get; set; } = 1360;
        public double WindowHeight { get; set; } = 880;
        public bool WindowMaximized { get; set; } = true;

        // --- retained for backwards compatibility with older save files ---
        public bool GaplessPlayback { get; set; }
        public double CrossfadeSeconds { get; set; }
        public bool Autoplay { get; set; }
        public string ShuffleMode { get; set; } = "Random";
        public string RepeatMode { get; set; } = "None";

        public void Normalize()
        {
            if (EqGains == null || EqGains.Length != 10)
            {
                var old = EqGains;
                EqGains = new float[10];
                // Older builds shipped a 5-band EQ; spread those across the 10 bands.
                if (old != null && old.Length == 5)
                    for (int i = 0; i < 10; i++) EqGains[i] = old[i / 2];
            }
            if (Volume < 0) Volume = 0;
            if (Volume > 100) Volume = 100;
            if (string.IsNullOrWhiteSpace(Theme)) Theme = "Dark";
            if (string.IsNullOrWhiteSpace(Accent)) Accent = "Violet";
            if (WindowWidth < 900) WindowWidth = 1360;
            if (WindowHeight < 600) WindowHeight = 880;
        }
    }
}
