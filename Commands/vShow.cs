using System;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Runtime.InteropWrappers;

namespace vTools.Commands;

/// <summary>
/// Shows all hidden document objects without cancelling the active command.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vShow : Command
{
  private const string Tag = "vShow";
  private const string SetPrompt =
    "Name of object set to show. Press Enter to show all named sets.";

  private static readonly MethodInfo? ObjectPointerMethod =
    typeof(RhinoObject).GetMethod(
      "ConstPointer",
      BindingFlags.Instance | BindingFlags.NonPublic);

  private static readonly MethodInfo? HideSetNameMethod =
    typeof(RhinoApp).Assembly
      .GetType("UnsafeNativeMethods", false)
      ?.GetMethod(
        "CRhinoObject_AttachHideGetName",
        BindingFlags.Static | BindingFlags.NonPublic);

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    Log.Write(Tag, "--- run start ---");

    var hideSetName = GetHideSetName(out var getResult);
    if (getResult != Result.Success || hideSetName == null)
    {
      Log.Write(Tag, $"  set prompt ended result={getResult}");
      return getResult;
    }

    if (ObjectPointerMethod == null || HideSetNameMethod == null)
    {
      RhinoApp.WriteLine("vShow: named hide-set access is unavailable.");
      Log.Write(Tag, "  Rhino hide-set attachment API unavailable");
      return Result.Failure;
    }

    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = false,
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

    var showAllNamedSets = string.IsNullOrEmpty(hideSetName);
    var objectHidden = doc.Objects.GetObjectList(settings)
      .Where(obj => obj.Attributes.Mode == ObjectMode.Hidden)
      .ToList();
    var hiddenIds = objectHidden
      .Where(obj => TryGetHideSetName(obj, out var objectSetName) &&
        (showAllNamedSets
          ? !string.IsNullOrEmpty(objectSetName)
          : string.Equals(objectSetName, hideSetName, StringComparison.OrdinalIgnoreCase)))
      .Select(obj => obj.Id)
      .ToList();
    Log.Write(Tag,
      $"  object-hidden candidates={objectHidden.Count}" +
      $" named matches={hiddenIds.Count}");
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
    foreach (var objectId in hiddenIds)
    {
      if (doc.Objects.Show(objectId, false))
        shownCount++;
    }

    doc.Views.Redraw();
    Log.Write(Tag,
      $"  shown={shownCount}/{hiddenIds.Count}" +
      $" hideSet={(showAllNamedSets ? "<all named>" : hideSetName)}");
    RhinoApp.WriteLine($"vShow: shown {shownCount} object(s).");

    return shownCount > 0 ? Result.Success : Result.Failure;
  }

  private static string? GetHideSetName(out Result commandResult)
  {
    using var getter = new GetString();
    getter.SetCommandPrompt(SetPrompt);
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);

    var result = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return null;

    if (result == GetResult.Nothing)
      return string.Empty;

    return result == GetResult.String
      ? (getter.StringResult() ?? string.Empty).Trim()
      : null;
  }

  private static bool TryGetHideSetName(
    RhinoObject obj,
    out string hideSetName)
  {
    hideSetName = string.Empty;
    if (ObjectPointerMethod == null || HideSetNameMethod == null)
      return false;

    try
    {
      var objectPointer = (IntPtr)(ObjectPointerMethod.Invoke(obj, null) ?? IntPtr.Zero);
      if (objectPointer == IntPtr.Zero)
        return false;

      using var value = new StringHolder();
      var found = (bool)(HideSetNameMethod.Invoke(
        null,
        new object[] { objectPointer, value.NonConstPointer() }) ?? false);
      if (!found)
        return false;

      hideSetName = value.ToStringSafe() ?? string.Empty;
      return !string.IsNullOrEmpty(hideSetName);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  hide-set lookup failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }
}
