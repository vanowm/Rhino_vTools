using System;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native scallop-arc command ported from Scallop.py.
/// </summary>
public sealed class vScallop : Command
{
  private const string OptionsSectionName = "vScallop";
  private const string SizeKey = "size";
  private const string DeleteOriginalKey = "deleteOriginal";
  private const string FreeKey = "free";

  private static double _size = 1.0;
  private static bool _deleteOriginal;
  private static bool _free;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vScallop";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    while (true)
    {
      // ── Outer prompt: select curve or choose Points ──────────────────
      var go = new GetObject();
      go.SetCommandPrompt("Select curve");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.AcceptNumber(true, false);

      var sizeOpt   = new OptionDouble(_size, doc.ModelAbsoluteTolerance, 1.0e300);
      var deleteOpt = new OptionToggle(_deleteOriginal, "No", "Yes");
      var freeOpt   = new OptionToggle(_free, "No", "Yes");
      go.AddOptionDouble("Size", ref sizeOpt);
      go.AddOptionToggle("DeleteOriginal", ref deleteOpt);
      go.AddOptionToggle("Free", ref freeOpt);
      var idxPoints = go.AddOption("Points");

      var outerResult = go.Get();

      if (go.CommandResult() != Result.Success)
      {
        SavePersistedOptions();
        return Result.Success;
      }

      _size           = Math.Max(sizeOpt.CurrentValue, doc.ModelAbsoluteTolerance);
      _deleteOriginal = deleteOpt.CurrentValue;
      _free           = freeOpt.CurrentValue;

      if (outerResult == GetResult.Number)
      {
        _size = Math.Max(go.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      // ── Handle options ────────────────────────────────────────────────
      var twoPointMode = false;
      if (outerResult == GetResult.Option)
      {
        var opt = go.Option();
        if (opt != null)
        {
          if (opt.Index == idxPoints)
            twoPointMode = true;
          else
          {
            SavePersistedOptions();
            continue;
          }
        }
        else
        {
          SavePersistedOptions();
          continue;
        }
      }

      // ── Resolve input points ───────────────────────────────────────────
      Point3d pointA, pointB;
      Guid sourceLineId;

      if (twoPointMode)
      {
        if (!TryPickPoint(doc, "Pick first point", null, out pointA))
          continue;
        if (!TryPickPoint(doc, "Pick second point", pointA, out pointB))
          continue;
        sourceLineId = Guid.Empty;
      }
      else if (outerResult == GetResult.Object)
      {
        var objRef = go.Object(0);
        if (objRef?.Curve() is not Curve curve)
          continue;

        if (curve.IsClosed)
        {
          RhinoApp.WriteLine("vScallop: selected curve is closed; need an open curve.");
          doc.Objects.UnselectAll();
          doc.Views.Redraw();
          continue;
        }

        pointA = curve.PointAtStart;
        pointB = curve.PointAtEnd;
        sourceLineId = objRef.ObjectId;
      }
      else
        continue;

      if (pointA.DistanceTo(pointB) <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine("vScallop: points are too close together.");
        continue;
      }

      // ── Side / bulge ───────────────────────────────────────────────────
      if (!TryGetSideAndBulge(doc, pointA, pointB, out var sideDirection, out var bulgeSize))
        continue;

      // ── Create arc ────────────────────────────────────────────────────
      var arcCurve = CreateScallopArc(pointA, pointB, sideDirection, bulgeSize);
      if (arcCurve == null || !arcCurve.IsValid)
      {
        RhinoApp.WriteLine("vScallop: failed to create arc.");
        continue;
      }

      var arcId = doc.Objects.AddCurve(arcCurve);
      if (arcId == Guid.Empty)
      {
        RhinoApp.WriteLine("vScallop: failed to add arc to document.");
        continue;
      }

      if (_deleteOriginal && sourceLineId != Guid.Empty)
        doc.Objects.Delete(sourceLineId, true);

      doc.Objects.UnselectAll();
      SavePersistedOptions();
      doc.Views.Redraw();
    }
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var size = _size;
        var deleteOriginal = _deleteOriginal;
        var free = _free;

        if (vToolsOptionStore.TryGetDouble(section, SizeKey, out var persistedSize) && persistedSize > RhinoMath.ZeroTolerance)
          size = persistedSize;
        if (vToolsOptionStore.TryGetBool(section, DeleteOriginalKey, out var persistedDelete))
          deleteOriginal = persistedDelete;
        if (vToolsOptionStore.TryGetBool(section, FreeKey, out var persistedFree))
          free = persistedFree;

        return (size, deleteOriginal, free);
      });

    _size = Math.Max(values.size, RhinoMath.ZeroTolerance);
    _deleteOriginal = values.deleteOriginal;
    _free = values.free;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[SizeKey] = _size;
        section[DeleteOriginalKey] = _deleteOriginal;
        section[FreeKey] = _free;
      });
  }

  private static bool TryPickPoint(RhinoDoc doc, string prompt, Point3d? basePoint, out Point3d point)
  {
    point = Point3d.Unset;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt(prompt);
      if (basePoint.HasValue && basePoint.Value.IsValid)
        gp.DrawLineFromPoint(basePoint.Value, true);

      gp.AcceptNumber(true, false);

      var sizeOption = new OptionDouble(_size, doc.ModelAbsoluteTolerance, 1.0e300);
      var freeOption = new OptionToggle(_free, "No", "Yes");

      gp.AddOptionDouble("Size", ref sizeOption);
      gp.AddOptionToggle("Free", ref freeOption);

      var result = gp.Get();
      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _size = Math.Max(sizeOption.CurrentValue, doc.ModelAbsoluteTolerance);
        _free = freeOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        _size = Math.Max(gp.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Point)
      {
        point = gp.Point();
        return true;
      }

      return false;
    }
  }

  private static bool TryGetSideAndBulge(
    RhinoDoc doc,
    Point3d pointA,
    Point3d pointB,
    out Vector3d sideDirection,
    out double bulgeSize)
  {
    sideDirection = Vector3d.Unset;
    bulgeSize = 0.0;

    var mid = 0.5 * (pointA + pointB);

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick side for arc bulge");
      gp.SetBasePoint(mid, true);
      gp.AcceptNumber(true, false);

      var sizeOption = new OptionDouble(_size, doc.ModelAbsoluteTolerance, 1.0e300);
      var freeOption = new OptionToggle(_free, "No", "Yes");

      gp.AddOptionDouble("Size", ref sizeOption);
      gp.AddOptionToggle("Free", ref freeOption);

      EventHandler<GetPointDrawEventArgs> draw = (_, e) =>
      {
        var preview = BuildPreviewArc(doc, pointA, pointB, e.CurrentPoint);
        if (preview != null)
          e.Display.DrawCurve(preview, Color.DeepSkyBlue, 2);
      };

      gp.DynamicDraw += draw;
      var result = gp.Get();
      gp.DynamicDraw -= draw;

      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _size = Math.Max(sizeOption.CurrentValue, doc.ModelAbsoluteTolerance);
        _free = freeOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        _size = Math.Max(gp.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      if (result != GetResult.Point)
        return false;

      var sidePoint = gp.Point();
      if (!TryResolvePerpDirection(doc, pointA, pointB, sidePoint, out sideDirection))
      {
        RhinoApp.WriteLine("vScallop: unable to resolve side direction.");
        continue;
      }

      bulgeSize = ResolveBulgeSize(pointA, pointB, sidePoint, sideDirection);
      if (bulgeSize <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine("vScallop: bulge size is too small.");
        continue;
      }

      return true;
    }
  }

  private static bool TryResolvePerpDirection(RhinoDoc doc, Point3d pointA, Point3d pointB, Point3d sidePoint, out Vector3d direction)
  {
    direction = Vector3d.Unset;

    var chord = pointB - pointA;
    if (!chord.Unitize())
      return false;

    var activeView = doc.Views.ActiveView;
    var zAxis = activeView != null
      ? activeView.ActiveViewport.ConstructionPlane().ZAxis
      : Vector3d.ZAxis;

    var perp = Vector3d.CrossProduct(zAxis, chord);
    if (!perp.Unitize())
      return false;

    var mid = 0.5 * (pointA + pointB);
    var toSide = sidePoint - mid;
    if (Vector3d.Multiply(perp, toSide) < 0.0)
      perp.Reverse();

    direction = perp;
    return true;
  }

  private static double ResolveBulgeSize(Point3d pointA, Point3d pointB, Point3d sidePoint, Vector3d sideDirection)
  {
    if (!_free)
      return _size;

    var mid = 0.5 * (pointA + pointB);
    var toSide = sidePoint - mid;
    return Math.Abs(Vector3d.Multiply(toSide, sideDirection));
  }

  private static Curve? BuildPreviewArc(RhinoDoc doc, Point3d pointA, Point3d pointB, Point3d sidePoint)
  {
    if (!TryResolvePerpDirection(doc, pointA, pointB, sidePoint, out var direction))
      return null;

    var bulge = ResolveBulgeSize(pointA, pointB, sidePoint, direction);
    if (bulge <= doc.ModelAbsoluteTolerance)
      return null;

    return CreateScallopArc(pointA, pointB, direction, bulge);
  }

  private static ArcCurve? CreateScallopArc(Point3d pointA, Point3d pointB, Vector3d sideDirection, double sizeValue)
  {
    var mid = 0.5 * (pointA + pointB);
    var arcMid = mid + (sideDirection * sizeValue);
    var arc = new Arc(pointA, arcMid, pointB);
    if (!arc.IsValid)
      return null;

    return new ArcCurve(arc);
  }
}
