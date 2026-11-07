// GhSpaceMousePlugIn.cs
// Plug-in entry point, the GhSpaceMouse command, the controller that finds the Grasshopper 2 editor
// and attaches a CanvasNavigator to it, and a small log.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

[assembly: Guid("6b1f6c9e-4f0d-4f0c-9a7e-2d6b9a1c3e51")]
[assembly: AssemblyTitle("GhSpaceMouse")]
[assembly: AssemblyDescription("Navigate the Grasshopper 2 canvas with a 3Dconnexion SpaceMouse")]
[assembly: AssemblyCompany("BeckmannRD")]
[assembly: AssemblyCopyright("Copyright (c) 2026 BeckmannRD. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: PlugInDescription(DescriptionType.Organization, "BeckmannRD")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/BeckmannRD/GhSpaceMouse")]

namespace GhSpaceMouse
{
  public class GhSpaceMousePlugIn : PlugIn
  {
    public static GhSpaceMousePlugIn Instance { get; private set; }
    internal Controller Controller { get; private set; }

    public GhSpaceMousePlugIn() { Instance = this; }

    // Load with Rhino so the behaviour is always available.
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    // Remembered between sessions.
    public bool Enabled
    {
      get => Settings.GetBool("Enabled", true);
      set { Settings.SetBool("Enabled", value); Controller?.SetEnabled(value); }
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
      if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        errorMessage = "GhSpaceMouse only works on Windows.";
        return LoadReturnCode.ErrorNoDialog;
      }
      Controller = new Controller();
      RhinoApp.Idle += FirstIdle;   // start once Rhino's UI is up
      return LoadReturnCode.Success;
    }

    void FirstIdle(object sender, EventArgs e)
    {
      RhinoApp.Idle -= FirstIdle;
      Log.Write("---- Rhino " + RhinoApp.Version + ", GhSpaceMouse " + typeof(GhSpaceMousePlugIn).Assembly.GetName().Version + ", enabled=" + Enabled);
      Controller.SetEnabled(Enabled);
    }

    protected override void OnShutdown()
    {
      try { Controller?.Dispose(); } catch { }
      base.OnShutdown();
    }
  }

  // Watches for the Grasshopper 2 editor and keeps a CanvasNavigator attached to its canvas.
  internal sealed class Controller : IDisposable
  {
    Eto.Forms.UITimer _watch;
    CanvasNavigator _nav;
    public string Status { get; private set; } = "starting";

    public void SetEnabled(bool on)
    {
      if (!on)
      {
        StopWatching(); Detach();
        Status = "off";
        Log.Write("disabled");
        return;
      }
      IntPtr lib;
      if (!NativeLibrary.TryLoad("TDxNavLib.dll", out lib))
      {
        Status = "unavailable: 3Dconnexion Navigation Library (TDxNavLib.dll) not found. Is 3DxWare installed?";
        RhinoApp.WriteLine("GhSpaceMouse: " + Status);
        Log.Write(Status);
        return;
      }
      Status = "waiting for the Grasshopper 2 editor";
      if (_watch == null)
      {
        _watch = new Eto.Forms.UITimer { Interval = 1.0 };
        _watch.Elapsed += (s, e) => Tick();
        _watch.Start();
      }
      Log.Write("enabled");
      Tick();
    }

    void StopWatching() { if (_watch != null) { _watch.Stop(); _watch = null; } }

    void Tick()
    {
      try
      {
        var editor = FindGh2Editor();
        object canvas = null; IntPtr hwnd = IntPtr.Zero;
        if (editor != null)
        {
          canvas = editor.GetType().GetProperty("Canvas")?.GetValue(editor);
          hwnd = editor.NativeHandle;
        }
        if (_nav != null && (_nav.Canvas != canvas || _nav.EditorHandle != hwnd)) Detach();
        if (_nav == null && canvas != null && hwnd != IntPtr.Zero)
        {
          _nav = new CanvasNavigator(canvas, hwnd);
          _nav.Fault += OnFault;
          Status = "on (attached to the Grasshopper 2 canvas)";
        }
        else if (_nav == null) Status = "on (waiting for the Grasshopper 2 editor)";
      }
      catch (Exception ex)
      {
        OnFault((ex.InnerException ?? ex).Message);
      }
    }

    // Find the GH2 editor without creating it: look among open Eto windows, then at Editor's static field.
    static Eto.Forms.Window FindGh2Editor()
    {
      var gh2 = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper2");
      if (gh2 == null) return null;           // Grasshopper 2 not loaded yet
      var editorType = gh2.GetType("Grasshopper2.UI.Editor");
      if (editorType == null) return null;
      foreach (var w in Eto.Forms.Application.Instance.Windows)
        if (editorType.IsInstanceOfType(w) && !w.IsDisposed) return w;
      foreach (var f in editorType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        if (editorType.IsAssignableFrom(f.FieldType))
        {
          var w = f.GetValue(null) as Eto.Forms.Window;
          if (w != null && !w.IsDisposed) return w;
        }
      return null;
    }

    void OnFault(string msg)
    {
      Detach(); StopWatching();
      Status = "stopped: " + msg;
      RhinoApp.WriteLine("GhSpaceMouse: " + msg + " SpaceMouse navigation of Grasshopper is off for this session.");
      Log.Write("stopped: " + msg);
    }

    void Detach()
    {
      if (_nav == null) return;
      _nav.Fault -= OnFault;
      try { _nav.Dispose(); } catch { }
      _nav = null;
    }

    public void Dispose() { StopWatching(); Detach(); }
  }

  [Guid("0f3d6a52-8d7c-4b8e-9a3e-5c1f2b7d4e90")]
  public class GhSpaceMouseCommand : Command
  {
    public override string EnglishName => "GhSpaceMouse";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var pi = GhSpaceMousePlugIn.Instance;
      var enabled = new OptionToggle(pi.Enabled, "Off", "On");
      var go = new GetOption();
      go.AcceptNothing(true);
      while (true)
      {
        go.ClearCommandOptions();
        go.SetCommandPrompt("GhSpaceMouse is " + pi.Controller.Status + ". Press Enter when done");
        go.AddOptionToggle("Enabled", ref enabled);
        int logIdx = go.AddOption("OpenLog");
        var r = go.Get();
        if (r == GetResult.Option)
        {
          if (go.OptionIndex() == logIdx) Log.Open();
          else if (enabled.CurrentValue != pi.Enabled) pi.Enabled = enabled.CurrentValue;
          continue;
        }
        break;
      }
      RhinoApp.WriteLine("GhSpaceMouse is " + pi.Controller.Status + ".");
      return Result.Success;
    }
  }

  // Tiny event log (connect/disconnect/errors only, never per frame). %TEMP%\GhSpaceMouse.log
  internal static class Log
  {
    static readonly object Lk = new object();
    public static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GhSpaceMouse.log");

    public static void Write(string s)
    {
      try
      {
        lock (Lk)
        {
          var fi = new FileInfo(PathName);
          if (fi.Exists && fi.Length > 512 * 1024) File.Delete(PathName);
          File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s + Environment.NewLine);
        }
      }
      catch { }
    }

    public static void Open()
    {
      try
      {
        if (!File.Exists(PathName)) Write("log created");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PathName) { UseShellExecute = true });
      }
      catch (Exception ex) { RhinoApp.WriteLine("GhSpaceMouse: could not open log: " + ex.Message); }
    }
  }
}
