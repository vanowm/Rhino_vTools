using Rhino;
using Rhino.PlugIns;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace vTools;

/// <summary>
/// Rhino plug-in entry point for the vTools command set.
/// </summary>
[System.Runtime.InteropServices.Guid("2607512e-a1fc-4cf9-9329-a293431437a0")]
public class vToolsPlugIn : PlugIn
{
  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

  protected override string LocalPlugInName => "vTools";

  public vToolsPlugIn() { Instance = this; }

  public static vToolsPlugIn Instance { get; private set; } = null!;

  protected override LoadReturnCode OnLoad(ref string errorMessage)
  {
    var asm = GetType().Assembly;
    var version = (!string.IsNullOrEmpty(asm.Location)
      ? System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion
      : null) ?? asm.GetName().Version?.ToString() ?? "unknown";

    Log.Initialize();
    Log.Write($"startup  version={version}  dll={asm.Location}");
    EnsureToolbarLoaded(asm.Location);

    var commandNames = CollectRegisteredCommandNames();
    Log.Write($"startup  commands ({commandNames.Count}): {string.Join(", ", commandNames)}");

    RhinoApp.WriteLine($"vTools v{version} loaded — {commandNames.Count} commands: {string.Join(", ", commandNames)}.");
    return LoadReturnCode.Success;
  }

  private static void EnsureToolbarLoaded(string assemblyLocation)
  {
    try
    {
      var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
      if (string.IsNullOrEmpty(assemblyDirectory))
        return;

      var toolbarPath = Path.Combine(assemblyDirectory, "vTools.rui");
      if (!File.Exists(toolbarPath))
      {
        Log.Write($"startup  toolbar missing path={toolbarPath}");
        return;
      }

      var toolbarFile = RhinoApp.ToolbarFiles.FindByName("vTools", true) ??
        RhinoApp.ToolbarFiles.FindByPath(toolbarPath);
      var openedNow = toolbarFile == null;
      toolbarFile ??= RhinoApp.ToolbarFiles.Open(toolbarPath);
      if (toolbarFile == null)
      {
        Log.Write($"startup  toolbar open failed path={toolbarPath}");
        return;
      }

      if (openedNow)
      {
        var group = toolbarFile.GetGroup("vTools Isolate");
        if (group != null)
          group.Visible = true;
      }

      Log.Write(
        $"startup  toolbar={(openedNow ? "opened" : "already open")}" +
        $" path={toolbarFile.Path}");
    }
    catch (Exception ex)
    {
      Log.Write("startup toolbar", ex.ToString());
    }
  }

  private List<string> CollectRegisteredCommandNames()
  {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
      foreach (var t in GetType().Assembly.GetTypes())
      {
        if (t == null || !t.IsClass || t.IsAbstract) continue;
        if (!typeof(Rhino.Commands.Command).IsAssignableFrom(t)) continue;
        try
        {
          if (Activator.CreateInstance(t) is Rhino.Commands.Command cmd)
          {
            var name = (cmd.EnglishName ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(name)) names.Add(name);
          }
        }
        catch { }
      }
    }
    catch { }
    return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
  }
}
