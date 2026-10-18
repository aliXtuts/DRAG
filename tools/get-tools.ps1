# Downloads the helper programs DRAQ bundles: yt-dlp, FFmpeg (+ffprobe) and aria2.
# Called by build.bat. Existing files are kept unless -Force is given.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$toolsDir = Join-Path $Root 'tools'
$tmp = Join-Path $env:TEMP ('draq-tools-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Get-File([string]$Url, [string]$Dest, [long]$MinBytes) {
    for ($i = 1; $i -le 3; $i++) {
        try {
            if (Test-Path $Dest) { Remove-Item $Dest -Force }
            Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
            $len = (Get-Item $Dest).Length
            if ($len -lt $MinBytes) { throw "downloaded file is too small ($len bytes)" }
            return
        }
        catch {
            Write-Host "    attempt $i failed: $($_.Exception.Message)"
            Start-Sleep -Seconds 3
        }
    }
    throw "Could not download $Url"
}

function Need([string]$Path) {
    return ($Force -or -not (Test-Path $Path))
}

try {
    # ---------------------------------------------------------------- yt-dlp
    $ytDir = Join-Path $toolsDir 'yt-dlp'
    $yt = Join-Path $ytDir 'yt-dlp.exe'
    New-Item -ItemType Directory -Force -Path $ytDir | Out-Null
    if (Need $yt) {
        Write-Host '  Downloading yt-dlp.exe (official release from github.com/yt-dlp/yt-dlp) ...'
        Get-File 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' $yt 5000000
    } else { Write-Host '  yt-dlp.exe already present.' }

    # ---------------------------------------------------------------- aria2
    $ariaDir = Join-Path $toolsDir 'aria2'
    $aria = Join-Path $ariaDir 'aria2c.exe'
    New-Item -ItemType Directory -Force -Path $ariaDir | Out-Null
    if (Need $aria) {
        Write-Host '  Downloading aria2 1.37.0 (official release from github.com/aria2/aria2) ...'
        $zip = Join-Path $tmp 'aria2.zip'
        Get-File 'https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip' $zip 1000000
        $out = Join-Path $tmp 'aria2'
        Expand-Archive -Path $zip -DestinationPath $out -Force
        $found = Get-ChildItem -Path $out -Recurse -Filter 'aria2c.exe' | Select-Object -First 1
        if (-not $found) { throw 'aria2c.exe was not found inside the aria2 download.' }
        Copy-Item $found.FullName $aria -Force
    } else { Write-Host '  aria2c.exe already present.' }

    # ---------------------------------------------------------------- ffmpeg + ffprobe
    $ffDir = Join-Path $toolsDir 'ffmpeg'
    $ffmpeg = Join-Path $ffDir 'ffmpeg.exe'
    $ffprobe = Join-Path $ffDir 'ffprobe.exe'
    New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
    if ((Need $ffmpeg) -or (Need $ffprobe)) {
        Write-Host '  Downloading FFmpeg (BtbN build from github.com/BtbN/FFmpeg-Builds, about 150 MB) - please wait ...'
        $zip = Join-Path $tmp 'ffmpeg.zip'
        Get-File 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' $zip 30000000
        $out = Join-Path $tmp 'ffmpeg'
        Expand-Archive -Path $zip -DestinationPath $out -Force
        $f1 = Get-ChildItem -Path $out -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
        $f2 = Get-ChildItem -Path $out -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
        if (-not $f1 -or -not $f2) { throw 'ffmpeg.exe / ffprobe.exe were not found inside the FFmpeg download.' }
        Copy-Item $f1.FullName $ffmpeg -Force
        Copy-Item $f2.FullName $ffprobe -Force
    } else { Write-Host '  ffmpeg.exe and ffprobe.exe already present.' }

    # ---------------------------------------------------------------- deno (JavaScript runtime yt-dlp needs for YouTube)
    $denoDir = Join-Path $toolsDir 'deno'
    $deno = Join-Path $denoDir 'deno.exe'
    New-Item -ItemType Directory -Force -Path $denoDir | Out-Null
    if (Need $deno) {
        Write-Host '  Downloading deno (official release from github.com/denoland/deno, needed by yt-dlp for YouTube) ...'
        $zip = Join-Path $tmp 'deno.zip'
        Get-File 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' $zip 10000000
        $out = Join-Path $tmp 'deno'
        Expand-Archive -Path $zip -DestinationPath $out -Force
        $fd = Get-ChildItem -Path $out -Recurse -Filter 'deno.exe' | Select-Object -First 1
        if (-not $fd) { throw 'deno.exe was not found inside the deno download.' }
        Copy-Item $fd.FullName $deno -Force
    } else { Write-Host '  deno.exe already present.' }

    # ---------------------------------------------------------------- verify
    $checks = @(
        @{ Path = $yt; Min = 5000000 },
        @{ Path = $aria; Min = 1000000 },
        @{ Path = $ffmpeg; Min = 20000000 },
        @{ Path = $ffprobe; Min = 20000000 },
        @{ Path = $deno; Min = 10000000 }
    )
    foreach ($c in $checks) {
        if (-not (Test-Path $c.Path)) { throw "Missing after download: $($c.Path)" }
        $len = (Get-Item $c.Path).Length
        if ($len -lt $c.Min) { throw "File looks incomplete ($len bytes): $($c.Path)" }
    }
    Write-Host '  All tools downloaded and checked.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    try { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

