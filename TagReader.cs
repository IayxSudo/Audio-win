using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AudioWin
{
    public class TrackTags
    {
        public string Title;
        public string Artist;
        public string Album;
        public double LengthSeconds;
        public byte[] Picture;
        public string PictureMime;
    }

    /// <summary>
    /// Minimal ID3 reader (v2.2 / v2.3 / v2.4 plus the v1 trailer).
    ///
    /// This is deliberately hand-rolled rather than pulling in TagLib#: the project
    /// ships as a single self-contained exe and every extra NuGet package is another
    /// assembly to bundle. We only need four fields and the embedded artwork.
    /// Anything we cannot parse just falls back to the file name, exactly like before.
    /// </summary>
    public static class TagReader
    {
        private const int MaxTagBytes = 16 * 1024 * 1024;

        public static string ArtCacheFolder
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AudioWin", "artwork");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static TrackTags Read(string path)
        {
            var tags = new TrackTags();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!ReadId3v2(fs, tags))
                    ReadId3v1(fs, tags);
            }
            catch { }

            Clean(tags);
            return tags;
        }

        /// <summary>
        /// Reads tags and, when artwork is embedded, writes it into the artwork cache
        /// and returns the cached file path so WPF/SMTC/Discord can all point at it.
        /// </summary>
        public static TrackTags ReadWithArtwork(string path, out string artworkPath)
        {
            artworkPath = null;
            var tags = Read(path);
            if (tags.Picture != null && tags.Picture.Length > 512)
            {
                try
                {
                    string ext = tags.PictureMime != null && tags.PictureMime.Contains("png") ? ".png" : ".jpg";
                    string name = Hash(path + "|" + tags.Picture.Length) + ext;
                    string dest = Path.Combine(ArtCacheFolder, name);
                    if (!File.Exists(dest))
                        File.WriteAllBytes(dest, tags.Picture);
                    artworkPath = dest;
                }
                catch { }
            }
            tags.Picture = null; // don't keep the bytes alive once cached
            return tags;
        }

        private static void Clean(TrackTags t)
        {
            t.Title = Trim(t.Title);
            t.Artist = Trim(t.Artist);
            t.Album = Trim(t.Album);
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Trim('\0').Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        // ------------------------------------------------------------------
        // ID3v2
        // ------------------------------------------------------------------

        private static bool ReadId3v2(FileStream fs, TrackTags tags)
        {
            fs.Position = 0;
            var header = new byte[10];
            if (fs.Read(header, 0, 10) != 10) return false;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return false;

            int major = header[3];
            if (major < 2 || major > 4) return false;

            byte flags = header[5];
            int size = SyncSafe(header, 6);
            if (size <= 0 || size > MaxTagBytes) return false;

            var body = new byte[size];
            if (fs.Read(body, 0, size) != size) return false;

            int pos = 0;

            // Skip the extended header if present.
            if ((flags & 0x40) != 0 && body.Length >= 4)
            {
                int extSize = major == 4 ? SyncSafe(body, 0) : (int)BigEndian(body, 0, 4) + 4;
                if (extSize > 0 && extSize < body.Length) pos = extSize;
            }

            int idLen = major == 2 ? 3 : 4;
            int sizeLen = major == 2 ? 3 : 4;
            int frameHeader = major == 2 ? 6 : 10;

            while (pos + frameHeader <= body.Length)
            {
                string id = Encoding.ASCII.GetString(body, pos, idLen);
                if (id[0] == '\0') break; // hit the padding

                int frameSize = major == 4
                    ? SyncSafe(body, pos + idLen)
                    : (int)BigEndian(body, pos + idLen, sizeLen);

                if (frameSize <= 0 || pos + frameHeader + frameSize > body.Length) break;

                int dataStart = pos + frameHeader;

                switch (id)
                {
                    case "TIT2":
                    case "TT2":
                        tags.Title ??= DecodeText(body, dataStart, frameSize);
                        break;
                    case "TPE1":
                    case "TP1":
                        tags.Artist ??= DecodeText(body, dataStart, frameSize);
                        break;
                    case "TALB":
                    case "TAL":
                        tags.Album ??= DecodeText(body, dataStart, frameSize);
                        break;
                    case "TLEN":
                    case "TLE":
                    {
                        var raw = DecodeText(body, dataStart, frameSize);
                        if (double.TryParse(raw, out double ms) && ms > 0)
                            tags.LengthSeconds = ms / 1000.0;
                        break;
                    }
                    case "APIC":
                    case "PIC":
                        if (tags.Picture == null)
                            ReadPicture(body, dataStart, frameSize, major, tags);
                        break;
                }

                pos = dataStart + frameSize;
            }

            return tags.Title != null || tags.Artist != null || tags.Picture != null;
        }

        private static void ReadPicture(byte[] b, int start, int len, int major, TrackTags tags)
        {
            try
            {
                int p = start;
                int end = start + len;
                if (p >= end) return;

                byte enc = b[p++];

                string mime;
                if (major == 2)
                {
                    if (p + 3 > end) return;
                    string fmt = Encoding.ASCII.GetString(b, p, 3).ToLowerInvariant();
                    p += 3;
                    mime = fmt == "png" ? "image/png" : "image/jpeg";
                }
                else
                {
                    int mimeEnd = p;
                    while (mimeEnd < end && b[mimeEnd] != 0) mimeEnd++;
                    mime = Encoding.ASCII.GetString(b, p, mimeEnd - p).ToLowerInvariant();
                    p = mimeEnd + 1;
                }

                if (p >= end) return;
                p++; // picture type byte

                // Description, terminated by one NUL for single-byte encodings and
                // two for the UTF-16 ones.
                if (enc == 1 || enc == 2)
                {
                    while (p + 1 < end && !(b[p] == 0 && b[p + 1] == 0)) p += 2;
                    p += 2;
                }
                else
                {
                    while (p < end && b[p] != 0) p++;
                    p += 1;
                }

                if (p >= end) return;

                int dataLen = end - p;
                if (dataLen <= 0 || dataLen > MaxTagBytes) return;

                var pic = new byte[dataLen];
                Buffer.BlockCopy(b, p, pic, 0, dataLen);
                tags.Picture = pic;
                tags.PictureMime = string.IsNullOrEmpty(mime) ? "image/jpeg" : mime;
            }
            catch { }
        }

        private static string DecodeText(byte[] b, int start, int len)
        {
            if (len <= 1) return null;
            byte enc = b[start];
            int p = start + 1;
            int n = len - 1;
            try
            {
                string s = enc switch
                {
                    1 => DecodeUtf16(b, p, n),
                    2 => Encoding.BigEndianUnicode.GetString(b, p, n),
                    3 => Encoding.UTF8.GetString(b, p, n),
                    _ => Encoding.GetEncoding(28591).GetString(b, p, n) // ISO-8859-1
                };
                int nul = s.IndexOf('\0');
                if (nul >= 0) s = s.Substring(0, nul);
                return s;
            }
            catch { return null; }
        }

        private static string DecodeUtf16(byte[] b, int p, int n)
        {
            if (n >= 2 && b[p] == 0xFF && b[p + 1] == 0xFE)
                return Encoding.Unicode.GetString(b, p + 2, n - 2);
            if (n >= 2 && b[p] == 0xFE && b[p + 1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b, p + 2, n - 2);
            return Encoding.Unicode.GetString(b, p, n);
        }

        private static int SyncSafe(byte[] b, int offset)
        {
            if (offset + 4 > b.Length) return 0;
            return ((b[offset] & 0x7F) << 21)
                 | ((b[offset + 1] & 0x7F) << 14)
                 | ((b[offset + 2] & 0x7F) << 7)
                 | (b[offset + 3] & 0x7F);
        }

        private static long BigEndian(byte[] b, int offset, int count)
        {
            if (offset + count > b.Length) return 0;
            long v = 0;
            for (int i = 0; i < count; i++) v = (v << 8) | b[offset + i];
            return v;
        }

        // ------------------------------------------------------------------
        // ID3v1 trailer
        // ------------------------------------------------------------------

        private static void ReadId3v1(FileStream fs, TrackTags tags)
        {
            try
            {
                if (fs.Length < 128) return;
                fs.Position = fs.Length - 128;
                var b = new byte[128];
                if (fs.Read(b, 0, 128) != 128) return;
                if (b[0] != 'T' || b[1] != 'A' || b[2] != 'G') return;

                var latin = Encoding.GetEncoding(28591);
                tags.Title ??= latin.GetString(b, 3, 30);
                tags.Artist ??= latin.GetString(b, 33, 30);
                tags.Album ??= latin.GetString(b, 63, 30);
            }
            catch { }
        }

        private static string Hash(string s)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var x in bytes) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
