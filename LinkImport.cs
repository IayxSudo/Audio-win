using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YoutubeExplode;

namespace AudioWin
{
    public class ImportResult
    {
        public bool Success;
        public string Error;
        public string Note;
        public string CollectionName;
        public List<Track> Tracks = new List<Track>();
    }

    /// <summary>
    /// Turns a YouTube or Spotify URL into playlist entries.
    ///
    /// Two tiers, on purpose:
    ///
    ///  * No credentials — uses each service's public oEmbed endpoint, which is
    ///    designed for exactly this and needs no key. You get the real title,
    ///    the uploader/artist and the cover art for a single track.
    ///
    ///  * With your own free API key — uses the official YouTube Data API and
    ///    Spotify Web API, which unlocks whole playlists and albums: every
    ///    track's title, artist, artwork and length in one paste.
    ///
    /// AudioWin stores the resulting entries as references. It does not download
    /// or decrypt audio from either service; a reference plays once you point it
    /// at a local file you already have (see MatchToLocalFiles), and otherwise
    /// opens in the browser or desktop app.
    /// </summary>
    public static class LinkImport
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AudioWin/2.0 (+desktop music player)");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        public static bool LooksLikeUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);
        }

        public static TrackSource DetectSource(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return TrackSource.Web;
            var u = url.ToLowerInvariant();
            if (u.Contains("youtube.com") || u.Contains("youtu.be")) return TrackSource.YouTube;
            if (u.Contains("spotify.com") || u.StartsWith("spotify:")) return TrackSource.Spotify;
            if (u.Contains("soundcloud.com")) return TrackSource.SoundCloud;
            return TrackSource.Web;
        }

        public static async Task<ImportResult> ImportAsync(string url, PlaybackSettings settings)
        {
            var result = new ImportResult();
            if (!LooksLikeUrl(url))
            {
                result.Error = "That doesn't look like a link. Paste a YouTube, Spotify, or SoundCloud URL.";
                return result;
            }

            url = url.Trim();

            try
            {
                switch (DetectSource(url))
                {
                    case TrackSource.YouTube: return await ImportYouTubeAsync(url, settings);
                    case TrackSource.Spotify: return await ImportSpotifyAsync(url, settings);
                    case TrackSource.SoundCloud: return await ImportSoundCloudAsync(url);
                    default: return await ImportGenericAsync(url);
                }
            }
            catch (TaskCanceledException)
            {
                result.Error = "The request timed out. Check your internet connection and try again.";
                return result;
            }
            catch (HttpRequestException ex)
            {
                result.Error = "Couldn't reach the service: " + ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Import failed: " + ex.Message;
                return result;
            }
        }

        // ==================================================================
        // YouTube
        // ==================================================================

        public static string GetYouTubeVideoId(string url)
        {
            var m = Regex.Match(url, @"(?:youtu\.be/|v=|/shorts/|/embed/|/live/)([A-Za-z0-9_\-]{11})");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string GetYouTubePlaylistId(string url)
        {
            var m = Regex.Match(url, @"[?&]list=([A-Za-z0-9_\-]+)");
            if (!m.Success) return null;
            var id = m.Groups[1].Value;
            // "RD..." mixes and the personal watch-later list aren't readable by the API.
            if (id.StartsWith("RD") || id == "WL" || id == "LL") return null;
            return id;
        }

        private static async Task<ImportResult> ImportYouTubeAsync(string url, PlaybackSettings settings)
        {
            var result = new ImportResult();
            string playlistId = GetYouTubePlaylistId(url);
            string videoId = GetYouTubeVideoId(url);
            string key = settings?.YouTubeApiKey?.Trim();

            if (!string.IsNullOrEmpty(playlistId))
            {
                if (!string.IsNullOrEmpty(key))
                {
                    var apiRes = await ImportYouTubePlaylistAsync(playlistId, key);
                    if (apiRes.Success && apiRes.Tracks.Count > 0) return apiRes;
                }

                var explodeRes = await ImportYouTubePlaylistViaExplodeAsync(playlistId);
                if (explodeRes.Success && explodeRes.Tracks.Count > 0) return explodeRes;

                if (string.IsNullOrEmpty(videoId))
                {
                    result.Error = explodeRes.Error ?? "That's a playlist link. Add a free YouTube Data API key in Settings to import every video in it.";
                    return result;
                }
            }

            if (string.IsNullOrEmpty(videoId))
            {
                result.Error = "Couldn't find a video ID in that YouTube link.";
                return result;
            }

            var track = await FetchYouTubeVideoViaOEmbedAsync(videoId);
            if (track == null)
            {
                result.Error = "That video couldn't be read. It may be private, age-restricted or removed.";
                return result;
            }

            result.Success = true;
            result.Tracks.Add(track);
            if (!string.IsNullOrEmpty(playlistId))
                result.Note = "Imported just this video. Add a YouTube API key in Settings to import the whole playlist.";
            return result;
        }

        private static async Task<Track> FetchYouTubeVideoViaOEmbedAsync(string videoId)
        {
            string watch = "https://www.youtube.com/watch?v=" + videoId;
            string endpoint = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(watch);

            using var resp = await Http.GetAsync(endpoint);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string rawTitle = Str(root, "title") ?? "Unknown title";
            string author = Str(root, "author_name");
            string thumb = Str(root, "thumbnail_url")
                           ?? $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            var (title, artist) = TitleSplitter.Split(rawTitle, author);

            return new Track
            {
                Title = title,
                Artist = artist,
                Source = TrackSource.YouTube,
                SourceUrl = watch,
                ImagePath = thumb,
                Duration = "--:--",
                DurationSeconds = 0
            };
        }

        private static async Task<ImportResult> ImportYouTubePlaylistAsync(string playlistId, string key)
        {
            var result = new ImportResult();
            var tracks = new List<Track>();
            var idsForDuration = new List<string>();

            string pageToken = null;
            int pages = 0;

            do
            {
                string endpoint =
                    "https://www.googleapis.com/youtube/v3/playlistItems" +
                    "?part=snippet,contentDetails&maxResults=50&playlistId=" + Uri.EscapeDataString(playlistId) +
                    "&key=" + Uri.EscapeDataString(key) +
                    (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");

                using var resp = await Http.GetAsync(endpoint);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    result.Error = DescribeYouTubeError(body, (int)resp.StatusCode);
                    return result;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("snippet", out var sn)) continue;

                        string rawTitle = Str(sn, "title");
                        if (string.IsNullOrWhiteSpace(rawTitle)) continue;
                        // The API keeps tombstones for removed entries.
                        if (rawTitle == "Deleted video" || rawTitle == "Private video") continue;

                        string vid = null;
                        if (item.TryGetProperty("contentDetails", out var cd))
                            vid = Str(cd, "videoId");
                        if (string.IsNullOrEmpty(vid)) continue;

                        string channel = Str(sn, "videoOwnerChannelTitle") ?? Str(sn, "channelTitle");
                        var (title, artist) = TitleSplitter.Split(rawTitle, channel);

                        tracks.Add(new Track
                        {
                            Title = title,
                            Artist = artist,
                            Source = TrackSource.YouTube,
                            SourceUrl = "https://www.youtube.com/watch?v=" + vid,
                            ImagePath = BestThumb(sn) ?? $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
                            Duration = "--:--"
                        });
                        idsForDuration.Add(vid);
                    }
                }

                pageToken = root.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
                pages++;
            }
            while (!string.IsNullOrEmpty(pageToken) && pages < 10); // hard cap ~500 items

            if (tracks.Count == 0)
            {
                result.Error = "That playlist came back empty, or it isn't public.";
                return result;
            }

            await FillYouTubeDurationsAsync(tracks, idsForDuration, key);

            result.Success = true;
            result.Tracks = tracks;
            result.CollectionName = await FetchYouTubePlaylistTitleAsync(playlistId, key);
            if (pages >= 10) result.Note = "Imported the first 500 videos.";
            return result;
        }

        private static async Task FillYouTubeDurationsAsync(List<Track> tracks, List<string> ids, string key)
        {
            try
            {
                for (int i = 0; i < ids.Count; i += 50)
                {
                    var batch = ids.Skip(i).Take(50).ToList();
                    string endpoint = "https://www.googleapis.com/youtube/v3/videos?part=contentDetails&id="
                                    + Uri.EscapeDataString(string.Join(",", batch))
                                    + "&key=" + Uri.EscapeDataString(key);

                    using var resp = await Http.GetAsync(endpoint);
                    if (!resp.IsSuccessStatusCode) return;

                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("items", out var items)) return;

                    foreach (var item in items.EnumerateArray())
                    {
                        string id = Str(item, "id");
                        if (id == null) continue;
                        if (!item.TryGetProperty("contentDetails", out var cd)) continue;
                        double secs = ParseIso8601Duration(Str(cd, "duration"));
                        if (secs <= 0) continue;

                        var t = tracks.FirstOrDefault(x => x.SourceUrl != null && x.SourceUrl.EndsWith("=" + id));
                        if (t != null)
                        {
                            t.DurationSeconds = secs;
                            t.Duration = Format(secs);
                        }
                    }
                }
            }
            catch { /* durations are a nicety, never fail the import over them */ }
        }

        private static async Task<string> FetchYouTubePlaylistTitleAsync(string playlistId, string key)
        {
            try
            {
                string endpoint = "https://www.googleapis.com/youtube/v3/playlists?part=snippet&id="
                                + Uri.EscapeDataString(playlistId) + "&key=" + Uri.EscapeDataString(key);
                using var resp = await Http.GetAsync(endpoint);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("items", out var items))
                    foreach (var it in items.EnumerateArray())
                        if (it.TryGetProperty("snippet", out var sn))
                            return Str(sn, "title");
            }
            catch { }
            return null;
        }

        private static string DescribeYouTubeError(string body, int status)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    string msg = Str(err, "message");
                    string reason = null;
                    if (err.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                        foreach (var e in errs.EnumerateArray()) { reason = Str(e, "reason"); break; }

                    if (reason == "quotaExceeded")
                        return "Your YouTube API key has hit its daily quota. It resets at midnight Pacific time.";
                    if (reason == "playlistNotFound")
                        return "That playlist wasn't found. Private playlists can't be imported.";
                    if (status == 400 || reason == "keyInvalid")
                        return "That YouTube API key was rejected. Check it in Settings.";
                    if (!string.IsNullOrEmpty(msg)) return "YouTube said: " + msg;
                }
            }
            catch { }
            return "YouTube returned HTTP " + status + ".";
        }

        private static string BestThumb(JsonElement snippet)
        {
            if (!snippet.TryGetProperty("thumbnails", out var th)) return null;
            foreach (var name in new[] { "maxres", "standard", "high", "medium", "default" })
                if (th.TryGetProperty(name, out var t))
                {
                    var u = Str(t, "url");
                    if (!string.IsNullOrEmpty(u)) return u;
                }
            return null;
        }

        private static double ParseIso8601Duration(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return 0;
            var m = Regex.Match(iso, @"^P(?:(\d+)D)?T?(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?$");
            if (!m.Success) return 0;
            double d = m.Groups[1].Success ? double.Parse(m.Groups[1].Value) : 0;
            double h = m.Groups[2].Success ? double.Parse(m.Groups[2].Value) : 0;
            double mi = m.Groups[3].Success ? double.Parse(m.Groups[3].Value) : 0;
            double s = m.Groups[4].Success ? double.Parse(m.Groups[4].Value) : 0;
            return d * 86400 + h * 3600 + mi * 60 + s;
        }

        // ==================================================================
        // Spotify
        // ==================================================================

        private static string spotifyToken;
        private static DateTime spotifyTokenExpiry = DateTime.MinValue;

        public static (string kind, string id) ParseSpotifyUrl(string url)
        {
            var m = Regex.Match(url, @"spotify[:/]+(track|album|playlist)[:/]+([A-Za-z0-9]+)");
            if (m.Success) return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);

            m = Regex.Match(url, @"open\.spotify\.com/(?:intl-[a-z\-]+/)?(track|album|playlist)/([A-Za-z0-9]+)");
            if (m.Success) return (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);

            return (null, null);
        }

        private static async Task<ImportResult> ImportSpotifyAsync(string url, PlaybackSettings settings)
        {
            var result = new ImportResult();
            var (kind, id) = ParseSpotifyUrl(url);

            if (kind == null)
            {
                result.Error = "Couldn't recognise that Spotify link. Use a track, album or playlist URL.";
                return result;
            }

            bool haveCreds = !string.IsNullOrWhiteSpace(settings?.SpotifyClientId)
                          && !string.IsNullOrWhiteSpace(settings?.SpotifyClientSecret);

            if (haveCreds)
            {
                var api = await ImportSpotifyViaApiAsync(kind, id, settings);
                // If the API path succeeds and has tracks, return it
                if (api.Success && api.Tracks.Count > 0) return api;
            }

            if (kind != "track")
            {
                // Fall back to public Spotify embed parser for playlists and albums
                var embedResult = await ImportSpotifyViaEmbedAsync(kind, id, url);
                if (embedResult.Success && embedResult.Tracks.Count > 0) return embedResult;

                result.Error = embedResult.Error ?? "Could not read that Spotify playlist/album.";
                return result;
            }

            var track = await FetchSpotifyViaOEmbedAsync(url);
            if (track == null)
            {
                result.Error = "That Spotify link couldn't be read.";
                return result;
            }

            result.Success = true;
            result.Tracks.Add(track);
            return result;
        }

        private static async Task<Track> FetchSpotifyViaOEmbedAsync(string url)
        {
            string endpoint = "https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(url);
            using var resp = await Http.GetAsync(endpoint);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string rawTitle = Str(root, "title") ?? "Unknown track";
            string thumb = Str(root, "thumbnail_url");
            var (title, artist) = TitleSplitter.Split(rawTitle, null);

            // The oEmbed API frequently omits the artist entirely. Scrape the HTML title to get the real metadata.
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                using var htmlResp = await Http.SendAsync(req);
                if (htmlResp.IsSuccessStatusCode)
                {
                    string html = await htmlResp.Content.ReadAsStringAsync();
                    var match = Regex.Match(html, @"<title>(.*?)</title>");
                    if (match.Success)
                    {
                        string pageTitle = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                        int pipeIndex = pageTitle.LastIndexOf(" | Spotify");
                        if (pipeIndex > 0)
                        {
                            pageTitle = pageTitle.Substring(0, pipeIndex).Trim();
                            int lastBy = pageTitle.LastIndexOf(" by ");
                            if (lastBy > 0)
                            {
                                artist = pageTitle.Substring(lastBy + 4).Trim();
                                string beforeBy = pageTitle.Substring(0, lastBy).Trim();
                                int dashIndex = beforeBy.LastIndexOf(" - ");
                                if (dashIndex > 0)
                                    title = beforeBy.Substring(0, dashIndex).Trim();
                                else
                                    title = beforeBy;
                            }
                        }
                    }
                }
            }
            catch { }

            return new Track
            {
                Title = title,
                Artist = artist ?? "Unknown Artist",
                Source = TrackSource.Spotify,
                SourceUrl = url,
                ImagePath = thumb,
                Duration = "--:--"
            };
        }

        private static async Task<string> GetSpotifyTokenAsync(PlaybackSettings settings)
        {
            if (spotifyToken != null && DateTime.UtcNow < spotifyTokenExpiry) return spotifyToken;

            var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                settings.SpotifyClientId.Trim() + ":" + settings.SpotifyClientSecret.Trim()));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string token = Str(doc.RootElement, "access_token");
            int expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int v) ? v : 3600;

            spotifyToken = token;
            spotifyTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 60));
            return token;
        }

        private static async Task<JsonDocument> SpotifyGetAsync(string endpoint, string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        private static async Task<ImportResult> ImportSpotifyViaApiAsync(string kind, string id, PlaybackSettings settings)
        {
            var result = new ImportResult();

            string token = await GetSpotifyTokenAsync(settings);
            if (token == null)
            {
                result.Error = "Spotify rejected those credentials. Double-check the Client ID and Secret in Settings.";
                return result;
            }

            if (kind == "track")
            {
                using var doc = await SpotifyGetAsync("https://api.spotify.com/v1/tracks/" + id, token);
                if (doc == null) { result.Error = "That Spotify track couldn't be read."; return result; }
                var t = ReadSpotifyTrack(doc.RootElement, null);
                if (t == null) { result.Error = "That Spotify track couldn't be read."; return result; }
                result.Success = true;
                result.Tracks.Add(t);
                return result;
            }

            if (kind == "album")
            {
                using var albumDoc = await SpotifyGetAsync("https://api.spotify.com/v1/albums/" + id, token);
                if (albumDoc == null) { result.Error = "That album couldn't be read."; return result; }

                var album = albumDoc.RootElement;
                result.CollectionName = Str(album, "name");
                string cover = FirstImage(album);

                if (album.TryGetProperty("tracks", out var tracksNode) &&
                    tracksNode.TryGetProperty("items", out var items))
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        var t = ReadSpotifyTrack(it, cover);
                        if (t != null) { t.Album = result.CollectionName; result.Tracks.Add(t); }
                    }
                }

                // Albums longer than 50 tracks are paged.
                string next = Next(album, "tracks");
                int guard = 0;
                while (!string.IsNullOrEmpty(next) && guard++ < 10)
                {
                    using var page = await SpotifyGetAsync(next, token);
                    if (page == null) break;
                    if (page.RootElement.TryGetProperty("items", out var more))
                        foreach (var it in more.EnumerateArray())
                        {
                            var t = ReadSpotifyTrack(it, cover);
                            if (t != null) { t.Album = result.CollectionName; result.Tracks.Add(t); }
                        }
                    next = Str(page.RootElement, "next");
                }

                result.Success = result.Tracks.Count > 0;
                if (!result.Success) result.Error = "That album came back empty.";
                return result;
            }

            // playlist
            {
                using var meta = await SpotifyGetAsync("https://api.spotify.com/v1/playlists/" + id + "?fields=name", token);
                result.CollectionName = meta != null ? Str(meta.RootElement, "name") : null;

                string endpoint = "https://api.spotify.com/v1/playlists/" + id + "/tracks?limit=100";
                int guard = 0;

                while (!string.IsNullOrEmpty(endpoint) && guard++ < 20)
                {
                    using var doc = await SpotifyGetAsync(endpoint, token);
                    if (doc == null)
                    {
                        if (result.Tracks.Count == 0)
                            result.Error = "That playlist couldn't be read. Spotify's own editorial playlists aren't available to third-party apps — user-made public playlists work.";
                        break;
                    }

                    if (doc.RootElement.TryGetProperty("items", out var items))
                        foreach (var wrapper in items.EnumerateArray())
                        {
                            if (!wrapper.TryGetProperty("track", out var tr) || tr.ValueKind != JsonValueKind.Object) continue;
                            var t = ReadSpotifyTrack(tr, null);
                            if (t != null) result.Tracks.Add(t);
                        }

                    endpoint = Str(doc.RootElement, "next");
                }

                result.Success = result.Tracks.Count > 0;
                if (!result.Success && result.Error == null) result.Error = "That playlist came back empty.";
                return result;
            }
        }

        private static Track ReadSpotifyTrack(JsonElement tr, string fallbackCover)
        {
            try
            {
                string name = Str(tr, "name");
                if (string.IsNullOrWhiteSpace(name)) return null;

                string artists = "Unknown Artist";
                if (tr.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var names = arr.EnumerateArray().Select(a => Str(a, "name")).Where(s => !string.IsNullOrEmpty(s));
                    var joined = string.Join(", ", names);
                    if (!string.IsNullOrWhiteSpace(joined)) artists = joined;
                }

                string cover = fallbackCover;
                string album = null;
                if (tr.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
                {
                    album = Str(alb, "name");
                    cover = FirstImage(alb) ?? cover;
                }

                double secs = 0;
                if (tr.TryGetProperty("duration_ms", out var ms) && ms.TryGetInt64(out long msv)) secs = msv / 1000.0;

                string url = null;
                if (tr.TryGetProperty("external_urls", out var ext)) url = Str(ext, "spotify");
                if (string.IsNullOrEmpty(url))
                {
                    string tid = Str(tr, "id");
                    if (!string.IsNullOrEmpty(tid)) url = "https://open.spotify.com/track/" + tid;
                }

                return new Track
                {
                    Title = name,
                    Artist = artists,
                    Album = album,
                    Source = TrackSource.Spotify,
                    SourceUrl = url,
                    ImagePath = cover,
                    DurationSeconds = secs,
                    Duration = secs > 0 ? Format(secs) : "--:--"
                };
            }
            catch { return null; }
        }

        private static string FirstImage(JsonElement node)
        {
            if (node.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var i in imgs.EnumerateArray())
                {
                    var u = Str(i, "url");
                    if (!string.IsNullOrEmpty(u)) return u;
                }
            return null;
        }

        private static string Next(JsonElement parent, string child)
        {
            if (parent.TryGetProperty(child, out var c)) return Str(c, "next");
            return null;
        }

        private static async Task<ImportResult> ImportYouTubePlaylistViaExplodeAsync(string playlistId)
        {
            var result = new ImportResult();
            var youtube = new YoutubeClient();
            try
            {
                var playlist = await youtube.Playlists.GetAsync(playlistId);
                result.CollectionName = playlist.Title;

                var tracks = new List<Track>();
                await foreach (var video in youtube.Playlists.GetVideosAsync(playlistId))
                {
                    if (string.IsNullOrWhiteSpace(video.Title)) continue;
                    var (title, artist) = TitleSplitter.Split(video.Title, video.Author.ChannelTitle);
                    double secs = video.Duration?.TotalSeconds ?? 0;
                    string thumb = $"https://i.ytimg.com/vi/{video.Id}/hqdefault.jpg";

                    tracks.Add(new Track
                    {
                        Title = title,
                        Artist = artist,
                        Source = TrackSource.YouTube,
                        SourceUrl = video.Url,
                        ImagePath = thumb,
                        DurationSeconds = secs,
                        Duration = secs > 0 ? Format(secs) : "--:--"
                    });

                    if (tracks.Count >= 500) break;
                }

                result.Tracks = tracks;
                result.Success = tracks.Count > 0;
                if (!result.Success) result.Error = "That YouTube playlist came back empty.";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Couldn't read that YouTube playlist: " + ex.Message;
                return result;
            }
        }

        private static async Task<ImportResult> ImportSpotifyViaEmbedAsync(string kind, string id, string originalUrl)
        {
            var result = new ImportResult();
            try
            {
                string embedUrl = $"https://open.spotify.com/embed/{kind}/{id}";
                var req = new HttpRequestMessage(HttpMethod.Get, embedUrl);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    result.Error = $"Spotify returned HTTP {(int)resp.StatusCode} for that {kind}.";
                    return result;
                }

                string html = await resp.Content.ReadAsStringAsync();
                var m = Regex.Match(html, @"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>", RegexOptions.Singleline);
                if (!m.Success)
                {
                    result.Error = "Could not parse Spotify embed data.";
                    return result;
                }

                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                if (!doc.RootElement.TryGetProperty("props", out var props) ||
                    !props.TryGetProperty("pageProps", out var pageProps) ||
                    !pageProps.TryGetProperty("state", out var state) ||
                    !state.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("entity", out var entity))
                {
                    result.Error = "Spotify embed structure was unexpected.";
                    return result;
                }

                string collectionName = Str(entity, "name");
                if (string.IsNullOrWhiteSpace(collectionName))
                    collectionName = Str(entity, "title");
                result.CollectionName = collectionName;

                // Cover image
                string cover = null;
                if (entity.TryGetProperty("visualIdentity", out var vi) &&
                    vi.TryGetProperty("image", out var images) &&
                    images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in images.EnumerateArray())
                    {
                        var u = Str(img, "url");
                        if (!string.IsNullOrEmpty(u)) cover = u;
                    }
                }

                if (string.IsNullOrEmpty(cover) && entity.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attr in attrs.EnumerateArray())
                    {
                        if (Str(attr, "key") == "image_url")
                            cover = Str(attr, "value");
                    }
                }

                if (entity.TryGetProperty("trackList", out var trackList) && trackList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tr in trackList.EnumerateArray())
                    {
                        string title = Str(tr, "title");
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        string artist = Str(tr, "subtitle") ?? "Unknown Artist";
                        double secs = 0;
                        if (tr.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out long ms))
                            secs = ms / 1000.0;

                        string uri = Str(tr, "uri");
                        string trackUrl = null;
                        if (!string.IsNullOrEmpty(uri) && uri.StartsWith("spotify:track:"))
                            trackUrl = "https://open.spotify.com/track/" + uri.Substring("spotify:track:".Length);
                        else if (!string.IsNullOrEmpty(uri))
                            trackUrl = uri;

                        result.Tracks.Add(new Track
                        {
                            Title = title,
                            Artist = artist,
                            Album = kind == "album" ? collectionName : null,
                            Source = TrackSource.Spotify,
                            SourceUrl = trackUrl,
                            ImagePath = cover,
                            DurationSeconds = secs,
                            Duration = secs > 0 ? Format(secs) : "--:--"
                        });
                    }
                }

                result.Success = result.Tracks.Count > 0;
                if (!result.Success && string.IsNullOrEmpty(result.Error))
                    result.Error = $"That Spotify {kind} came back empty.";

                return result;
            }
            catch (Exception ex)
            {
                result.Error = $"Failed to import Spotify {kind}: {ex.Message}";
                return result;
            }
        }

        // ==================================================================
        // SoundCloud
        // ==================================================================

        private static string soundcloudClientId = "Pb72ranhoyt6gw7hM7TkzUItXlMWSNSo";
        private static readonly object scLock = new object();

        public static async Task<string> GetSoundCloudClientIdAsync(string html = null, System.Threading.CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(soundcloudClientId)) return soundcloudClientId;

            try
            {
                if (string.IsNullOrEmpty(html))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://soundcloud.com");
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    using var resp = await Http.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode) html = await resp.Content.ReadAsStringAsync();
                }

                if (!string.IsNullOrEmpty(html))
                {
                    var scriptMatches = Regex.Matches(html, @"<script[^>]+src=""([^""]+\.js)""");
                    for (int i = scriptMatches.Count - 1; i >= 0; i--)
                    {
                        string scriptUrl = scriptMatches[i].Groups[1].Value;
                        using var jsReq = new HttpRequestMessage(HttpMethod.Get, scriptUrl);
                        jsReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                        using var jsResp = await Http.SendAsync(jsReq, ct);
                        if (jsResp.IsSuccessStatusCode)
                        {
                            string js = await jsResp.Content.ReadAsStringAsync();
                            var m = Regex.Match(js, @"client_id[:=][""']([a-zA-Z0-9]{32})[""']");
                            if (m.Success)
                            {
                                lock (scLock) soundcloudClientId = m.Groups[1].Value;
                                return soundcloudClientId;
                            }
                        }
                    }
                }
            }
            catch { }

            return soundcloudClientId ?? "Pb72ranhoyt6gw7hM7TkzUItXlMWSNSo";
        }

        private static async Task<ImportResult> ImportSoundCloudAsync(string url)
        {
            var result = new ImportResult();

            // Resolve mobile shortlinks (e.g. on.soundcloud.com/...)
            if (url.Contains("on.soundcloud.com"))
            {
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Get, url);
                    headReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    using var headResp = await Http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead);
                    if (headResp.RequestMessage?.RequestUri != null)
                        url = headResp.RequestMessage.RequestUri.ToString();
                }
                catch { }
            }

            bool isSet = url.IndexOf("/sets/", StringComparison.OrdinalIgnoreCase) >= 0;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    return await ImportSoundCloudOEmbedAsync(url);
                }

                string html = await resp.Content.ReadAsStringAsync();
                var m = Regex.Match(html, @"window\.__sc_hydration\s*=\s*(\[.*?\]);", RegexOptions.Singleline);
                if (!m.Success)
                {
                    return await ImportSoundCloudOEmbedAsync(url);
                }

                string clientId = await GetSoundCloudClientIdAsync(html);

                using var doc = JsonDocument.Parse(m.Groups[1].Value);

                if (isSet)
                {
                    // Find playlist node
                    JsonElement? playlistNode = null;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (Str(item, "hydratable") == "playlist" && item.TryGetProperty("data", out var d))
                        {
                            playlistNode = d;
                            break;
                        }
                    }

                    if (playlistNode == null)
                    {
                        return await ImportSoundCloudOEmbedAsync(url);
                    }

                    var pl = playlistNode.Value;
                    string plTitle = Str(pl, "title") ?? "SoundCloud Playlist";
                    result.CollectionName = plTitle;
                    string plCover = Str(pl, "artwork_url");
                    if (!string.IsNullOrEmpty(plCover))
                        plCover = plCover.Replace("-large.jpg", "-t500x500.jpg");

                    var allTracks = new List<Track>();
                    var missingIds = new List<long>();

                    if (pl.TryGetProperty("tracks", out var tracksNode) && tracksNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tracksNode.EnumerateArray())
                        {
                            string tTitle = Str(t, "title");
                            if (!string.IsNullOrWhiteSpace(tTitle))
                            {
                                string artist = "Unknown Artist";
                                if (t.TryGetProperty("user", out var u)) artist = Str(u, "username") ?? artist;
                                string art = Str(t, "artwork_url");
                                if (!string.IsNullOrEmpty(art)) art = art.Replace("-large.jpg", "-t500x500.jpg");
                                double secs = 0;
                                if (t.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out long dms))
                                    secs = dms / 1000.0;
                                string trackUrl = Str(t, "permalink_url") ?? url;

                                allTracks.Add(new Track
                                {
                                    Title = tTitle,
                                    Artist = artist,
                                    Album = plTitle,
                                    Source = TrackSource.SoundCloud,
                                    SourceUrl = trackUrl,
                                    ImagePath = art ?? plCover,
                                    DurationSeconds = secs,
                                    Duration = secs > 0 ? Format(secs) : "--:--"
                                });
                            }
                            else if (t.TryGetProperty("id", out var idNode) && idNode.TryGetInt64(out long tid))
                            {
                                missingIds.Add(tid);
                            }
                        }
                    }

                    // Batch fetch any stub track IDs
                    if (missingIds.Count > 0 && !string.IsNullOrEmpty(clientId))
                    {
                        for (int i = 0; i < missingIds.Count; i += 50)
                        {
                            var chunk = missingIds.Skip(i).Take(50);
                            string batchUrl = $"https://api-v2.soundcloud.com/tracks?ids={string.Join(",", chunk)}&client_id={clientId}";
                            try
                            {
                                var batchReq = new HttpRequestMessage(HttpMethod.Get, batchUrl);
                                batchReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                                using var batchResp = await Http.SendAsync(batchReq);
                                if (batchResp.IsSuccessStatusCode)
                                {
                                    using var batchDoc = JsonDocument.Parse(await batchResp.Content.ReadAsStringAsync());
                                    var dict = new Dictionary<long, JsonElement>();
                                    foreach (var trObj in batchDoc.RootElement.EnumerateArray())
                                    {
                                        if (trObj.TryGetProperty("id", out var idElem) && idElem.TryGetInt64(out long idVal))
                                            dict[idVal] = trObj;
                                    }

                                    foreach (var id in chunk)
                                    {
                                        if (dict.TryGetValue(id, out var tr))
                                        {
                                            string tTitle = Str(tr, "title");
                                            if (string.IsNullOrWhiteSpace(tTitle)) continue;

                                            string artist = "Unknown Artist";
                                            if (tr.TryGetProperty("user", out var u)) artist = Str(u, "username") ?? artist;
                                            string art = Str(tr, "artwork_url");
                                            if (!string.IsNullOrEmpty(art)) art = art.Replace("-large.jpg", "-t500x500.jpg");
                                            double secs = 0;
                                            if (tr.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out long dms))
                                                secs = dms / 1000.0;
                                            string trackUrl = Str(tr, "permalink_url");

                                            allTracks.Add(new Track
                                            {
                                                Title = tTitle,
                                                Artist = artist,
                                                Album = plTitle,
                                                Source = TrackSource.SoundCloud,
                                                SourceUrl = trackUrl,
                                                ImagePath = art ?? plCover,
                                                DurationSeconds = secs,
                                                Duration = secs > 0 ? Format(secs) : "--:--"
                                            });
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    result.Tracks = allTracks;
                    result.Success = allTracks.Count > 0;
                    if (!result.Success) result.Error = "That SoundCloud playlist came back empty.";
                    return result;
                }
                else
                {
                    // Single track
                    JsonElement? soundNode = null;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (Str(item, "hydratable") == "sound" && item.TryGetProperty("data", out var d))
                        {
                            soundNode = d;
                            break;
                        }
                    }

                    if (soundNode == null)
                    {
                        return await ImportSoundCloudOEmbedAsync(url);
                    }

                    var sound = soundNode.Value;
                    string title = Str(sound, "title") ?? "Unknown Title";
                    string artist = "Unknown Artist";
                    if (sound.TryGetProperty("user", out var u)) artist = Str(u, "username") ?? artist;
                    string art = Str(sound, "artwork_url");
                    if (string.IsNullOrEmpty(art) && sound.TryGetProperty("user", out var u2))
                        art = Str(u2, "avatar_url");
                    if (!string.IsNullOrEmpty(art)) art = art.Replace("-large.jpg", "-t500x500.jpg");

                    double secs = 0;
                    if (sound.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out long dms))
                        secs = dms / 1000.0;

                    string trackUrl = Str(sound, "permalink_url") ?? url;

                    result.Tracks.Add(new Track
                    {
                        Title = title,
                        Artist = artist,
                        Source = TrackSource.SoundCloud,
                        SourceUrl = trackUrl,
                        ImagePath = art,
                        DurationSeconds = secs,
                        Duration = secs > 0 ? Format(secs) : "--:--"
                    });

                    result.Success = true;
                    return result;
                }
            }
            catch (Exception ex)
            {
                var oEmbed = await ImportSoundCloudOEmbedAsync(url);
                if (oEmbed.Success) return oEmbed;
                result.Error = "Could not import from SoundCloud: " + ex.Message;
                return result;
            }
        }

        private static async Task<ImportResult> ImportSoundCloudOEmbedAsync(string url)
        {
            var result = new ImportResult();
            try
            {
                string endpoint = "https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(url);
                using var resp = await Http.GetAsync(endpoint);
                if (!resp.IsSuccessStatusCode)
                {
                    result.Error = "That SoundCloud link couldn't be read.";
                    return result;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                string rawTitle = Str(root, "title") ?? "SoundCloud Track";
                string author = Str(root, "author_name") ?? "Unknown Artist";
                string thumb = Str(root, "thumbnail_url");
                if (!string.IsNullOrEmpty(thumb))
                    thumb = thumb.Replace("-large.jpg", "-t500x500.jpg");

                string title = rawTitle;
                string suffix = " by " + author;
                if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    title = title.Substring(0, title.Length - suffix.Length).Trim();

                bool isSet = url.IndexOf("/sets/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSet)
                {
                    result.CollectionName = title;
                }

                result.Tracks.Add(new Track
                {
                    Title = title,
                    Artist = author,
                    Source = TrackSource.SoundCloud,
                    SourceUrl = url,
                    ImagePath = thumb,
                    Duration = "--:--"
                });

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "SoundCloud oEmbed failed: " + ex.Message;
                return result;
            }
        }

        // ==================================================================
        // Generic link
        // ==================================================================

        private static Task<ImportResult> ImportGenericAsync(string url)
        {
            var result = new ImportResult();
            string title;
            try { title = new Uri(url).Host; } catch { title = "Web link"; }

            result.Success = true;
            result.Tracks.Add(new Track
            {
                Title = title,
                Artist = "Web link",
                Source = TrackSource.Web,
                SourceUrl = url,
                Duration = "--:--"
            });
            return Task.FromResult(result);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() : null;

        public static string Format(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "0:00";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }
    }

    /// <summary>
    /// Uploaded video titles are messy. This pulls a usable artist/title pair out
    /// of things like "Artist - Song (Official Music Video) [4K]".
    /// </summary>
    public static class TitleSplitter
    {
        private static readonly string[] NoisePatterns =
        {
            @"\(\s*official\s*(music\s*)?(video|audio|visualizer|lyric[s]?\s*video)?\s*\)",
            @"\[\s*official\s*(music\s*)?(video|audio|visualizer|lyric[s]?\s*video)?\s*\]",
            @"\(\s*lyric[s]?(\s*video)?\s*\)",
            @"\[\s*lyric[s]?(\s*video)?\s*\]",
            @"\(\s*audio\s*\)", @"\[\s*audio\s*\]",
            @"\(\s*visuali[sz]er\s*\)",
            @"\(\s*hd\s*\)", @"\[\s*hd\s*\]",
            @"\[\s*4k\s*\]", @"\(\s*4k\s*\)",
            @"\(\s*explicit\s*\)", @"\[\s*explicit\s*\]",
            @"\(\s*free\s*download\s*\)",
            @"\|\s*official\s*video",
        };

        public static (string title, string artist) Split(string rawTitle, string author)
        {
            string title = Clean(rawTitle);
            string artist = CleanAuthor(author);

            // Topic channels are auto-generated and already name the artist exactly.
            bool topicChannel = !string.IsNullOrEmpty(author) &&
                                author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);

            if (!topicChannel)
            {
                // "Artist - Song" is by far the most common upload convention.
                var m = Regex.Match(title, @"^(.{1,60}?)\s+[-–—]\s+(.{1,120})$");
                if (m.Success)
                {
                    string left = m.Groups[1].Value.Trim();
                    string right = m.Groups[2].Value.Trim();
                    if (left.Length > 0 && right.Length > 0)
                        return (right, left);
                }
            }

            if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown Artist";
            return (string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title, artist);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            foreach (var p in NoisePatterns)
                s = Regex.Replace(s, p, " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim(' ', '-', '|', '_', '\t');
        }

        private static string CleanAuthor(string a)
        {
            if (string.IsNullOrWhiteSpace(a)) return null;
            a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"\s*VEVO$", "", RegexOptions.IgnoreCase);
            return a.Trim();
        }
    }
}
