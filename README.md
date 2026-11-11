# NyaDownloader

App for Windows and macOS for downloading videos, audio and photos from YouTube, TikTok and Instagram. A GUI over [yt-dlp](https://github.com/yt-dlp/yt-dlp).

- Paste a link: the site is detected, and so is an Instagram reel, photo or carousel
- Video (mp4, up to 4K) or audio: the original m4a without re-encoding, or mp3; optionally with the cover (jpg)
- YouTube playlists and channels: pick the videos you want in a list
- Subtitles (srt) in the language you choose, including YouTube's auto-translated ones
- Only a fragment (from-to) or chosen chapters, cut exactly
- cookies.txt for age-restricted or private videos: drop the file into the window
- Missing yt-dlp, ffmpeg or Deno can be installed from the app
- Progress on the taskbar button (the Dock icon on a Mac) and a notification when a download finishes in the background
- Cancel removes the unfinished files
- The app checks GitHub for a new version and updates itself in one click

## Build

Needs the .NET 10 SDK.

```
dotnet publish -c Release -o publish
```

The result is one self-contained `publish\NyaDownloader.exe` for Windows 10/11 x64; .NET doesn't have to be installed.

### macOS

The Mac version lives in `Mac/`: the same `Core.cs` with an [Avalonia](https://avaloniaui.net) UI instead of WinForms. On a Mac with the .NET 10 SDK:

```
Mac/build-mac.sh
```

The result is `publish/mac/NyaDownloader-mac-arm64.zip` (Apple Silicon) and `publish/mac/NyaDownloader-mac-x64.zip` (Intel), each with a self-contained `NyaDownloader.app` for macOS 13 or later. Attach both zips to a GitHub release under these names, and the Mac app will offer to update itself to it.

The app is signed ad hoc, so macOS refuses to open it after a download from the internet: open it once from System Settings > Privacy & Security > Open Anyway, or run `xattr -dr com.apple.quarantine /Applications/NyaDownloader.app`.

On a Mac the components come from [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases) (`yt-dlp_macos`), [martin-riedl.de](https://ffmpeg.martin-riedl.de) (ffmpeg, ffprobe) and [Deno](https://github.com/denoland/deno/releases), and go to `~/Library/Application Support/NyaDownloader/tools`. A yt-dlp, ffmpeg or Node installed with Homebrew is used too. Settings are kept in `~/Library/Application Support/NyaDownloader`, logs in `~/Library/Logs/NyaDownloader`.

## Tests

```
cd Tests
dotnet test
```

`dotnet test --filter Category=Unit` runs only the offline tests. The E2E tests drive `publish\NyaDownloader.exe`, so publish first.
