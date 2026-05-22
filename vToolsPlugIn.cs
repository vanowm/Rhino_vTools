using Rhino;
using Rhino.PlugIns;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace vTools;

/// <summary>
/// Rhino plug-in entry point for the vTools command set.
/// </summary>
[System.Runtime.InteropServices.Guid("2607512e-a1fc-4cf9-9329-a293431437a0")]
public class vToolsPlugIn : PlugIn
{
  /// <summary>
  /// Loads the plug-in at Rhino startup so commands are available immediately.
  /// </summary>
  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

  /// <summary>
  /// Display name shown by Rhino for this plug-in.
  /// </summary>
  protected override string LocalPlugInName => "vTools";

  /// <summary>
  /// Creates the singleton plug-in instance used by Rhino.
  /// </summary>
  public vToolsPlugIn()
  {
    Instance = this;
  }

  /// <summary>
  /// Gets the current loaded plug-in instance.
  /// </summary>
  public static vToolsPlugIn Instance { get; private set; } = null!;

  /// <summary>
  /// Handles Rhino plug-in load and writes a startup diagnostic message.
  /// </summary>
  protected override LoadReturnCode OnLoad(ref string errorMessage)
  {
    var asm = GetType().Assembly;
    var version = (!string.IsNullOrEmpty(asm.Location)
      ? System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion
      : null) ?? asm.GetName().Version?.ToString() ?? "unknown";
    var commandNames = CollectRegisteredCommandNames();
    TryLog($"OnLoad OK. Version={version}. Assembly={GetType().Assembly.Location}");
    Commands.vBiminiParts.InitLog();
    RhinoApp.WriteLine($"vTools v{version} loaded. Commands registered ({commandNames.Count}): {string.Join(", ", commandNames)}");
    return LoadReturnCode.Success;
  }

  /// <summary>
  /// Collects all non-abstract command names from this plug-in assembly.
  /// </summary>
  private List<string> CollectRegisteredCommandNames()
  {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
      var commandTypes = GetType()
        .Assembly
        .GetTypes()
        .Where(t =>
          t != null &&
          t.IsClass &&
          !t.IsAbstract &&
          typeof(Rhino.Commands.Command).IsAssignableFrom(t));

      foreach (var commandType in commandTypes)
      {
        try
        {
          if (Activator.CreateInstance(commandType) is Rhino.Commands.Command command)
          {
            var name = (command.EnglishName ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(name))
              names.Add(name);
          }
        }
        catch
        {
        }
      }
    }
    catch
    {
    }

    var ordered = names
      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
      .ToList();

    return ordered;
  }

  /// <summary>
  /// Appends one log message to the plug-in log file.
  /// </summary>
  internal static void TryLog(string message)
  {
    try
    {
      var logDir = ResolveProjectLogsDir();
      if (string.IsNullOrWhiteSpace(logDir))
        return;

      Directory.CreateDirectory(logDir);
      var path = Path.Combine(logDir, "vTools.log");
      File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
    }
    catch
    {
    }
  }

  /// <summary>
  /// Resolves a writable logs directory near the project root with a safe fallback.
  /// </summary>
  private static string ResolveProjectLogsDir()
  {
    try
    {
      var asmDir = Path.GetDirectoryName(typeof(vToolsPlugIn).Assembly.Location) ?? ".";
      var dir = new DirectoryInfo(asmDir);

      while (dir != null)
      {
        if (File.Exists(Path.Combine(dir.FullName, "vTools.csproj")))
        {
          var projectLogs = Path.Combine(dir.FullName, "logs");
          Directory.CreateDirectory(projectLogs);
          return projectLogs;
        }

        dir = dir.Parent;
      }

      var fallbackLogs = Path.Combine(asmDir, "logs");
      Directory.CreateDirectory(fallbackLogs);
      return fallbackLogs;
    }
    catch
    {
      return string.Empty;
    }
  }
}
