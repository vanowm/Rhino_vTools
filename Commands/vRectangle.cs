using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native rectangle command ported from Rectangle.py.
/// Creates an axis-aligned rectangle polyline from width/height inputs and a corner point.
/// Width and height can be driven by total selected curve length.
/// </summary>
public sealed class vRectangle : Command
{
  private const string OptionsSectionName = "vRectangle";
  private const string WidthKey = "width";
  private const string HeightKey = "height";
  private const string LastBlXKey = "lastBlX";
  private const string LastBlYKey = "lastBlY";
  private const string LastBlZKey = "lastBlZ";
  private const string LastBrXKey = "lastBrX";
  private const string LastBrYKey = "lastBrY";
  private const string LastBrZKey = "lastBrZ";

  private static double _width = 10.0;
  private static double _height = 5.0;
  private static Point3d? _lastBottomLeft;
  private static Point3d? _lastBottomRight;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vRectangle";

  /// <summary>
  /// Creates an axis-aligned rectangle from width/height and a bottom-left corner pick.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    double width;
    double height;

    // If curves are already selected, use their total length as the width.
    var preselectedWidth = SelectedObjectsTotalCurveLength(doc);
    if (preselectedWidth.HasValue)
    {
      width = preselectedWidth.Value;
      height = _height;
      RhinoApp.WriteLine($"vRectangle: Width from selected objects: {width:G}");
    }
    else
    {
      // Prompt for width — accept curve selection, number input, or Enter for current.
      var pickedWidth = PromptDimension(doc, $"Width <{_width:G}> (select curves, type number, or Enter for current)", _width);
      if (!pickedWidth.HasValue)
        return Result.Cancel;
      width = pickedWidth.Value;

      // Clear selection so height prompt starts fresh.
      doc.Objects.UnselectAll();
      doc.Views.Redraw();

      var pickedHeight = PromptDimension(doc, $"Height <{_height:G}> (select curves, type number, or Enter for current)", _height);
      if (!pickedHeight.HasValue)
        return Result.Cancel;
      height = pickedHeight.Value;
    }

    // Default corner: last bottom-right, then last bottom-left, then nothing.
    var defaultCorner = _lastBottomRight ?? _lastBottomLeft;

    if (!PickBottomLeftCorner(doc, ref width, ref height, defaultCorner, out var bottomLeft))
      return Result.Cancel;

    var rectId = AddRectangle(doc, bottomLeft, width, height);
    if (rectId == Guid.Empty)
    {
      RhinoApp.WriteLine("vRectangle: failed to create rectangle.");
      return Result.Failure;
    }

    _width = width;
    _height = height;
    _lastBottomLeft = bottomLeft;
    _lastBottomRight = new Point3d(bottomLeft.X + width, bottomLeft.Y, bottomLeft.Z);
    SavePersistedOptions();

    doc.Views.Redraw();
    return Result.Success;
  }

  // -------------------------------------------------------------------------
  // Dimension prompt: curves → total length, number → direct, Enter → default.
  // -------------------------------------------------------------------------

  private static double? PromptDimension(RhinoDoc doc, string prompt, double currentValue)
  {
    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt(prompt);
      go.GeometryFilter = ObjectType.Curve;
      go.EnablePreSelect(false, true);
      go.AcceptNumber(true, false);
      go.AcceptNothing(true);

      var res = go.GetMultiple(1, 0);

      if (go.CommandResult() != Result.Success)
        return null;

      if (res == GetResult.Object && go.ObjectCount > 0)
      {
        var curves = new List<Curve>();
        for (var i = 0; i < go.ObjectCount; i++)
        {
          var c = go.Object(i).Curve();
          if (c != null)
            curves.Add(c);
        }

        var total = SumCurveLengths(curves);
        if (total.HasValue)
        {
          RhinoApp.WriteLine($"vRectangle: selected total length: {total.Value:G}");
          return total.Value;
        }

        RhinoApp.WriteLine("vRectangle: no valid curve length found. Select curve objects.");
        continue;
      }

      if (res == GetResult.Number)
      {
        var n = go.Number();
        if (n > 0.0)
          return n;
        RhinoApp.WriteLine("vRectangle: value must be greater than zero.");
        continue;
      }

      if (res == GetResult.Nothing)
      {
        if (currentValue > 0.0)
          return currentValue;
        RhinoApp.WriteLine("vRectangle: value must be greater than zero.");
        continue;
      }

      return null;
    }
  }

  // -------------------------------------------------------------------------
  // Bottom-left corner pick with live preview and Width/Height option buttons.
  // -------------------------------------------------------------------------

  private static bool PickBottomLeftCorner(
    RhinoDoc doc,
    ref double width,
    ref double height,
    Point3d? defaultCorner,
    out Point3d bottomLeft)
  {
    bottomLeft = Point3d.Unset;
    var w = width;
    var h = height;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt(defaultCorner.HasValue
        ? "Pick bottom-left corner (Enter for last position)"
        : "Pick bottom-left corner");
      gp.AcceptNothing(defaultCorner.HasValue);

      var widthOpt = new OptionDouble(w, true, 0.0);
      var heightOpt = new OptionDouble(h, true, 0.0);
      var idxWidth = gp.AddOptionDouble("Width", ref widthOpt);
      var idxHeight = gp.AddOptionDouble("Height", ref heightOpt);

      EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
      {
        if (w <= 0.0 || h <= 0.0)
          return;
        var poly = BuildRectanglePolyline(e.CurrentPoint, w, h);
        e.Display.DrawPolyline(poly, Color.Cyan, 2);
      };

      gp.DynamicDraw += drawPreview;
      var res = gp.Get();
      gp.DynamicDraw -= drawPreview;

      if (gp.CommandResult() != Result.Success)
        return false;

      if (res == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt != null)
        {
          if (opt.Index == idxWidth)
          {
            w = widthOpt.CurrentValue;
            _width = w;
            SavePersistedOptions();
          }
          else if (opt.Index == idxHeight)
          {
            h = heightOpt.CurrentValue;
            _height = h;
            SavePersistedOptions();
          }
        }

        if (w <= 0.0 || h <= 0.0)
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");

        continue;
      }

      if (res == GetResult.Point)
      {
        if (w <= 0.0 || h <= 0.0)
        {
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");
          continue;
        }

        width = w;
        height = h;
        bottomLeft = gp.Point();
        return true;
      }

      if (res == GetResult.Nothing)
      {
        if (!defaultCorner.HasValue)
        {
          RhinoApp.WriteLine("vRectangle: no previous rectangle position available.");
          continue;
        }

        if (w <= 0.0 || h <= 0.0)
        {
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");
          continue;
        }

        width = w;
        height = h;
        bottomLeft = defaultCorner.Value;
        return true;
      }

      return false;
    }
  }

  // -------------------------------------------------------------------------
  // Geometry helpers.
  // -------------------------------------------------------------------------

  private static Polyline BuildRectanglePolyline(Point3d bl, double w, double h)
  {
    var br = new Point3d(bl.X + w, bl.Y, bl.Z);
    var tr = new Point3d(bl.X + w, bl.Y + h, bl.Z);
    var tl = new Point3d(bl.X, bl.Y + h, bl.Z);
    return new Polyline(new[] { bl, br, tr, tl, bl });
  }

  private static Guid AddRectangle(RhinoDoc doc, Point3d bl, double w, double h)
    => doc.Objects.AddPolyline(BuildRectanglePolyline(bl, w, h));

  private static double? SumCurveLengths(IReadOnlyList<Curve> curves)
  {
    if (curves.Count == 0)
      return null;
    var total = 0.0;
    foreach (var c in curves)
      total += c.GetLength();
    return total > 0.0 ? total : null;
  }

  private static double? SelectedObjectsTotalCurveLength(RhinoDoc doc)
  {
    var curves = new List<Curve>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj?.Geometry is Curve c)
        curves.Add(c);
    }
    return SumCurveLengths(curves);
  }

  // -------------------------------------------------------------------------
  // Option persistence.
  // -------------------------------------------------------------------------

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var width = _width;
        var height = _height;
        Point3d? lastBl = null;
        Point3d? lastBr = null;

        if (vToolsOptionStore.TryGetDouble(section, WidthKey, out var w) && w > 0.0)
          width = w;
        if (vToolsOptionStore.TryGetDouble(section, HeightKey, out var h) && h > 0.0)
          height = h;

        if (vToolsOptionStore.TryGetDouble(section, LastBlXKey, out var blX) &&
            vToolsOptionStore.TryGetDouble(section, LastBlYKey, out var blY) &&
            vToolsOptionStore.TryGetDouble(section, LastBlZKey, out var blZ))
          lastBl = new Point3d(blX, blY, blZ);

        if (vToolsOptionStore.TryGetDouble(section, LastBrXKey, out var brX) &&
            vToolsOptionStore.TryGetDouble(section, LastBrYKey, out var brY) &&
            vToolsOptionStore.TryGetDouble(section, LastBrZKey, out var brZ))
          lastBr = new Point3d(brX, brY, brZ);

        return (width, height, lastBl, lastBr);
      });

    _width = values.width;
    _height = values.height;
    _lastBottomLeft = values.lastBl;
    _lastBottomRight = values.lastBr;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[WidthKey] = _width;
        section[HeightKey] = _height;

        if (_lastBottomLeft.HasValue)
        {
          section[LastBlXKey] = _lastBottomLeft.Value.X;
          section[LastBlYKey] = _lastBottomLeft.Value.Y;
          section[LastBlZKey] = _lastBottomLeft.Value.Z;
        }

        if (_lastBottomRight.HasValue)
        {
          section[LastBrXKey] = _lastBottomRight.Value.X;
          section[LastBrYKey] = _lastBottomRight.Value.Y;
          section[LastBrZKey] = _lastBottomRight.Value.Z;
        }
      });
  }
}
