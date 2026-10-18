# Third-party software

DRAQ downloads the following separate command-line programs during its build and includes them in the distributed application. Their source code and licenses are maintained by their respective projects. These licenses do not change the MIT license for DRAQ's own source code.

| Program | Project | License |
|---|---|---|
| yt-dlp | [yt-dlp/yt-dlp](https://github.com/yt-dlp/yt-dlp) | Unlicense |
| aria2 | [aria2/aria2](https://github.com/aria2/aria2) | GPL-2.0-or-later |
| FFmpeg and ffprobe | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) | GPL build; see the distributed build's license and configuration |
| Deno | [denoland/deno](https://github.com/denoland/deno) | MIT |
| Microsoft.Data.Sqlite | [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | MIT |

The build obtains these programs from the upstream project release locations. Check the upstream license files and the exact build's notices when redistributing the application. FFmpeg is distributed as a separate executable; its GPL terms still apply to that executable and its redistribution.

