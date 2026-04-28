using System;
using System.Collections.Generic;
using System.IO;
using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Runs existing Rhino Python scripts from vTools command wrappers.
/// </summary>
internal static class vPythonScriptCommandRunner
{
  /// <summary>
  /// Resolves and runs a Python script file.
  /// </summary>
  internal static Result Run(string scriptFileName, string commandName)
  {
    var scriptPath = ResolveScriptPath(scriptFileName);
    if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
    {
      RhinoApp.WriteLine($"{commandName}: script not found ({scriptFileName}).");
      return Result.Failure;
    }

    var escapedPath = scriptPath.Replace("\"", "\"\"");
    var command = $"-_RunPythonScript \"{escapedPath}\" _Enter";
    if (!RhinoApp.RunScript(command, false))
    {
      RhinoApp.WriteLine($"{commandName}: failed to run script ({scriptPath}).");
      return Result.Failure;
    }

    return Result.Success;
  }

  private static string? ResolveScriptPath(string scriptFileName)
  {
    foreach (var root in EnumerateScriptRoots())
    {
      try
      {
        var candidate = Path.Combine(root, scriptFileName);
        if (File.Exists(candidate))
          return candidate;
      }
      catch
      {
        // Keep probing remaining roots.
      }
    }

    return null;
  }

  private static IEnumerable<string> EnumerateScriptRoots()
  {
    var roots = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void AddRoot(string? path)
    {
      if (string.IsNullOrWhiteSpace(path))
        return;

      try
      {
        var full = Path.GetFullPath(path);
        if (seen.Add(full))
          roots.Add(full);
      }
      catch
      {
        // Ignore invalid root.
      }
    }

    var assemblyDir = Path.GetDirectoryName(typeof(vToolsPlugIn).Assembly.Location);
    AddRoot(assemblyDir);

    try
    {
      var probe = string.IsNullOrWhiteSpace(assemblyDir) ? null : new DirectoryInfo(assemblyDir);
      while (probe != null)
      {
        var projectFile = Path.Combine(probe.FullName, "vTools.csproj");
        if (File.Exists(projectFile))
        {
          AddRoot(Path.Combine(probe.Parent?.FullName ?? probe.FullName, "scripts"));
          break;
        }

        probe = probe.Parent;
      }
    }
    catch
    {
      // Keep fallback roots.
    }

    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    AddRoot(Path.Combine(appData, "McNeel", "Rhinoceros", "8.0", "scripts"));
    AddRoot(Path.Combine(Environment.CurrentDirectory, "scripts"));
    AddRoot(Environment.CurrentDirectory);

    return roots;
  }
}
