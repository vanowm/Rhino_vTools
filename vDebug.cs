using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace vTools;

/// <summary>
/// Lightweight, opt-in per-command debug logger.
///
/// Usage:
///   vDebug.Enable("vFitBox");          // enable one tag (command name)
///   vDebug.EnableAll = true;           // enable every tag
///   vDebug.Log("vFitBox", "msg {0}", value);
///   vDebug.Disable("vFitBox");
///
/// One log file is created per Rhino session under the project logs directory,
/// named  debug_YYYYMMDD_HHmmss.log  (set at first write).
/// All writes append so multiple commands share the same session file.
/// </summary>
internal static class vDebug
{
  // -- Configuration --------------------------------------------------------

  /// <summary>When true every tag is logged, regardless of individual flags.</summary>
  public static bool EnableAll { get; set; } = false;

  private static readonly HashSet<string> _enabled =
    new(StringComparer.OrdinalIgnoreCase);

  private static string? _logPath;      // set once on first write
  private static readonly object _lock = new();

  // -- Tag control ----------------------------------------------------------

  public static void Enable(string tag)  => _enabled.Add(tag);
  public static void Disable(string tag) => _enabled.Remove(tag);
  public static bool IsEnabled(string tag) =>
    EnableAll || _enabled.Contains(tag);

  // -- Logging --------------------------------------------------------------

  /// <summary>Write one line when the given tag is enabled.</summary>
  public static void Log(string tag, string message,
    [CallerMemberName] string caller = "")
  {
    if (!IsEnabled(tag)) return;
    WriteRaw($"[{tag}] [{caller}] {message}");
  }

  /// <summary>Write a formatted line when the given tag is enabled.</summary>
  public static void Log(string tag, string format, params object[] args)
  {
    if (!IsEnabled(tag)) return;
    WriteRaw($"[{tag}] {string.Format(format, args)}");
  }

  /// <summary>Write several lines when the given tag is enabled.</summary>
  public static void Log(string tag, IEnumerable<string> lines)
  {
    if (!IsEnabled(tag)) return;
    var prefix = $"[{tag}]";
    foreach (var line in lines)
      WriteRaw($"{prefix} {line}");
  }

  /// <summary>Write a section separator (always, regardless of tag).</summary>
  public static void Separator(string tag, string label = "")
  {
    if (!IsEnabled(tag)) return;
    WriteRaw($"[{tag}] --- {label} {DateTime.Now:HH:mm:ss.fff} ---");
  }

  // -- Internal -------------------------------------------------------------

  private static void WriteRaw(string text)
  {
    try
    {
      lock (_lock)
      {
        _logPath ??= ResolveLogPath();
        if (string.IsNullOrEmpty(_logPath)) return;
        File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
      }
    }
    catch { /* never crash the caller */ }
  }

  private static string ResolveLogPath()
  {
    try
    {
      var logsDir = ResolveLogsDir();
      if (string.IsNullOrEmpty(logsDir)) return string.Empty;
      Directory.CreateDirectory(logsDir);
      return Path.Combine(logsDir, $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }
    catch { return string.Empty; }
  }

  private static string ResolveLogsDir()
  {
    try
    {
      var asmDir = Path.GetDirectoryName(
        typeof(vDebug).Assembly.Location) ?? ".";
      var dir = new DirectoryInfo(asmDir);
      while (dir != null)
      {
        if (File.Exists(Path.Combine(dir.FullName, "vTools.csproj")))
          return Path.Combine(dir.FullName, "logs");
        dir = dir.Parent;
      }
      return Path.Combine(asmDir, "logs");
    }
    catch { return string.Empty; }
  }
}
