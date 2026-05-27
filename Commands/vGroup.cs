using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Groups selected objects by closed-curve boundaries.
/// Boundaries are detected by joining all selected curves with
/// <see cref="Curve.JoinCurves"/>: any joined result that is closed and
/// planar becomes a boundary, so open or overlapping curves that together
/// enclose a region are handled correctly.
/// Every selected object whose representative point falls inside a boundary
/// is added to that boundary's group.
/// </summary>
public sealed class vGroup : Command
{
  /// <summary>Rhino command name.</summary>
  public override string EnglishName => "vGroup";

  /// <summary>
  /// Joins all selected curves, uses any closed planar results as
  /// boundaries, and groups each selected object with the boundary that
  /// contains it.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select objects to group by closed curve boundary");
    go.GroupSelect = true;
    go.SubObjectSelect = false;
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    var tol = doc.ModelAbsoluteTolerance;

    // Collect all selected objects and extract curves for joining.
    var allIds   = new List<Guid>();
    var allCurves = new List<Curve>();

    foreach (var objRef in go.Objects())
    {
      var id  = objRef.ObjectId;
      allIds.Add(id);

      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is Curve c)
        allCurves.Add(c);
    }

    // Split all curves at their mutual intersections, then join the segments.
    // Handles both curves connected end-to-end AND curves that cross each other.
    var boundaries = new List<(Curve Curve, Plane Plane)>();

    if (allCurves.Count > 0)
    {
      // Collect split parameters per curve index.
      var splitParams = new Dictionary<int, List<double>>();
      for (int i = 0; i < allCurves.Count; i++)
      for (int j = i + 1; j < allCurves.Count; j++)
      {
        var events = Intersection.CurveCurve(allCurves[i], allCurves[j], tol, tol);
        if (events == null) continue;
        foreach (var e in events)
        {
          if (!e.IsPoint) continue;
          if (!splitParams.ContainsKey(i)) splitParams[i] = new List<double>();
          if (!splitParams.ContainsKey(j)) splitParams[j] = new List<double>();
          splitParams[i].Add(e.ParameterA);
          splitParams[j].Add(e.ParameterB);
        }
      }

      // Split each curve at its intersection parameters.
      var segments = new List<Curve>();
      for (int i = 0; i < allCurves.Count; i++)
      {
        if (!splitParams.TryGetValue(i, out var parms) || parms.Count == 0)
        {
          segments.Add(allCurves[i]);
          continue;
        }
        var splits = allCurves[i].Split(parms);
        if (splits != null && splits.Length > 0)
          segments.AddRange(splits);
        else
          segments.Add(allCurves[i]);
      }

      // Join all segments; keep closed planar results as boundaries.
      var joined = Curve.JoinCurves(segments, tol);
      if (joined != null)
      {
        foreach (var j in joined)
        {
          if (j != null && j.IsClosed && j.TryGetPlane(out var pln, tol))
            boundaries.Add((j, pln));
        }
      }
    }

    if (boundaries.Count == 0)
    {
      var sb = new System.Text.StringBuilder("No closed planar boundary found after joining curves.");
      foreach (var c in allCurves)
        sb.Append($" [{c.GetType().Name} IsClosed={c.IsClosed} TryGetPlane={c.TryGetPlane(out _, tol)}]");
      Log.Write(EnglishName, sb.ToString());
      RhinoApp.WriteLine("vGroup: no closed planar boundary found in selection.");
      return Result.Nothing;
    }

    // For each boundary, collect selected objects whose representative
    // point is inside or on the boundary (segment curves land Coincident).
    int groupCount = 0;

    foreach (var (bound, plane) in boundaries)
    {
      var members = new List<Guid>();

      foreach (var id in allIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        var pt = RepresentativePoint(obj);
        if (pt == null)
          continue;

        var containment = bound.Contains(pt.Value, plane, tol);
        if (containment is PointContainment.Inside or PointContainment.Coincident)
          members.Add(id);
      }

      if (members.Count < 2)
        continue;

      var grpIdx = doc.Groups.Add();
      foreach (var id in members)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        obj.Attributes.AddToGroup(grpIdx);
        obj.CommitChanges();
      }

      groupCount++;
    }

    if (groupCount == 0)
      RhinoApp.WriteLine("vGroup: no enclosed objects found.");
    else
      RhinoApp.WriteLine($"vGroup: created {groupCount} group{(groupCount == 1 ? "" : "s")}.");

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Returns a single representative 3-D point for an object used to test
  /// containment inside a closed planar curve:
  /// <list type="bullet">
  ///   <item>Point object → its location.</item>
  ///   <item>Curve → mid-domain point.</item>
  ///   <item>Everything else → bounding-box centre.</item>
  /// </list>
  /// Returns <see langword="null"/> when no valid point can be determined.
  /// </summary>
  private static Point3d? RepresentativePoint(RhinoObject obj)
  {
    var geo = obj.Geometry;
    if (geo == null)
      return null;

    if (geo is Point pt)
      return pt.Location;

    if (geo is Curve c)
      return c.PointAt(c.Domain.Mid);

    var bbox = geo.GetBoundingBox(accurate: true);
    return bbox.IsValid ? bbox.Center : null;
  }
}
