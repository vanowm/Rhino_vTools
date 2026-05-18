using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vTrimOff : Command
{
  public override string EnglishName => "vTrimOff";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var tol = doc.ModelAbsoluteTolerance;

    var go = new GetObject();
    go.SetCommandPrompt("Select curves to trim. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    while (true)
    {
      go.GetMultiple(1, 0);
      if (go.CommandResult() != Result.Success)
        return go.CommandResult();

      if (go.ObjectsWerePreselected)
      {
        go.EnablePreSelect(false, true);
        continue;
      }

      break;
    }

    var objRefs = new List<ObjRef>();
    var curves  = new List<Curve>();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
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

    var boundary = DetectBoundary(curves, plane, tol);
    if (boundary.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no enclosed region found.");
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

