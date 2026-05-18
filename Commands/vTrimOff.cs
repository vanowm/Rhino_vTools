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

    var optMaxGap = new OptionDouble(_maxGap.Value, true, 0.0);
    go.AddOptionDouble("MaxGap", ref optMaxGap);

    while (go.GetMultiple(2, 0) == GetResult.Option)
      _maxGap = optMaxGap.CurrentValue;

    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

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

    // Build the working set: original curves + synthesised gap bridges.
    // allObjRefs[i] == null means curve i is a bridge (not in the document).
    var allCurves = new List<Curve>(curves);
    var allObjRefs = new List<ObjRef?>();
    foreach (var r in objRefs) allObjRefs.Add(r);

    // Attribute template for bridge lines: inherit first object's layer/colour.
    var bridgeAttr = objRefs[0].Object()?.Attributes?.Duplicate() ?? new ObjectAttributes();

    if (maxGap > tol)
    {
      var before = allCurves.Count;
      AddGapBridges(curves, maxGap, tol, allCurves, allObjRefs);
      var added = allCurves.Count - before;
      if (added > 0)
        RhinoApp.WriteLine($"vTrimOff: bridged {added} gap{(added == 1 ? "" : "s")}.");
    }

    // Find the combined outer boundary of all enclosed regions.
    var regions = Curve.CreateBooleanRegions(allCurves, plane, combineRegions: true, tol);

    if (regions == null || regions.RegionCount == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no enclosed region found. Try increasing MaxGap.");
      return Result.Nothing;
    }

    var boundary = new List<Curve>();
    for (var r = 0; r < regions.RegionCount; r++)
    {
      var rc = regions.RegionCurves(r);
      if (rc == null) continue;
      foreach (var c in rc)
        if (c != null && c.IsClosed) boundary.Add(c);
    }

    if (boundary.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: failed to extract closed boundary.");
      return Result.Failure;
    }

    // For each curve (original or bridge): split at boundary crossings, keep inside/on parts.
    var keepPairs = new List<(Curve Curve, ObjectAttributes Attr)>();

    for (var i = 0; i < allCurves.Count; i++)
    {
      var crv = allCurves[i];
      var srcAttr = allObjRefs[i]?.Object()?.Attributes?.Duplicate() ?? bridgeAttr;
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

    // Delete only original doc objects; bridges were never added to the document.
    foreach (var objRef in objRefs)
      doc.Objects.Delete(objRef.ObjectId, true);

    foreach (var (crv, attr) in keepPairs)
      doc.Objects.AddCurve(crv, attr);

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// For every pair of endpoints on different curves within (tol, maxGap], adds a straight
  /// bridge LineCurve to allCurves (with a null allObjRefs entry).
  /// </summary>
  private static void AddGapBridges(
    List<Curve> curves, double maxGap, double tol,
    List<Curve> allCurves, List<ObjRef?> allObjRefs)
  {
    for (var i = 0; i < curves.Count; i++)
    {
      var a0 = curves[i].PointAtStart;
      var a1 = curves[i].PointAtEnd;
      for (var j = i + 1; j < curves.Count; j++)
      {
        var b0 = curves[j].PointAtStart;
        var b1 = curves[j].PointAtEnd;
        TryBridge(a0, b0, maxGap, tol, allCurves, allObjRefs);
        TryBridge(a0, b1, maxGap, tol, allCurves, allObjRefs);
        TryBridge(a1, b0, maxGap, tol, allCurves, allObjRefs);
        TryBridge(a1, b1, maxGap, tol, allCurves, allObjRefs);
      }
    }
  }

  private static void TryBridge(
    Point3d a, Point3d b, double maxGap, double tol,
    List<Curve> allCurves, List<ObjRef?> allObjRefs)
  {
    var dist = a.DistanceTo(b);
    if (dist > tol && dist <= maxGap)
    {
      allCurves.Add(new LineCurve(a, b));
      allObjRefs.Add(null);
    }
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

