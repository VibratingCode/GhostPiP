# Ghost PiP

Click-through picture-in-picture for **any** window on Windows 10/11.

Ghost PiP mirrors a live window (a YouTube video, a Twitch stream, a Discord call) into a borderless,
always-on-top overlay that your mouse passes straight through. Make it as see-through as you like, trim
it to just the part you care about, and keep watching while you play or work underneath it.

## What's new in 2.0

| | v1 | v2 |
|---|---|---|
| Built with | Python, Tkinter, Pillow | C#, WPF on .NET 10: one exe |
| Picking a window | text list | gallery of **live previews**, with search and keyboard control |
| Mirrors at once | 1 | **as many as you like**, each with its own settings |
| Crop, size, position | one global set | **remembered per site or app** |
| Crop editor | still snapshot | **live video** behind the crop frame, plus ratio presets |
| Find video | about 3 px accuracy | tightened to the video's exact pixel edges |
| Play / pause | global media key, state guessed | talks to the **mirrored app's** media session where it can |
| Controller | icon strip | mini-player with title, artwork and progress bar |
| Sound | system volume | **per-app volume and mute**, system controls too |
| Hotkeys | fixed | rebindable, with conflict warnings |
| New behaviours | | fade on hover, hide when its window is in front, hide from screen capture, hold Ctrl+Alt to grab, tray icon, start with Windows |

Your v1 settings are upgraded on first run. The old file is kept as `settings.v1.json`, and the old
Python version is in `_backup_v1/` and git history.

## Run

Double-click `GhostPiP.exe` (needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).

```bash
GhostPiP.exe youtube
```

floats the first window whose title contains that word. Running it again while it's open hands the
request to the running copy. Other arguments: `--home`, `--studio`, `--pixel`, `--unlock`, `--quit`.

## Build

```bash
build.cmd
dotnet test tests/GhostPiP.Tests
```

Needs the .NET 10 SDK. `python tools/make_icon.py` redraws the icon.

## Hotkeys

All can be changed on the **Keys** page, and work from anywhere, including in-game.

| Ctrl + Alt + | Action |
|---|---|
| ↑ / ↓ | Opacity up / down |
| = / − (or numpad) | Bigger / smaller |
| ← / → | Seek −/+ 10 s |
| Space | Play / pause |
| M | Mute the mirrored app |
| L | Unlock to drag and resize |
| K | Move to the next corner |
| T | Controller: auto-hide, always, off |
| N | Float the window you're in |
| G | Hide / show all mirrors |
| O | Switch between mirrors |
| J | Go to the source window |
| C / P | Studio / pixel trim |
| H | Open Ghost PiP |
| Q | Quit |

Hold **Ctrl+Alt** over a locked mirror to drag it without unlocking.

## Tips

- **Games:** use *borderless* or *windowed fullscreen*. Exclusive fullscreen draws over everything.
- **Mirror freezes when the browser is covered?** Browsers stop drawing hidden windows. For Firefox,
  Studio's **Fix it** button turns that off (restart Firefox afterwards). For Chromium browsers, leave a
  sliver of the window visible.
- **Minimizing the source** pauses the mirror, because Windows stops drawing minimized windows. Turn on
  *Keep its window drawing* to have it restored behind everything instead.
- **Watch one tab while browsing others:** drag the tab into its own window and mirror that.

## How it works

The overlay is a bare Win32 layered window (`WS_EX_LAYERED | WS_EX_TRANSPARENT`) holding a DWM thumbnail
of the source (`DwmRegisterThumbnail`), with the crop applied through the thumbnail's source rectangle,
so the compositor does the work. Find video, Snap and Shave read snapshots from `PrintWindow`.
