using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace AudioWin
{
    public static class StorageManager
    {
        public static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioWin");

        private static readonly string DataPath = Path.Combine(AppDataFolder, "audiowin_data.json");
        private static readonly string StatsPath = Path.Combine(AppDataFolder, "audiowin_stats.json");
        private static readonly string SettingsPath = Path.Combine(AppDataFolder, "audiowin_settings.json");
        public static readonly string LogPath = Path.Combine(AppDataFolder, "audiowin_log.txt");

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly object writeLock = new object();

        static StorageManager()
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);

                MigrateFile(Path.Combine(AppContext.BaseDirectory, "audiowin_data.json"), DataPath);
                MigrateFile(Path.Combine(AppContext.BaseDirectory, "audiowin_stats.json"), StatsPath);
                MigrateFile(Path.Combine(AppContext.BaseDirectory, "audiowin_settings.json"), SettingsPath);

                AutoMigrateFromAnywhere();
            }
            catch { }
        }

        // ==================================================================
        // Cache Management
        // ==================================================================

        public static string GetTempAudioFile(string trackId)
        {
            var cacheDir = Path.Combine(AppDataFolder, "Cache");
            Directory.CreateDirectory(cacheDir);
            // using .mp4 / .m4a generic extension since MediaFoundationReader likes extensions
            return Path.Combine(cacheDir, trackId + ".m4a");
        }

        public static void ClearCache()
        {
            try
            {
                var cacheDir = Path.Combine(AppDataFolder, "Cache");
                if (Directory.Exists(cacheDir))
                {
                    foreach (var file in Directory.GetFiles(cacheDir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        // ==================================================================
        // Public API
        // ==================================================================

        public static void Save(ObservableCollection<Playlist> playlists)
            => WriteAtomic(DataPath, JsonSerializer.Serialize(playlists, WriteOptions));

        public static ObservableCollection<Playlist> Load()
        {
            var loaded = ReadJson<ObservableCollection<Playlist>>(DataPath);
            if (loaded == null) return null;

            // Repair anything the older schema didn't carry.
            foreach (var p in loaded)
            {
                if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.Icon)) p.Icon = p.Initial;
                p.Tracks ??= new ObservableCollection<Track>();

                int i = 1;
                foreach (var t in p.Tracks)
                {
                    if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N");
                    if (t.DurationSeconds <= 0) t.DurationSeconds = ParseDuration(t.Duration);
                    if (string.IsNullOrWhiteSpace(t.Duration)) t.Duration = LinkImport.Format(t.DurationSeconds);
                    if (string.IsNullOrWhiteSpace(t.Artist)) t.Artist = "Unknown Artist";
                    if (string.IsNullOrWhiteSpace(t.Title))
                    {
                        try { t.Title = Path.GetFileNameWithoutExtension(t.FilePath ?? "") ; } catch { }
                        if (string.IsNullOrWhiteSpace(t.Title)) t.Title = "Unknown Title";
                    }
                    t.Index = i++;
                }
            }
            return loaded;
        }

        public static void SaveStats(AppStats stats)
            => WriteAtomic(StatsPath, JsonSerializer.Serialize(stats, WriteOptions));

        public static AppStats LoadStats() => ReadJson<AppStats>(StatsPath) ?? new AppStats();

        public static void SaveSettings(PlaybackSettings settings)
            => WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, WriteOptions));

        public static PlaybackSettings LoadSettings()
        {
            var s = ReadJson<PlaybackSettings>(SettingsPath) ?? new PlaybackSettings();
            s.Normalize();
            return s;
        }

        public static double ParseDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var parts = text.Split(':');
            try
            {
                if (parts.Length == 2)
                    return int.Parse(parts[0]) * 60 + int.Parse(parts[1]);
                if (parts.Length == 3)
                    return int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]);
            }
            catch { }
            return 0;
        }

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        // ==================================================================
        // Internals
        // ==================================================================

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<T>(json, ReadOptions);
            }
            catch (Exception ex)
            {
                Log("Failed to read " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Writes to a temp file then swaps it in. A crash or power cut halfway
        /// through a direct File.WriteAllText leaves a truncated JSON file, which
        /// is exactly how people lose every playlist they own.
        /// </summary>
        private static void WriteAtomic(string path, string contents)
        {
            lock (writeLock)
            {
                try
                {
                    Directory.CreateDirectory(AppDataFolder);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, contents);

                    if (File.Exists(path))
                    {
                        var backup = path + ".bak";
                        try { File.Replace(tmp, path, backup, true); }
                        catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                }
                catch (Exception ex)
                {
                    Log("Failed to write " + Path.GetFileName(path) + ": " + ex.Message);
                }
            }
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

        /// <summary>
        /// Looks for data left behind by an older copy of AudioWin (next to the exe,
        /// on the Desktop, in Downloads) so upgrading by unzipping into a new folder
        /// doesn't look like all your playlists vanished.
        /// </summary>
        private static void AutoMigrateFromAnywhere()
        {
            try
            {
                if (File.Exists(DataPath)) return;

                var currentDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                var roots = new List<string>();

                var parent = Path.GetDirectoryName(currentDir);
                if (!string.IsNullOrEmpty(parent)) roots.Add(parent);
                roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

                foreach (var root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                    string[] matches;
                    try { matches = Directory.GetDirectories(root, "*AudioWin*", SearchOption.TopDirectoryOnly); }
                    catch { continue; }

                    foreach (var dir in matches)
                    {
                        if (dir.Equals(currentDir, StringComparison.OrdinalIgnoreCase)) continue;
                        if (TryMigrateFrom(dir)) return;

                        string[] subs;
                        try { subs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories); }
                        catch { continue; }

                        foreach (var sub in subs)
                        {
                            if (sub.Equals(currentDir, StringComparison.OrdinalIgnoreCase)) continue;
                            if (TryMigrateFrom(sub)) return;
                        }
                    }
                }
            }
            catch { }
        }

        private static bool TryMigrateFrom(string dir)
        {
            try
            {
                var candidate = Path.Combine(dir, "audiowin_data.json");
                if (!File.Exists(candidate)) return false;

                MigrateFile(candidate, DataPath);
                MigrateFile(Path.Combine(dir, "audiowin_stats.json"), StatsPath);
                MigrateFile(Path.Combine(dir, "audiowin_settings.json"), SettingsPath);
                Log("Migrated existing library from " + dir);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Coalesces save requests. The old build called SaveData() from every volume
    /// slider tick and every EQ slider tick, writing three JSON files to disk on
    /// each pixel of a drag. This batches them into one write shortly after you
    /// stop fiddling.
    /// </summary>
    public class SaveScheduler : IDisposable
    {
        private readonly Action saveAction;
        private readonly Timer timer;
        private readonly object sync = new object();
        private bool dirty;
        private bool disposed;

        public SaveScheduler(Action saveAction, int delayMs = 700)
        {
            this.saveAction = saveAction;
            timer = new Timer(_ => Tick(), null, delayMs, delayMs);
        }

        public void Request()
        {
            lock (sync) dirty = true;
        }

        private void Tick()
        {
            lock (sync)
            {
                if (disposed || !dirty) return;
                dirty = false;
            }
            try { saveAction(); } catch { }
        }

        /// <summary>Write immediately if anything is outstanding (used on shutdown).</summary>
        public void FlushNow()
        {
            lock (sync) dirty = false;
            try { saveAction(); } catch { }
        }

        public void Dispose()
        {
            lock (sync) disposed = true;
            try { timer?.Dispose(); } catch { }
        }
    }
}
