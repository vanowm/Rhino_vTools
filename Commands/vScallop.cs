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

  /// <summary>
  /// Creates a scallop arc from a selected line or two picked points.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    if (!TryGetInputBase(doc, out var pointA, out var pointB, out var sourceLineId))
      return Result.Cancel;

    if (pointA.DistanceTo(pointB) <= doc.ModelAbsoluteTolerance)
    {
      RhinoApp.WriteLine("vScallop: points are too close together.");
      return Result.Failure;
    }

    if (!TryGetSideAndBulge(doc, pointA, pointB, out var sideDirection, out var bulgeSize))
      return Result.Cancel;

    var arcCurve = CreateScallopArc(pointA, pointB, sideDirection, bulgeSize);
    if (arcCurve == null || !arcCurve.IsValid)
    {
      RhinoApp.WriteLine("vScallop: failed to create arc.");
      return Result.Failure;
    }

    var newId = doc.Objects.AddCurve(arcCurve);
    if (newId == Guid.Empty)
    {
      RhinoApp.WriteLine("vScallop: failed to add arc to document.");
      return Result.Failure;
    }

    if (_deleteOriginal && sourceLineId != Guid.Empty)
      doc.Objects.Delete(sourceLineId, true);

    SavePersistedOptions();
    doc.Views.Redraw();
    return Result.Success;
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

  private static bool TryGetInputBase(RhinoDoc doc, out Point3d pointA, out Point3d pointB, out Guid sourceLineId)
  {
    pointA = Point3d.Unset;
    pointB = Point3d.Unset;
    sourceLineId = Guid.Empty;

    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select line or press Enter for points");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.AcceptNothing(true);
      go.AcceptNumber(true, false);

      var sizeOption = new OptionDouble(_size, doc.ModelAbsoluteTolerance, 1.0e300);
      var deleteOption = new OptionToggle(_deleteOriginal, "No", "Yes");
      var freeOption = new OptionToggle(_free, "No", "Yes");

      go.AddOptionDouble("Size", ref sizeOption);
      go.AddOptionToggle("DeleteOriginal", ref deleteOption);
      go.AddOptionToggle("Free", ref freeOption);

      var result = go.Get();
      if (go.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _size = Math.Max(sizeOption.CurrentValue, doc.ModelAbsoluteTolerance);
        _deleteOriginal = deleteOption.CurrentValue;
        _free = freeOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        _size = Math.Max(go.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Nothing)
      {
        if (!TryPickPoint(doc, "Pick first point", null, out pointA))
          return false;
        if (!TryPickPoint(doc, "Pick second point", pointA, out pointB))
          return false;
        return true;
      }

      if (result != GetResult.Object)
        return false;

      var objRef = go.Object(0);
      if (objRef?.Curve() is not Curve curve)
        continue;

      if (!TryGetLineLikeEndpoints(curve, doc.ModelAbsoluteTolerance, out pointA, out pointB))
      {
        RhinoApp.WriteLine("vScallop: selected curve is not a line.");
        doc.Objects.UnselectAll();
        doc.Views.Redraw();
        continue;
      }

      sourceLineId = objRef.ObjectId;
      return true;
    }
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

  private static bool TryGetLineLikeEndpoints(Curve curve, double tolerance, out Point3d pointA, out Point3d pointB)
  {
    pointA = Point3d.Unset;
    pointB = Point3d.Unset;

    if (curve is LineCurve lineCurve)
    {
      pointA = lineCurve.PointAtStart;
      pointB = lineCurve.PointAtEnd;
      return true;
    }

    if (curve.IsLinear(tolerance))
    {
      pointA = curve.PointAtStart;
      pointB = curve.PointAtEnd;
      return true;
    }

    return false;
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
