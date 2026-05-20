using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace AudioWin
{
    public static class StorageManager
    {
        private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioWin");
        
        private static readonly string DataPath = Path.Combine(AppDataFolder, "audiowin_data.json");
        private static readonly string StatsPath = Path.Combine(AppDataFolder, "audiowin_stats.json");
        private static readonly string SettingsPath = Path.Combine(AppDataFolder, "audiowin_settings.json");

        static StorageManager()
        {
            try
            {
                // Ensure target AppData folder exists
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                // Perform automatic migration from base directory to AppData folder if files exist there
                MigrateFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_data.json"), DataPath);
                MigrateFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_stats.json"), StatsPath);
                MigrateFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiowin_settings.json"), SettingsPath);

                // If they still don't have their settings/playlists, search Desktop and Downloads dynamically for folders containing 'AudioWin'
                AutoMigrateFromAnywhere();
            }
            catch { }
        }

        private static void MigrateFile(string oldPath, string newPath)
        {
            try
            {
                if (File.Exists(oldPath) && !File.Exists(newPath))
                {
                    File.Copy(oldPath, newPath, true);
                    File.Delete(oldPath);
                }
            }
            catch { }
        }

        private static void AutoMigrateFromAnywhere()
        {
            try
            {
                // 1. If AppData data already exists, do not overwrite it automatically.
                if (File.Exists(DataPath)) return;

                // 2. Try simple sibling folders (e.g. if the user runs the new version in a folder right next to the old version)
                var currentExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                var parentDir = Path.GetDirectoryName(currentExeDir);
                if (parentDir != null && Directory.Exists(parentDir))
                {
                    var siblingDirs = Directory.GetDirectories(parentDir, "*AudioWin*");
                    foreach (var sibling in siblingDirs)
                    {
                        if (sibling.Equals(currentExeDir, StringComparison.OrdinalIgnoreCase)) continue;
                        
                        var candidate = Path.Combine(sibling, "audiowin_data.json");
                        if (File.Exists(candidate))
                        {
                            MigrateFile(candidate, DataPath);
                            
                            var statsCand = Path.Combine(sibling, "audiowin_stats.json");
                            if (File.Exists(statsCand)) MigrateFile(statsCand, StatsPath);
                            
                            var settingsCand = Path.Combine(sibling, "audiowin_settings.json");
                            if (File.Exists(settingsCand)) MigrateFile(settingsCand, SettingsPath);
                            
                            return; // Found and migrated!
                        }

                        // Also search net10.0-windows etc inside the sibling dir
                        try
                        {
                            var binDirs = Directory.GetDirectories(sibling, "*", SearchOption.AllDirectories);
                            foreach (var binDir in binDirs)
                            {
                                var subCand = Path.Combine(binDir, "audiowin_data.json");
                                if (File.Exists(subCand))
                                {
                                    MigrateFile(subCand, DataPath);
                                    
                                    var statsSub = Path.Combine(binDir, "audiowin_stats.json");
                                    if (File.Exists(statsSub)) MigrateFile(statsSub, StatsPath);
                                    
                                    var settingsSub = Path.Combine(binDir, "audiowin_settings.json");
                                    if (File.Exists(settingsSub)) MigrateFile(settingsSub, SettingsPath);
                                    
                                    return; // Found and migrated!
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 3. Try searching the standard folders (Desktop and Downloads)
                var searchDirs = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                };

                foreach (var searchDir in searchDirs)
                {
                    if (!Directory.Exists(searchDir)) continue;
                    var matchDirs = Directory.GetDirectories(searchDir, "*AudioWin*", SearchOption.TopDirectoryOnly);
                    foreach (var match in matchDirs)
                    {
                        if (match.Equals(currentExeDir, StringComparison.OrdinalIgnoreCase)) continue;

                        var candidate = Path.Combine(match, "audiowin_data.json");
                        if (File.Exists(candidate))
                        {
                            MigrateFile(candidate, DataPath);
                            
                            var statsCand = Path.Combine(match, "audiowin_stats.json");
                            if (File.Exists(statsCand)) MigrateFile(statsCand, StatsPath);
                            
                            var settingsCand = Path.Combine(match, "audiowin_settings.json");
                            if (File.Exists(settingsCand)) MigrateFile(settingsCand, SettingsPath);
                            
                            return;
                        }

                        // Also subdirs
                        try
                        {
                            var subdirs = Directory.GetDirectories(match, "*", SearchOption.AllDirectories);
                            foreach (var subdir in subdirs)
                            {
                                if (subdir.Equals(currentExeDir, StringComparison.OrdinalIgnoreCase)) continue;

                                var subCand = Path.Combine(subdir, "audiowin_data.json");
                                if (File.Exists(subCand))
                                {
                                    MigrateFile(subCand, DataPath);
                                    
                                    var statsSub = Path.Combine(subdir, "audiowin_stats.json");
                                    if (File.Exists(statsSub)) MigrateFile(statsSub, StatsPath);
                                    
                                    var settingsSub = Path.Combine(subdir, "audiowin_settings.json");
                                    if (File.Exists(settingsSub)) MigrateFile(settingsSub, SettingsPath);
                                    
                                    return;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void Save(ObservableCollection<Playlist> playlists)
        {
            try {
                var json = JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DataPath, json);
            } catch { }
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
            try {
                var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StatsPath, json);
            } catch { }
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
            try {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            } catch { }
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
