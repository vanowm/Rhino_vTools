using Rhino;
using Rhino.PlugIns;
using System;
using System.IO;

namespace vTools;

[System.Runtime.InteropServices.Guid("2607512e-a1fc-4cf9-9329-a293431437a0")]
/// <summary>
/// Rhino plug-in entry point for the vTools command set.
/// </summary>
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
    var version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
    TryLog($"OnLoad OK. Version={version}. Assembly={GetType().Assembly.Location}");
    RhinoApp.WriteLine($"vTools v{version} loaded. Commands registered: vCurveToSpline, vFitBox, vOrient2pt, vOrient3pt, vUzip");
    return LoadReturnCode.Success;
  }

  /// <summary>
  /// Appends one log message to the plug-in log file.
  /// </summary>
  private static void TryLog(string message)
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
