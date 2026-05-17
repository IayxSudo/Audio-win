using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AudioWin
{
    public class Playlist : INotifyPropertyChanged
    {
        private string name;
        public string Name 
        { 
            get => name; 
            set { name = value; OnPropertyChanged(); } 
        }

        private string icon;
        public string Icon 
        { 
            get => icon; 
            set { icon = value; OnPropertyChanged(); } 
        }

        private string description = "A custom playlist collection.";
        public string Description 
        { 
            get => description; 
            set { description = value; OnPropertyChanged(); } 
        }

        public string Creator { get; set; } = "You";
        public string ImagePath { get; set; }
        
        public ObservableCollection<Track> Tracks { get; set; } = new ObservableCollection<Track>();

        public int PlayCount { get; set; }
        public DateTime? LastPlayed { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class Track : INotifyPropertyChanged
    {
        private int index;
        public int Index 
        { 
            get => index; 
            set { index = value; OnPropertyChanged(); } 
        }

        public string Title { get; set; }
        public string Artist { get; set; }
        public string FilePath { get; set; }
        public string Duration { get; set; }
        public string ImagePath { get; set; }

        
        private bool isLiked;
        public bool IsLiked 
        { 
            get => isLiked; 
            set { isLiked = value; OnPropertyChanged(); } 
        }

        public int PlayCount { get; set; }
        public DateTime? LastPlayed { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class AppStats
    {
        public string FirstListenedSong { get; set; }
        public double TotalListenedSeconds { get; set; }
        public string MostPlayedPlaylist { get; set; }
        public int TotalSongsPlayed { get; set; }
        public int TotalLikedSongs { get; set; }
    }

    public class PlaybackSettings
    {
        public bool GaplessPlayback { get; set; }
        public double CrossfadeSeconds { get; set; }
        public bool Autoplay { get; set; }
        public string ShuffleMode { get; set; } = "Random";
        public string RepeatMode { get; set; } = "None";
        public bool MonoAudio { get; set; }
        public bool Normalization { get; set; }
        public double Volume { get; set; } = 70; // Defaults to 70% so we don't blast the user's ears on first boot.
        public bool IsShuffleOn { get; set; }
        public RepeatState RepeatState { get; set; } = RepeatState.Off;
    }

    public enum RepeatState
    {
        Off,
        One,
        All
    }
}
