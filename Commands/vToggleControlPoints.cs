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

    var displayMode = SelectionDisplayMode(doc, context);
    Log.Write(Tag, $"selection mode={displayMode}");

    if (displayMode == PointDisplayMode.ControlPoints)
      ShowEditPoints(doc, context);
    else if (displayMode == PointDisplayMode.EditPoints)
      ShowControlPoints(doc, context);
    else
      ShowEditPoints(doc, context);

    return Result.Success;
  }

  private static PointDisplayMode SelectionDisplayMode(RhinoDoc doc, SelectionContext context)
  {
    var (controlPointCapable, _) = SplitEditPointOnly(doc, context.ObjectIds);
    if (controlPointCapable.Count == 0)
      return PointDisplayMode.None;

    var capable = new HashSet<Guid>(controlPointCapable);
    if (context.PointRecords.Count > 0)
    {
      var pointRecordMode = PointRecordMode(doc, context.PointRecords.Where(r => capable.Contains(r.OwnerId)));
      if (pointRecordMode != PointDisplayMode.None)
        return pointRecordMode;
    }

    if (context.SubObjectOnly)
      return PointDisplayMode.EditPoints;

    var checkIds = new List<Guid>();
    var seen = new HashSet<Guid>();
    foreach (var record in context.PointRecords)
    {
      if (capable.Contains(record.OwnerId))
        AddUniqueExistingId(doc, checkIds, seen, record.OwnerId);
    }

    if (checkIds.Count == 0)
    {
      foreach (var id in controlPointCapable)
        AddUniqueExistingId(doc, checkIds, seen, id);
    }

    var foundEditPoints = false;
    foreach (var id in checkIds)
    {
      var objectMode = ObjectDisplayMode(doc, id);
      if (objectMode == PointDisplayMode.ControlPoints)
        return PointDisplayMode.ControlPoints;
      if (objectMode == PointDisplayMode.EditPoints)
        foundEditPoints = true;
    }

    return foundEditPoints ? PointDisplayMode.EditPoints : PointDisplayMode.None;
  }

  private static PointDisplayMode PointRecordMode(RhinoDoc doc, IEnumerable<PointRecord> pointRecords)
  {
    var foundEditPoints = false;
    var tolerance = OnGeometryTolerance(doc);

    foreach (var record in pointRecords)
    {
      var distance = DistanceToGeometry(doc, record.OwnerId, record.Point);
      if (!distance.HasValue)
        continue;

      if (distance.Value > tolerance)
        return PointDisplayMode.ControlPoints;

      foundEditPoints = true;
    }

    return foundEditPoints ? PointDisplayMode.EditPoints : PointDisplayMode.None;
  }

  private static PointDisplayMode ObjectDisplayMode(RhinoDoc doc, Guid objectId)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj == null)
      return PointDisplayMode.None;

    var grips = VisibleGrips(obj);
    var gripsOn = obj.GripsOn;

    if (grips.Count > 0 && !gripsOn)
      return PointDisplayMode.EditPoints;

    if (!gripsOn || grips.Count == 0)
      return PointDisplayMode.None;

    var onGeometry = GripsAreOnGeometry(doc, objectId, grips);
    if (onGeometry == true)
      return PointDisplayMode.EditPoints;
    if (onGeometry == false)
      return PointDisplayMode.ControlPoints;

    return PointDisplayMode.ControlPoints;
  }

  private static bool? GripsAreOnGeometry(RhinoDoc doc, Guid objectId, IReadOnlyList<GripObject> grips)
  {
    if (grips.Count == 0)
      return null;

    var tolerance = OnGeometryTolerance(doc);
    var foundDistance = false;

    foreach (var index in SampleIndices(grips.Count))
    {
      var distance = DistanceToGeometry(doc, objectId, grips[index].CurrentLocation);
      if (!distance.HasValue)
        continue;

      foundDistance = true;
      if (distance.Value > tolerance)
        return false;
    }

    return foundDistance ? true : null;
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
    var gripStates = CaptureGripStates(doc, context.ObjectIds);
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
        RestoreGripStates(doc, gripStates);
      }
    }
    finally
    {
      doc.Views.RedrawEnabled = previousRedraw;
    }

    RhinoApp.WriteLine(editPointsOn ? "Grips: On" : "Grips: failed to turn on");
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
    var result = RhinoApp.RunScript(command, false);
    Log.Write(Tag, $"command '{command}' result={result}");
    if (!result && !command.StartsWith("'", StringComparison.Ordinal))
    {
      var transparentCommand = "'" + command;
      result = RhinoApp.RunScript(transparentCommand, false);
      Log.Write(Tag, $"command '{transparentCommand}' result={result}");
    }

    return result;
  }

  private static Dictionary<Guid, bool> CaptureGripStates(RhinoDoc doc, IEnumerable<Guid> objectIds)
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

  private static void RestoreGripStates(RhinoDoc doc, IReadOnlyDictionary<Guid, bool> states)
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

  private enum PointDisplayMode
  {
    None,
    ControlPoints,
    EditPoints
  }

  private readonly record struct PointRecord(Guid OwnerId, int Index, Point3d Point);

  private sealed class SelectionContext
  {
    private SelectionContext(
      List<Guid> objectIds,
      List<PointRecord> pointRecords,
      bool pointOnly,
      bool subObjectOnly)
    {
      ObjectIds = objectIds;
      PointRecords = pointRecords;
      PointOnly = pointOnly;
      SubObjectOnly = subObjectOnly;
    }

    public List<Guid> ObjectIds { get; }
    public List<PointRecord> PointRecords { get; }
    public bool PointOnly { get; }
    public bool SubObjectOnly { get; }

    public static SelectionContext FromDocument(RhinoDoc doc)
    {
      var ids = new List<Guid>();
      var seen = new HashSet<Guid>();
      var pointRecords = new List<PointRecord>();
      var pointOwnerIds = new HashSet<Guid>();
      var selectedObjectIds = new List<Guid>();
      var normalSelectedIds = new HashSet<Guid>(
        doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id));

      foreach (var selected in doc.Objects.GetSelectedObjects(false, true))
      {
        if (selected is GripObject grip)
        {
          pointRecords.Add(new PointRecord(grip.OwnerId, grip.Index, grip.CurrentLocation));
          pointOwnerIds.Add(grip.OwnerId);
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

      var pointOnly = pointRecords.Count > 0 && !selectedObjectIds.Any(id => !pointOwnerIds.Contains(id));
      var subObjectOnly = ids.Count > 0 && !ids.Any(normalSelectedIds.Contains);

      Log.Write(Tag, string.Create(
        CultureInfo.InvariantCulture,
        $"selection ids={ids.Count} pointRecords={pointRecords.Count} pointOnly={pointOnly} subObjectOnly={subObjectOnly}"));

      return new SelectionContext(ids, pointRecords, pointOnly, subObjectOnly);
    }
  }
}
