using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InteropWrappers;

namespace vTools.Commands;

internal static class HideSetState
{
  internal sealed class SelectionSnapshot
  {
    internal List<SelectionEntry> Entries { get; } = new();
  }

  internal sealed class SelectionEntry
  {
    internal SelectionEntry(
      RhinoObject obj,
      bool wholeObject,
      bool persistent,
      ComponentIndex[] subObjects)
    {
      Object = obj;
      WholeObject = wholeObject;
      Persistent = persistent;
      SubObjects = subObjects;
    }

    internal RhinoObject Object { get; }
    internal bool WholeObject { get; }
    internal bool Persistent { get; }
    internal ComponentIndex[] SubObjects { get; }
  }

  private sealed class HidePollContext
  {
    public HidePollContext(uint documentSerialNumber)
    {
      DocumentSerialNumber = documentSerialNumber;
    }

    public uint DocumentSerialNumber { get; }
    public Dictionary<Guid, string> PreviousNativeNames { get; } = new();
  }

  private const string Tag = "HideSetState";
  private const string TrackingKey = "vTools.HideSet";
  private const string TrackingOrderKey = "vTools.HideSetOrder";

  private static readonly MethodInfo? ObjectPointerMethod =
    typeof(RhinoObject).GetMethod(
      "ConstPointer",
      BindingFlags.Instance | BindingFlags.NonPublic);

  private static readonly Type? UnsafeNativeMethodsType =
    typeof(RhinoApp).Assembly.GetType("UnsafeNativeMethods", false);

  private static readonly MethodInfo? GetNameMethod =
    UnsafeNativeMethodsType?.GetMethod(
      "CRhinoObject_AttachHideGetName",
      BindingFlags.Static | BindingFlags.NonPublic);

  private static readonly MethodInfo? RemoveNameMethod =
    UnsafeNativeMethodsType?.GetMethod(
      "CRhinoObject_RemoveHideName",
      BindingFlags.Static | BindingFlags.NonPublic);

  private static bool _polling;
  private static HidePollContext? _hidePollContext;

  public static bool NativeAccessAvailable =>
    ObjectPointerMethod != null &&
    GetNameMethod != null &&
    RemoveNameMethod != null;

  public static void StartPolling()
  {
    if (_polling)
      return;

    Command.BeginCommand += OnBeginCommand;
    Command.EndCommand += OnEndCommand;
    RhinoDoc.ModifyObjectAttributes += OnModifyObjectAttributes;
    _polling = true;
  }

  public static void StopPolling()
  {
    if (!_polling)
      return;

    Command.BeginCommand -= OnBeginCommand;
    Command.EndCommand -= OnEndCommand;
    RhinoDoc.ModifyObjectAttributes -= OnModifyObjectAttributes;
    _hidePollContext = null;
    _polling = false;
  }

  public static string GetTrackedName(RhinoObject obj) =>
    (obj.Attributes.GetUserString(TrackingKey) ?? string.Empty).Trim();

  public static long GetTrackedOrder(RhinoObject obj) =>
    long.TryParse(
      obj.Attributes.GetUserString(TrackingOrderKey),
      NumberStyles.Integer,
      CultureInfo.InvariantCulture,
      out var order)
      ? order
      : obj.RuntimeSerialNumber;

  public static bool IsHidden(RhinoObject obj) =>
    obj.IsHidden || obj.Attributes.Mode == ObjectMode.Hidden;

  public static string NormalizeInput(string? input)
  {
    var value = (input ?? string.Empty).Trim();
    if (value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') ||
         (value[0] == '\'' && value[^1] == '\'')))
    {
      value = value[1..^1].Trim();
    }

    return value;
  }

  public static SelectionSnapshot? CaptureNestedSelection(RhinoDoc doc)
  {
    if (RhinoApp.InCommand <= 1)
      return null;

    var snapshot = new SelectionSnapshot();
    foreach (var obj in doc.Objects.GetSelectedObjects(true, true))
    {
      var selectionState = obj.IsSelected(true);
      if (selectionState == 0)
        continue;

      snapshot.Entries.Add(new SelectionEntry(
        obj,
        selectionState is 1 or 2,
        selectionState == 2,
        obj.GetSelectedSubObjects() ?? Array.Empty<ComponentIndex>()));
    }

    Log.Write(Tag, $"  captured nested selection count={snapshot.Entries.Count}");
    return snapshot;
  }

  public static void RestoreNestedSelection(
    RhinoDoc doc,
    SelectionSnapshot? snapshot)
  {
    if (snapshot == null)
      return;

    doc.Objects.UnselectAll();
    foreach (var entry in snapshot.Entries)
    {
      var obj = entry.Object;
      if (entry.WholeObject)
      {
        obj.Select(
          true,
          true,
          entry.Persistent,
          true,
          true,
          true);
      }

      foreach (var componentIndex in entry.SubObjects)
        obj.SelectSubObject(componentIndex, true, true, false);
    }

    doc.Views.Redraw();
    Log.Write(Tag, $"  restored nested selection count={snapshot.Entries.Count}");
  }

  public static bool SetTrackedName(
    RhinoDoc doc,
    Guid objectId,
    string hideSetName,
    long order = 0)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj == null)
      return false;

    var attributes = obj.Attributes.Duplicate();
    bool changed;
    if (string.IsNullOrEmpty(hideSetName))
    {
      changed = attributes.DeleteUserString(TrackingKey);
      changed = attributes.DeleteUserString(TrackingOrderKey) || changed;
    }
    else
    {
      var orderText = order.ToString(CultureInfo.InvariantCulture);
      changed = !string.Equals(
          attributes.GetUserString(TrackingKey),
          hideSetName,
          StringComparison.Ordinal) ||
        !string.Equals(
          attributes.GetUserString(TrackingOrderKey),
          orderText,
          StringComparison.Ordinal);
      attributes.SetUserString(TrackingKey, hideSetName);
      attributes.SetUserString(TrackingOrderKey, orderText);
    }

    return !changed || doc.Objects.ModifyAttributes(objectId, attributes, true);
  }

  public static bool TryGetNativeName(
    RhinoObject obj,
    out string hideSetName)
  {
    hideSetName = string.Empty;
    if (!TryGetPointer(obj, out var objectPointer) || GetNameMethod == null)
      return false;

    try
    {
      using var value = new StringHolder();
      var found = (bool)(GetNameMethod.Invoke(
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

  public static bool RemoveNativeName(RhinoObject obj)
  {
    if (!TryGetPointer(obj, out var objectPointer) || RemoveNameMethod == null)
      return false;

    try
    {
      return (bool)(RemoveNameMethod.Invoke(
        null,
        new object[] { objectPointer }) ?? false);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  hide-set removal failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }

  private static bool TryGetPointer(
    RhinoObject obj,
    out IntPtr objectPointer)
  {
    objectPointer = IntPtr.Zero;
    if (ObjectPointerMethod == null)
      return false;

    try
    {
      objectPointer =
        (IntPtr)(ObjectPointerMethod.Invoke(obj, null) ?? IntPtr.Zero);
      return objectPointer != IntPtr.Zero;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  pointer lookup failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }

  private static void OnBeginCommand(object? sender, CommandEventArgs e)
  {
    if (!string.Equals(e.CommandEnglishName, "Hide", StringComparison.OrdinalIgnoreCase))
      return;

    var doc = e.Document;
    var context = new HidePollContext(doc.RuntimeSerialNumber);
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
      CapturePreviousNativeName(context, obj);
    _hidePollContext = context;
    Log.Write(Tag,
      $"  polling default Hide begin preselected={context.PreviousNativeNames.Count}");
  }

  private static void OnModifyObjectAttributes(
    object? sender,
    RhinoModifyObjectAttributesEventArgs e)
  {
    var context = _hidePollContext;
    if (context == null ||
        context.DocumentSerialNumber != e.Document.RuntimeSerialNumber)
    {
      return;
    }

    CapturePreviousNativeName(context, e.RhinoObject);
  }

  private static void OnEndCommand(object? sender, CommandEventArgs e)
  {
    if (!string.Equals(e.CommandEnglishName, "Hide", StringComparison.OrdinalIgnoreCase))
      return;

    var context = _hidePollContext;
    _hidePollContext = null;
    var doc = e.Document;
    if (context == null ||
        context.DocumentSerialNumber != doc.RuntimeSerialNumber)
    {
      return;
    }

    var order = DateTime.UtcNow.Ticks;
    var namedCount = 0;
    var unnamedCount = 0;
    foreach (var entry in context.PreviousNativeNames)
    {
      var obj = doc.Objects.FindId(entry.Key);
      if (obj == null || !IsHidden(obj))
        continue;

      var hasNativeName = TryGetNativeName(obj, out var nativeName);
      var newlyNamed = hasNativeName &&
        !string.Equals(
          nativeName,
          entry.Value,
          StringComparison.OrdinalIgnoreCase);
      if (newlyNamed)
      {
        if (SetTrackedName(doc, obj.Id, nativeName, order))
          namedCount++;
        continue;
      }

      SetTrackedName(doc, obj.Id, string.Empty);
      if (hasNativeName)
      {
        var currentObject = doc.Objects.FindId(obj.Id);
        if (currentObject != null)
          RemoveNativeName(currentObject);
      }
      unnamedCount++;
    }

    Log.Write(Tag,
      $"  polling default Hide end result={e.CommandResult}" +
      $" affected={context.PreviousNativeNames.Count}" +
      $" named={namedCount} unnamed={unnamedCount}");
  }

  private static void CapturePreviousNativeName(
    HidePollContext context,
    RhinoObject obj)
  {
    if (context.PreviousNativeNames.ContainsKey(obj.Id))
      return;

    context.PreviousNativeNames[obj.Id] =
      TryGetNativeName(obj, out var name) ? name : string.Empty;
  }
}
