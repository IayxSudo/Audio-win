using System;
using System.Threading;
using DiscordRPC;
using DiscordRPC.Logging;

namespace AudioWin
{
    /// <summary>Immutable description of what should be on screen in Discord right now.</summary>
    public class PresenceSnapshot
    {
        public string Title;
        public string Artist;
        public string Album;
        public string ArtworkUrl;
        public string SourceUrl;
        public double Elapsed;
        public double Total;
        public bool IsPlaying;
        public bool IsIdle;

        public bool SameAs(PresenceSnapshot o)
        {
            if (o == null) return false;
            return Title == o.Title
                && Artist == o.Artist
                && ArtworkUrl == o.ArtworkUrl
                && IsPlaying == o.IsPlaying
                && IsIdle == o.IsIdle
                && Math.Abs(Elapsed - o.Elapsed) < 2.0
                && Math.Abs(Total - o.Total) < 1.0;
        }
    }

    /// <summary>
    /// Discord Rich Presence.
    ///
    /// Rewritten around a coalescing queue. The previous version called
    /// SetPresence directly from the seek slider's ValueChanged handler, which
    /// fires on every pixel of a drag — far past Discord's ~5 updates/20s rate
    /// limit, so the presence would freeze or get dropped entirely. Now every
    /// caller just describes the desired state and a timer pushes at most one
    /// update every couple of seconds, always the newest one.
    /// </summary>
    public class DiscordRpcService : IDisposable
    {
        // Create your own application at https://discord.com/developers/applications
        // and paste its Application ID here to control the name and icon Discord shows.
        private const string DiscordAppId = "1511314008563646545";

        private const string FallbackArtwork =
            "https://cdn.discordapp.com/app-icons/1511314008563646545/a1e2392ee7a1c6725fa481fe28629932.png";

        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2.2);

        private DiscordRpcClient client;
        private Timer flushTimer;
        private readonly object sync = new object();

        private PresenceSnapshot pending;
        private PresenceSnapshot lastSent;
        private DateTime lastSendUtc = DateTime.MinValue;
        private bool connected;
        private bool disposed;

        public bool Enabled { get; set; } = true;
        public bool ShowSourceButton { get; set; } = true;

        public void Initialize()
        {
            lock (sync)
            {
                if (client != null || disposed) return;
                try
                {
                    client = new DiscordRpcClient(DiscordAppId);
                    client.Logger = new ConsoleLogger { Level = LogLevel.Error };

                    client.OnReady += (s, e) =>
                    {
                        lock (sync) { connected = true; lastSent = null; }
                        Flush(null); // re-push whatever we have as soon as we're live
                    };
                    client.OnConnectionFailed += (s, e) => { lock (sync) connected = false; };
                    client.OnClose += (s, e) => { lock (sync) connected = false; };

                    client.Initialize();

                    flushTimer = new Timer(Flush, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
                catch
                {
                    // Discord not installed or the pipe is unavailable. Not fatal.
                    client = null;
                }
            }
        }

        /// <summary>Queue a presence update. Cheap; safe to call as often as you like.</summary>
        public void Update(PresenceSnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (sync) { pending = snapshot; }

            // Push straight away when it has been quiet, so starting a song feels instant.
            if (DateTime.UtcNow - lastSendUtc > MinInterval) Flush(null);
        }

        public void SetIdle()
        {
            Update(new PresenceSnapshot { IsIdle = true });
        }

        private void Flush(object _)
        {
            PresenceSnapshot snap;
            lock (sync)
            {
                if (disposed || client == null || !Enabled) return;
                if (pending == null) return;
                if (DateTime.UtcNow - lastSendUtc < MinInterval) return;
                if (pending.SameAs(lastSent)) return;

                snap = pending;
                lastSent = snap;
                lastSendUtc = DateTime.UtcNow;
            }

            try
            {
                if (snap.IsIdle || string.IsNullOrWhiteSpace(snap.Title))
                {
                    client.SetPresence(new RichPresence
                    {
                        Type = ActivityType.Listening,
                        Details = "Idle",
                        State = "Nothing playing",
                        Assets = new Assets
                        {
                            LargeImageKey = FallbackArtwork,
                            LargeImageText = "AudioWin"
                        }
                    });
                    return;
                }

                var presence = new RichPresence
                {
                    Type = ActivityType.Listening,
                    Details = Clamp(snap.Title, 128),
                    State = Clamp(FormatState(snap), 128),
                    Assets = new Assets
                    {
                        LargeImageKey = ResolveArtwork(snap.ArtworkUrl),
                        LargeImageText = Clamp(
                            string.IsNullOrWhiteSpace(snap.Album) ? "Playing in AudioWin" : snap.Album, 128),
                        SmallImageText = snap.IsPlaying ? "Playing" : "Paused"
                    }
                };

                if (snap.IsPlaying && snap.Total > 1)
                {
                    // Discord derives the scrubber from absolute wall-clock times.
                    var start = DateTime.UtcNow.AddSeconds(-Math.Max(0, snap.Elapsed));
                    presence.Timestamps = new Timestamps
                    {
                        Start = start,
                        End = start.AddSeconds(snap.Total)
                    };
                }
                else if (!snap.IsPlaying)
                {
                    // No timestamps at all while paused, otherwise Discord keeps
                    // counting up as if the song were still running.
                    presence.State = Clamp("⏸ " + FormatState(snap), 128);
                }

                if (ShowSourceButton)
                {
                    var buttons = new System.Collections.Generic.List<DiscordRPC.Button>();
                    buttons.Add(new DiscordRPC.Button { Label = "Download", Url = "https://audiowin.lol" });

                    if (IsHttpUrl(snap.SourceUrl))
                    {
                        buttons.Add(new DiscordRPC.Button { Label = "Listen along", Url = snap.SourceUrl });
                    }

                    presence.Buttons = buttons.ToArray();
                }

                client.SetPresence(presence);
            }
            catch { /* presence is cosmetic; never let it break playback */ }
        }

        private static string FormatState(PresenceSnapshot s)
        {
            string artist = string.IsNullOrWhiteSpace(s.Artist) ? "Unknown Artist" : s.Artist;
            return "by " + artist;
        }

        /// <summary>
        /// Discord can only fetch artwork over http(s). Embedded album art is
        /// extracted to a local file, which it cannot read, so fall back to the
        /// app icon rather than showing a broken image.
        /// </summary>
        private static string ResolveArtwork(string art)
            => IsHttpUrl(art) ? art : FallbackArtwork;

        private static bool IsHttpUrl(string s)
            => !string.IsNullOrWhiteSpace(s) &&
               (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        private static string Clamp(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Discord requires 2..128 characters for these fields.
            if (s.Length == 1) s += " ";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public void ClearPresence()
        {
            lock (sync) { pending = null; lastSent = null; }
            try { client?.ClearPresence(); } catch { }
        }

        /// <summary>Turn the integration on or off without restarting the app.</summary>
        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (enabled)
            {
                Initialize();
                lock (sync) { lastSent = null; }
                Flush(null);
            }
            else
            {
                lock (sync)
                {
                    try { flushTimer?.Dispose(); } catch { }
                    flushTimer = null;

                    try { client?.ClearPresence(); } catch { }
                    try { client?.Dispose(); } catch { }
                    client = null;
                    connected = false;
                    lastSent = null;
                    pending = null;
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }

            try { flushTimer?.Dispose(); } catch { }
            flushTimer = null;

            try { client?.ClearPresence(); } catch { }
            try { client?.Dispose(); } catch { }
            client = null;
        }
    }
}
