# NyaDownloader

Windows app for downloading videos, audio and photos from YouTube, TikTok and Instagram. A GUI over [yt-dlp](https://github.com/yt-dlp/yt-dlp).

- Paste a link: the site is detected, and so is an Instagram reel, photo or carousel
- Video (mp4, up to 4K) or audio (mp3), optionally with the cover (jpg)
- cookies.txt for age-restricted or private videos: drop the file into the window
- Missing yt-dlp, ffmpeg or Deno can be installed from the app

## Build

Needs the .NET 10 SDK.

```
dotnet publish -c Release -o publish
```

The result is one self-contained `publish\NyaDownloader.exe` for Windows 10/11 x64; .NET doesn't have to be installed.

## Tests

```
cd Tests
dotnet test
```

`dotnet test --filter Category=Unit` runs only the offline tests. The E2E tests drive `publish\NyaDownloader.exe`, so publish first.
