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

    // If preselection was consumed silently, re-prompt on the same go instance so the user
    // can add/remove curves and adjust MaxGap before pressing Enter to confirm.
    if (go.ObjectsWerePreselected)
    {
      // Re-select visually. GetOption (used below) does not touch selection state,
      // so objects remain highlighted while the user reviews/adjusts options.
      for (var i = 0; i < go.ObjectCount; i++)
        doc.Objects.Select(go.Object(i).ObjectId, true);

      var confirm = new GetOption();
      confirm.SetCommandPrompt($"vTrimOff ({go.ObjectCount} curves) — press Enter or adjust options");
      confirm.AcceptNothing(true);

      var optMaxGap2 = new OptionDouble(_maxGap.Value, true, 0.0);
      confirm.AddOptionDouble("MaxGap", ref optMaxGap2);

      GetResult r2;
      while ((r2 = confirm.Get()) == GetResult.Option)
        _maxGap = optMaxGap2.CurrentValue;

      if (r2 == GetResult.Cancel) return Result.Cancel;
    }

    var objRefs = new List<ObjRef>();
    var curves = new List<Curve>();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef.Curve() is { } crv)
      {
        objRefs.Add(objRef);
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

