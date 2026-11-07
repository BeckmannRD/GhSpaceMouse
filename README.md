# GhSpaceMouse

**Navigate the Grasshopper 2 canvas with a 3Dconnexion SpaceMouse, in Rhino 9 on Windows.**

Rhino has always supported the SpaceMouse in its viewports, but the Grasshopper canvas never did. GhSpaceMouse makes the SpaceMouse follow your cursor:

| Cursor is over… | SpaceMouse does… |
|---|---|
| The **Grasshopper 2** window | Pans the canvas (push left/right/forward/back) and zooms it (pull up / push down) |
| **Rhino** (anywhere else) | Moves the Rhino viewport exactly as before |

There's no clicking or mode switching. Just move your cursor.

It's lightweight. Instead of the old "hidden decoy viewport" workaround, it registers the canvas with the 3Dconnexion driver as its own 2D view, so it does nothing while you aren't touching the puck.

> **Not affiliated with, or endorsed by, 3Dconnexion or Robert McNeel & Associates.** SpaceMouse and 3DxWare are trademarks of 3Dconnexion. Rhino and Grasshopper are trademarks of Robert McNeel & Associates.

## Requirements

- Windows
- Rhino 9 with **Grasshopper 2** (Grasshopper 1 isn't supported)
- 3Dconnexion **3DxWare** driver installed, with the SpaceMouse already working in Rhino

Tested on Rhino 9.0.26300 (WIP) and 3DxWare 10 with a SpaceMouse Enterprise. Grasshopper 2 is still evolving, so a future Rhino update may break it (see *Status* below).

## Install

**Option A: Rhino Package Manager** (if published there): type `PackageManager` in Rhino, search **ghspacemouse**, click Install, and restart Rhino.

**Option B: download the package:** grab `ghspacemouse-1.0.0-rh9_0-win.yak` from the [`dist`](dist) folder, drag it onto an open Rhino 9 window, and restart Rhino.

It loads with Rhino and switches on automatically when you open Grasshopper 2.

## Use

- **Turn on or off:** type `GhSpaceMouse` → click **Enabled** to toggle → Enter. Your choice is remembered.
- **Log:** `GhSpaceMouse` → **OpenLog** opens `%TEMP%\GhSpaceMouse.log`.
- **Speed, direction, axis inversion:** set these in 3Dconnexion's settings while hovering the canvas. The canvas appears as its own application, **"Grasshopper 2 Canvas"**, separate from Rhino.
- **Uninstall:** `PackageManager` → Installed → ghspacemouse → Uninstall.

If something it relies on is missing or has changed, it prints a message on the command line and switches itself off for the session rather than misbehaving.

## Build it yourself

You don't need Visual Studio or the .NET SDK. Rhino 9 ships with a C# compiler.

1. In Rhino 9, run `ScriptEditor` and open `source/Build.cs`.
2. Press **Run** and pick the `source` folder when asked.
3. The new `.yak` lands in `dist/`.

## Status: provided as-is

This was built to scratch one itch, and it's shared so others can use it. **It isn't actively maintained.** The MIT license means you're free to fork it, fix it, change it and republish it however you like.

**If it breaks after a Rhino or 3DxWare update**, start with [`DEVELOPER_NOTES.md`](DEVELOPER_NOTES.md). It records how everything works, the undocumented Grasshopper 2 and 3Dconnexion details, every pitfall hit during development, and a test procedure. It's written so that a developer, or an AI coding assistant, can pick it up cold.

## How it works (short version)

The plugin is a client of the 3Dconnexion Navigation Library (`TDxNavLib.dll`, installed with 3DxWare). It presents the Grasshopper 2 canvas as a flat, non-rotating orthographic view.

- When the cursor enters the Grasshopper 2 window, the plugin connects and takes navigation focus.
- When the cursor leaves, it disconnects, and the driver hands control back to Rhino's own 3Dconnexion plugin.
- The driver zooms a flat view by moving the camera closer, and the plugin converts that into a canvas zoom.
- All connection handling runs off Rhino's UI thread, so switching never freezes Rhino.

## License

[MIT](LICENSE) © 2026 BeckmannRD. Built with the help of Claude (Anthropic).
