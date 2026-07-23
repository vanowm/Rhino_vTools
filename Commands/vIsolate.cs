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
/// Keeps selected objects visible and hides every other visible normal object,
/// optionally assigning the hidden objects to a named Rhino hide group.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vIsolate : Command
{
  private const string Tag = "vIsolate";
  private const string HideSetPrompt =
    "Name of object set to isolate. Press Enter to isolate with no named set.";

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    Log.Write(Tag, "--- run start ---");

    var isolateIds = GetObjectsToIsolate(
      doc,
      out var requestedHideSetName,
      out var selectionResult);
    if (isolateIds.Count == 0)
    {
      RhinoApp.WriteLine("vIsolate: no objects selected.");
      Log.Write(Tag, $"  no isolate objects result={selectionResult}");
      return selectionResult == Result.Success ? Result.Nothing : selectionResult;
    }

    var isolateSet = isolateIds.ToHashSet();
    var objectsToHide = VisibleNormalObjects(doc)
      .Where(obj => !isolateSet.Contains(obj.Id))
      .ToList();

    Log.Write(Tag,
      $"  isolate={isolateIds.Count} hide candidates={objectsToHide.Count}");

    if (objectsToHide.Count == 0)
    {
      RestoreSelection(doc, isolateIds);
      doc.Views.Redraw();
      RhinoApp.WriteLine("vIsolate: nothing to hide.");
      return Result.Success;
    }

    string? hideSetName;
    if (requestedHideSetName != null)
    {
      hideSetName = requestedHideSetName;
    }
    else
    {
      hideSetName = GetHideSetName(out var nameResult);
      if (nameResult != Result.Success || hideSetName == null)
      {
        RestoreSelection(doc, isolateIds);
        doc.Views.Redraw();
        RhinoApp.WriteLine("vIsolate canceled.");
        Log.Write(Tag, $"  hide-set prompt canceled result={nameResult}");
        return nameResult;
      }
    }

    doc.Objects.UnselectAll();
    var hideSetOrder = string.IsNullOrEmpty(hideSetName)
      ? 0
      : DateTime.UtcNow.Ticks;
    var hiddenCount = 0;
    var trackedCount = 0;
    var clearedCount = 0;
    foreach (var obj in objectsToHide)
    {
      var hasTrackedName = !string.IsNullOrEmpty(HideSetState.GetTrackedName(obj));
      var trackingReady = HideSetState.SetTrackedName(
        doc,
        obj.Id,
        hideSetName ?? string.Empty,
        hideSetOrder);
      if (string.IsNullOrEmpty(hideSetName))
      {
        var currentObject = doc.Objects.FindId(obj.Id);
        var nativeCleared = currentObject != null &&
          HideSetState.RemoveNativeName(currentObject);
        if ((hasTrackedName && trackingReady) || nativeCleared)
          clearedCount++;
      }

      using var objRef = new ObjRef(doc, obj.Id);
      var hidden = string.IsNullOrEmpty(hideSetName)
        ? doc.Objects.Hide(objRef, false)
        : doc.Objects.Hide(objRef, false, hideSetName);
      if (!hidden)
      {
        if (!string.IsNullOrEmpty(hideSetName))
          HideSetState.SetTrackedName(doc, obj.Id, string.Empty);
        continue;
      }

      hiddenCount++;
      if (!string.IsNullOrEmpty(hideSetName) && trackingReady)
        trackedCount++;
    }

    RestoreSelection(doc, isolateIds);
    doc.Views.Redraw();

    Log.Write(Tag,
      $"  hidden={hiddenCount}/{objectsToHide.Count}" +
      $" hideSet={(string.IsNullOrEmpty(hideSetName) ? "<none>" : hideSetName)}" +
      $" tracked={trackedCount} cleared={clearedCount}");

    if (hiddenCount != objectsToHide.Count)
    {
      RhinoApp.WriteLine(
        $"vIsolate: hid {hiddenCount} of {objectsToHide.Count} object(s).");
      return hiddenCount > 0 ? Result.Success : Result.Failure;
    }

    RhinoApp.WriteLine($"vIsolate: hidden {hiddenCount} object(s).");
    return Result.Success;
  }

  private static List<Guid> GetObjectsToIsolate(
    RhinoDoc doc,
    out string? requestedHideSetName,
    out Result commandResult)
  {
    requestedHideSetName = null;
    var preselected = ValidObjectIds(
      doc,
      doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id));
    if (preselected.Count > 0)
    {
      commandResult = Result.Success;
      return preselected;
    }

    using var getter = new GetObject();
    getter.SetCommandPrompt("Select objects to isolate");
    getter.SubObjectSelect = false;
    getter.GroupSelect = true;
    getter.AcceptNothing(false);
    getter.EnablePreSelect(true, true);
    getter.DeselectAllBeforePostSelect = false;
    getter.EnableTransparentCommands(true);

    var setAOption = getter.AddOption("A", "A", true);
    var setBOption = getter.AddOption("B", "B", true);
    var setCOption = getter.AddOption("C", "C", true);
    var setDOption = getter.AddOption("D", "D", true);
    var setEOption = getter.AddOption("E", "E", true);

    while (true)
    {
      var getResult = getter.GetMultiple(1, 0);
      commandResult = getter.CommandResult();
      if (commandResult != Result.Success)
        return new List<Guid>();

      if (getResult == GetResult.Option)
      {
        var optionIndex = getter.Option().Index;
        requestedHideSetName = optionIndex == setAOption ? "A" :
          optionIndex == setBOption ? "B" :
          optionIndex == setCOption ? "C" :
          optionIndex == setDOption ? "D" :
          optionIndex == setEOption ? "E" : requestedHideSetName;
        continue;
      }

      if (getResult != GetResult.Object)
        return new List<Guid>();

      break;
    }

    return ValidObjectIds(
      doc,
      getter.Objects().Select(objRef => objRef.ObjectId));
  }

  private static string? GetHideSetName(out Result commandResult)
  {
    using var getter = new GetString();
    getter.SetCommandPrompt(HideSetPrompt);
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);

    var getResult = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return null;

    if (getResult == GetResult.Nothing)
      return string.Empty;

    if (getResult != GetResult.String)
      return null;

    var value = (getter.StringResult() ?? string.Empty).Trim();
    return value is "_A" or "_B" or "_C" or "_D" or "_E"
      ? value[1..]
      : value;
  }

  private static IEnumerable<RhinoObject> VisibleNormalObjects(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      IdefObjects = false,
      DeletedObjects = false,
      ActiveObjects = true,
      ReferenceObjects = false,
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      VisibleFilter = true
    };

    return doc.Objects.GetObjectList(settings);
  }

  private static List<Guid> ValidObjectIds(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds)
  {
    var result = new List<Guid>();
    var seen = new HashSet<Guid>();
    foreach (var objectId in objectIds)
    {
      if (objectId == Guid.Empty ||
          !seen.Add(objectId) ||
          doc.Objects.FindId(objectId) == null)
      {
        continue;
      }

      result.Add(objectId);
    }

    return result;
  }

  private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    doc.Objects.UnselectAll();
    foreach (var objectId in ValidObjectIds(doc, objectIds))
      doc.Objects.Select(objectId);
  }
}
