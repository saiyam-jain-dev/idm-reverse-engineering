# Independent Download Manager and IDM Research Notes

An independently implemented Windows download manager. It is not affiliated
with Internet Download Manager and does not use IDM code, artwork, or branding.
The interface uses a familiar download-manager layout, with its own styling.

This repository contains the app and Chrome extension source, plus static
reverse-engineering notes about an installed IDM version. It does not include
IDM binaries, extension packages, copied assets, or decompiled IDM code. The
research notes describe observations and limitations; they are not an IDM
implementation specification or proof of runtime behavior.

## Current features

- Add direct HTTP and HTTPS URLs; newly added downloads start automatically
  when the sequential queue is idle. Queued downloads restored at startup also
  resume automatically; intentionally paused downloads remain paused.
- Choose a destination folder; existing filenames are not overwritten.
- View all, active, queued, unfinished, and completed downloads.
- Automatically group common file extensions into categories.
- Show download state, known file size, progress, transfer rate, and estimated
  time remaining while a transfer is active.
- Pause, resume, retry failed downloads, cancel selected jobs, and stop queued
  or active work.
- Keep a `.part` file on pause and attempt to resume using HTTP range requests.
  Resume depends on server support; if the server does not support ranges, the
  transfer restarts from the beginning.
- Open completed files or their containing folder.
- Remove completed entries from the list without deleting the downloaded files.
- Report transfer errors in the download list.
- Restore the download list at startup and pause an active transfer cleanly on
  exit so its partial file can be resumed later.

The saved queue is stored in the current Windows user's local application data
folder. It contains the URLs and destination paths entered by the user; URLs
can include private query tokens, so the file should be treated as personal
data. Categorization is based on filename extensions. This build supports
direct file URLs. It does not support segmented downloads, download
authentication/cookie management, scheduler automation, a media grabber, or
streaming/video quality selection. These are separate features that require
implementation and testing, not just UI controls.

The independent Chrome extension in [chrome-extension/README.md](chrome-extension/README.md)
can send direct HTTP(S) URLs to the desktop app through a per-user Chrome Native
Messaging host and a same-user named pipe. Media scans remain user-triggered
and support only direct media-file URLs. The extension also offers new browser
HTTP(S) downloads to the desktop app by pausing the Chrome download and
prompting the user; fast downloads may complete before Chrome reports them.
This handoff is best-effort, and browser cookies/authentication are not
transferred. Segmented downloads and streaming quality detection are not
implemented. Register the host with
`chrome-extension\install-native-host.ps1` after building or publishing.

## Run

From PowerShell in this directory:

```powershell
dotnet run -c Release
```

## Build and publish

```powershell
dotnet build -c Release
$publishDir = Join-Path $PWD.Path 'bin\Release\net8.0-windows\win-x64\publish\BridgeReady\'
dotnet publish DownloadManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:PublishDir=$publishDir"
dotnet publish native-host\NativeMessagingHost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "-p:PublishDir=$publishDir"
.\chrome-extension\install-native-host.ps1
```

The desktop app and native host are published side-by-side under
`bin\Release\net8.0-windows\win-x64\publish\BridgeReady\`. The installer script
registers the native host for the current Windows user; restart Chrome after
loading the extension and installing the host.

## Check the Chrome extension

Run its dependency-free tests with Node.js:

```powershell
node --test chrome-extension\tests\service-worker.test.mjs
```
