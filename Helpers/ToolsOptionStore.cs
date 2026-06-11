using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace vTools.Commands;

/// <summary>
/// Shared persisted option storage backed by vTools.config.json.
/// </summary>
internal static class ToolsOptionStore
{
  private const string ToolsConfigFileName = "vTools.config.json";
  private static readonly object Sync = new();

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true
  };

  /// <summary>
  /// Reads one command section and converts it to the requested value.
  /// </summary>
  internal static T Read<T>(string sectionName, Func<JsonObject?, T> reader)
  {
    lock (Sync)
    {
      var root = LoadRoot();
      var section = root[sectionName] as JsonObject;
      return reader(section);
    }
  }

internal static string LastError { get; private set; } = "";

  /// <summary>
  /// Updates one command section and saves the shared config.
  /// </summary>
internal static bool Update(string sectionName, Action<JsonObject> updater)
{
  lock (Sync)
  {
    try
    {
      LastError = "";

      var root = LoadRoot();

      var section = root[sectionName] as JsonObject;
      if (section == null)
      {
        section = new JsonObject();
        root[sectionName] = section;
      }

      updater(section);
      return SaveRoot(root);
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      return false;
    }
  }
}
  /// <summary>
  /// Attempts to read a string key from a command section.
  /// </summary>
  internal static bool TryGetString(JsonObject? section, string key, out string value)
  {
    value = string.Empty;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
      {
        value = s.Trim();
        return true;
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Attempts to read a bool key from a command section.
  /// </summary>
  internal static bool TryGetBool(JsonObject? section, string key, out bool value)
  {
    value = false;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<bool>(out var b))
      {
        value = b;
        return true;
      }

      if (jsonValue.TryGetValue<int>(out var i))
      {
        value = i != 0;
        return true;
      }

      if (jsonValue.TryGetValue<string>(out var s) && bool.TryParse(s, out b))
      {
        value = b;
        return true;
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Attempts to read a double key from a command section.
  /// </summary>
  internal static bool TryGetDouble(JsonObject? section, string key, out double value)
  {
    value = 0.0;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<double>(out var d))
      {
        value = d;
        return true;
      }

      if (jsonValue.TryGetValue<int>(out var i))
      {
        value = i;
        return true;
      }

      if (jsonValue.TryGetValue<string>(out var s))
      {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ||
            double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
        {
          value = d;
          return true;
        }
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Loads root config object from vTools.config.json.
  /// </summary>
  private static JsonObject LoadRoot()
  {
    try
    {
      var path = GetToolsConfigPath();
      if (!File.Exists(path))
        return new JsonObject();

      var json = File.ReadAllText(path);
      if (string.IsNullOrWhiteSpace(json))
        return new JsonObject();

      var node = JsonNode.Parse(
        json,
        nodeOptions: null,
        documentOptions: new JsonDocumentOptions
        {
          AllowTrailingCommas = true,
          CommentHandling = JsonCommentHandling.Skip
        });

      return node as JsonObject ?? new JsonObject();
    }
    catch
    {
      return new JsonObject();
    }
  }

  /// <summary>
  /// Saves root config object to vTools.config.json atomically.
  /// </summary>
  private static bool SaveRoot(JsonObject root)
  {
    try
    {
      var path = GetToolsConfigPath();
      var parent = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(parent))
        Directory.CreateDirectory(parent);

      var tmp = path + ".tmp";
      File.WriteAllText(tmp, root.ToJsonString(JsonOptions));
      File.Copy(tmp, path, true);
      File.Delete(tmp);
      return true;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      return false;
    }
}

  /// <summary>
  /// Resolves config path in plug-in deployment directory.
  /// </summary>
  private static string GetToolsConfigPath()
  {
    var pluginDir = Path.GetDirectoryName(typeof(ToolsOptionStore).Assembly.Location) ?? ".";
    Directory.CreateDirectory(pluginDir);
    return Path.Combine(pluginDir, ToolsConfigFileName);
  }
}
