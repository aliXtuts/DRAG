# DRAQ

**Download, Resume, Arrange & Queue** is a Windows desktop app for managing video, file, playlist, and torrent downloads in one queue.

> **Version 1.0.0 — first release.** This is the first version, so bugs are possible. I’m actively working on improvements. Please report issues through [GitHub Issues](https://github.com/aliXtuts/DRAG/issues).

## Download

Download the latest `DRAQ-Setup.exe` from [GitHub Releases](https://github.com/aliXtuts/DRAG/releases). The installer includes the app and its required utilities; users do not need to download a separate utilities ZIP or install .NET.

The release workflow also creates `DRAQ-Portable-win-x64.zip`. Extract it and run `DRAQ.exe` to use the portable build without installing it. Keep the whole extracted folder together.

## Features

- A shared queue for direct links, supported video sites, playlists, and torrents.
- Pause, resume, retry, reorder, and monitor downloads.
- Organize drama and series playlists, track new episodes, and import existing video folders.
- Keep a local history, settings, and queue database.

Supported sites and behavior depend on the included `yt-dlp` version and each site's access rules. DRAQ does not bypass DRM or access controls.

## Build from source

Requirements: Windows 10 or 11 (64-bit), .NET 8 SDK, and internet access to NuGet and GitHub for the dependency downloads. `global.json` selects a compatible .NET 8 SDK for repeatable builds.

1. Clone this repository.
2. Run `build.bat` from the repository folder.
3. Run `dist\DRAQ\DRAQ.exe`, or package the portable folder as a ZIP.

The build downloads `yt-dlp`, FFmpeg (`ffmpeg` and `ffprobe`), `aria2`, and Deno into `tools/`. Those executables are intentionally not committed to this repository. The published app is self-contained and includes the .NET runtime and these utilities. A separate utilities download is not needed by end users.

The first build downloads a sizable FFmpeg archive. `build.bat update` refreshes the downloaded utilities. Release builds are produced by the GitHub Actions workflow when manually dispatched or when a `v*` tag is pushed.

## User data

The app stores its database, logs, backups, and torrent files in a `data` folder beside the executable when writable, or under `%LocalAppData%\DRAQ` otherwise. Do not share this folder publicly; it may contain personal paths and activity history.

## License

DRAQ source code is available under the MIT License. See [LICENSE](LICENSE). Third-party utilities have their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contact

Created and maintained by **Ali Faiz** ([aliXtuts](https://github.com/aliXtuts)).

- Email: [alifaiz62742@gmail.com](mailto:alifaiz62742@gmail.com)
- GitHub: [github.com/aliXtuts](https://github.com/aliXtuts)

