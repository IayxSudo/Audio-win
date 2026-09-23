using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace AudioWin
{
    /// <summary>
    /// Multi-tiered, highly resilient audio stream resolver and downloader.
    /// Uses YoutubeExplode 6.6.2 as the primary engine with automatic cascading
    /// fallbacks to public Invidious/Piped stream APIs and YouTube InnerTube endpoints.
    /// </summary>
    public static class AudioStreamResolver
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly string[] PipedInstances = new[]
        {
            "https://pipedapi.kavin.rocks",
            "https://api.piped.privacydev.net",
            "https://piped-api.garudalinux.org",
            "https://pipedapi.leptons.xyz"
        };

        private static readonly string[] InvidiousInstances = new[]
        {
            "https://inv.nadeko.net",
            "https://invidious.nerdvpn.de",
            "https://yt.artemislena.eu",
            "https://invidious.private.coffee",
            "https://invidious.projectsegfau.lt"
        };

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }

        public static async Task<string> ResolveAndDownloadAsync(Track track, Action<string> progressCallback = null, CancellationToken cancellationToken = default)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));

            string tempFile = StorageManager.GetTempAudioFile(track.Id);

            // 1. If valid cached audio file exists and is > 20KB, return it
            if (File.Exists(tempFile))
            {
                try
                {
                    var fileInfo = new FileInfo(tempFile);
                    if (fileInfo.Length > 20480)
                    {
                        return tempFile;
                    }
                    File.Delete(tempFile); // Delete broken/0-byte cache
                }
                catch { }
            }

            var youtube = new YoutubeClient();
            var candidates = new List<string>();

            // Determine candidate video IDs
            if (track.Source == TrackSource.YouTube)
            {
                string id = LinkImport.GetYouTubeVideoId(track.SourceUrl);
                if (string.IsNullOrEmpty(id))
                {
                    var parsedId = YoutubeExplode.Videos.VideoId.TryParse(track.SourceUrl);
                    if (parsedId.HasValue)
                        id = parsedId.Value.Value;
                }

                if (!string.IsNullOrEmpty(id))
                {
                    candidates.Add(id);
                }
            }
            else if (track.Source == TrackSource.SoundCloud)
            {
                // Attempt direct SoundCloud progressive download
                try
                {
                    progressCallback?.Invoke("Resolving SoundCloud audio stream...");
                    string scStreamUrl = await ResolveSoundCloudAudioUrlAsync(track.SourceUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(scStreamUrl))
                    {
                        progressCallback?.Invoke("Downloading SoundCloud stream...");
                        if (await DownloadDirectUrlAsync(scStreamUrl, tempFile, cancellationToken))
                        {
                            if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 10000)
                            {
                                return tempFile;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    StorageManager.Log("SoundCloud direct stream failed, falling back to YouTube: " + ex.Message);
                }
            }

            if (candidates.Count == 0)
            {
                // Spotify or search-based track: Search for the best matching YouTube videos
                var queries = new List<string>();
                if (!string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(track.Title))
                {
                    queries.Add($"{track.Artist} - {track.Title} audio");
                    queries.Add($"{track.Artist} - {track.Title}");
                    queries.Add($"{track.Title} {track.Artist}");
                }
                else if (!string.IsNullOrWhiteSpace(track.Title))
                {
                    queries.Add(track.Title);
                }

                progressCallback?.Invoke("Searching for audio stream...");

                foreach (var q in queries)
                {
                    try
                    {
                        int count = 0;
                        await foreach (var res in youtube.Search.GetVideosAsync(q, cancellationToken))
                        {
                            if (res != null && !string.IsNullOrEmpty(res.Id.Value) && !candidates.Contains(res.Id.Value))
                            {
                                candidates.Add(res.Id.Value);
                            }
                            if (++count >= 4) break;
                        }
                    }
                    catch { }

                    if (candidates.Count >= 3) break;
                }

                // If YoutubeExplode search yielded nothing, try Piped/Invidious search
                if (candidates.Count == 0 && queries.Count > 0)
                {
                    foreach (var q in queries)
                    {
                        var searchResults = await SearchFallbackAsync(q, cancellationToken);
                        foreach (var id in searchResults)
                        {
                            if (!candidates.Contains(id)) candidates.Add(id);
                        }
                        if (candidates.Count > 0) break;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("Could not find any matching video/audio streams online.");
            }

            Exception lastError = null;

            // Try resolving each candidate video ID across our tiered engines
            foreach (var videoId in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressCallback?.Invoke("Buffering stream...");

                // Tier 1: Try YoutubeExplode
                try
                {
                    var manifest = await youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken);
                    var audioStreamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    if (audioStreamInfo != null)
                    {
                        await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempFile, cancellationToken: cancellationToken);
                        if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 10000)
                        {
                            return tempFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                // Tier 2: Try Piped API Instances
                try
                {
                    string streamUrl = await GetPipedAudioStreamUrlAsync(videoId, cancellationToken);
                    if (!string.IsNullOrEmpty(streamUrl))
                    {
                        if (await DownloadDirectUrlAsync(streamUrl, tempFile, cancellationToken))
                        {
                            return tempFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                // Tier 3: Try Invidious API Instances
                try
                {
                    string streamUrl = await GetInvidiousAudioStreamUrlAsync(videoId, cancellationToken);
                    if (!string.IsNullOrEmpty(streamUrl))
                    {
                        if (await DownloadDirectUrlAsync(streamUrl, tempFile, cancellationToken))
                        {
                            return tempFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                // Tier 4: Try YouTube InnerTube Android/TV Client
                try
                {
                    string streamUrl = await GetInnerTubeAudioStreamUrlAsync(videoId, cancellationToken);
                    if (!string.IsNullOrEmpty(streamUrl))
                    {
                        if (await DownloadDirectUrlAsync(streamUrl, tempFile, cancellationToken))
                        {
                            return tempFile;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (lastError != null)
            {
                throw lastError;
            }

            throw new InvalidOperationException("Audio stream could not be downloaded from available sources.");
        }

        private static async Task<List<string>> SearchFallbackAsync(string query, CancellationToken ct)
        {
            var list = new List<string>();
            foreach (var instance in PipedInstances)
            {
                try
                {
                    var url = $"{instance}/search?q={Uri.EscapeDataString(query)}&filter=videos";
                    using var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("url", out var itemUrl))
                            {
                                var id = LinkImport.GetYouTubeVideoId(itemUrl.GetString());
                                if (!string.IsNullOrEmpty(id) && !list.Contains(id))
                                {
                                    list.Add(id);
                                    if (list.Count >= 3) return list;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        private static async Task<string> GetPipedAudioStreamUrlAsync(string videoId, CancellationToken ct)
        {
            foreach (var instance in PipedInstances)
            {
                try
                {
                    var url = $"{instance}/streams/{videoId}";
                    using var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("audioStreams", out var audioStreams) && audioStreams.ValueKind == JsonValueKind.Array)
                    {
                        string bestUrl = null;
                        long bestBitrate = 0;

                        foreach (var stream in audioStreams.EnumerateArray())
                        {
                            if (stream.TryGetProperty("url", out var streamUrlProp))
                            {
                                long bitrate = 0;
                                if (stream.TryGetProperty("bitrate", out var brProp) && brProp.TryGetInt64(out var br))
                                {
                                    bitrate = br;
                                }

                                if (bitrate > bestBitrate || string.IsNullOrEmpty(bestUrl))
                                {
                                    bestBitrate = bitrate;
                                    bestUrl = streamUrlProp.GetString();
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(bestUrl)) return bestUrl;
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<string> GetInvidiousAudioStreamUrlAsync(string videoId, CancellationToken ct)
        {
            foreach (var instance in InvidiousInstances)
            {
                try
                {
                    var url = $"{instance}/api/v1/videos/{videoId}";
                    using var resp = await Http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("adaptiveFormats", out var formats) && formats.ValueKind == JsonValueKind.Array)
                    {
                        string bestUrl = null;
                        long bestBitrate = 0;

                        foreach (var format in formats.EnumerateArray())
                        {
                            if (format.TryGetProperty("type", out var typeProp) && typeProp.GetString()?.StartsWith("audio") == true)
                            {
                                if (format.TryGetProperty("url", out var urlProp))
                                {
                                    long bitrate = 0;
                                    if (format.TryGetProperty("bitrate", out var brProp) && brProp.TryGetInt64(out var br))
                                    {
                                        bitrate = br;
                                    }

                                    if (bitrate > bestBitrate || string.IsNullOrEmpty(bestUrl))
                                    {
                                        bestBitrate = bitrate;
                                        bestUrl = urlProp.GetString();
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(bestUrl)) return bestUrl;
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<string> GetInnerTubeAudioStreamUrlAsync(string videoId, CancellationToken ct)
        {
            try
            {
                var payload = new
                {
                    context = new
                    {
                        client = new
                        {
                            clientName = "ANDROID_TESTSUITE",
                            clientVersion = "1.9",
                            androidSdkVersion = 30,
                            hl = "en",
                            gl = "US"
                        }
                    },
                    videoId = videoId
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync("https://www.youtube.com/youtubei/v1/player", content, ct);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("streamingData", out var streamingData))
                {
                    if (streamingData.TryGetProperty("adaptiveFormats", out var formats) && formats.ValueKind == JsonValueKind.Array)
                    {
                        string bestUrl = null;
                        long bestBitrate = 0;

                        foreach (var format in formats.EnumerateArray())
                        {
                            if (format.TryGetProperty("mimeType", out var mimeProp) && mimeProp.GetString()?.StartsWith("audio") == true)
                            {
                                if (format.TryGetProperty("url", out var urlProp))
                                {
                                    long bitrate = 0;
                                    if (format.TryGetProperty("bitrate", out var brProp) && brProp.TryGetInt64(out var br))
                                    {
                                        bitrate = br;
                                    }

                                    if (bitrate > bestBitrate || string.IsNullOrEmpty(bestUrl))
                                    {
                                        bestBitrate = bitrate;
                                        bestUrl = urlProp.GetString();
                                    }
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(bestUrl)) return bestUrl;
                    }
                }
            }
            catch { }

            return null;
        }

        private static async Task<bool> DownloadDirectUrlAsync(string streamUrl, string destinationPath, CancellationToken ct)
        {
            try
            {
                using var response = await Http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return false;

                string tempDownload = destinationPath + ".part";
                if (File.Exists(tempDownload)) File.Delete(tempDownload);

                using (var sourceStream = await response.Content.ReadAsStreamAsync(ct))
                using (var fileStream = new FileStream(tempDownload, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await sourceStream.CopyToAsync(fileStream, ct);
                }

                if (File.Exists(tempDownload) && new FileInfo(tempDownload).Length > 10000)
                {
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    File.Move(tempDownload, destinationPath);
                    return true;
                }

                if (File.Exists(tempDownload)) File.Delete(tempDownload);
            }
            catch { }

            return false;
        }

        public static async Task<string> ResolveSoundCloudAudioUrlAsync(string trackUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(trackUrl)) return null;

            // Follow redirect if shortlink
            if (trackUrl.Contains("on.soundcloud.com"))
            {
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Get, trackUrl);
                    headReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    using var headResp = await Http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (headResp.RequestMessage?.RequestUri != null)
                        trackUrl = headResp.RequestMessage.RequestUri.ToString();
                }
                catch { }
            }

            var req = new HttpRequestMessage(HttpMethod.Get, trackUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string html = await resp.Content.ReadAsStringAsync();
            var m = Regex.Match(html, @"window\.__sc_hydration\s*=\s*(\[.*?\]);", RegexOptions.Singleline);
            if (!m.Success) return null;

            string clientId = await LinkImport.GetSoundCloudClientIdAsync(html, ct);
            if (string.IsNullOrEmpty(clientId)) return null;

            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("hydratable", out var h) && h.GetString() == "sound" &&
                    item.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("media", out var media) &&
                    media.TryGetProperty("transcodings", out var transcodings) &&
                    transcodings.ValueKind == JsonValueKind.Array)
                {
                    string progressiveUrl = null;
                    foreach (var t in transcodings.EnumerateArray())
                    {
                        if (t.TryGetProperty("format", out var fmt) &&
                            fmt.TryGetProperty("protocol", out var proto) &&
                            proto.GetString() == "progressive" &&
                            t.TryGetProperty("url", out var u))
                        {
                            progressiveUrl = u.GetString();
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(progressiveUrl))
                    {
                        string apiEndpoint = progressiveUrl + (progressiveUrl.Contains("?") ? "&" : "?") + "client_id=" + clientId;
                        var transReq = new HttpRequestMessage(HttpMethod.Get, apiEndpoint);
                        transReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                        using var transResp = await Http.SendAsync(transReq, ct);
                        if (transResp.IsSuccessStatusCode)
                        {
                            using var transDoc = JsonDocument.Parse(await transResp.Content.ReadAsStringAsync());
                            if (transDoc.RootElement.TryGetProperty("url", out var streamUrlNode))
                            {
                                return streamUrlNode.GetString();
                            }
                        }
                    }
                }
            }

            return null;
        }
    }
}
