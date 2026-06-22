using System;
using System.Reflection;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace vTools.Commands;

/// <summary>
/// Toggles visible viewport axes: grid/construction axes plus display-mode Z axis.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vAxesToggle : Command
{
  private static readonly string[] GridAxesPropertyNames =
  [
    "ShowGridAxes",
    "GridAxes",
    "GridAxesVisible",
    "ShowConstructionAxes",
    "ConstructionAxesVisible",
    "DrawConstructionAxes",
    "DrawGridAxes",
    "AxesVisible"
  ];

  private static readonly string[] StaticSettingsTypeNames =
  [
    "Rhino.ApplicationSettings.ModelAidSettings",
    "Rhino.ApplicationSettings.ViewSettings",
    "Rhino.ApplicationSettings.AppearanceSettings"
  ];

  private static AxisProperty? _gridAxesProperty;

  public override string EnglishName => "vAxesToggle";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var current = TryGetGridAxesState(doc, out var gridState)
      ? gridState
      : TryGetCurrentDisplayModeZAxisState(doc, out var zState)
        ? zState
        : true;

    var newState = !current;

    var gridUpdated = TrySetGridAxesState(doc, newState);
    var zUpdated = TrySetCurrentDisplayModeZAxisState(doc, newState, out var currentModeId);

    doc.Views.Redraw();

    // Match the Python reference behavior: current mode updates immediately,
    // all other display modes update in the background so the command returns fast.
    if (zUpdated)
    {
      ThreadPool.QueueUserWorkItem(_ => TrySetOtherDisplayModesZAxisState(newState, currentModeId));
    }

    if (!gridUpdated && !zUpdated)
    {
      RhinoApp.WriteLine("vAxesToggle: no supported axis visibility properties found.");
      return Result.Failure;
    }

    // RhinoApp.WriteLine($"vAxesToggle: axes {(newState ? "shown" : "hidden")}.");
    return Result.Success;
  }

  private static bool TryGetGridAxesState(RhinoDoc doc, out bool state)
  {
    var accessor = _gridAxesProperty ??= FindGridAxesProperty(doc);
    if (accessor != null && accessor.TryGet(doc, out state))
      return true;

    state = false;
    return false;
  }

  private static bool TrySetGridAxesState(RhinoDoc doc, bool state)
  {
    var accessor = _gridAxesProperty ??= FindGridAxesProperty(doc);
    return accessor != null && accessor.TrySet(doc, state);
  }

  private static AxisProperty? FindGridAxesProperty(RhinoDoc doc)
  {
    // First try the active viewport, because rhinoscriptsyntax.ShowGridAxes()
    // operates on the active view when no view name is supplied.
    var viewport = doc.Views.ActiveView?.ActiveViewport;
    if (viewport != null)
    {
      var prop = FindBoolProperty(viewport.GetType(), instance: true);
      if (prop != null)
        return AxisProperty.ForActiveViewport(prop);
    }

    // Then try Rhino application settings as a fallback.
    var asm = typeof(RhinoApp).Assembly;
    foreach (var typeName in StaticSettingsTypeNames)
    {
      var type = asm.GetType(typeName, throwOnError: false);
      if (type == null)
        continue;

      var prop = FindBoolProperty(type, instance: false);
      if (prop != null)
        return AxisProperty.ForStatic(prop);
    }

    return null;
  }

  private static PropertyInfo? FindBoolProperty(Type type, bool instance)
  {
    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                (instance ? BindingFlags.Instance : BindingFlags.Static);

    foreach (var name in GridAxesPropertyNames)
    {
      var prop = type.GetProperty(name, flags);
      if (prop is { PropertyType: { } propType } &&
          propType == typeof(bool) &&
          prop.CanRead &&
          prop.CanWrite)
        return prop;
    }

    return null;
  }

  private static bool TryGetCurrentDisplayModeZAxisState(RhinoDoc doc, out bool state)
  {
    state = false;

    try
    {
      var mode = doc.Views.ActiveView?.ActiveViewport?.DisplayMode;
      if (mode == null)
        return false;

      state = mode.DisplayAttributes.ViewSpecificAttributes.DrawZAxis;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static bool TrySetCurrentDisplayModeZAxisState(RhinoDoc doc, bool state, out Guid currentModeId)
  {
    currentModeId = Guid.Empty;

    try
    {
      currentModeId = doc.Views.ActiveView?.ActiveViewport?.DisplayMode?.Id ?? Guid.Empty;
      if (currentModeId == Guid.Empty)
        return false;

      var currentMode = DisplayModeDescription.GetDisplayMode(currentModeId);
      if (currentMode == null)
        return false;

      currentMode.DisplayAttributes.ViewSpecificAttributes.DrawZAxis = state;
      DisplayModeDescription.UpdateDisplayMode(currentMode);
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static void TrySetOtherDisplayModesZAxisState(bool state, Guid currentModeId)
  {
    try
    {
      foreach (var displayMode in DisplayModeDescription.GetDisplayModes())
      {
        if (displayMode == null)
          continue;

        if (currentModeId != Guid.Empty && displayMode.Id == currentModeId)
          continue;

        displayMode.DisplayAttributes.ViewSpecificAttributes.DrawZAxis = state;
        DisplayModeDescription.UpdateDisplayMode(displayMode);
      }
    }
    catch
    {
    }
  }

  private sealed class AxisProperty
  {
    private readonly PropertyInfo _property;
    private readonly bool _activeViewport;

    private AxisProperty(PropertyInfo property, bool activeViewport)
    {
      _property = property;
      _activeViewport = activeViewport;
    }

    public static AxisProperty ForActiveViewport(PropertyInfo property)
      => new(property, activeViewport: true);

    public static AxisProperty ForStatic(PropertyInfo property)
      => new(property, activeViewport: false);

    public bool TryGet(RhinoDoc doc, out bool value)
    {
      value = false;

      try
      {
        var target = Target(doc);
        value = (bool)_property.GetValue(target)!;
        return true;
      }
      catch
      {
        return false;
      }
    }

    public bool TrySet(RhinoDoc doc, bool value)
    {
      try
      {
        var target = Target(doc);
        _property.SetValue(target, value);
        return true;
      }
      catch
      {
        return false;
      }
    }

    private object? Target(RhinoDoc doc)
    {
      return _activeViewport
        ? doc.Views.ActiveView?.ActiveViewport
        : null;
    }
  }
}
