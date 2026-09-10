# ClipWatch

A Windows tray app that turns OBS's replay buffer into a clip library.

Play a game, hit a key, and the last 60 seconds are saved. ClipWatch keeps the buffer
running only while a game is open, collects the clips into a browsable library, and gives
you a trim-and-export editor so you can cut a clip down and share it without opening a
video editor.

## What it does

- Starts and stops the OBS replay buffer automatically, based on which game is running.
- Saves a clip on a key press, with an optional instant-replay popup to keep or discard it.
- Shows every clip as a thumbnail grid you can search, rename, delete or open.
- Trims clips on a filmstrip timeline, with frame-accurate or instant stream-copy output.
- Exports to MP4 or MKV at a chosen resolution and quality, with a live size estimate.
- Optionally records each app to its own audio track, so you can mute Discord or turn
  down Spotify after the clip already exists.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11
- OBS Studio 28 or later, with a working capture source
- ffmpeg, for thumbnails, trimming and export. ClipWatch can download it for you.

ClipWatch does not capture the screen itself. OBS does the recording; ClipWatch drives it.

## Install

Download `ClipWatch-Setup.exe` from the releases page and run it. It installs to your user
folder, adds a Start Menu shortcut, and appears in Add/Remove Programs. Nothing else is
needed, the .NET runtime is included.

Running the setup again on a machine that already has ClipWatch offers to repair, upgrade
or uninstall it.

Prefer not to install? `ClipWatch-portable.exe` is the same program as a single file. Put
it anywhere and run it. Settings still live in `%APPDATA%\ClipWatch`.

Command-line options for setup: `/S` installs silently, `/uninstall` removes, `/allusers`
installs to Program Files instead (needs an elevated shell).

## Setup

1. Install and run OBS once, so it creates its configuration.
2. Add a capture source in OBS (Display Capture or Game Capture) and confirm you can see
   your game in the preview.
3. Start ClipWatch. It enables the OBS websocket and a 60 second replay buffer for you,
   then asks you to restart OBS if anything changed.
4. In OBS, go to Settings then Hotkeys and bind **Save Replay** to F8.
5. Focus a game and press **Shift+F7**. That teaches ClipWatch the game, and clipping
   starts whenever that game is running.

The rail on the left shows the current status: Clipping, Idle, or Waiting for OBS.

If you would rather not teach it each game, turn on **Always clipping** in Settings and the
buffer runs constantly.

## Using it

| Key | Action |
| --- | --- |
| F8 | Save the last 60 seconds (bound in OBS) |
| Shift+F9 | Instant replay popup, with keep or discard |
| Shift+F7 | Add or remove the focused game |
| Space | Play or pause in the editor |

Click any clip to open the editor. Drag the ends of the filmstrip to trim, adjust the
audio tracks, choose your export options, and press Export.

Fast export copies the video stream, so it finishes instantly but starts at the nearest
keyframe. Precise export re-encodes and is frame accurate. Changing a track's volume only
re-encodes the audio, so the video stays untouched and the export stays quick.

## Audio layers

By default OBS records one mixed audio track, so game audio and Discord are baked
together. Turn on **Audio layers** in Settings and add the apps you want separated.

ClipWatch switches OBS to multi-track recording, creates an Application Audio Capture
source per app, and routes each to its own track. Clips recorded afterwards show one
volume slider per app in the editor, so you can drop a voice call out of a clip without
losing the game.

Your OBS encoder is left alone, and turning the feature off restores the previous output
mode.

## Configuration

Settings are in `%APPDATA%\ClipWatch\config.json`, and the learned game list is in
`games.json` beside it. Everything in those files is editable from the Settings page.

The clips folder comes from OBS unless you override it in Settings.

## Building

Requires the .NET 8 SDK.

```powershell
dotnet build                 # Debug
dotnet build -c Release      # Release, and writes dist\
```

A Release build produces both distributables in `dist\`:

| File | Description |
| --- | --- |
| `ClipWatch-<version>-portable.exe` | Single file, run from anywhere |
| `ClipWatch-Setup-<version>.exe` | Installer |

Both bundle the .NET runtime. For smaller builds that require the .NET 8 Desktop Runtime
to be installed, use `.\build.ps1 -FrameworkDependent`. To compile Release without
packaging, pass `-p:SkipPackaging=true`.

Run the regression checks with:

```powershell
dotnet run --project tests/ClipWatch.Checks.csproj
```

## Project layout

| Path | Contents |
| --- | --- |
| `src/App` | Entry point, controller, settings |
| `src/Capture` | Game detection and global hotkeys |
| `src/Obs` | OBS websocket client, setup, audio routing |
| `src/Media` | ffmpeg, probing, export, clip library |
| `src/Ui` | Windows, views, shared visual pieces |
| `resources` | Theme and application icon |
| `installer` | Setup project and install scripts |
| `tests` | Regression checks |

## Notes and limits

- Notifications cannot draw over exclusive fullscreen games. Use borderless windowed.
- Preview uses the Windows media stack. MP4, MOV, M4V, AVI and WMV normally play; MKV and
  FLV can be trimmed without preview, or converted to MP4 first.
- A saved replay can still be black if the OBS capture source was not working. Check the
  OBS preview if that happens.
