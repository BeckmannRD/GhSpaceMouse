# Notes for AI coding assistants

Read [`DEVELOPER_NOTES.md`](DEVELOPER_NOTES.md) before touching anything. It contains:

- the verified (undocumented) Grasshopper 2 canvas API details and the 3Dconnexion navlib C interface,
- how focus hand-off with Rhino's own 3Dconnexion plugin actually behaves,
- every bug hit during development and why (including one that crashes Rhino),
- how to build with only Rhino 9 (no .NET SDK), and quirks of Rhino's C# script runner,
- a manual test procedure (it needs a human with a SpaceMouse).

The code is small: `source/CanvasNavigator.cs` (core) and `source/GhSpaceMousePlugIn.cs` (plugin, command, controller, log).
Never change the plugin or command GUIDs.
