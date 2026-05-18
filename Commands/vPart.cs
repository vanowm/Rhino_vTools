using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Captures a closed perimeter (selected curves, optionally gap-bridged) and
/// all visible curves inside it (trimmed at the boundary), then lets the user
/// pick a placement point with a full DynamicDraw preview.  Originals are not
/// modified; the Part is added as new objects at the chosen location.
/// </summary>
public sealed class vPart : Command
{
  public override string EnglishName => "vPart";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var tol = doc.ModelAbsoluteTolerance;

    // ── 1. Select perimeter curves ─────────────────────────────────────────

    var go = new GetObject();
    go.SetCommandPrompt("Select perimeter curves. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    if (go.ObjectsWerePreselected)
    {
      for (var i = 0; i < go.ObjectCount; i++)
        go.Object(i).Object()?.Select(true);

      go = new GetObject();
      go.SetCommandPrompt("Select perimeter curves. Press Enter when done");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.GroupSelect = true;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.EnablePreSelect(false, false);
      go.AcceptNothing(true);

      go.GetMultiple(1, 0);
      if (go.CommandResult() != Result.Success)
        return go.CommandResult();
    }

    var perimIds   = new HashSet<Guid>();
    var perimCrvs  = new List<Curve>();
    var perimAttr  = new ObjectAttributes();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      if (r.Curve() is { } crv)
      {
        perimIds.Add(r.ObjectId);
        perimCrvs.Add(crv.DuplicateCurve());
        if (i == 0)
          perimAttr = r.Object()?.Attributes?.Duplicate() ?? new ObjectAttributes();
      }
    }

    if (perimCrvs.Count == 0)
    {
      RhinoApp.WriteLine("vPart: no curves selected.");
      return Result.Nothing;
    }

    // ── 2. Build closed perimeter (join + gap-bridge) ──────────────────────

    var perimeter = BuildClosedPerimeter(perimCrvs, tol);
    if (perimeter == null)
    {
      RhinoApp.WriteLine("vPart: could not form a closed perimeter from selected curves.");
      return Result.Nothing;
    }

    // ── 3. View plane for containment testing ──────────────────────────────

    var vp    = doc.Views.ActiveView?.ActiveViewport;
    var plane = Plane.WorldXY;
    if (vp != null && vp.GetCameraFrame(out var camFrame))
      plane = new Plane(Point3d.Origin, camFrame.XAxis, camFrame.YAxis);

    // ── 4. Collect inside curves (trimmed at perimeter boundary) ──────────

    var insidePairs = CollectInsideCurves(doc, perimIds, perimeter, plane, tol);

    // ── 5. Assemble Part items ─────────────────────────────────────────────

    var partItems = new List<(Curve Crv, ObjectAttributes Attr)>
    {
      (perimeter, perimAttr)
    };
    partItems.AddRange(insidePairs);

    // ── 6. Base point = bounding box center ───────────────────────────────

    var bbox = perimeter.GetBoundingBox(true);
    foreach (var (c, _) in insidePairs)
    {
      var b = c.GetBoundingBox(true);
      if (b.IsValid) bbox = bbox.IsValid ? BoundingBox.Union(bbox, b) : b;
    }
    var basePoint = bbox.IsValid ? bbox.Center : perimeter.PointAtStart;

    // ── 7. DynamicDraw preview + placement ────────────────────────────────

    var previewItems = partItems.Select(p =>
    {
      var layer = doc.Layers[p.Attr.LayerIndex];
      var color = layer?.Color ?? Color.Cyan;
      return (Crv: p.Crv.DuplicateCurve(), Color: color);
    }).ToList();

    var gp = new GetPoint();
    gp.SetCommandPrompt("Pick placement point for Part (Esc to cancel)");
    gp.DynamicDraw += (_, e) =>
    {
      var xform = Transform.Translation(e.CurrentPoint - basePoint);
      foreach (var (crv, color) in previewItems)
      {
        var draw = crv.DuplicateCurve();
        if (draw == null) continue;
        draw.Transform(xform);
        e.Display.DrawCurve(draw, color, 2);
      }
    };

    var gpResult = gp.Get();
    if (gpResult != GetResult.Point)
      return Result.Cancel;

    // ── 8. Commit ─────────────────────────────────────────────────────────

    var translation = Transform.Translation(gp.Point() - basePoint);
    foreach (var (crv, attr) in partItems)
    {
      var placed = crv.DuplicateCurve();
      if (placed == null) continue;
      placed.Transform(translation);
      doc.Objects.AddCurve(placed, attr);
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Joins curves into a single closed loop.  If endpoints don't quite meet,
  /// bridges the nearest open-endpoint pairs with straight line segments
  /// (within tol×200) before a final re-join.
  /// </summary>
  private static Curve? BuildClosedPerimeter(List<Curve> curves, double tol)
  {
    // Single closed curve → use directly
    if (curves.Count == 1 && curves[0].IsClosed)
      return curves[0].DuplicateCurve();

    // Try joining at 10× model tolerance
    var joined = Curve.JoinCurves(curves.ToArray(), tol * 10);
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return joined[0];

    // Collect remaining open pieces from the join result (or originals)
    var pieces = (joined ?? curves.Select(c => c.DuplicateCurve()).ToArray())
      .Select(c => c.DuplicateCurve()).ToList();

    // Build list of open endpoints
    var openEnds = new List<(int CrvIdx, bool IsStart, Point3d Pt)>();
    for (var i = 0; i < pieces.Count; i++)
    {
      if (pieces[i].IsClosed) continue;
      openEnds.Add((i, true,  pieces[i].PointAtStart));
      openEnds.Add((i, false, pieces[i].PointAtEnd));
    }

    // Greedily bridge closest endpoint pairs from different curves
    var used    = new HashSet<int>();
    var bridges = new List<Curve>();
    for (var i = 0; i < openEnds.Count; i++)
    {
      if (used.Contains(i)) continue;
      var bestDist = tol * 200;
      var bestJ    = -1;
      for (var j = i + 1; j < openEnds.Count; j++)
      {
        if (used.Contains(j)) continue;
        if (openEnds[j].CrvIdx == openEnds[i].CrvIdx) continue;
        var d = openEnds[i].Pt.DistanceTo(openEnds[j].Pt);
        if (d > tol && d < bestDist) { bestDist = d; bestJ = j; }
      }
      if (bestJ >= 0)
      {
        bridges.Add(new LineCurve(openEnds[i].Pt, openEnds[bestJ].Pt));
        used.Add(i);
        used.Add(bestJ);
      }
    }

    if (bridges.Count > 0)
    {
      pieces.AddRange(bridges);
      var reJoined = Curve.JoinCurves(pieces.ToArray(), tol * 10);
      if (reJoined != null && reJoined.Length == 1 && reJoined[0].IsClosed)
        return reJoined[0];
    }

    // Last resort: try with 100× tolerance on all pieces + bridges
    var allPieces = pieces.ToArray();
    var final = Curve.JoinCurves(allPieces, tol * 100);
    if (final != null && final.Length == 1 && final[0].IsClosed)
      return final[0];

    return null;
  }

  /// <summary>
  /// Returns all visible curves in the document (excluding selected perimeter
  /// objects) that are wholly or partly inside the perimeter.  Curves that
  /// cross the perimeter are split; only the inside segments are returned.
  /// </summary>
  private static List<(Curve Crv, ObjectAttributes Attr)> CollectInsideCurves(
    RhinoDoc doc, HashSet<Guid> excludeIds, Curve perimeter, Plane plane, double tol)
  {
    var result   = new List<(Curve, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.Curve,
      VisibleFilter    = true,
      DeletedObjects   = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (excludeIds.Contains(obj.Id)) continue;
      if (obj.Geometry is not Curve crv) continue;

      var srcAttr    = obj.Attributes.Duplicate();
      var splitParams = new SortedSet<double>();

      var events = Intersection.CurveCurve(crv, perimeter, tol, tol);
      if (events != null)
        foreach (var ev in events)
        {
          if (ev.IsOverlap)
          {
            splitParams.Add(ev.OverlapA.T0);
            splitParams.Add(ev.OverlapA.T1);
          }
          else
          {
            splitParams.Add(ev.ParameterA);
          }
        }

      if (splitParams.Count == 0)
      {
        var mid = crv.PointAtNormalizedLength(0.5);
        if (IsInsideOrOn(mid, boundary, plane, tol))
          result.Add((crv.DuplicateCurve(), srcAttr));
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
            result.Add((seg, srcAttr));
        }
      }
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
