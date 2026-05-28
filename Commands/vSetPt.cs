using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Automatically detects the closest-together endpoints of the selected
/// open curves and forwards those control-point grips to the built-in
/// -SetPt command, so the user only has to click the target location.
///
/// Workflow:
///   1. Select curves (or use pre-selection).
///   2. The command finds which endpoint of each curve is nearest to the
///      tightest cluster of endpoints across all selected curves.
///   3. Grips are turned on and the identified endpoint grips are selected.
///   4. Control is transferred to -SetPt with the defaults
///      XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No.
/// </summary>
public sealed class vSetPt : Command
{
  private static bool _restartingAfterDelegate;
  private static EventHandler? _pendingIdleHandler;
  private static (Guid id, bool isStart)[]? _pendingGripPicks;
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

    // Accept pre-selected curves or prompt for selection.
    var go = new GetObject();
    go.SetCommandPrompt("Select curves");
    go.GeometryFilter  = ObjectType.Curve;
    go.GroupSelect     = true;
    go.SubObjectSelect = false;
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
    {
      Log.Write(Tag, "selection cancelled");
      return go.CommandResult();
    }

    var tol = doc.ModelAbsoluteTolerance;

    // Collect open curves only — closed curves have no distinct endpoints.
    var curveData = new List<(Guid id, Curve c)>();
    foreach (var objRef in go.Objects())
    {
      var obj = doc.Objects.FindId(objRef.ObjectId);
      if (obj?.Geometry is Curve c && !c.IsClosed)
        curveData.Add((objRef.ObjectId, c));
    }

    Log.Write(Tag, $"  open curves: {curveData.Count}");

    if (curveData.Count < 2)
    {
      RhinoApp.WriteLine("vSetPt: select at least 2 open curves.");
      return Result.Nothing;
    }

    // Build all endpoint candidates: (curveIndex, isStart, position).
    var eps = new List<(int idx, bool isStart, Point3d pt)>();
    for (int i = 0; i < curveData.Count; i++)
    {
      var c = curveData[i].c;
      eps.Add((i, true,  c.PointAtStart));
      eps.Add((i, false, c.PointAtEnd));
    }

    // Find the globally closest pair of endpoints from different curves.
    int bestA = -1, bestB = -1;
    double bestDist = double.MaxValue;
    for (int a = 0; a < eps.Count; a++)
    for (int b = a + 1; b < eps.Count; b++)
    {
      if (eps[a].idx == eps[b].idx) continue; // same curve
      var d = eps[a].pt.DistanceTo(eps[b].pt);
      if (d < bestDist) { bestDist = d; bestA = a; bestB = b; }
    }

    if (bestA < 0)
    {
      RhinoApp.WriteLine("vSetPt: could not find a closest endpoint pair.");
      return Result.Nothing;
    }

    // Centroid of the closest pair = the "meeting point".
    var pA     = eps[bestA].pt;
    var pB     = eps[bestB].pt;
    var meetPt = pA + (pB - pA) * 0.5;

    // Search radius: 3× the initial gap, at least 100× model tolerance.
    var radius = Math.Max(bestDist * 3.0, tol * 100.0);

    Log.Write(Tag,
      $"  meet=({meetPt.X:F3},{meetPt.Y:F3},{meetPt.Z:F3})" +
      $" gap={bestDist:G4} radius={radius:G4}");

    // For each curve, pick the endpoint closer to the meeting point,
    // and include it only if it falls within the search radius.
    var picks = new List<(Guid id, bool isStart)>();
    for (int i = 0; i < curveData.Count; i++)
    {
      var c            = curveData[i].c;
      var dStart       = meetPt.DistanceTo(c.PointAtStart);
      var dEnd         = meetPt.DistanceTo(c.PointAtEnd);
      bool chooseStart = dStart <= dEnd;
      double dist      = chooseStart ? dStart : dEnd;

      Log.Write(Tag,
        $"  curve[{i}] dStart={dStart:G4} dEnd={dEnd:G4}" +
        $" pick={( chooseStart ? "start" : "end" )} dist={dist:G4} include={dist <= radius}");

      if (dist <= radius)
        picks.Add((curveData[i].id, chooseStart));
    }

    if (picks.Count == 0)
    {
      RhinoApp.WriteLine("vSetPt: no endpoint cluster found.");
      return Result.Nothing;
    }

    Log.Write(Tag, $"  grip picks: {picks.Count}");

    _pendingGripPicks   = picks.ToArray();
    _pendingDocSerial   = doc.RuntimeSerialNumber;
    _pendingIdleHandler = OnIdleLaunch;
    RhinoApp.Idle      += _pendingIdleHandler;
    return Result.Success;
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

    // Enable grips for each target curve and select the endpoint grip.
    int selectedCount = 0;
    foreach (var (id, isStart) in picks)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;

      // Turn on control-point grips.
      obj.GripsOn = true;
      obj.CommitChanges();

      var grips = obj.GetGrips();
      if (grips == null || grips.Length == 0) continue;

      var curve = obj.Geometry as Curve;
      if (curve == null) continue;

      // Find the grip nearest to the target endpoint position.
      var targetPt = isStart ? curve.PointAtStart : curve.PointAtEnd;
      GripObject? best = null;
      double bestD = double.MaxValue;
      foreach (var grip in grips)
      {
        var d = grip.CurrentLocation.DistanceTo(targetPt);
        if (d < bestD) { bestD = d; best = grip; }
      }

      if (best == null) continue;
      best.Select(true);
      selectedCount++;

      Log.Write(Tag,
        $"  grip selected: {id} {( isStart ? "start" : "end" )} gripDist={bestD:G4}");
    }

    doc.Views.Redraw();

    if (selectedCount == 0)
    {
      Log.Write(Tag, "  no grips could be selected");
      RhinoApp.WriteLine("vSetPt: failed to select endpoint grips.");
      return;
    }

    Log.Write(Tag, $"  launching -SetPt with {selectedCount} grip(s) selected");

    // Delegate to -SetPt; pre-selected grips bypass the "Select points" step
    // so the user only has to click the target location.
    // XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No are the desired defaults.
    var ok = RhinoApp.RunScript(
      "_-SetPt _XSet=_Yes _YSet=_Yes _ZSet=_Yes _Alignment=_World _Copy=_No", false);

    Log.Write(Tag, $"  -SetPt result={ok}");

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    // Silently re-run vSetPt so pressing Enter repeats this command, not -SetPt.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vSetPt", false);
    _restartingAfterDelegate = false;
  }
}
