// CanvasNavigator.cs
// Makes the Grasshopper 2 canvas a 2D "view" for the 3Dconnexion Navigation Library (TDxNavLib.dll, part of 3DxWare).
// While the cursor is over the Grasshopper 2 editor window we hold a navlib connection, so the driver sends
// motion to the canvas instead of the Rhino viewport. When the cursor leaves we close the connection and the
// driver falls back to Rhino's own 3Dconnexion plugin (3dxRhino.9.rhp).
//
// Threads:
//   poll thread   - reads the cursor every 15 ms, flips _focus, wakes the worker. Never touches navlib.
//   worker thread - the ONLY place NlCreate / NlClose / NlWriteValue are called. Keeps the UI thread free.
//   navlib thread - calls Get/Set (bMultiThreaded = true). Only reads/writes cached view state under _lk.
//   UI thread     - applies the view to the canvas (Flush) and resyncs from the canvas when idle.
//
// All Grasshopper 2 access is by reflection so this assembly has no compile-time dependency on Grasshopper2.dll.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Eto.Drawing;

namespace GhSpaceMouse
{
  internal sealed class CanvasNavigator : IDisposable
  {
    // ---- native API -------------------------------------------------------------------------------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AccFn(ulong param, IntPtr name, IntPtr value);
    [DllImport("TDxNavLib.dll", CallingConvention = CallingConvention.Cdecl)] static extern int NlCreate(out ulong nh, [MarshalAs(UnmanagedType.LPStr)] string app, IntPtr accessors, UIntPtr count, IntPtr options);
    [DllImport("TDxNavLib.dll", CallingConvention = CallingConvention.Cdecl)] static extern int NlClose(ulong nh);
    [DllImport("TDxNavLib.dll", CallingConvention = CallingConvention.Cdecl)] static extern int NlWriteValue(ulong nh, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    // navlib value_t type tags (verified with NlGetType on 3DxWare 10, TDxNavLib 0.9.24354)
    const int T_BOOL = 1, T_POINT = 5, T_MATRIX = 7, T_BOX = 11;
    const int E_NODATA = unchecked((int)0x80004001);
    public const string AppName = "Grasshopper 2 Canvas"; // appears as its own application profile in 3DxWare settings

    // Delegates + native memory live for the whole process. navlib may still call an accessor briefly
    // after NlClose; a collected delegate there kills Rhino (this happened during development).
    static readonly List<object> Roots = new List<object>();

    static readonly string[] Props = {
      "coordinateSystem","view.affine","view.extents","view.perspective","view.rotatable","model.extents",
      "pivot.position","selection.empty","transaction","view.target","views.front","view.constructionPlane",
      "pivot.visible","pivot.user","hit.lookfrom","hit.direction","hit.aperture","hit.selectionOnly","hit.lookat" };

    readonly AccFn _get, _set;
    readonly IntPtr _acc, _opt, _val;
    ulong _handle;                       // worker thread only
    volatile bool _focus, _disposed, _flushQueued;
    double _changedAt, _lastSetAt = -1e9;
    readonly Thread _worker;
    readonly AutoResetEvent _wake = new AutoResetEvent(false);
    readonly Timer _poll;
    readonly Eto.Forms.UITimer _uiTimer;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double Now => _clock.Elapsed.TotalMilliseconds;
    int _flushErrors;

    // ---- Grasshopper 2 canvas (reflection) ---------------------------------------------------------
    readonly object _canvas;
    readonly IntPtr _editorHwnd;
    readonly PropertyInfo _pProjection, _pVisibleFrame, _pInnerBounds, _pMinZoom, _pMaxZoom;
    readonly MethodInfo _mRedraw, _mSetZoom, _mSetCentre;

    // ---- cached view (canvas content coords, y down). Height = _w * _aspect.
    // navlib zooms a non-rotatable ortho view by dollying the camera (view.affine z); _z is echoed back
    // exactly as navlib set it during a motion and rebased when idle.
    readonly object _lk = new object();
    double _cx, _cy, _w, _aspect = 1, _z, _wBase, _zBase;
    RectangleF _model;

    public event Action<string> Fault;   // raised on the UI thread when the navigator gives up
    public object Canvas => _canvas;
    public IntPtr EditorHandle => _editorHwnd;
    public bool Connected => _handle != 0;

    public CanvasNavigator(object canvas, IntPtr editorHwnd)
    {
      _canvas = canvas; _editorHwnd = editorHwnd;
      var ct = canvas.GetType();
      _pProjection = Need(ct.GetProperty("Projection"), "Canvas.Projection");
      _pVisibleFrame = Need(ct.GetProperty("VisibleFrame"), "Canvas.VisibleFrame");
      _pInnerBounds = Need(ct.GetProperty("InnerBounds"), "Canvas.InnerBounds");
      _pMinZoom = Need(ct.GetProperty("MinimumZoom"), "Canvas.MinimumZoom");
      _pMaxZoom = Need(ct.GetProperty("MaximumZoom"), "Canvas.MaximumZoom");
      _mRedraw = Need(ct.GetMethod("ScheduleRedraw", Type.EmptyTypes), "Canvas.ScheduleRedraw()");
      var pt = _pProjection.PropertyType;
      _mSetZoom = Need(pt.GetMethod("SetZoom", new[] { typeof(float) }), "Projection.SetZoom(float)");
      _mSetCentre = Need(pt.GetMethod("SetCentre", new[] { typeof(PointF), typeof(RectangleF) }), "Projection.SetCentre(PointF,RectangleF)");

      _get = Get; _set = Set;
      lock (Roots) { Roots.Add(_get); Roots.Add(_set); Roots.Add(this); }
      int n = Props.Length;
      _acc = Marshal.AllocHGlobal(32 * n);                 // accessor_t { const char* name; get; set; uint64 param; }
      for (int i = 0; i < n; i++)
      {
        Marshal.WriteIntPtr(_acc, i * 32, Marshal.StringToHGlobalAnsi(Props[i]));
        Marshal.WriteIntPtr(_acc, i * 32 + 8, Marshal.GetFunctionPointerForDelegate(_get));
        Marshal.WriteIntPtr(_acc, i * 32 + 16, Marshal.GetFunctionPointerForDelegate(_set));
        Marshal.WriteInt64(_acc, i * 32 + 24, 0);
      }
      _opt = Marshal.AllocHGlobal(16);                     // nlCreateOptions_t { uint32 size; bool bMultiThreaded; uint32 options; }
      for (int i = 0; i < 16; i++) Marshal.WriteByte(_opt, i, 0);
      Marshal.WriteInt32(_opt, 0, 12); Marshal.WriteByte(_opt, 4, 1);
      _val = Marshal.AllocHGlobal(256);

      SyncFromCanvas();
      _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GhSpaceMouse navlib" };
      _worker.Start();
      _uiTimer = new Eto.Forms.UITimer { Interval = 0.05 };
      _uiTimer.Elapsed += (s, e) => { try { if (!_disposed && Now - _lastSetAt > 250) SyncFromCanvas(); } catch (Exception ex) { Log.Write("sync: " + ex.Message); } };
      _uiTimer.Start();
      _poll = new Timer(_ => Poll(), null, 0, 15);
      Log.Write("navigator attached to Grasshopper 2 canvas");
    }

    static T Need<T>(T member, string what) where T : class
    {
      if (member == null) throw new NotSupportedException("Grasshopper 2 API not found: " + what + " (a Rhino update may have changed Grasshopper 2)");
      return member;
    }

    // ---- cursor polling (background) ---------------------------------------------------------------
    void Poll()
    {
      try
      {
        if (_disposed) return;
        GetCursorPos(out POINT p);
        bool over = GetAncestor(WindowFromPoint(p), 2 /*GA_ROOT*/) == _editorHwnd && IsWindowVisible(_editorHwnd);
        if (over != _focus) { _focus = over; _changedAt = Now; _wake.Set(); }
      }
      catch { }
    }

    // ---- connection management (worker thread only) ------------------------------------------------
    void WorkerLoop()
    {
      while (true)
      {
        _wake.WaitOne(250);
        try
        {
          if (_disposed) { CloseHandle(); return; }
          bool want = _focus;
          if (want && _handle == 0) Open();
          else if (!want && _handle != 0) CloseHandle();
        }
        catch (DllNotFoundException) { RaiseFault("3Dconnexion Navigation Library (TDxNavLib.dll) not found. Is 3DxWare installed?"); return; }
        catch (Exception ex) { Log.Write("worker: " + ex.Message); }
      }
    }
    void Open()
    {
      var t = Now;
      ulong h;
      int r = NlCreate(out h, AppName, _acc, (UIntPtr)Props.Length, _opt);
      if (r != 0) { Log.Write("NlCreate failed 0x" + r.ToString("X")); return; }
      _handle = h; WriteBool("active", true); WriteBool("focus", true);
      Log.Write("enter: connected " + (Now - t).ToString("F0") + " ms (" + (Now - _changedAt).ToString("F0") + " ms after cursor moved)");
    }
    void CloseHandle()
    {
      if (_handle == 0) return;
      var h = _handle; _handle = 0; var t = Now; NlClose(h);
      Log.Write("leave: disconnected " + (Now - t).ToString("F0") + " ms (" + (Now - _changedAt).ToString("F0") + " ms after cursor moved)");
    }
    int WriteBool(string name, bool b)
    {
      if (_handle == 0) return -1;
      for (int i = 0; i < 16; i++) Marshal.WriteByte(_val, i, 0);
      Marshal.WriteInt32(_val, 0, T_BOOL); Marshal.WriteByte(_val, 8, (byte)(b ? 1 : 0));
      return NlWriteValue(_handle, name, _val);
    }

    // ---- view state ----------------------------------------------------------------------------------
    void SyncFromCanvas() // UI thread
    {
      var fr = (RectangleF)_pVisibleFrame.GetValue(_canvas);
      lock (_lk)
      {
        _cx = fr.X + fr.Width / 2; _cy = fr.Y + fr.Height / 2; _w = fr.Width;
        _aspect = fr.Height / Math.Max(fr.Width, 1e-6f);
        _z = _zBase = Math.Max(fr.Width, fr.Height); _wBase = _w;
        // model box fixed per idle period so the driver's speed scaling stays steady (GH2 ContentBounds is empty)
        float s = Math.Max(fr.Width, fr.Height) * 2f;
        _model = new RectangleF((float)_cx - s / 2, (float)_cy - s / 2, s, s);
      }
    }

    static void WD(IntPtr v, int type, params double[] d) { Marshal.WriteInt32(v, 0, type); Marshal.Copy(d, 0, v + 8, d.Length); }
    static void WB(IntPtr v, bool b) { Marshal.WriteInt32(v, 0, T_BOOL); Marshal.WriteByte(v, 8, (byte)(b ? 1 : 0)); }

    // navlib reads the view. World = canvas with y flipped; camera looks down -Z, no rotation.
    int Get(ulong param, IntPtr np, IntPtr v)
    {
      if (_disposed) return E_NODATA;
      try
      {
        var name = Marshal.PtrToStringAnsi(np);
        double cx, cy, w, h, z; RectangleF cb;
        lock (_lk) { cx = _cx; cy = _cy; w = _w; h = _w * _aspect; z = _z; cb = _model; }
        switch (name)
        {
          case "coordinateSystem": WD(v, T_MATRIX, 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1); return 0;
          case "view.affine":      WD(v, T_MATRIX, 1,0,0,0, 0,1,0,0, 0,0,1,0, cx,-cy,z,1); return 0;   // column-major
          case "view.extents":     WD(v, T_BOX, -w/2,-h/2,-z*10, w/2,h/2,z*10); return 0;
          case "view.perspective":
          case "view.rotatable":   WB(v, false); return 0;
          case "selection.empty":  WB(v, true); return 0;
          case "pivot.position":   WD(v, T_POINT, cx,-cy,0); return 0;
          case "model.extents":    WD(v, T_BOX, cb.X,-(cb.Y+cb.Height),-1, cb.X+cb.Width,-cb.Y,1); return 0;
          default: return E_NODATA;
        }
      }
      catch (Exception ex) { Log.Write("get: " + ex.Message); return E_NODATA; }
    }

    // navlib writes the new view.
    int Set(ulong param, IntPtr np, IntPtr v)
    {
      if (_disposed) return E_NODATA;
      try
      {
        var name = Marshal.PtrToStringAnsi(np);
        if (name == "transaction") { if (Marshal.ReadInt32(v, 8) == 0) QueueFlush(); return 0; }
        if (name != "view.affine" && name != "view.extents") return E_NODATA;
        if (!_focus) return 0;     // stray motion after the cursor left: ignore
        _lastSetAt = Now;
        lock (_lk)
        {
          if (name == "view.affine")
          {
            var m = new double[16]; Marshal.Copy(v + 8, m, 0, 16);
            _cx = m[12]; _cy = -m[13];
            double nz = m[14];
            if (nz > _zBase * 1e-3 && Math.Abs(nz - _z) > _zBase * 1e-9) { _z = nz; _w = _wBase * (_z / _zBase); }
          }
          else
          {
            var b = new double[6]; Marshal.Copy(v + 8, b, 0, 6); double nw = b[3] - b[0];
            if (nw > 1e-6 && Math.Abs(nw - _w) > _w * 1e-9) { _w = nw; _wBase = _w * (_zBase / _z); }
          }
        }
        QueueFlush();
        return 0;
      }
      catch (Exception ex) { Log.Write("set: " + ex.Message); return E_NODATA; }
    }

    void QueueFlush()
    {
      if (_flushQueued || _disposed) return;
      _flushQueued = true;
      Eto.Forms.Application.Instance.AsyncInvoke(Flush);
    }

    // UI thread: at most one pending at a time, so one canvas update per frame.
    void Flush()
    {
      _flushQueued = false;
      try
      {
        if (!_focus || _disposed) return;
        double cx, cy, w; lock (_lk) { cx = _cx; cy = _cy; w = _w; }
        var inner = (RectangleF)_pInnerBounds.GetValue(_canvas);
        float z = (float)(inner.Width / w);
        float zc = Math.Max((float)_pMinZoom.GetValue(_canvas), Math.Min((float)_pMaxZoom.GetValue(_canvas), z));
        var p = _pProjection.GetValue(_canvas);
        p = _mSetZoom.Invoke(p, new object[] { zc });
        p = _mSetCentre.Invoke(p, new object[] { new PointF((float)cx, (float)cy), inner });
        _pProjection.SetValue(_canvas, p);
        if (zc != z) lock (_lk) { _w = inner.Width / zc; _wBase = _w * (_zBase / _z); }
        _mRedraw.Invoke(_canvas, null);
        _flushErrors = 0;
      }
      catch (Exception ex)
      {
        var root = ex.InnerException ?? ex;
        Log.Write("flush: " + root.Message);
        if (++_flushErrors > 20) RaiseFault("Could not move the Grasshopper 2 canvas: " + root.Message);
      }
    }

    void RaiseFault(string msg)
    {
      Log.Write("FAULT: " + msg);
      Eto.Forms.Application.Instance.AsyncInvoke(() => { var f = Fault; if (f != null) f(msg); });
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true; _focus = false;
      try { _poll.Dispose(); } catch { }
      try { _uiTimer.Stop(); } catch { }
      _wake.Set();
      _worker.Join(2000);
      Log.Write("navigator detached");
    }
  }
}
