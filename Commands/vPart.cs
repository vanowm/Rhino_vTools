using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Captures a closed perimeter (selected curves, optionally gap-bridged) and
/// all visible objects inside it — curves are trimmed at the boundary; text,
/// dots, points, and other types are included whole when their representative
/// point falls inside.  The original perimeter curves are kept as individual
/// output segments on their own layers.  Gap-bridge segments go on the current
/// layer.  The user picks a placement point with a full DynamicDraw preview.
/// Originals are not modified; the Part is added as new objects.
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

    // Collect perimeter curves — keep each with its own attributes
    var perimIds  = new HashSet<Guid>();
    var perimList = new List<(Curve Crv, ObjectAttributes Attr)>();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      if (r.Curve() is { } crv)
      {
        perimIds.Add(r.ObjectId);
        perimList.Add((crv.DuplicateCurve(), r.Object()?.Attributes?.Duplicate() ?? new ObjectAttributes()));
      }
    }

    if (perimList.Count == 0)
    {
      RhinoApp.WriteLine("vPart: no curves selected.");
      return Result.Nothing;
    }

    // ── 2. Build closed perimeter for containment testing ─────────────────────
    //  Returns the joined closed curve (internal use only) + any bridge
    //  segments that filled gaps (these go into the output Part).

    var (perimeter, bridges) = BuildClosedPerimeter(perimList.Select(p => p.Crv).ToList(), tol);
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

    // ── 4. Collect inside objects (all visible types, trimmed for curves) ──

    var insideObjects = CollectInsideObjects(doc, perimIds, perimeter, plane, tol);

    // ── 5. Assemble Part items ─────────────────────────────────────────────────────
    //  • Original perimeter curves — each on its own layer
    //  • Bridge segments (gap fillers) — on current doc layer
    //  • Inside objects — on their original layers

    var currentLayerAttr = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };

    var partItems = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    foreach (var (crv, attr) in perimList)
      partItems.Add((crv, attr));
    foreach (var bridge in bridges)
      partItems.Add((bridge, currentLayerAttr.Duplicate()));
    partItems.AddRange(insideObjects);

    // ── 6. Base point = bounding box center of perimeter ─────────────────

    var bbox = perimeter.GetBoundingBox(true);
    foreach (var (geom, _) in insideObjects)
    {
      var b = geom.GetBoundingBox(true);
      if (b.IsValid) bbox = bbox.IsValid ? BoundingBox.Union(bbox, b) : b;
    }
    var basePoint = bbox.IsValid ? bbox.Center : perimeter.PointAtStart;

    // ── 7. DynamicDraw preview + placement ────────────────────────────────

    var previewItems = partItems.Select(p =>
    {
      var layer = doc.Layers[p.Attr.LayerIndex];
      var color  = layer?.Color ?? Color.Cyan;
      return (Geom: p.Geom.Duplicate()!, Color: color);
    }).Where(p => p.Geom != null).ToList();

    var gp = new GetPoint();
    gp.SetCommandPrompt("Pick placement point for Part (Esc to cancel)");
    gp.DynamicDraw += (_, e) =>
    {
      var xform = Transform.Translation(e.CurrentPoint - basePoint);
      foreach (var (geom, color) in previewItems)
      {
        var draw = geom.Duplicate();
        if (draw == null) continue;
        draw.Transform(xform);
        DrawPreview(e.Display, draw, color);
      }
    };

    var gpResult = gp.Get();
    if (gpResult != GetResult.Point)
      return Result.Cancel;

    // ── 8. Commit ─────────────────────────────────────────────────────────

    var translation = Transform.Translation(gp.Point() - basePoint);
    foreach (var (geom, attr) in partItems)
    {
      var placed = geom.Duplicate();
      if (placed == null) continue;
      placed.Transform(translation);
      AddObjectToDoc(doc, placed, attr);
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a single closed loop for containment testing.  Does NOT join the
  /// originals — returns them untouched.  Gap-bridging segments are returned
  /// separately so the caller can include them in the output Part.
  /// </summary>
  private static (Curve? Closed, List<LineCurve> Bridges) BuildClosedPerimeter(
    List<Curve> curves, double tol)
  {
    var bridges = new List<LineCurve>();

    // Single closed curve → nothing to join or bridge
    if (curves.Count == 1 && curves[0].IsClosed)
      return (curves[0].DuplicateCurve(), bridges);

    // Build a working copy for containment-testing join (originals stay intact)
    var pieces = curves.Select(c => c.DuplicateCurve()).ToList();

    // Greedily bridge nearest open-endpoint pairs across different curves
    var openEnds = new List<(int CrvIdx, bool IsStart, Point3d Pt)>();
    for (var i = 0; i < pieces.Count; i++)
    {
      if (pieces[i].IsClosed) continue;
      openEnds.Add((i, true,  pieces[i].PointAtStart));
      openEnds.Add((i, false, pieces[i].PointAtEnd));
    }

    var used = new HashSet<int>();
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
        var bridge = new LineCurve(openEnds[i].Pt, openEnds[bestJ].Pt);
        bridges.Add(bridge);
        pieces.Add(bridge);
        used.Add(i);
        used.Add(bestJ);
      }
    }

    // Join copies (only for containment testing)
    var joined = Curve.JoinCurves(pieces.ToArray(), tol * 10);
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return (joined[0], bridges);

    var final = Curve.JoinCurves(pieces.ToArray(), tol * 100);
    if (final != null && final.Length == 1 && final[0].IsClosed)
      return (final[0], bridges);

    return (null, bridges);
  }

  /// <summary>
  /// Returns all visible objects (excluding selected perimeter IDs) whose
  /// content falls inside the closed perimeter.
  /// • Curves: split at the perimeter boundary, keep inside segments.
  /// • All other types (text, dots, points, hatches, …): included whole when
  ///   the representative point is inside.
  /// </summary>
  private static List<(GeometryBase Geom, ObjectAttributes Attr)> CollectInsideObjects(
    RhinoDoc doc, HashSet<Guid> excludeIds, Curve perimeter, Plane plane, double tol)
  {
    var result   = new List<(GeometryBase, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject,
      VisibleFilter    = true,
      DeletedObjects   = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (excludeIds.Contains(obj.Id)) continue;
      var geom = obj.Geometry;
      if (geom == null) continue;

      var attr = obj.Attributes.Duplicate();

      if (geom is Curve crv)
      {
        // Curves: split at perimeter and keep inside segments
        var splitParams = new SortedSet<double>();
        var events = Intersection.CurveCurve(crv, perimeter, tol, tol);
        if (events != null)
          foreach (var ev in events)
          {
            if (ev.IsOverlap) { splitParams.Add(ev.OverlapA.T0); splitParams.Add(ev.OverlapA.T1); }
            else               splitParams.Add(ev.ParameterA);
          }

        if (splitParams.Count == 0)
        {
          if (IsInsideOrOn(crv.PointAtNormalizedLength(0.5), boundary, plane, tol))
            result.Add((crv.DuplicateCurve(), attr));
        }
        else
        {
          var segments = crv.Split(splitParams);
          if (segments == null) continue;
          foreach (var seg in segments)
          {
            if (seg.GetLength() < tol) continue;
            if (IsInsideOrOn(seg.PointAtNormalizedLength(0.5), boundary, plane, tol))
              result.Add((seg, attr));
          }
        }
      }
      else
      {
        // All other types: test a representative point for containment
        var testPt = RepresentativePoint(geom);
        if (testPt.IsValid && IsInsideOrOn(testPt, boundary, plane, tol))
          result.Add((geom.Duplicate()!, attr));
      }
    }

    return result;
  }

  /// <summary>Returns the most meaningful single point for containment testing.</summary>
  private static Point3d RepresentativePoint(GeometryBase geom)
  {
    switch (geom)
    {
      case TextEntity te:              return te.Plane.Origin;
      case TextDot td:                 return td.Point;
      case Rhino.Geometry.Point pt:    return pt.Location;
      default:
        var bb = geom.GetBoundingBox(true);
        return bb.IsValid ? bb.Center : Point3d.Unset;
    }
  }

  private static bool IsInsideOrOn(Point3d pt, List<Curve> closed, Plane plane, double tol)
  {
    if (!pt.IsValid) return false;
    foreach (var c in closed)
    {
      var r = c.Contains(pt, plane, tol);
      if (r == PointContainment.Inside || r == PointContainment.Coincident)
        return true;
    }
    return false;
  }

  private static void DrawPreview(DisplayPipeline display, GeometryBase geom, Color color)
  {
    switch (geom)
    {
      case Curve c:                          display.DrawCurve(c, color, 2);                        break;
      case TextEntity te:                    display.DrawAnnotation(te, color);                     break;
      case TextDot td:                       display.DrawDot(td, color, Color.Black, color);        break;
      case Rhino.Geometry.Point pt:          display.DrawPoint(pt.Location, color);                 break;
      case Mesh m:                           display.DrawMeshWires(m, color);                       break;
      case Brep b:                           display.DrawBrepWires(b, color, 1);                    break;
    }
  }

  private static void AddObjectToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr)
  {
    switch (geom)
    {
      case Curve c:                          doc.Objects.AddCurve(c, attr);                         break;
      case TextEntity te:                    doc.Objects.AddText(te, attr);                         break;
      case TextDot td:                       doc.Objects.AddTextDot(td, attr);                      break;
      case Rhino.Geometry.Point pt:          doc.Objects.AddPoint(pt.Location, attr);               break;
      case Hatch h:                          doc.Objects.AddHatch(h, attr);                         break;
      case Brep b:                           doc.Objects.AddBrep(b, attr);                          break;
      case Mesh m:                           doc.Objects.AddMesh(m, attr);                          break;
      default:                               doc.Objects.Add(geom, attr);                           break;
    }
  }
}
