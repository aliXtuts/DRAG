@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title DRAQ - build

REM Usage:  build.bat            normal build
REM         build.bat update     also re-download the newest yt-dlp / ffmpeg / aria2
REM         build.bat nopause    do not wait for a key at the end

set "FORCE="
set "NOPAUSE="
for %%A in (%*) do (
    if /i "%%A"=="update" set "FORCE=-Force"
    if /i "%%A"=="nopause" set "NOPAUSE=1"
)

echo.
echo ============================================================
echo   DRAQ - Download, Resume, Arrange ^& Queue   -   build
echo ============================================================
echo.

REM ------------------------------------------------------------ 1. .NET SDK
echo [1/6] Checking the .NET SDK ...
where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk

set "SDKVER="
for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "SDKVER=%%v"
if not defined SDKVER goto :nosdk
for /f "tokens=1 delims=." %%m in ("%SDKVER%") do set "SDKMAJOR=%%m"
if %SDKMAJOR% LSS 8 goto :oldsdk

set "TFM=net8.0-windows"
if %SDKMAJOR% GEQ 10 set "TFM=net10.0-windows"
echo       .NET SDK %SDKVER% found. Target: %TFM%
echo.

REM ------------------------------------------------------------ 2. helper programs
echo [2/6] Getting the bundled tools (yt-dlp, FFmpeg, ffprobe, aria2) ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\get-tools.ps1" -Root "%~dp0." %FORCE%
if errorlevel 1 goto :toolsfail

for %%F in ("tools\yt-dlp\yt-dlp.exe" "tools\ffmpeg\ffmpeg.exe" "tools\ffmpeg\ffprobe.exe" "tools\aria2\aria2c.exe" "tools\deno\deno.exe" "assets\draq.ico") do (
    if not exist "%%~F" (
        echo       MISSING: %%~F
        goto :toolsfail
    )
)
echo       All tools are present.
echo.

REM ------------------------------------------------------------ 3. restore
echo [3/6] Restoring NuGet packages ...
dotnet restore "DRAQ\DRAQ.csproj" -r win-x64 -p:DraqTfm=%TFM%
if errorlevel 1 goto :restorefail
echo.

REM ------------------------------------------------------------ 4. publish
echo [4/6] Building and publishing (self-contained, Windows x64) ...
if exist "dist\DRAQ" rmdir /s /q "dist\DRAQ"
dotnet publish "DRAQ\DRAQ.csproj" -c Release -r win-x64 --self-contained true --no-restore -p:DraqTfm=%TFM% -p:DebugType=None -p:DebugSymbols=false -o "dist\DRAQ"
if errorlevel 1 goto :buildfail
echo.

REM ------------------------------------------------------------ 5. make sure the tools + icon are in the portable folder
echo [5/6] Adding the tools and icon to dist\DRAQ ...
if not exist "dist\DRAQ\tools" mkdir "dist\DRAQ\tools"
if not exist "dist\DRAQ\assets" mkdir "dist\DRAQ\assets"
copy /y "tools\yt-dlp\yt-dlp.exe" "dist\DRAQ\tools\" >nul
copy /y "tools\ffmpeg\ffmpeg.exe" "dist\DRAQ\tools\" >nul
copy /y "tools\ffmpeg\ffprobe.exe" "dist\DRAQ\tools\" >nul
copy /y "tools\aria2\aria2c.exe" "dist\DRAQ\tools\" >nul
copy /y "tools\deno\deno.exe" "dist\DRAQ\tools\" >nul
copy /y "assets\draq.ico" "dist\DRAQ\assets\" >nul
echo.

REM ------------------------------------------------------------ 6. verify
echo [6/6] Verifying the result ...
set "BAD="
for %%F in ("DRAQ.exe" "DRAQ.dll" "Microsoft.Data.Sqlite.dll" "tools\yt-dlp.exe" "tools\ffmpeg.exe" "tools\ffprobe.exe" "tools\aria2c.exe" "tools\deno.exe" "assets\draq.ico") do (
    if not exist "dist\DRAQ\%%~F" (
        echo       MISSING in dist\DRAQ: %%~F
        set "BAD=1"
    )
)
dir /s /b "dist\DRAQ\e_sqlite3.dll" >nul 2>nul
if errorlevel 1 (
    echo       MISSING in dist\DRAQ: e_sqlite3.dll  ^(SQLite native library^)
    set "BAD=1"
)
if not exist "dist\DRAQ\coreclr.dll" (
    echo       MISSING in dist\DRAQ: coreclr.dll  ^(the build is not self-contained^)
    set "BAD=1"
)
if not exist "dist\DRAQ\PresentationFramework.dll" (
    echo       MISSING in dist\DRAQ: PresentationFramework.dll  ^(WPF runtime not included^)
    set "BAD=1"
)
if defined BAD goto :verifyfail

echo.
echo ============================================================
echo   SUCCESS
echo.
echo   Your portable program is in:   %~dp0dist\DRAQ\
echo   Start it with:                 dist\DRAQ\DRAQ.exe
echo.
echo   You can copy the whole dist\DRAQ folder to any Windows 10/11
echo   64-bit PC and run DRAQ.exe there. Nothing else must be installed.
echo ============================================================
echo.
if not defined NOPAUSE pause
exit /b 0

:nosdk
echo.
echo   ERROR: The .NET SDK was not found.
echo   Install the .NET SDK ^(version 8 or newer, x64^) from:
echo       https://dotnet.microsoft.com/download
echo   Then close this window, open a new one and run build.bat again.
goto :fail

:oldsdk
echo.
echo   ERROR: The installed .NET SDK is version %SDKVER%, but version 8 or newer is required.
echo   Install a newer SDK from https://dotnet.microsoft.com/download and run build.bat again.
goto :fail

:toolsfail
echo.
echo   ERROR: The helper programs could not be downloaded or are incomplete.
echo   Check your internet connection and run build.bat again.
echo   ^(They come from github.com: yt-dlp, aria2 and BtbN FFmpeg-Builds.^)
goto :fail

:restorefail
echo.
echo   ERROR: NuGet packages could not be restored. Check your internet connection
echo   ^(nuget.org must be reachable^) and run build.bat again.
goto :fail

:buildfail
echo.
echo   ERROR: The build failed. Read the messages above. Please send them to whoever
echo   is helping you, together with the .NET SDK version: %SDKVER%
goto :fail

:verifyfail
echo.
echo   ERROR: The published folder is incomplete. It was NOT marked as successful.
goto :fail

:fail
echo.
echo   BUILD FAILED.
echo.
if not defined NOPAUSE pause
exit /b 1

