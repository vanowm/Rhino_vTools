using System;
using System.IO;

namespace vTools;

/// <summary>
/// Append-only diagnostic log shared by all vTools commands and helpers.
///
/// Usage:
///   Log.Write("message");                      // plain, caller name auto-captured
///   Log.Write("vGroup", "message");            // tagged with command name
///   Log.Write("vGroup", "x={0} y={1}", x, y); // tagged + formatted
///
/// A single file named vtools.log beside the loaded plug-in DLL is used per
/// session and cleared on startup.
/// Call  Log.Initialize()  once from  PlugIn.OnLoad.
/// </summary>
internal static class Log
{
  private static string? _path;
  private static readonly object _lock = new();

  /// <summary>Full path of the log file resolved at startup, or null on failure.</summary>
  public static string? FilePath => _path;

  // ── Startup ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Clears the log file and resolves its path.  Call once from
  /// <c>PlugIn.OnLoad</c> so each Rhino session starts with a fresh file.
  /// </summary>
  public static void Initialize()
  {
    try
    {
      lock (_lock)
      {
        _path = ResolvePath();
        if (!string.IsNullOrEmpty(_path))
          File.WriteAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] vTools log initialized\n");
      }
    }
    catch { }
  }

  // ── Write overloads ───────────────────────────────────────────────────────

  /// <summary>Appends a plain message.</summary>
  public static void Write(string message)
    => Append(message);

  /// <summary>Appends a tagged message (tag is typically the command name).</summary>
  public static void Write(string tag, string message)
    => Append($"[{tag}] {message}");

  /// <summary>Appends a tagged formatted message.</summary>
  public static void Write(string tag, string format, params object[] args)
    => Append($"[{tag}] {string.Format(format, args)}");

  // ── Internal ──────────────────────────────────────────────────────────────

  private static void Append(string text)
  {
    try
    {
      lock (_lock)
      {
        _path ??= ResolvePath();
        if (string.IsNullOrEmpty(_path)) return;
        File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
      }
    }
    catch { }
  }

  private static string ResolvePath()
  {
    try
    {
      return PluginPaths.ResolveFile("vTools.log");
    }
    catch { return string.Empty; }
  }
}
