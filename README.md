# Ghost PiP

Click-through picture-in-picture for **any** window on Windows 10/11.

Ghost PiP mirrors a live window — a YouTube video, a Twitch stream, a Discord call — into a
borderless, always-on-top overlay that your mouse passes straight through. Make it as see-through
as you like, trim it down to just the part you care about, scale it to any size, and keep watching
while you play a game or work underneath it.

## Features

- **Truly click-through** — the overlay never steals clicks; whatever is under it stays usable.
- **Adjustable transparency** — 10–100% opacity from the toolbar, settings, or hotkeys.
- **Live, not a screenshot** — uses the Windows DWM thumbnail API, so the mirror is real-time
  and costs almost nothing.
- **Trim the fat** — drag any edge of the crop box, use per-side sliders, or hit **Find video**
  to auto-crop to whatever is moving (the playing video).
- **Precision trim** — magnified loupes (up to 16×, with a pixel grid) on every cut line,
  arrow-key nudging, typed pixel values, snap-to-edge, shave flat bars, undo/redo.
- **Scale** — 100% shows the video at its real pixel size; resize from 10% up to whatever fits
  on screen. It grows and shrinks from the screen corner it's parked in.
- **Pixel-style toolbar** — play/pause, ±10 s, prev/next, size, lock, settings. Auto-hides and
  only appears when you rest the cursor on the overlay (never while a game has hidden the cursor).
- **Tab lock** — hides itself when you switch away from the tab you were watching, and follows
  that tab if you drag it into its own browser window.
- **Snaps to screen edges**, remembers its position, size, crop and opacity between runs.
- **Health check** — warns when Firefox is set up to freeze covered windows, with a one-click fix.

## Requirements

- Windows 10 or 11
- Python 3.9+ (from [python.org](https://www.python.org/downloads/), which includes Tkinter)
- [Pillow](https://pypi.org/project/pillow/)

```bash
pip install -r requirements.txt
```

## Run

```bash
pythonw ghost_pip.pyw
```

That opens the home screen: pick a window and hit **Start mirroring**. To skip straight to
mirroring the first window whose title contains a word:

```bash
pythonw ghost_pip.pyw youtube
```

Running it again while it's open just brings the home screen back. Settings are saved to
`settings.json` next to the script.

## Hotkeys

All are **Ctrl + Alt + …** and work from anywhere, including in-game.

| Key | Action |
|---|---|
| ↑ / ↓ | Opacity up / down |
| + / − (or numpad) | Size bigger / smaller |
| ← / → | Seek −/+ 10 s |
| Space | Play / pause |
| L | Unlock to drag and scroll-resize (lock again to go click-through) |
| T | Toolbar: auto-hide → always → off |
| C | Settings & crop |
| P | Precision trim |
| H | Home screen |
| Q | Quit |

## Tips

- **Games:** run the game in *borderless* / *windowed fullscreen*. Exclusive fullscreen draws
  over everything, so no overlay can show on top of it.
- **Mirror freezes when the browser is covered?** Browsers stop drawing fully hidden windows.
  For Firefox, the home screen's **Fix it** button turns that off
  (`widget.windows.window_occlusion_tracking.enabled = false`) — restart Firefox afterwards.
  For Chromium browsers, leave a sliver of the window visible.
- **Don't minimize** the window you're mirroring — Windows stops drawing minimized windows.
- **Watch one tab while browsing others:** drag the video's tab out into its own window and
  mirror that window.

## How it works

The overlay is a layered, transparent-to-input (`WS_EX_LAYERED | WS_EX_TRANSPARENT`) topmost
window. The picture inside it is a DWM thumbnail of the source window
(`DwmRegisterThumbnail`), with the crop applied through the thumbnail's source rectangle, so the
compositor does all the work. Previews in the crop and precision tools use `PrintWindow`, which
captures the window even when it's covered.
