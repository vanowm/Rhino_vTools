using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Previews the result of moving preselected on-curve edit-point grips, or
/// otherwise the cursor-nearest endpoint control point of each selected open
/// curve, to a cursor-driven target before forwarding those grips to -SetPt.
///
/// Workflow:
///   1. Select curves, starting with any pre-selected curves, and freely
///      add or remove curves before accepting the selection.
///   2. A preselected edit-point grip overrides endpoint detection for its
///      curve; otherwise the endpoint nearest the viewport cursor is used.
///      The resulting curves preview at a target that follows the cursor.
///   3. Grips are turned on and the identified grips are selected.
///   4. Control is transferred to -SetPt with the defaults
///      XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No.
/// </summary>
public sealed class vSetPt : Command
{
  private readonly record struct PreselectedEditPoint(
    int GripIndex,
    double CurveParameter,
    Point3d Point);

  private readonly record struct PendingCurvePick(
    Guid Id,
    bool IsStart,
    PreselectedEditPoint[] EditPoints,
    bool GripsWereOn);

  private static bool _restartingAfterDelegate;
  private static EventHandler? _pendingIdleHandler;
  private static PendingCurvePick[]? _pendingGripPicks;
  private static uint _pendingDocSerial;

  private const string Tag = "vSetPt";

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating to -SetPt — registers vSetPt
    // as the repeatable last command without running anything visible.
    if (_restartingAfterDelegate)
    {
      _restartingAfterDelegate = false;
      return Result.Success;
    }

    CancelPending();
    Log.Write(Tag, "--- run start ---");
    var preselectedEditPoints = CapturePreselectedEditPoints(doc);

    // Accept pre-selected curves or prompt for selection.
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curves");
    go.GeometryFilter  = ObjectType.Curve;
    go.GroupSelect     = true;
    go.SubObjectSelect = false;
    go.EnablePreSelect(true, true);
    go.AlreadySelectedObjectSelect = true;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    var preview = new EndpointPreviewConduit { Enabled = true };
    var cursorTracker = new EndpointCursorCallback(
      doc, preview, preselectedEditPoints) { Enabled = true };
    var preselectedWaitingForConfirmation = false;

    EventHandler<RhinoObjectSelectionEventArgs> onSelectionChanged = (_, e) =>
    {
      if (e.Document == doc)
        cursorTracker.RefreshFromSelection();
    };

    RhinoDoc.SelectObjects   += onSelectionChanged;
    RhinoDoc.DeselectObjects += onSelectionChanged;
    cursorTracker.InitializeFromCurrentCursor();

    try
    {
      while (true)
      {
        var getResult = go.GetMultiple(1, 0);
        cursorTracker.RefreshFromSelection();

        if (go.CommandResult() != Result.Success)
        {
          Log.Write(Tag, "selection cancelled");
          return go.CommandResult();
        }

        if (getResult == GetResult.Object &&
            go.ObjectsWerePreselected &&
            !preselectedWaitingForConfirmation)
        {
          preselectedWaitingForConfirmation = true;
          go.EnablePreSelect(false, true);
          continue;
        }

        if (getResult == GetResult.Object || getResult == GetResult.Nothing)
          break;
      }
    }
    finally
    {
      RhinoDoc.SelectObjects   -= onSelectionChanged;
      RhinoDoc.DeselectObjects -= onSelectionChanged;
      cursorTracker.Enabled = false;
      preview.Enabled = false;
      doc.Views.Redraw();
    }

    var curveData = new List<(Guid id, Curve c)>();
    var originalGripStates = new Dictionary<Guid, bool>();
    var seenIds = new HashSet<Guid>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj?.Geometry is Curve c && !c.IsClosed && seenIds.Add(obj.Id))
      {
        curveData.Add((obj.Id, c));
        originalGripStates[obj.Id] = obj.GripsOn;
      }
    }

    Log.Write(Tag, $"  open curves: {curveData.Count}");

    if (curveData.Count < 2)
    {
      RhinoApp.WriteLine("vSetPt: select at least 2 open curves.");
      return Result.Nothing;
    }

    var cursorPicks = cursorTracker.SnapshotPicks();
    var fallbackPicks = BuildClosestClusterPicks(curveData);
    var picks = new List<PendingCurvePick>();
    for (int i = 0; i < curveData.Count; i++)
    {
      var id = curveData[i].id;
      var chooseStart = cursorPicks.TryGetValue(id, out var previewPick)
        ? previewPick
        : fallbackPicks[id];

      var editPoints = preselectedEditPoints.TryGetValue(id, out var selectedPoints)
        ? selectedPoints
        : Array.Empty<PreselectedEditPoint>();
      Log.Write(Tag, editPoints.Length > 0
        ? $"  curve[{i}] preselected edit points={editPoints.Length}"
        : $"  curve[{i}] cursor pick={(chooseStart ? "start" : "end")}");
      picks.Add(new PendingCurvePick(
        id, chooseStart, editPoints, originalGripStates[id]));
    }

    if (picks.Count == 0)
    {
      RhinoApp.WriteLine("vSetPt: no open curves to process.");
      return Result.Nothing;
    }

    Log.Write(Tag, $"  grip picks: {picks.Count}");

    _pendingGripPicks   = picks.ToArray();
    _pendingDocSerial   = doc.RuntimeSerialNumber;
    _pendingIdleHandler = OnIdleLaunch;
    RhinoApp.Idle      += _pendingIdleHandler;
    return Result.Success;
  }

  private sealed class EndpointPreviewConduit : DisplayConduit
  {
    private readonly List<Curve> _curves = new();

    public void SetCurves(IEnumerable<Curve> curves)
    {
      _curves.Clear();
      _curves.AddRange(curves);
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      foreach (var curve in _curves)
        e.Display.DrawCurve(curve, Color.Cyan, 3);
    }
  }

  private sealed class EndpointCursorCallback : MouseCallback
  {
    private readonly RhinoDoc _doc;
    private readonly EndpointPreviewConduit _preview;
    private readonly IReadOnlyDictionary<Guid, PreselectedEditPoint[]>
      _preselectedEditPoints;
    private readonly Dictionary<Guid, (bool IsStart, Point3d Point)> _picks = new();
    private RhinoView? _view;
    private Point2d _cursor;
    private bool _hasCursor;

    public EndpointCursorCallback(
      RhinoDoc doc,
      EndpointPreviewConduit preview,
      IReadOnlyDictionary<Guid, PreselectedEditPoint[]> preselectedEditPoints)
    {
      _doc = doc;
      _preview = preview;
      _preselectedEditPoints = preselectedEditPoints;
    }

    public void InitializeFromCurrentCursor()
    {
      var view = _doc.Views.ActiveView;
      if (view == null)
        return;

      var client = view.ScreenToClient(System.Windows.Forms.Cursor.Position);
      SetCursor(view, client.X, client.Y);
    }

    public Dictionary<Guid, bool> SnapshotPicks()
    {
      return _picks.ToDictionary(pair => pair.Key, pair => pair.Value.IsStart);
    }

    public void RefreshFromSelection()
    {
      if (!_hasCursor || _view?.ActiveViewport == null)
        return;

      var viewport = _view.ActiveViewport;
      var next = new Dictionary<Guid, (bool IsStart, Point3d Point)>();
      var curves = new Dictionary<Guid, Curve>();
      foreach (var obj in _doc.Objects.GetSelectedObjects(false, false))
      {
        if (obj?.Geometry is not Curve curve || curve.IsClosed)
          continue;

        try
        {
          curves[obj.Id] = curve;
          if (_preselectedEditPoints.TryGetValue(obj.Id, out var editPoints) &&
              editPoints.Length > 0)
          {
            next[obj.Id] = (false, editPoints[0].Point);
            continue;
          }

          var start = viewport.WorldToClient(curve.PointAtStart);
          var end = viewport.WorldToClient(curve.PointAtEnd);
          var startDx = start.X - _cursor.X;
          var startDy = start.Y - _cursor.Y;
          var endDx = end.X - _cursor.X;
          var endDy = end.Y - _cursor.Y;
          var chooseStart =
            (startDx * startDx) + (startDy * startDy) <=
            (endDx * endDx) + (endDy * endDy);
          next[obj.Id] = (
            chooseStart,
            chooseStart ? curve.PointAtStart : curve.PointAtEnd);
        }
        catch
        {
        }
      }

      _picks.Clear();
      foreach (var pair in next)
        _picks[pair.Key] = pair.Value;

      var previews = new List<Curve>();
      if (_picks.Count > 0)
      {
        var anchorPoints = new List<Point3d>();
        foreach (var pair in _picks)
        {
          if (_preselectedEditPoints.TryGetValue(pair.Key, out var editPoints) &&
              editPoints.Length > 0)
          {
            anchorPoints.AddRange(editPoints.Select(point => point.Point));
          }
          else
          {
            anchorPoints.Add(pair.Value.Point);
          }
        }

        var x = anchorPoints.Sum(point => point.X);
        var y = anchorPoints.Sum(point => point.Y);
        var z = anchorPoints.Sum(point => point.Z);
        var anchor = new Point3d(
          x / anchorPoints.Count,
          y / anchorPoints.Count,
          z / anchorPoints.Count);
        var target = anchor;
        var cursorRay = viewport.ClientToWorld(_cursor);
        var cursorPlane = new Plane(anchor, viewport.CameraDirection);
        if (Rhino.Geometry.Intersect.Intersection.LinePlane(
              cursorRay, cursorPlane, out var rayParameter))
        {
          target = cursorRay.PointAt(rayParameter);
        }

        foreach (var pair in _picks)
        {
          if (!curves.TryGetValue(pair.Key, out var curve))
            continue;

          _preselectedEditPoints.TryGetValue(
            pair.Key, out var selectedEditPoints);
          var result = CreateSetPtPreview(
            curve, pair.Value.IsStart, selectedEditPoints, target);
          if (result != null)
            previews.Add(result);
        }
      }

      _preview.SetCurves(previews);
      _doc.Views.Redraw();
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      if (e.View != null)
        SetCursor(e.View, e.ViewportPoint.X, e.ViewportPoint.Y);

      base.OnMouseMove(e);
    }

    private void SetCursor(RhinoView view, double x, double y)
    {
      _view = view;
      _cursor = new Point2d(x, y);
      _hasCursor = true;
      RefreshFromSelection();
    }
  }

  private static Curve? CreateSetPtPreview(
    Curve curve,
    bool isStart,
    IReadOnlyList<PreselectedEditPoint>? selectedEditPoints,
    Point3d target)
  {
    var result = curve.ToNurbsCurve();
    if (result == null || result.Points.Count == 0)
      return null;

    if (selectedEditPoints is { Count: > 0 })
    {
      var editPoints = result.GrevillePoints(false);
      if (editPoints == null || editPoints.Count == 0)
        return null;
      var editParameters = result.GrevilleParameters();
      var parametersMatch = editParameters.Length == editPoints.Count;

      var changedIndices = new HashSet<int>();
      foreach (var selectedPoint in selectedEditPoints)
      {
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < editPoints.Count; index++)
        {
          var distance = parametersMatch
            ? Math.Abs(editParameters[index] - selectedPoint.CurveParameter)
            : editPoints[index].DistanceTo(selectedPoint.Point);
          if (distance >= bestDistance)
            continue;

          bestDistance = distance;
          bestIndex = index;
        }

        if (bestIndex >= 0 && changedIndices.Add(bestIndex))
          editPoints[bestIndex] = target;
      }

      return changedIndices.Count > 0 && result.SetGrevillePoints(editPoints)
        ? result
        : null;
    }

    var endpointIndex = isStart ? 0 : result.Points.Count - 1;
    var controlPoint = result.Points[endpointIndex];
    return result.Points.SetPoint(endpointIndex, target, controlPoint.Weight)
      ? result
      : null;
  }

  private static Dictionary<Guid, PreselectedEditPoint[]>
    CapturePreselectedEditPoints(RhinoDoc doc)
  {
    var pointsByOwner = new Dictionary<Guid, List<PreselectedEditPoint>>();
    var capturedGrips = new List<GripObject>();
    var selected = doc.Objects.GetSelectedObjects(false, true).ToList();

    foreach (var selectedObject in selected)
    {
      if (selectedObject is not GripObject grip)
        continue;

      try
      {
        if (!grip.GetCurveParameters(out var curveParameter))
          continue;

        var owner = doc.Objects.FindId(grip.OwnerId);
        if (owner?.Geometry is not Curve curve || curve.IsClosed)
          continue;

        if (!pointsByOwner.TryGetValue(grip.OwnerId, out var points))
        {
          points = new List<PreselectedEditPoint>();
          pointsByOwner.Add(grip.OwnerId, points);
        }

        if (points.Any(point => point.GripIndex == grip.Index))
          continue;

        points.Add(new PreselectedEditPoint(
          grip.Index,
          curveParameter,
          grip.CurrentLocation));
        capturedGrips.Add(grip);
      }
      catch
      {
      }
    }

    foreach (var grip in capturedGrips)
      grip.Select(false);

    foreach (var ownerId in pointsByOwner.Keys)
      doc.Objects.FindId(ownerId)?.Select(true);

    var result = pointsByOwner.ToDictionary(
      pair => pair.Key,
      pair => pair.Value.ToArray());
    Log.Write(Tag,
      $"  preselected edit points: {result.Values.Sum(points => points.Length)}" +
      $" on {result.Count} curve(s)");
    return result;
  }

  private static Dictionary<Guid, bool> BuildClosestClusterPicks(
    List<(Guid id, Curve c)> curveData)
  {
    var endpoints = new List<(int CurveIndex, Point3d Point)>();
    for (var i = 0; i < curveData.Count; i++)
    {
      endpoints.Add((i, curveData[i].c.PointAtStart));
      endpoints.Add((i, curveData[i].c.PointAtEnd));
    }

    var bestA = 0;
    var bestB = 1;
    var bestDistance = double.MaxValue;
    for (var a = 0; a < endpoints.Count; a++)
    for (var b = a + 1; b < endpoints.Count; b++)
    {
      if (endpoints[a].CurveIndex == endpoints[b].CurveIndex)
        continue;

      var distance = endpoints[a].Point.DistanceTo(endpoints[b].Point);
      if (distance >= bestDistance)
        continue;

      bestDistance = distance;
      bestA = a;
      bestB = b;
    }

    var meetingPoint = endpoints[bestA].Point +
      ((endpoints[bestB].Point - endpoints[bestA].Point) * 0.5);
    var result = new Dictionary<Guid, bool>();
    foreach (var (id, curve) in curveData)
    {
      result[id] = meetingPoint.DistanceTo(curve.PointAtStart) <=
                   meetingPoint.DistanceTo(curve.PointAtEnd);
    }

    return result;
  }

  private static void CancelPending()
  {
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }
    _pendingGripPicks = null;
    _pendingDocSerial = 0u;
  }

  private static void OnIdleLaunch(object? sender, EventArgs e)
  {
    // Remove the handler and capture pending data before anything else.
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }

    var picks     = _pendingGripPicks;
    var docSerial = _pendingDocSerial;
    _pendingGripPicks = null;
    _pendingDocSerial = 0u;

    if (picks == null || picks.Length == 0) return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != docSerial) return;

    doc.Objects.UnselectAll();

    // Enable grips for each target curve and select the requested grips.
    int selectedCount = 0;
    foreach (var pick in picks)
    {
      var id = pick.Id;
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;

      // Preserve an existing edit-point grip mode; hidden curves need endpoint
      // control points enabled for the fallback path.
      if (!obj.GripsOn)
      {
        obj.GripsOn = true;
        obj.CommitChanges();
      }

      var grips = obj.GetGrips();
      if (grips == null || grips.Length == 0) continue;

      var curve = obj.Geometry as Curve;
      if (curve == null) continue;

      if (pick.EditPoints.Length > 0)
      {
        var selectedGripIndices = new HashSet<int>();
        foreach (var selectedPoint in pick.EditPoints)
        {
          var exactGrip = grips.FirstOrDefault(
            grip => grip.Index == selectedPoint.GripIndex);
          var bestDistance = exactGrip?.CurrentLocation.DistanceTo(selectedPoint.Point)
            ?? double.MaxValue;
          if (exactGrip == null)
          {
            foreach (var grip in grips)
            {
              var distance = grip.CurrentLocation.DistanceTo(selectedPoint.Point);
              if (distance >= bestDistance)
                continue;

              bestDistance = distance;
              exactGrip = grip;
            }
          }

          if (exactGrip == null || !selectedGripIndices.Add(exactGrip.Index))
            continue;

          exactGrip.Select(true);
          selectedCount++;
          Log.Write(Tag,
            $"  preselected edit-point grip restored: {id} index={exactGrip.Index}" +
            $" gripDist={bestDistance:G4}");
        }

        continue;
      }

      var targetPt = pick.IsStart ? curve.PointAtStart : curve.PointAtEnd;
      GripObject? endpointGrip = null;
      double bestD = double.MaxValue;
      foreach (var grip in grips)
      {
        var d = grip.CurrentLocation.DistanceTo(targetPt);
        if (d < bestD) { bestD = d; endpointGrip = grip; }
      }
      if (endpointGrip == null) continue;
      endpointGrip.Select(true);
      selectedCount++;

      Log.Write(Tag,
        $"  grip selected: {id} {(pick.IsStart ? "start" : "end")} gripDist={bestD:G4}");
    }

    doc.Views.Redraw();

    if (selectedCount == 0)
    {
      Log.Write(Tag, "  no grips could be selected");
      RhinoApp.WriteLine("vSetPt: failed to select control-point grips.");
      doc.Objects.UnselectAll();
      RestoreGripStates(doc, picks);
      doc.Views.Redraw();
      return;
    }

    Log.Write(Tag, $"  launching -SetPt with {selectedCount} grip(s) selected");

    // Delegate to -SetPt; pre-selected grips bypass the "Select points" step
    // so the user only has to click the target location.
    // XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No are the desired defaults.
    var ok = false;
    try
    {
      ok = RhinoApp.RunScript(
        "_-SetPt _XSet=_Yes _YSet=_Yes _ZSet=_Yes _Alignment=_World _Copy=_No", false);
      Log.Write(Tag, $"  -SetPt result={ok}");
    }
    finally
    {
      doc.Objects.UnselectAll();
      RestoreGripStates(doc, picks);
      doc.Views.Redraw();
    }

    // Silently re-run vSetPt so pressing Enter repeats this command, not -SetPt.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vSetPt", false);
    _restartingAfterDelegate = false;
  }

  private static void RestoreGripStates(
    RhinoDoc doc,
    IEnumerable<PendingCurvePick> picks)
  {
    foreach (var pick in picks)
    {
      var obj = doc.Objects.FindId(pick.Id);
      if (obj == null || obj.GripsOn == pick.GripsWereOn)
        continue;

      obj.GripsOn = pick.GripsWereOn;
      obj.CommitChanges();
    }
  }
}
