// Build.cs  -  rebuilds GhSpaceMouse.rhp and the .yak package using only Rhino 9 (no Visual Studio / .NET SDK needed).
//
// How to run: in Rhino 9 type ScriptEditor, open this file, press Run
// (or ask Claude to run it through the Rhino connection with run_csharp).
// It compiles every .cs file in this folder except Build.cs with the C# compiler (Roslyn) that ships with Rhino,
// runs Rhino's Yak.exe to make the package, and copies the .yak into ..\dist next to the README.
//
// Roslyn is called through reflection on purpose: Rhino's script runner can't reference Microsoft.CodeAnalysis directly.
// Bump the version in GhSpaceMousePlugIn.cs (AssemblyVersion etc.) AND in manifest.yml together.

using System;
using System.IO;
using System.Linq;
using System.Reflection;

string step = "start";
try
{
  Assembly Load(string n) { var a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == n); return a ?? Assembly.Load(n); }
  object Def(ParameterInfo p) { if (p.HasDefaultValue && p.DefaultValue != null) return p.DefaultValue; return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null; }
  object Call(MethodBase m, object target, params object[] first) { var ps = m.GetParameters(); var args = ps.Select((p, k) => k < first.Length ? first[k] : Def(p)).ToArray(); return m is ConstructorInfo c ? c.Invoke(args) : m.Invoke(target, args); }

  var mca = Load("Microsoft.CodeAnalysis"); var cs = Load("Microsoft.CodeAnalysis.CSharp");
  var tSyntaxTree = mca.GetType("Microsoft.CodeAnalysis.SyntaxTree");
  var tMetaRef = mca.GetType("Microsoft.CodeAnalysis.MetadataReference");
  var tOutputKind = mca.GetType("Microsoft.CodeAnalysis.OutputKind");
  var tOptLevel = mca.GetType("Microsoft.CodeAnalysis.OptimizationLevel");
  var tCsTree = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
  var tCsOpts = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
  var tCsComp = cs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");

  // Folder containing the .cs files. Leave empty to be asked with a folder picker.
  var sourceDir = @"";
  if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
  {
    var dlg = new Eto.Forms.SelectFolderDialog { Title = "Select the GhSpaceMouse 'source' folder" };
    if (dlg.ShowDialog(null) != Eto.Forms.DialogResult.Ok) { Console.WriteLine("cancelled"); return; }
    sourceDir = dlg.Directory;
  }
  var rootDir = Path.Combine(Path.GetDirectoryName(sourceDir), "dist");
  Directory.CreateDirectory(rootDir);
  var distDir = Path.Combine(Path.GetTempPath(), "GhSpaceMouse_dist");
  var rhinoCommon = typeof(Rhino.RhinoApp).Assembly.Location;          // ...\Rhino 9\System\netcore\RhinoCommon.dll
  var eto = typeof(Eto.Forms.Application).Assembly.Location;           // ...\Rhino 9\System\Eto.dll
  var yakExe = Path.Combine(Path.GetDirectoryName(eto), "Yak.exe");    // ...\Rhino 9\System\Yak.exe

  step = "parse";
  var files = Directory.GetFiles(sourceDir, "*.cs").Where(f => !Path.GetFileName(f).Equals("Build.cs", StringComparison.OrdinalIgnoreCase)).ToArray();
  var pm = tCsTree.GetMethods(BindingFlags.Public | BindingFlags.Static).First(x => x.Name == "ParseText" && x.GetParameters()[0].ParameterType == typeof(string) && x.GetParameters().Length == 5);
  var trees = Array.CreateInstance(tSyntaxTree, files.Length);
  for (int i = 0; i < files.Length; i++) trees.SetValue(Call(pm, null, File.ReadAllText(files[i]), null, files[i], System.Text.Encoding.UTF8), i);

  step = "refs";
  var refPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
    .Where(p => Path.GetFileName(p).StartsWith("System") || Path.GetFileName(p) == "netstandard.dll" || Path.GetFileName(p) == "mscorlib.dll" || Path.GetFileName(p).StartsWith("Microsoft.Win32"))
    .Concat(new[] { rhinoCommon, eto }).Distinct().ToArray();
  var cff = tMetaRef.GetMethods(BindingFlags.Public | BindingFlags.Static).First(x => x.Name == "CreateFromFile");
  var refs = Array.CreateInstance(tMetaRef, refPaths.Length);
  for (int i = 0; i < refPaths.Length; i++) refs.SetValue(Call(cff, null, refPaths[i]), i);

  step = "options";
  var ctor = tCsOpts.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First(c => c.GetParameters()[0].ParameterType == tOutputKind);
  var opts = Call(ctor, null, Enum.Parse(tOutputKind, "DynamicallyLinkedLibrary"));
  opts = tCsOpts.GetMethod("WithOptimizationLevel").Invoke(opts, new[] { Enum.Parse(tOptLevel, "Release") });

  step = "compile";
  var create = tCsComp.GetMethods(BindingFlags.Public | BindingFlags.Static).First(x => x.Name == "Create" && x.GetParameters().Length == 4);
  var comp = create.Invoke(null, new object[] { "GhSpaceMouse", trees, refs, opts });

  step = "emit";
  if (Directory.Exists(distDir)) Directory.Delete(distDir, true);
  Directory.CreateDirectory(distDir);
  var rhp = Path.Combine(distDir, "GhSpaceMouse.rhp");
  object res;
  using (var fs = File.Create(rhp))
  {
    var emit = comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "Emit" && x.GetParameters()[0].ParameterType == typeof(Stream)).OrderByDescending(x => x.GetParameters().Length).First();
    res = Call(emit, comp, fs);
  }
  bool ok = (bool)res.GetType().GetProperty("Success").GetValue(res);
  foreach (var d in (System.Collections.IEnumerable)res.GetType().GetProperty("Diagnostics").GetValue(res))
  {
    var sev = d.GetType().GetProperty("Severity").GetValue(d).ToString();
    if (sev == "Error" || sev == "Warning") Console.WriteLine(d.ToString());
  }
  if (!ok) { Console.WriteLine("BUILD FAILED"); return; }
  Console.WriteLine("compiled " + rhp + " (" + new FileInfo(rhp).Length + " bytes)");

  step = "yak";
  File.Copy(Path.Combine(sourceDir, "manifest.yml"), Path.Combine(distDir, "manifest.yml"));
  var psi = new System.Diagnostics.ProcessStartInfo(yakExe, "build --platform win")
    { WorkingDirectory = distDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
  using (var p = System.Diagnostics.Process.Start(psi)) { Console.WriteLine(p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()); p.WaitForExit(60000); }
  foreach (var old in Directory.GetFiles(rootDir, "ghspacemouse-*.yak")) File.Delete(old);
  foreach (var yak in Directory.GetFiles(distDir, "*.yak")) { var dest = Path.Combine(rootDir, Path.GetFileName(yak)); File.Copy(yak, dest, true); Console.WriteLine("package: " + dest); }
}
catch (Exception ex)
{
  var e = ex; while (e.InnerException != null) e = e.InnerException;
  Console.WriteLine("FAILED at " + step + ": " + e.GetType().Name + ": " + e.Message);
}
