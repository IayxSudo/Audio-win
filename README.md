# AudioWin 2.0

A fast, offline-first music player for Windows, built with WPF, C# and NAudio.

![AudioWin](https://i.postimg.cc/XqqvxQ9G/Screenshot-2026-05-16-183330.png)
*(screenshot is from 1.x — the 2.0 interface is a full redesign)*

---

## What's new

A completely rebuilt interface
- New dark theme built on a proper design system: one palette, one motion
  language, consistent spacing and type throughout.
- Six accent colours to pick from (Violet, Ocean, Ember, Mint, Rose, Gold). The
  play button, progress bar, active nav item and visualiser all follow it.
- Every control redrawn: sliders that grow under the cursor, a gradient play
  button that lifts on hover, animated toggle switches, a sliding queue panel,
  soft page transitions, rounded cards and thin auto-fading scrollbars.
- Toast notifications instead of blocking dialog boxes.
- Light theme kept and fixed — it was previously unusable in places.

#Add tracks from a YouTube or Spotify link
Press the link button in the sidebar (or `Ctrl+L`), paste a URL, done. AudioWin
pulls in the real title, artist and cover art.

| Link type | Works with no setup | Works with a free API key |
|---|---|---|
| YouTube video | Yes | — |
| YouTube playlist | — | Yes, every video |
| Spotify track | Yes | Yes |
| Spotify album | — | Yes, every track |
| Spotify playlist | — | Yes, every track |

Without keys AudioWin uses each service's public oEmbed endpoint. Adding your
own free keys in **Settings → Link import** switches it to the official YouTube
Data API and Spotify Web API, which is what unlocks whole playlists and albums.
Keys stay on your PC.

How playback works for imported tracks.** AudioWin does not download or
decrypt audio from YouTube or Spotify — Spotify's catalogue is DRM-protected and
there is no legitimate way to do it, and ripping YouTube breaks its terms. So an
imported entry is a *reference*: correct title, artist and artwork, marked
"needs a local file".

Two ways to make one playable:
- **Match to local files** (the wand button on a playlist) — point it at your
  music folder and it fuzzy-matches the whole tracklist to files you already
  own, in one go.
- **Link a local audio file…** from a track's right-click menu, for one-offs.

Anything still unmatched opens in your browser or the Spotify app instead, and
is skipped automatically during auto-advance.

Rebuilt Discord Rich Presence
- Shows as a proper *Listening* activity with cover art, artist and a live
  progress scrubber that matches the real position.
- Pausing now actually reads as paused instead of letting the timer run on.
- A "Listen along" button appears for tracks that came from a link.
- Updates are coalesced and rate-limited, so seeking no longer spams Discord
  into dropping the presence entirely.
- Reconnects on its own if Discord starts after AudioWin, and can be switched
  off in Settings.

Other additions
- Reads ID3 tags, so tracks show real artist/album names and embedded cover art
  instead of "Unknown Artist" everywhere.
- Supports mp3, wav, flac, m4a, aac, wma, aiff, ogg and opus.
- Slide-out queue panel with "up next", plus add-to-queue on any track.
- Rename playlists, edit a track's title and artist, change covers.
- Sort by title, artist, duration, recently added or most played.
- Real statistics: most played playlist and most played track are now actually
  calculated (they always said "None" before).
- Drag and drop with a proper drop target, including whole folders.
- Keyboard shortcuts, window size remembered between sessions.

---

Bugs fixed from 1.x

**Audio**
- Volume reset to 100% on every track change — it was set on the output device,
  which is rebuilt for each song. Now applied through the sample chain.
- The spectrum analyser read interleaved stereo as one stream, doubling every
  apparent frequency. Channels are averaged first.
- EQ filter state could swap between left and right channels.
- Seeking could land mid-frame and produce a burst of static.
- Play, pause and seek now ramp over ~12ms instead of clicking.
- EQ is bypassed entirely when every band sits at 0dB.

**Playback logic**
- With repeat off, the last track looped back to the first forever instead of
  stopping.
- End-of-track was detected by polling the position, which could fire twice and
  skip a song. Driven by the real playback-stopped event now.
- Shuffle picked a fresh random index each time, so tracks repeated constantly
  and some never played. Proper shuffled order now.
- Next/Previous ignored shuffle.
- Playing a song from Liked Songs restarted it instead of pausing, because
  tracks were compared by object reference and that view handed out copies.
- Browsing to another playlist hijacked what Next/Previous did — playback and
  browsing are separate now.
- Previous restarts the current track if you're more than 3 seconds in.

**Data**
- Playlists were written with a plain overwrite; a crash mid-write left a
  truncated file and lost everything. Writes are atomic with a backup now.
- Every volume-slider and EQ-slider movement wrote three JSON files to disk.
  Saves are batched.
- A custom EQ curve reverted the dropdown to "Flat" on restart.
- Duration sorting compared strings, so "10:00" sorted before "9:00".
- Track numbers weren't renumbered after removing a track.

**Interface**
- Right-clicking a playlist and choosing Delete acted on whichever playlist was
  *left*-clicked last — it could delete the wrong one.
- The track list lived inside an outer ScrollViewer, which disabled
  virtualisation and made large playlists crawl. The list scrolls itself now.
- All views shared one scroll position.
- The search box placeholder painted the textbox background solid black, which
  broke the light theme.
- Active shuffle/repeat icons were hard-coded white and invisible in light mode.
- A bad cover-art path raised a "Playback Error" popup.
- Cover images kept a file handle open on every image shown.
- The maximise icon didn't update after an Aero Snap.
- Arrow keys were swallowed globally, breaking list navigation.
- The visualiser rebuilt 20 shapes ~90 times a second on the UI thread.

---

## Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `←` / `→` | Seek 5 seconds |
| `↑` / `↓` | Volume |
| `M` | Mute |
| `Ctrl+N` | New playlist |
| `Ctrl+L` | Add from a link |
| `Ctrl+F` | Search |
| `Ctrl+Q` | Queue |
| `Esc` | Close dialog or queue |

Media keys and the Windows volume overlay work too, as do the taskbar thumbnail
buttons.

---

## Getting API keys (optional)

**YouTube** — console.cloud.google.com → new project → enable "YouTube Data API
v3" → Credentials → Create API key. Paste it into Settings.

**Spotify** — developer.spotify.com/dashboard → Create app (any name, any
redirect URI) → copy the Client ID and Client Secret into Settings.

Note that Spotify's own editorial playlists (Discover Weekly, Today's Top Hits)
are not available to third-party apps. User-made public playlists work fine.

---

## Built with

- .NET 10.0 (WPF)
- NAudio 2.3.0
- DiscordRichPresence 1.6.1.70
- System.Text.Json

No other dependencies. ID3 parsing, theming and link import are all in-tree.

## Building

```bash
dotnet restore
dotnet build -c Release
```

Or run `build_release.ps1` to produce a single self-contained `AudioWin.exe`.

Your playlists, settings and stats live in
`%LOCALAPPDATA%\AudioWin\`. Upgrading in place keeps them; there's also
Import/Export in Settings.
