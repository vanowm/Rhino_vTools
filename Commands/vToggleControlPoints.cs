using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Toggles selected objects between edit points and control points.
/// </summary>
[CommandStyle(Style.Transparent | Style.ScriptRunner)]
public sealed class vToggleControlPoints : Command
{
  private const string Tag = "vToggleControlPoints";
  private const double OnGeometryToleranceFactor = 0.01;

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    Log.Write(Tag, "--- run start ---");

    var context = SelectionContext.FromDocument(doc);
    if (context.ObjectIds.Count == 0)
    {
      HidePoints(doc);
      return Result.Success;
    }

    var pointMode = SelectionPointMode(doc, context);
    Log.Write(Tag, $"selection pointMode={pointMode}");

    if (pointMode == SelectionPointDisplayMode.ControlPoints ||
        pointMode == SelectionPointDisplayMode.None)
    {
      ShowEditPoints(doc, context);
    }
    else
    {
      ShowControlPoints(doc, context);
    }

    return Result.Success;
  }

  private static SelectionPointDisplayMode SelectionPointMode(RhinoDoc doc, SelectionContext context)
  {
    var foundEditPoints = false;

    foreach (var record in context.PointRecords)
    {
      var mode = PointRecordMode(doc, record);
      if (mode == SelectionPointDisplayMode.ControlPoints)
        return mode;
      if (mode == SelectionPointDisplayMode.EditPoints)
        foundEditPoints = true;
    }

    foreach (var id in context.ObjectIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;

      var grips = VisibleGrips(obj);
      if (grips.Count == 0)
        continue;

      var mode = GripLocationMode(doc, id, grips);
      if (mode == SelectionPointDisplayMode.ControlPoints)
        return mode;
      if (mode == SelectionPointDisplayMode.EditPoints)
        foundEditPoints = true;
    }

    return foundEditPoints ? SelectionPointDisplayMode.EditPoints : SelectionPointDisplayMode.None;
  }

  private static SelectionPointDisplayMode GripLocationMode(
    RhinoDoc doc,
    Guid objectId,
    IReadOnlyList<GripObject> grips)
  {
    var foundEditPoints = false;

    foreach (var index in SampleIndices(grips.Count))
    {
      var mode = GripPointMode(doc, objectId, grips[index]);
      if (mode == SelectionPointDisplayMode.ControlPoints)
        return mode;
      if (mode == SelectionPointDisplayMode.EditPoints)
        foundEditPoints = true;
    }

    return foundEditPoints ? SelectionPointDisplayMode.EditPoints : SelectionPointDisplayMode.None;
  }

  private static SelectionPointDisplayMode PointRecordMode(RhinoDoc doc, PointRecord record)
  {
    var grip = GripAtIndex(doc, record.OwnerId, record.Index);
    if (grip != null)
    {
      var mode = GripPointMode(doc, record.OwnerId, grip);
      if (mode != SelectionPointDisplayMode.None)
        return mode;

      return SelectionPointDisplayMode.None;
    }

    return PointLocationMode(doc, record.OwnerId, record.Point, ignoreCurveEndpoints: true);
  }

  private static SelectionPointDisplayMode GripPointMode(RhinoDoc doc, Guid objectId, GripObject grip)
  {
    var mode = GripMetadataMode(doc, objectId, grip);
    return mode != SelectionPointDisplayMode.None
      ? mode
      : PointLocationMode(doc, objectId, grip.CurrentLocation, ignoreCurveEndpoints: true);
  }

  private static SelectionPointDisplayMode GripMetadataMode(RhinoDoc doc, Guid objectId, GripObject grip)
  {
    var isControlPoint = IsCurveControlPointGrip(grip);
    var isEditPoint = IsCurveEditPointGrip(grip);

    if (isEditPoint && !isControlPoint)
      return SelectionPointDisplayMode.EditPoints;

    if (isControlPoint && !isEditPoint)
      return SelectionPointDisplayMode.ControlPoints;

    if (isControlPoint && isEditPoint)
    {
      Log.Write(Tag, $"ambiguous grip metadata owner={objectId} index={grip.Index}");
      return PointLocationMode(doc, objectId, grip.CurrentLocation, ignoreCurveEndpoints: true);
    }

    return SelectionPointDisplayMode.None;
  }

  private static bool IsCurveControlPointGrip(GripObject grip)
  {
    try
    {
      return grip.GetCurveCVIndices(out var indices) > 0 && indices != null && indices.Length > 0;
    }
    catch
    {
      return false;
    }
  }

  private static bool IsCurveEditPointGrip(GripObject grip)
  {
    try
    {
      return grip.GetCurveParameters(out _);
    }
    catch
    {
      return false;
    }
  }

  private static SelectionPointDisplayMode PointLocationMode(
    RhinoDoc doc,
    Guid objectId,
    Point3d point,
    bool ignoreCurveEndpoints = false)
  {
    var distance = DistanceToGeometry(doc, objectId, point);
    if (!distance.HasValue)
      return SelectionPointDisplayMode.None;

    if (distance.Value > OnGeometryTolerance(doc))
      return SelectionPointDisplayMode.ControlPoints;

    if (ignoreCurveEndpoints && IsCurveEndpoint(doc, objectId, point))
      return SelectionPointDisplayMode.None;

    return SelectionPointDisplayMode.EditPoints;
  }

  private static bool IsCurveEndpoint(RhinoDoc doc, Guid objectId, Point3d point)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not Curve curve)
      return false;

    var tolerance = OnGeometryTolerance(doc);
    try
    {
      return point.DistanceTo(curve.PointAtStart) <= tolerance ||
             point.DistanceTo(curve.PointAtEnd) <= tolerance;
    }
    catch
    {
      return false;
    }
  }

  private static IEnumerable<int> SampleIndices(int count)
  {
    if (count <= 0)
      yield break;

    if (count <= 9)
    {
      for (var i = 0; i < count; i++)
        yield return i;
      yield break;
    }

    var used = new HashSet<int>();
    var step = (count - 1) / 8.0;
    for (var i = 0; i < 9; i++)
    {
      var index = (int)Math.Round(step * i);
      if (used.Add(index))
        yield return index;
    }
  }

  private static double OnGeometryTolerance(RhinoDoc doc)
  {
    return Math.Max(doc.ModelAbsoluteTolerance * OnGeometryToleranceFactor, 1.0e-8);
  }

  private static double? DistanceToGeometry(RhinoDoc doc, Guid objectId, Point3d point)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj == null)
      return null;

    try
    {
      if (obj.Geometry is Curve curve)
      {
        return curve.ClosestPoint(point, out var t)
          ? point.DistanceTo(curve.PointAt(t))
          : null;
      }

      if (obj.Geometry is Surface surface)
      {
        return surface.ClosestPoint(point, out var u, out var v)
          ? point.DistanceTo(surface.PointAt(u, v))
          : null;
      }
    }
    catch
    {
    }

    return null;
  }

  private static (List<Guid> ControlPointCapable, List<Guid> EditPointOnly) SplitEditPointOnly(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds)
  {
    var controlPointCapable = new List<Guid>();
    var editPointOnly = new List<Guid>();

    foreach (var id in objectIds)
    {
      if (IsEditPointOnlyObject(doc, id))
        editPointOnly.Add(id);
      else
        controlPointCapable.Add(id);
    }

    return (controlPointCapable, editPointOnly);
  }

  private static bool IsEditPointOnlyObject(RhinoDoc doc, Guid objectId)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not Curve curve)
      return false;

    if (curve is PolylineCurve)
      return true;

    try
    {
      if (curve.TryGetPolyline(out _))
        return true;
    }
    catch
    {
    }

    return false;
  }

  private static List<GripObject> VisibleGrips(RhinoObject obj)
  {
    try
    {
      return obj.GetGrips()?.Where(g => g != null).ToList() ?? new List<GripObject>();
    }
    catch
    {
      return new List<GripObject>();
    }
  }

  private static void ShowControlPoints(RhinoDoc doc, SelectionContext context)
  {
    var (controlPointCapable, editPointOnly) = SplitEditPointOnly(doc, context.ObjectIds);
    var previousRedraw = doc.Views.RedrawEnabled;
    doc.Views.RedrawEnabled = false;

    try
    {
      PointsOff(doc, context.ObjectIds);

      if (editPointOnly.Count > 0)
      {
        UnselectObjects(doc, controlPointCapable);
        SelectObjects(doc, editPointOnly);
        RunCommand("_EditPtOn _Enter");
      }

      foreach (var id in controlPointCapable)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        obj.GripsOn = true;
        obj.CommitChanges();
      }

      if (controlPointCapable.Count > 0)
        SelectObjects(doc, controlPointCapable);

      RestorePointSelection(doc, context, useNearest: false);
    }
    finally
    {
      doc.Views.RedrawEnabled = previousRedraw;
    }

    RhinoApp.WriteLine("Control points: On");
    doc.Views.ActiveView?.Redraw();
  }

  private static void ShowEditPoints(RhinoDoc doc, SelectionContext context)
  {
    var controlPointStates = CaptureControlPointStates(doc, context.ObjectIds);
    var previousRedraw = doc.Views.RedrawEnabled;
    doc.Views.RedrawEnabled = false;
    var editPointsOn = false;

    try
    {
      PointsOff(doc, context.ObjectIds);
      SelectObjects(doc, context.ObjectIds);
      editPointsOn = RunCommand("_EditPtOn _Enter");
      if (editPointsOn)
      {
        RestorePointSelection(doc, context, useNearest: true);
      }
      else
      {
        RestoreControlPointStates(doc, controlPointStates);
      }
    }
    finally
    {
      doc.Views.RedrawEnabled = previousRedraw;
    }

    RhinoApp.WriteLine(editPointsOn ? "Edit points: On" : "Edit points: failed to turn on");
    doc.Views.ActiveView?.Redraw();
  }

  private static void HidePoints(RhinoDoc doc)
  {
    var previousRedraw = doc.Views.RedrawEnabled;
    doc.Views.RedrawEnabled = false;

    try
    {
      PointsOff(doc, null);
    }
    finally
    {
      doc.Views.RedrawEnabled = previousRedraw;
    }

    RhinoApp.WriteLine("Points: Off");
    doc.Views.Redraw();
  }

  private static void PointsOff(RhinoDoc doc, IEnumerable<Guid>? objectIds)
  {
    RunCommand("_PointsOff");
    if (objectIds == null)
      return;

    foreach (var id in objectIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null || !obj.GripsOn)
        continue;

      obj.GripsOn = false;
      obj.CommitChanges();
    }
  }

  private static bool RunCommand(string command)
  {
    var nestedCommand = IsNestedTransparentRun();
    var primaryCommand = nestedCommand && !command.StartsWith("'", StringComparison.Ordinal)
      ? "'" + command
      : command;

    var result = RhinoApp.RunScript(primaryCommand, false);
    Log.Write(Tag, $"command '{primaryCommand}' result={result} nested={nestedCommand}");
    if (!result && !nestedCommand && !command.StartsWith("'", StringComparison.Ordinal))
    {
      var transparentCommand = "'" + command;
      result = RhinoApp.RunScript(transparentCommand, false);
      Log.Write(Tag, $"command '{transparentCommand}' result={result} nested={nestedCommand}");
    }

    return result;
  }

  private static bool IsNestedTransparentRun()
  {
    try
    {
      return Command.GetCommandStack().Length > 1;
    }
    catch
    {
      return false;
    }
  }

  private static Dictionary<Guid, bool> CaptureControlPointStates(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var states = new Dictionary<Guid, bool>();
    foreach (var id in objectIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;

      states[id] = obj.GripsOn;
    }

    return states;
  }

  private static void RestoreControlPointStates(RhinoDoc doc, IReadOnlyDictionary<Guid, bool> states)
  {
    foreach (var (id, gripsOn) in states)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null)
        continue;

      obj.GripsOn = gripsOn;
      obj.CommitChanges();
    }
  }

  private static void RestorePointSelection(RhinoDoc doc, SelectionContext context, bool useNearest)
  {
    if (context.PointRecords.Count == 0)
      return;

    if (context.PointOnly)
      UnselectObjects(doc, context.ObjectIds);

    foreach (var record in context.PointRecords)
    {
      var grip = useNearest
        ? NearestGrip(doc, record.OwnerId, record.Index, record.Point)
        : GripAtIndex(doc, record.OwnerId, record.Index);

      grip?.Select(true);
    }
  }

  private static GripObject? GripAtIndex(RhinoDoc doc, Guid ownerId, int index)
  {
    var obj = doc.Objects.FindId(ownerId);
    if (obj == null)
      return null;

    var grips = VisibleGrips(obj);
    return index >= 0 && index < grips.Count ? grips[index] : null;
  }

  private static GripObject? NearestGrip(RhinoDoc doc, Guid ownerId, int index, Point3d point)
  {
    var indexedGrip = GripAtIndex(doc, ownerId, index);
    if (indexedGrip != null)
      return indexedGrip;

    var obj = doc.Objects.FindId(ownerId);
    if (obj == null)
      return null;

    GripObject? bestGrip = null;
    var bestDistance = double.MaxValue;
    foreach (var grip in VisibleGrips(obj))
    {
      var distance = grip.CurrentLocation.DistanceTo(point);
      if (distance >= bestDistance)
        continue;

      bestDistance = distance;
      bestGrip = grip;
    }

    return bestGrip;
  }

  private static void SelectObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    foreach (var id in objectIds)
      doc.Objects.FindId(id)?.Select(true);
  }

  private static void UnselectObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    foreach (var id in objectIds)
      doc.Objects.FindId(id)?.Select(false);
  }

  private static void AddUniqueExistingId(RhinoDoc doc, List<Guid> ids, HashSet<Guid> seen, Guid objectId)
  {
    if (seen.Contains(objectId) || doc.Objects.FindId(objectId) == null)
      return;

    seen.Add(objectId);
    ids.Add(objectId);
  }

  private enum SelectionPointDisplayMode
  {
    None,
    EditPoints,
    ControlPoints
  }

  private readonly record struct PointRecord(Guid OwnerId, int Index, Point3d Point);

  private sealed class SelectionContext
  {
    private SelectionContext(
      List<Guid> objectIds,
      List<PointRecord> pointRecords,
      bool pointOnly)
    {
      ObjectIds = objectIds;
      PointRecords = pointRecords;
      PointOnly = pointOnly;
    }

    public List<Guid> ObjectIds { get; }
    public List<PointRecord> PointRecords { get; }
    public bool PointOnly { get; }

    public static SelectionContext FromDocument(RhinoDoc doc)
    {
      var ids = new List<Guid>();
      var seen = new HashSet<Guid>();
      var pointRecords = new List<PointRecord>();
      var selectedObjectIds = new List<Guid>();
      var normalSelectedIds = new HashSet<Guid>(
        doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id));

      foreach (var selected in doc.Objects.GetSelectedObjects(false, true))
      {
        if (selected is GripObject grip)
        {
          pointRecords.Add(new PointRecord(grip.OwnerId, grip.Index, grip.CurrentLocation));
          AddUniqueExistingId(doc, ids, seen, grip.OwnerId);
          continue;
        }

        selectedObjectIds.Add(selected.Id);
        AddUniqueExistingId(doc, ids, seen, selected.Id);
      }

      if (ids.Count == 0)
      {
        foreach (var id in normalSelectedIds)
        {
          selectedObjectIds.Add(id);
          AddUniqueExistingId(doc, ids, seen, id);
        }
      }

      var pointOnly = pointRecords.Count > 0 && selectedObjectIds.Count == 0;

      Log.Write(Tag, string.Create(
        CultureInfo.InvariantCulture,
        $"selection ids={ids.Count} pointRecords={pointRecords.Count} pointOnly={pointOnly}"));

      return new SelectionContext(ids, pointRecords, pointOnly);
    }
  }
}
