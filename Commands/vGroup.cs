using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Groups selected objects by closed-curve boundaries: each closed planar
/// curve in the selection becomes the boundary of a new group that contains
/// itself and every other selected object whose representative point lies
/// inside it.
/// </summary>
public sealed class vGroup : Command
{
  /// <summary>Rhino command name.</summary>
  public override string EnglishName => "vGroup";

  /// <summary>
  /// For every closed planar curve in the selection, creates a group
  /// containing that curve and all other selected objects whose
  /// representative point falls inside it.
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

    // Separate closed planar curves (boundaries) from everything else.
    var boundaries = new List<(Guid Id, Curve Curve, Plane Plane)>();
    var allIds = new List<Guid>();

    var tol = doc.ModelAbsoluteTolerance;

    foreach (var objRef in go.Objects())
    {
      var id = objRef.ObjectId;
      allIds.Add(id);

      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is Curve c
          && c.IsClosed
          && c.IsPlanar(tol)
          && c.TryGetPlane(out var pln, tol))
      {
        boundaries.Add((id, c, pln));
      }
    }

    if (boundaries.Count == 0)
    {
      RhinoApp.WriteLine("vGroup: no closed planar curves found in selection.");
      return Result.Nothing;
    }

    // For each boundary, collect the objects whose representative point
    // is inside it (other closed curves are also tested for containment).
    int groupCount = 0;

    foreach (var (boundId, bound, plane) in boundaries)
    {
      var members = new List<Guid> { boundId };

      foreach (var id in allIds)
      {
        if (id == boundId)
          continue;

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
        continue; // nothing inside this boundary — skip

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
