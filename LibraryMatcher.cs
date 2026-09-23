using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AudioWin
{
    public class MatchCandidate
    {
        public string Path;
        public string Normalized;
    }

    /// <summary>
    /// Links imported references to audio you already own.
    ///
    /// Importing a Spotify playlist gives you the tracklist; this walks a folder
    /// you choose and pairs each entry with the closest matching file, so the
    /// playlist actually plays. Matching is on normalised "artist + title" text
    /// with a similarity floor, so near-misses are left alone rather than
    /// confidently attached to the wrong song.
    /// </summary>
    public static class LibraryMatcher
    {
        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ogg", ".opus"
        };

        public static bool IsAudioFile(string path)
        {
            try
            {
                var ext = Path.GetExtension(path);
                return !string.IsNullOrEmpty(ext) &&
                       AudioExtensions.Contains(ext.ToLowerInvariant());
            }
            catch { return false; }
        }

        public static List<MatchCandidate> ScanFolder(string folder, CancellationToken ct = default)
        {
            var list = new List<MatchCandidate>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return list;

            var pending = new Queue<string>();
            pending.Enqueue(folder);
            int guard = 0;

            while (pending.Count > 0 && guard++ < 20000)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Dequeue();

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; } // permission denied, reparse points, etc.

                foreach (var f in files)
                {
                    if (!IsAudioFile(f)) continue;
                    list.Add(new MatchCandidate
                    {
                        Path = f,
                        Normalized = Normalize(Path.GetFileNameWithoutExtension(f))
                    });
                }

                try { foreach (var d in Directory.GetDirectories(dir)) pending.Enqueue(d); }
                catch { }
            }

            return list;
        }

        /// <summary>Returns the number of tracks that got a file attached.</summary>
        public static int MatchTracks(IEnumerable<Track> tracks, List<MatchCandidate> candidates,
                                      double threshold = 0.66)
        {
            if (candidates == null || candidates.Count == 0) return 0;
            int matched = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var track in tracks)
            {
                if (track == null || !track.NeedsFile) continue;

                string wanted = Normalize((track.Artist ?? "") + " " + (track.Title ?? ""));
                string titleOnly = Normalize(track.Title ?? "");
                if (wanted.Length < 3 && titleOnly.Length < 3) continue;

                MatchCandidate best = null;
                double bestScore = 0;

                foreach (var c in candidates)
                {
                    if (used.Contains(c.Path)) continue;
                    double score = Math.Max(Similarity(wanted, c.Normalized),
                                            Similarity(titleOnly, c.Normalized));

                    // A filename that literally contains the title is a strong signal
                    // even if extra words drag the raw ratio down.
                    if (titleOnly.Length >= 5 && c.Normalized.Contains(titleOnly))
                        score = Math.Max(score, 0.9);

                    if (score > bestScore) { bestScore = score; best = c; }
                }

                if (best != null && bestScore >= threshold)
                {
                    track.FilePath = best.Path;
                    used.Add(best.Path);
                    track.RefreshComputed();
                    matched++;
                }
            }

            return matched;
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.ToLowerInvariant();

            // Strip leading track numbers like "03 - " or "03. "
            s = Regex.Replace(s, @"^\s*\d{1,3}\s*[-._)]\s*", " ");
            // Bracketed noise
            s = Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]", " ");
            // Featuring credits shouldn't stop a match
            s = Regex.Replace(s, @"\b(feat|ft|featuring|with)\b.*$", " ");
            s = Regex.Replace(s, @"\b(official|video|audio|lyrics?|hd|4k|remaster(ed)?|explicit|mv)\b", " ");

            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_') sb.Append(' ');
            }

            return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        }

        /// <summary>Normalised Levenshtein similarity in the range 0..1.</summary>
        public static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            if (a == b) return 1;

            int max = Math.Max(a.Length, b.Length);
            // Wildly different lengths can't be the same song; skip the O(n*m) work.
            if (Math.Min(a.Length, b.Length) * 2 < max && !b.Contains(a) && !a.Contains(b)) return 0;

            int distance = Levenshtein(a, b);
            return 1.0 - (double)distance / max;
        }

        private static int Levenshtein(string a, string b)
        {
            // Two-row variant: the full matrix is unnecessary and this runs over
            // every candidate file, so the allocation matters.
            int n = a.Length, m = b.Length;
            var prev = new int[m + 1];
            var cur = new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                char ca = a[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    int cost = ca == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }

            return prev[m];
        }
    }
}
