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
/// Native scallop-arc command ported from Scallop.py.
/// </summary>
[CommandStyle(Style.NotUndoable)]
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

  // Each arc gets its own native undo record (Ctrl+Z undoes one arc at a time after
  // the command ends). In-command Undo/Redo options manage a parallel manual stack
  // and clean up the native record via ClearUndoRecords when an arc is in-command-undone.
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var undoStack = new Stack<ScallopUndoEntry>();
    var redoStack = new Stack<ScallopUndoEntry>();

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
      var idxUndo   = undoStack.Count > 0 ? go.AddOption("Undo") : -1;
      var idxRedo   = redoStack.Count > 0 ? go.AddOption("Redo") : -1;

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
          if (opt.Index == idxUndo && undoStack.Count > 0)
          {
            ApplyUndo(doc, undoStack.Pop(), redoStack);
            doc.Views.Redraw();
            SavePersistedOptions();
            continue;
          }
          if (opt.Index == idxRedo && redoStack.Count > 0)
          {
            ApplyRedo(doc, redoStack.Pop(), undoStack);
            doc.Views.Redraw();
            SavePersistedOptions();
            continue;
          }
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

      // Capture source geometry before deleting it (needed for in-command Undo)
      Curve? sourceCurveGeom = null;
      ObjectAttributes? sourceCurveAttr = null;
      var willDelete = _deleteOriginal && sourceLineId != Guid.Empty;
      if (willDelete)
      {
        var srcObj = doc.Objects.FindId(sourceLineId);
        sourceCurveGeom = (srcObj?.Geometry as Curve)?.DuplicateCurve();
        sourceCurveAttr = srcObj?.Attributes.Duplicate();
        if (sourceCurveGeom == null)
          willDelete = false;
      }

      var undoSerial = doc.BeginUndoRecord("vScallop");

      var arcId = doc.Objects.AddCurve(arcCurve);
      if (arcId == Guid.Empty)
      {
        doc.EndUndoRecord(undoSerial);
        RhinoApp.WriteLine("vScallop: failed to add arc to document.");
        continue;
      }

      var arcAttr = doc.Objects.FindId(arcId)?.Attributes.Duplicate() ?? new ObjectAttributes();

      if (willDelete)
        doc.Objects.Delete(sourceLineId, true);

      doc.EndUndoRecord(undoSerial);

      undoStack.Push(new ScallopUndoEntry(
        undoSerial, arcId, arcCurve, arcAttr,
        willDelete, sourceLineId,
        sourceCurveGeom, sourceCurveAttr));
      redoStack.Clear();
      SavePersistedOptions();
      doc.Views.Redraw();
    }
  }

  // ── In-command undo/redo helpers ──────────────────────────────────────────

  private sealed record ScallopUndoEntry(
    uint UndoSerial,
    Guid CreatedArcId,
    ArcCurve ArcGeometry,
    ObjectAttributes ArcAttributes,
    bool HadDeletedSource,
    Guid SourceId,
    Curve? SourceGeometry,
    ObjectAttributes? SourceAttributes);

  private static void ApplyUndo(
    RhinoDoc doc,
    ScallopUndoEntry entry,
    Stack<ScallopUndoEntry> redoStack)
  {
    doc.Objects.Delete(entry.CreatedArcId, true);

    var restoredSourceId = Guid.Empty;
    if (entry.HadDeletedSource && entry.SourceGeometry != null)
      restoredSourceId = doc.Objects.AddCurve(entry.SourceGeometry, entry.SourceAttributes ?? new ObjectAttributes());

    // Remove the native undo record so post-command Ctrl+Z skips this arc.
    doc.ClearUndoRecords(entry.UndoSerial, false);

    redoStack.Push(entry with
    {
      UndoSerial = 0,
      CreatedArcId = Guid.Empty,
      SourceId = restoredSourceId
    });
  }

  private static void ApplyRedo(
    RhinoDoc doc,
    ScallopUndoEntry entry,
    Stack<ScallopUndoEntry> undoStack)
  {
    if (entry.HadDeletedSource && entry.SourceId != Guid.Empty)
      doc.Objects.Delete(entry.SourceId, true);

    var serial = doc.BeginUndoRecord("vScallop");
    var newArcId = doc.Objects.AddCurve(entry.ArcGeometry, entry.ArcAttributes);
    var newAttr  = doc.Objects.FindId(newArcId)?.Attributes.Duplicate() ?? new ObjectAttributes();
    doc.EndUndoRecord(serial);

    undoStack.Push(entry with
    {
      UndoSerial   = serial,
      CreatedArcId = newArcId,
      ArcAttributes = newAttr
    });
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
