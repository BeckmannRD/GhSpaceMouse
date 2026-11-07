# GhSpaceMouse: developer notes

Written for any developer or AI coding assistant picking this up cold. Read this whole file before changing code.
The user-facing overview is in `README.md`. The project is MIT-licensed and not actively maintained, so forks are welcome.

## 1. Goal and acceptance criteria

The original author (Windows, Rhino 9, Grasshopper **2**, 3Dconnexion SpaceMouse) wanted:

- Cursor over the GH2 editor window → SpaceMouse pans/zooms the GH2 canvas, and the Rhino viewport does **not** move.
- Cursor anywhere else → SpaceMouse drives the Rhino viewport exactly as normal (Rhino's own 3Dconnexion plugin).
- Hand-off follows the cursor (hover, no click), with no noticeable lag either way.
- Negligible CPU when idle. Must not crash Rhino.
- Loads at Rhino startup, can be toggled (persisted), installs on other machines via a `.yak`.

Status at v1.0.0: **all met** and tested by hand on the setup below.

## 2. Environment it was built and tested on (Nov 2026)

| Item | Value |
|---|---|
| Rhino | 9.0.26300.9401 (WIP), `C:\Program Files\Rhino 9` |
| Runtime | .NET 10.0.2 (`C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.2`) |
| RhinoCommon | `C:\Program Files\Rhino 9\System\netcore\RhinoCommon.dll` (note `netcore\`) |
| Eto | 2.13, `C:\Program Files\Rhino 9\System\Eto.dll`; GH2 uses Eto.Wpf on Windows |
| Grasshopper 2 | `C:\Program Files\Rhino 9\Plug-ins\Grasshopper2\net8.0\Grasshopper2.dll` 9.0.26300.9401 |
| 3DxWare | `C:\Windows\System32\TDxNavLib.dll` 0.9.24354.21309, `siappdll.dll`; Rhino plugin `C:\Program Files\Rhino 9\Plug-ins\3dxRhino.9.rhp` |
| Device | 3Dconnexion VID 0x256F PID 0xC633, HID usage page 0x01 / usage 0x08, one 13-byte report id 1 with all 6 axes |
| Yak | 0.19.0, `C:\Program Files\Rhino 9\System\Yak.exe` |
| No .NET SDK / Visual Studio installed | the build uses Roslyn shipped inside Rhino (see section 6) |

## 3. Architecture (source in `source\`)

- `GhSpaceMousePlugIn.cs`
  - `GhSpaceMousePlugIn`: `LoadTime = AtStartup`. `OnLoad` refuses non-Windows. It starts the `Controller` on the first `RhinoApp.Idle`. Setting `Enabled` is persisted in `PlugIn.Settings` (default true).
  - `Controller`: checks `NativeLibrary.TryLoad("TDxNavLib.dll")`, then a 1 s `UITimer` finds the GH2 editor **without creating it**: `Eto.Forms.Application.Instance.Windows` filtered by type `Grasshopper2.UI.Editor`, falling back to static fields of that type. **Do not call `Editor.Instance`**, because it may lazily create the editor. It attaches a `CanvasNavigator` and re-attaches if the canvas object or editor HWND changes. If the navigator raises `Fault`, it detaches, prints to the command line and stops for that session (it does not persist `Enabled=false`).
  - `GhSpaceMouseCommand` (`GhSpaceMouse`): `Enabled` toggle plus `OpenLog`.
  - `Log`: `%TEMP%\GhSpaceMouse.log`, events only, auto-deleted above 512 KB.
- `CanvasNavigator.cs`: the core. It is a 3Dconnexion **Navigation Library (navlib) client** that presents the GH2 canvas as a non-rotatable orthographic view. Threads:
  - **poll** (`System.Threading.Timer`, 15 ms): `GetCursorPos` → `WindowFromPoint` → `GetAncestor(GA_ROOT)` == editor HWND && `IsWindowVisible`. Flips `_focus` and signals the worker. Never touches navlib.
  - **worker** (dedicated thread): the **only** place `NlCreate`/`NlClose`/`NlWriteValue` run. Converges on `_focus`: open (create, then write `active=true`, `focus=true`) or close.
  - **navlib** callbacks (`bMultiThreaded=true`): `Get`/`Set` only touch cached state under `_lk`. `Set` ignores motion when `!_focus`.
  - **UI**: `Flush` (queued via `Application.Instance.AsyncInvoke`, at most one pending) applies state to the canvas. A 50 ms `UITimer` resyncs from the canvas when no navlib set has happened for 250 ms, which follows manual mouse panning and rebases the zoom.
  - All GH2 access is **reflection** (cached `PropertyInfo`/`MethodInfo`), so there's no compile-time dependency on Grasshopper2.dll and the plugin can load before GH2. If a member is missing, `NotSupportedException` is thrown and the controller reports it.

## 4. Grasshopper 2 canvas facts (discovered by reflection, 9.0.26300)

- `Grasshopper2.UI.Editor` (an Eto `Form`/`RhinoSlavedForm`) → `.Canvas` (`Grasshopper2.UI.Canvas.Canvas : FlexControl`).
- `FlexControl.Projection` (`Grasshopper2.UI.Flex.Projection`, public get/set) is **immutable**. `SetZoom(float)`, `SetCentre(PointF centre, RectangleF frame)`, `SetOrigin`, `PerformPan(SizeF)`, `PerformZoom(...)` all **return a new Projection**, which you must assign back. Calling them without assigning does nothing.
- `Projection.Zoom` = control pixels per content unit (`ControlPixelsPerContentPixel`). `Origin` is in control pixels and is rounded to integers (`IntegerOrigin`).
- **Trust `VisibleFrame`** (content-space rectangle actually visible) and `InnerBounds` (control size). Mapping: `content = (control - origin) / zoom`.
- **Don't trust `ControlToContent` / `ContentToControl`.** They gave results inconsistent with `VisibleFrame` and the Zoom semantics (they behave as if swapped or inverted).
- Positioning a view: `canvas.Projection = proj.SetZoom(z).SetCentre(centreContent, InnerBounds)`, then `ScheduleRedraw()`. Costs about 0.005 ms. A GH2 redraw is about 1.8 ms.
- `MinimumZoom`/`MaximumZoom` = 0.05 / 20. `ContentBounds` is `0,0,0,0` (unusable for model extents).
- `ScreenBounds` is in logical (DIP) coords. The editor HWND comes from `((Eto.Forms.Window)editor).NativeHandle`.

## 5. 3Dconnexion navlib facts (empirically verified)

- `TDxNavLib.dll` exports only `NlCreate, NlClose, NlGetType, NlReadValue, NlWriteValue` (C, cdecl, x64).
- `long NlCreate(uint64* handle, const char* appName, const accessor_t* accessors, size_t count, const nlCreateOptions_t* opts)`.
  - `accessor_t` = 32 bytes: `{ const char* name; fnGet; fnSet; uint64 param; }`.
  - Callbacks: `long fn(uint64 param, const char* name, value_t* value)`. Return 0 = OK; return any error (we use 0x80004001) for "no data".
  - `nlCreateOptions_t` = `{ uint32 size=12; bool bMultiThreaded; uint32 options; }`. **`bMultiThreaded` must be true** (see the pitfalls in section 7).
- `value_t` = `{ int32 type; pad; union at offset 8 }`. Type tags from `NlGetType`: bool=1, long=2, float=3, double=4, point=5, matrix=7, string=8, box=11, frustum=12, unknown name=-2. Matrices are 16 doubles, **column-major** (translation in [12],[13],[14]). Box = min xyz, max xyz.
- Properties navlib actually queried: view.affine, coordinateSystem, views.front, view.perspective, view.extents, view.constructionPlane, view.rotatable, model.extents, view.target, selection.empty, hit.lookat. Properties it set: hit.* (selectionOnly, aperture, direction, lookfrom), pivot.position, pivot.visible, transaction, view.affine, view.extents.
- With `view.perspective=false` and `view.rotatable=false`, navlib **zooms by dollying the camera along view Z** (it changes `view.affine[14]`). It did *not* change `view.extents` in testing (`extents=0` sets). We convert: `w = wBase * z / zBase`, and echo `z` back exactly as navlib set it during motion, rebasing only when idle. Reporting a different z each frame made navlib fight back (a CPU spike).
- `model.extents`: navlib scales speed from it. A box that changed with every pan made the speed erratic, so we use a fixed square 2× the visible frame, recomputed only when idle.
- **Focus hand-off with Rhino's plugin (3dxRhino.9.rhp, which also uses navlib in-process):**
  - Writing `focus=true` on our instance steals navigation from Rhino. ✅
  - Writing `focus=false` and/or `active=false` does **not** give it back. Rhino's instance never re-asserts focus, and clicking the Rhino window doesn't help. ❌
  - **`NlClose` on our instance does give it back** to Rhino. ✅ So the design is: connect on enter, disconnect on leave.
  - Measured: NlCreate is about 65–115 ms and NlClose about 40–100 ms (now on the worker thread, so the UI doesn't stall). The first motion arrives roughly 0.1 s after connecting in multithreaded mode. Single-threaded mode with reconnects was about 0.8 s and starved.
- Raw input (`RegisterRawInputDevices`, page 1 usage 8, INPUTSINK) also works alongside 3DxWare (nothing else in Rhino registers it), but it can't stop the Rhino viewport moving, so it isn't used.

## 6. Building (no SDK needed)

- Run `source\Build.cs` inside Rhino 9 (ScriptEditor → open → Run, or paste it into a Rhino MCP `run_csharp` call). Set `sourceDir` at the top or pick the folder when prompted. It:
  1. compiles every `source\*.cs` except Build.cs with Rhino's Roslyn (`Microsoft.CodeAnalysis*.dll` 5.0 in `Rhino 9\System`), **via reflection**,
  2. references the TPA `System*`/netstandard/mscorlib/Microsoft.Win32* plus RhinoCommon (netcore path) and Eto,
  3. emits `%TEMP%\GhSpaceMouse_dist\GhSpaceMouse.rhp` (about 22 KB), copies `manifest.yml`, runs `Yak.exe build --platform win`,
  4. copies `ghspacemouse-<ver>-rh9_0-win.yak` to `dist\` (next to `source\`), deleting old versions.
- Version bump: change `AssemblyVersion`/`AssemblyFileVersion`/`AssemblyInformationalVersion` in `GhSpaceMousePlugIn.cs` **and** `version:` in `manifest.yml`.
- Plugin GUID `6b1f6c9e-4f0d-4f0c-9a7e-2d6b9a1c3e51` and command GUID `0f3d6a52-8d7c-4b8e-9a3e-5c1f2b7d4e90`. **Never change these**, or Rhino will treat it as a different plugin and lose its settings.
- Quirks of Rhino's `run_csharp`/script runner (as of this build):
  - C# raw string literals (`"""`) → compile error.
  - `#r "path.dll"` → compile error.
  - Referencing `Grasshopper2.UI.*` types directly sometimes failed to compile. Using `dynamic` or reflection works.
  - Compile errors give no details ("Compile Error"), so isolate the problem with small scripts.
  - Wrap scripts in try/catch and print the innermost exception. Otherwise you only see "Exception has been thrown by the target of an invocation".
  - To move big files onto the machine: tar.gz + base64 here, then `GZipStream` + `System.Formats.Tar.TarFile.ExtractToDirectory` in Rhino.
- State survives between `run_csharp` calls via `AppDomain.CurrentDomain.SetData/GetData`. That's useful for live prototyping without the plugin. **Stop the old instance before installing a new one.**

## 7. Pitfalls we hit (don't repeat them)

1. **Garbage-collected delegate → Rhino FailFast.** navlib kept calling an accessor from an orphaned connection after the script object was replaced. The event log said "A callback was made on a garbage collected delegate of type …AccFn::Invoke". Fix: root delegates for the process lifetime (`static Roots`) and only open/close connections in one thread, so handles can't be orphaned.
2. **Overlapping `System.Threading.Timer` callbacks** (NlCreate takes about 100 ms > the poll interval) created two navlib instances and leaked one. Never call navlib from the poll callback.
3. **Single-threaded navlib floods the UI thread** with posted messages. WM_TIMER and repaint are low priority and starve, causing laggy canvas updates and a hover-detection timer that didn't fire for 30 s. Use `bMultiThreaded=true`.
4. Applying the canvas on each `view.affine`/`view.extents` set double-applies. Coalesce to one `Flush` per frame (on `transaction == 0` or the first set).
5. `Projection` immutability (see section 4) and misleading transform matrices.

## 8. Testing protocol (needs a human with a SpaceMouse)

1. Hover GH2 and pan/zoom for about 5 s. It should be smooth, zoom should work, and speed should be steady.
2. Hover Rhino and navigate. The viewport should move and the canvas should stay still.
3. Toggle back and forth several times, quickly.
4. Check `%TEMP%\GhSpaceMouse.log`: you should see matched `enter: connected` / `leave: disconnected` lines, with "ms after cursor moved".
5. `GhSpaceMouse` → Enabled Off. The SpaceMouse should only move Rhino. Turn it back On.
6. Close and reopen the GH2 editor. It should re-attach within about 1 s.
7. Restart Rhino. It should load and attach automatically.

## 9. Known risks and ideas

- GH2 is evolving. Breakage most likely shows up in the members listed in `CanvasNavigator` (`Need(...)` calls). Re-discover them by reflection on `Grasshopper2.UI.Canvas.Canvas` and `Grasshopper2.UI.Flex.Projection`.
- A 3DxWare update could change the navlib type tags or focus behaviour. Re-run `NlGetType` checks and the focus hand-off test.
- Possible improvements:
  - Grasshopper 1 support (WinForms `GH_Canvas.Viewport`).
  - Restricting focus to the canvas area rather than the whole editor window.
  - Using navlib `commands`/buttons, for example "fit" → GH zoom extents.
  - Code signing for locked-down work machines.
