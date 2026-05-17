using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace AudioWin
{
    public static class StorageManager
    {
        private static readonly string DataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_data.json");
        private static readonly string StatsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_stats.json");
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_settings.json");

        public static void Save(ObservableCollection<Playlist> playlists)
        {
            var json = JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DataPath, json);
        }

        public static ObservableCollection<Playlist> Load()
        {
            if (!File.Exists(DataPath)) return null;
            try {
                var json = File.ReadAllText(DataPath);
                return JsonSerializer.Deserialize<ObservableCollection<Playlist>>(json);
            } catch { return null; }
        }

        public static void SaveStats(AppStats stats)
        {
            var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StatsPath, json);
        }

        public static AppStats LoadStats()
        {
            if (!File.Exists(StatsPath)) return null;
            try {
                var json = File.ReadAllText(StatsPath);
                return JsonSerializer.Deserialize<AppStats>(json);
            } catch { return null; }
        }

        public static void SaveSettings(PlaybackSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }

        public static PlaybackSettings LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return new PlaybackSettings();
            try {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<PlaybackSettings>(json);
            } catch { return new PlaybackSettings(); }
        }
    }
}
