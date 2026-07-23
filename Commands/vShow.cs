using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Shows all hidden document objects without cancelling the active command.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vShow : Command
{
  private sealed class ActiveHideSet
  {
    public ActiveHideSet(string name, long order)
    {
      Name = name;
      Order = order;
    }

    public string Name { get; }
    public long Order { get; set; }
    public List<Guid> ObjectIds { get; } = new();
  }

  private const string Tag = "vShow";
  private const string SetPrompt =
    "Name of object set to show. Press Enter to show all named sets.";

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var selectionSnapshot = HideSetState.CaptureNestedSelection(doc);
    try
    {
      return RunCommandCore(doc);
    }
    finally
    {
      HideSetState.RestoreNestedSelection(doc, selectionSnapshot);
    }
  }

  private static Result RunCommandCore(RhinoDoc doc)
  {
    Log.Write(Tag, "--- run start ---");

    if (!HideSetState.NativeAccessAvailable)
    {
      RhinoApp.WriteLine("vShow: named hide-set access is unavailable.");
      Log.Write(Tag, "  Rhino hide-set attachment API unavailable");
      return Result.Failure;
    }

    var activeSets = GetActiveHideSets(
      doc,
      out var objectHiddenCount,
      out var nativeNamedCount);
    var recentSets = activeSets
      .OrderByDescending(set => set.Order)
      .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
      .Take(20)
      .ToList();
    Log.Write(Tag,
      $"  object-hidden candidates={objectHiddenCount}" +
      $" native-named={nativeNamedCount}" +
      $" active sets={activeSets.Count}" +
      $" recent=[{string.Join(", ", recentSets.Select(set => set.Name))}]");

    var hideSetName = GetHideSetName(recentSets, out var getResult);
    if (getResult != Result.Success || hideSetName == null)
    {
      Log.Write(Tag, $"  set prompt ended result={getResult}");
      return getResult;
    }

    var showAllNamedSets = string.IsNullOrEmpty(hideSetName);
    var hiddenIds = activeSets
      .Where(set => showAllNamedSets ||
        string.Equals(set.Name, hideSetName, StringComparison.OrdinalIgnoreCase))
      .SelectMany(set => set.ObjectIds)
      .ToList();
    Log.Write(Tag, $"  named matches={hiddenIds.Count}");
    if (hiddenIds.Count == 0)
    {
      var setDescription = showAllNamedSets
        ? "any named set"
        : $"set {hideSetName}";
      RhinoApp.WriteLine($"vShow: no hidden objects in {setDescription}.");
      Log.Write(Tag, $"  no hidden objects hideSet={setDescription}");
      return Result.Nothing;
    }

    var shownCount = 0;
    var clearedCount = 0;
    foreach (var objectId in hiddenIds)
    {
      if (!doc.Objects.Show(objectId, false))
        continue;

      shownCount++;
      HideSetState.SetTrackedName(doc, objectId, string.Empty);
      var shownObject = doc.Objects.FindId(objectId);
      if (shownObject != null && HideSetState.RemoveNativeName(shownObject))
        clearedCount++;
    }

    doc.Views.Redraw();
    Log.Write(Tag,
      $"  shown={shownCount}/{hiddenIds.Count}" +
      $" cleared={clearedCount}" +
      $" hideSet={(showAllNamedSets ? "<all named>" : hideSetName)}");
    RhinoApp.WriteLine($"vShow: shown {shownCount} object(s).");

    return shownCount > 0 ? Result.Success : Result.Failure;
  }

  private static List<ActiveHideSet> GetActiveHideSets(
    RhinoDoc doc,
    out int objectHiddenCount,
    out int nativeNamedCount)
  {
    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = true,
      IdefObjects = false,
      DeletedObjects = false,
      ActiveObjects = true,
      ReferenceObjects = false,
      IncludeLights = true,
      IncludeGrips = false,
      IncludePhantoms = false,
      VisibleFilter = false
    };

    var objectHidden = doc.Objects.GetObjectList(settings)
      .Where(HideSetState.IsHidden)
      .ToList();
    objectHiddenCount = objectHidden.Count;
    nativeNamedCount = 0;
    var sets = new Dictionary<string, ActiveHideSet>(
      StringComparer.OrdinalIgnoreCase);
    foreach (var obj in objectHidden)
    {
      if (!HideSetState.TryGetNativeName(obj, out var nativeName))
        continue;

      nativeNamedCount++;
      var trackedName = HideSetState.GetTrackedName(obj);
      if (!string.Equals(nativeName, trackedName, StringComparison.OrdinalIgnoreCase))
        continue;

      var order = HideSetState.GetTrackedOrder(obj);
      if (!sets.TryGetValue(nativeName, out var set))
      {
        set = new ActiveHideSet(nativeName, order);
        sets.Add(nativeName, set);
      }

      set.Order = Math.Max(set.Order, order);
      set.ObjectIds.Add(obj.Id);
    }

    return sets.Values.ToList();
  }

  private static string? GetHideSetName(
    IReadOnlyList<ActiveHideSet> recentSets,
    out Result commandResult)
  {
    using var getter = new GetString();
    getter.SetCommandPrompt(SetPrompt);
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);

    var optionSets = new Dictionary<int, string>();
    var optionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < recentSets.Count; i++)
    {
      var setName = recentSets[i].Name;
      var optionName = OptionName(setName, i + 1, optionNames);
      var optionIndex = string.Equals(optionName, setName, StringComparison.Ordinal)
        ? getter.AddOption(optionName)
        : getter.AddOption(optionName, setName);
      if (optionIndex > 0)
        optionSets[optionIndex] = setName;
    }

    var result = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return null;

    if (result == GetResult.Nothing)
      return string.Empty;

    if (result == GetResult.Option &&
        optionSets.TryGetValue(getter.Option().Index, out var selectedSet))
    {
      return selectedSet;
    }

    return result == GetResult.String
      ? HideSetState.NormalizeInput(getter.StringResult())
      : null;
  }

  private static string OptionName(
    string setName,
    int position,
    HashSet<string> usedNames)
  {
    var characters = setName.Where(IsAsciiLetterOrDigit).ToArray();
    var candidate = new string(characters);
    if (string.IsNullOrEmpty(candidate) || !IsAsciiLetter(candidate[0]))
      candidate = $"Set{position}";

    var uniqueName = candidate;
    var suffix = 2;
    while (!usedNames.Add(uniqueName))
      uniqueName = $"{candidate}{suffix++}";
    return uniqueName;
  }

  private static bool IsAsciiLetterOrDigit(char value) =>
    IsAsciiLetter(value) || value is >= '0' and <= '9';

  private static bool IsAsciiLetter(char value) =>
    value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

}
