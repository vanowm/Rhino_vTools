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
/// Parts of each curve that lie outside the boundary are removed; interior parts are kept intact.
/// </summary>
public sealed class vTrimOff : Command
{
  public override string EnglishName => "vTrimOff";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to trim");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.GetMultiple(2, 0);

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
        curves.Add(crv.DuplicateCurve()); // duplicate geometry before doc objects are deleted
      }
    }

    if (curves.Count < 2)
    {
      RhinoApp.WriteLine("vTrimOff: select at least 2 curves.");
      return Result.Nothing;
    }

    var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    var tol = doc.ModelAbsoluteTolerance;

    // Find the combined outer boundary of all enclosed regions.
    // combineRegions:true merges all adjacent finite regions into a single outer boundary loop.
    var regions = Curve.CreateBooleanRegions(curves, plane, combineRegions: true, tol);

    if (regions == null || regions.RegionCount == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no enclosed region found in the selected curves.");
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

    // For each input curve: split it at every crossing with the boundary,
    // then keep only the sub-segments whose midpoint lies inside or on the boundary.
    var keepCurves = new List<Curve>();

    foreach (var crv in curves)
    {
      var splitParams = new SortedSet<double>();

      foreach (var bc in boundary)
      {
        var events = Intersection.CurveCurve(crv, bc, tol, tol);
        if (events == null) continue;
        foreach (var e in events)
        {
          if (e.IsOverlap)
          {
            splitParams.Add(e.OverlapA.T0);
            splitParams.Add(e.OverlapA.T1);
          }
          else
          {
            splitParams.Add(e.ParameterA);
          }
        }
      }

      if (splitParams.Count == 0)
      {
        // Curve does not cross the boundary — keep it only if it is entirely inside.
        var testPt = crv.PointAtNormalizedLength(0.5);
        if (IsInsideOrOn(testPt, boundary, plane, tol))
          keepCurves.Add(crv);
      }
      else
      {
        var segments = crv.Split(splitParams);
        if (segments == null) continue;
        foreach (var seg in segments)
        {
          if (seg.GetLength() < tol) continue; // skip degenerate zero-length artifacts
          var mid = seg.PointAtNormalizedLength(0.5);
          if (IsInsideOrOn(mid, boundary, plane, tol))
            keepCurves.Add(seg);
        }
      }
    }

    if (keepCurves.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no curves remain inside the boundary.");
      return Result.Nothing;
    }

    var attr = objRefs[0].Object()?.Attributes?.Duplicate() ?? new ObjectAttributes();

    foreach (var objRef in objRefs)
      doc.Objects.Delete(objRef.ObjectId, true);

    foreach (var crv in keepCurves)
      doc.Objects.AddCurve(crv, attr);

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>Returns true if pt lies strictly inside or on any of the closed boundary curves.</summary>
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
