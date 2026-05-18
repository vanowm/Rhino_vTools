using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Trims selected curves to the outer boundary of the enclosed region they collectively form.
/// Parts outside the boundary are removed; interior parts are kept intact.
/// When curves do not quite meet, MaxGap auto-bridges near-endpoint pairs with short lines.
/// </summary>
public sealed class vTrimOff : Command
{
  public override string EnglishName => "vTrimOff";

  // Persists across runs; null = initialise from model tolerance on first call.
  private static double? _maxGap;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var tol = doc.ModelAbsoluteTolerance;
    _maxGap ??= tol * 1000; // sensible first-run default (e.g. 1 mm in a mm model at tol=0.001)

    var go = new GetObject();
    go.SetCommandPrompt("Select curves to trim");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnableClearObjectsOnEntry(false); // retain preselection across multiple GetMultiple calls

    var optMaxGap = new OptionDouble(_maxGap.Value, true, 0.0);
    go.AddOptionDouble("MaxGap", ref optMaxGap);

    // First pass: picks up preselection immediately OR waits for interactive selection.
    GetResult res;
    while ((res = go.GetMultiple(2, 0)) == GetResult.Option)
      _maxGap = optMaxGap.CurrentValue;

    if (res == GetResult.Cancel) return Result.Cancel;

    // Collect the final ObjRef list — either directly (no preselect)
    // or via a second interactive pass that supports add/remove toggle.
    var selectedRefs = new List<ObjRef>();

    if (!go.ObjectsWerePreselected)
    {
      for (var i = 0; i < go.ObjectCount; i++)
        selectedRefs.Add(go.Object(i));
    }
    else
    {
      // Preselection was consumed silently. Re-select in doc so objects stay highlighted,
      // then open a fresh GetObject for modification.
      // Clicking a preselected curve toggles it OFF; clicking a new curve toggles it ON.
      var preRefs = new List<ObjRef>();
      var preIds  = new HashSet<Guid>();
      for (var i = 0; i < go.ObjectCount; i++)
      {
        var r = go.Object(i);
        preRefs.Add(r);
        preIds.Add(r.ObjectId);
        doc.Objects.Select(r.ObjectId, true);
      }

      var go2 = new GetObject();
      go2.SetCommandPrompt($"vTrimOff: {preRefs.Count} curves — click to toggle, press Enter to accept");
      go2.GeometryFilter = ObjectType.Curve;
      go2.SubObjectSelect = false;
      go2.GroupSelect = true;
      go2.EnablePreSelect(false, false);    // don't fire immediately; preselected stays highlighted in doc
      go2.AcceptNothing(true);             // Enter = accept current set
      go2.AlreadySelectedObjectSelect = true; // allow clicking doc-selected (preselected) curves

      var optMaxGap2 = new OptionDouble(_maxGap.Value, true, 0.0);
      go2.AddOptionDouble("MaxGap", ref optMaxGap2);

      GetResult res2;
      while ((res2 = go2.GetMultiple(0, 0)) == GetResult.Option)
        _maxGap = optMaxGap2.CurrentValue;

      if (res2 == GetResult.Cancel) return Result.Cancel;

      // Toggle: clicked preselected = remove; clicked new = add.
      var go2Ids = new HashSet<Guid>();
      for (var i = 0; i < go2.ObjectCount; i++)
        go2Ids.Add(go2.Object(i).ObjectId);

      foreach (var r in preRefs)
        if (!go2Ids.Contains(r.ObjectId)) selectedRefs.Add(r);  // kept
      for (var i = 0; i < go2.ObjectCount; i++)
        if (!preIds.Contains(go2.Object(i).ObjectId)) selectedRefs.Add(go2.Object(i));  // added
    }

    var objRefs = new List<ObjRef>();
    var curves  = new List<Curve>();

    foreach (var r in selectedRefs)
    {
      if (r.Curve() is { } crv)
      {
        objRefs.Add(r);
        curves.Add(crv.DuplicateCurve());
      }
    }

    if (curves.Count < 2)
    {
      RhinoApp.WriteLine("vTrimOff: select at least 2 curves.");
      return Result.Nothing;
    }

    var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    var maxGap = _maxGap.Value;

    // Try exact-tolerance boundary first; fall back to JoinCurves with maxGap.
    var boundary = DetectBoundary(curves, plane, tol);
    if (boundary.Count == 0 && maxGap > tol)
    {
      boundary = JoinBoundary(curves, maxGap);
      if (boundary.Count > 0)
        RhinoApp.WriteLine("vTrimOff: boundary closed via MaxGap.");
    }

    if (boundary.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no enclosed region found. Try increasing MaxGap.");
      return Result.Nothing;
    }

    // Split each original curve against the boundary; keep inside/on segments.
    var keepPairs = new List<(Curve Curve, ObjectAttributes Attr)>();

    for (var i = 0; i < curves.Count; i++)
    {
      var crv = curves[i];
      var srcAttr = objRefs[i].Object()?.Attributes?.Duplicate() ?? new ObjectAttributes();
      var splitParams = new SortedSet<double>();

      foreach (var bc in boundary)
      {
        var events = Intersection.CurveCurve(crv, bc, tol, tol);
        if (events == null) continue;
        foreach (var e in events)
        {
          if (e.IsOverlap) { splitParams.Add(e.OverlapA.T0); splitParams.Add(e.OverlapA.T1); }
          else splitParams.Add(e.ParameterA);
        }
      }

      if (splitParams.Count == 0)
      {
        var testPt = crv.PointAtNormalizedLength(0.5);
        if (IsInsideOrOn(testPt, boundary, plane, tol))
          keepPairs.Add((crv, srcAttr));
      }
      else
      {
        var segments = crv.Split(splitParams);
        if (segments == null) continue;
        foreach (var seg in segments)
        {
          if (seg.GetLength() < tol) continue;
          var mid = seg.PointAtNormalizedLength(0.5);
          if (IsInsideOrOn(mid, boundary, plane, tol))
            keepPairs.Add((seg, srcAttr));
        }
      }
    }

    if (keepPairs.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no curves remain inside the boundary.");
      return Result.Nothing;
    }

    foreach (var objRef in objRefs)
      doc.Objects.Delete(objRef.ObjectId, true);

    foreach (var (crv, attr) in keepPairs)
      doc.Objects.AddCurve(crv, attr);

    doc.Views.Redraw();
    return Result.Success;
  }

  // Returns closed boundary curves from CreateBooleanRegions at exact model tolerance.
  private static List<Curve> DetectBoundary(List<Curve> curves, Plane plane, double tol)
  {
    var result = new List<Curve>();
    var regions = Curve.CreateBooleanRegions(curves, plane, combineRegions: true, tol);
    if (regions == null) return result;
    for (var r = 0; r < regions.RegionCount; r++)
    {
      var rc = regions.RegionCurves(r);
      if (rc == null) continue;
      foreach (var c in rc)
        if (c != null && c.IsClosed) result.Add(c);
    }
    return result;
  }

  // Joins curves with gap tolerance = maxGap; returns any closed results as the boundary.
  private static List<Curve> JoinBoundary(List<Curve> curves, double maxGap)
  {
    var result = new List<Curve>();
    var joined = Curve.JoinCurves(curves, maxGap);
    if (joined == null) return result;
    foreach (var c in joined)
      if (c != null && c.IsClosed) result.Add(c);
    return result;
  }

  private static bool IsInsideOrOn(Point3d pt, List<Curve> closed, Plane plane, double tol)
  {
    foreach (var c in closed)
    {
      var r = c.Contains(pt, plane, tol);
      if (r == PointContainment.Inside || r == PointContainment.Coincident)
        return true;
    }
    return false;
  }
}

