using System;
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
/// Creates a facing piece from selected base + side curves.
/// Offsets the base by Size toward the sides, closes the boundary,
/// captures inside objects, and places the group at a user-picked point.
/// </summary>
public sealed class vFacing : Command
{
  private const string SectionName = "vFacing";
  private const string SizeKey     = "size";

  private static double _size = 3.0;

  public override string EnglishName => "vFacing";

  private static void LoadOptions() =>
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetDouble(section, SizeKey, out var v)) _size = v;
      return 0;
    });

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section => { section[SizeKey] = _size; });

  // ── Entry point ──────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var tol = doc.ModelAbsoluteTolerance;

    // 1. Pick curves
    if (!TryPickCurves(doc, out var selectedCurves))
      return Result.Cancel;

    // 2. Analyze topology → base, side1, side2 (oriented: side starts at base endpoint)
    if (!TryAnalyzeTopology(doc, selectedCurves, tol,
          out var baseCurve, out var side1, out var side2))
    {
      foreach (var (id, _) in selectedCurves)
        doc.Objects.FindId(id)?.Select(false);
      doc.Views.Redraw();
      return Result.Nothing;
    }

    // 3. Offset base + extend sides → closed boundary
    var cplane = ActiveCPlane(doc);
    var boundary = BuildFacingBoundary(baseCurve!, side1!, side2!, _size, tol, cplane);
    if (boundary == null)
    {
      RhinoApp.WriteLine("vFacing: could not build facing boundary. Check that sides are long enough.");
      return Result.Nothing;
    }

    // 4. Collect inside objects (trimmed to boundary, like vPart)
    var excludeIds = new HashSet<Guid>(selectedCurves.Select(c => c.Id));
    var viewPlane = ViewPlane(doc);
    var inside    = CollectInsideObjects(doc, excludeIds, boundary, viewPlane, tol);

    // 5. Build item list: boundary + inside
    var currentAttr = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };
    var items = new List<(GeometryBase Geom, ObjectAttributes Attr)>
    {
      (boundary, currentAttr.Duplicate())
    };
    items.AddRange(inside);

    // 6. Deselect source curves
    foreach (var (id, _) in selectedCurves)
      doc.Objects.FindId(id)?.Select(false);
    doc.Views.Redraw();

    // 7. Placement: DynamicDraw preview + pick point
    var bbox      = boundary.GetBoundingBox(true);
    var basePoint = bbox.IsValid ? bbox.Center : boundary.PointAtStart;

    var currentColor  = doc.Layers[doc.Layers.CurrentLayerIndex]?.Color ?? Color.Cyan;
    var previewGeoms  = items
      .Select(p => (Geom: p.Geom.Duplicate()!, Color: doc.Layers[p.Attr.LayerIndex]?.Color ?? currentColor))
      .Where(p => p.Geom != null)
      .ToList();

    var sizeOpt = new OptionDouble(_size, 0.001, double.MaxValue);
    var gp      = new GetPoint();
    gp.SetCommandPrompt("Pick placement point for Facing");
    gp.AddOptionDouble("Size", ref sizeOpt);
    gp.DynamicDraw += (_, e) =>
    {
      var xf = Transform.Translation(e.CurrentPoint - basePoint);
      foreach (var (geom, color) in previewGeoms)
      {
        var d = geom.Duplicate();
        if (d == null) continue;
        d.Transform(xf);
        DrawPreview(e.Display, d, color);
      }
    };

    GetResult gpRes;
    do
    {
      gpRes = gp.Get();
      if (gpRes == GetResult.Option)
      {
        _size = sizeOpt.CurrentValue;
        SaveOptions();
      }
    }
    while (gpRes == GetResult.Option);

    if (gpRes != GetResult.Point)
      return Result.Cancel;

    // 8. Commit
    var xform    = Transform.Translation(gp.Point() - basePoint);
    var addedIds = new List<Guid>();

    foreach (var (geom, attr) in items)
    {
      var placed = geom.Duplicate();
      if (placed == null) continue;
      placed.Transform(xform);
      attr.RemoveFromAllGroups();
      var id = AddToDoc(doc, placed, attr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    if (addedIds.Count > 1)
    {
      var grp = doc.Groups.Add();
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        obj.Attributes.AddToGroup(grp);
        obj.CommitChanges();
      }
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Selection ────────────────────────────────────────────────────────────

  private static bool TryPickCurves(RhinoDoc doc, out List<(Guid Id, Curve Crv)> result)
  {
    result = new List<(Guid, Curve)>();

    var sizeOpt = new OptionDouble(_size, 0.001, double.MaxValue);
    var go      = new GetObject();
    go.SetCommandPrompt("Select facing curves (base + two sides). Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = false;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.AlreadySelectedObjectSelect = true;
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    while (true)
    {
      go.ClearCommandOptions();
      go.AddOptionDouble("Size", ref sizeOpt);

      var res = go.GetMultiple(1, 0);
      _size = sizeOpt.CurrentValue;

      if (res == GetResult.Option) continue;
      if (go.CommandResult() != Result.Success) { SaveOptions(); return false; }
      if (go.ObjectsWerePreselected) { go.EnablePreSelect(false, false); continue; }
      break;
    }

    SaveOptions();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      if (r.Curve() is { } crv)
        result.Add((r.ObjectId, crv.DuplicateCurve()));
    }

    return result.Count > 0;
  }

  // ── Topology analysis ────────────────────────────────────────────────────

  private static bool TryAnalyzeTopology(
    RhinoDoc doc,
    List<(Guid Id, Curve Crv)> input,
    double tol,
    out Curve? baseCurve,
    out Curve? side1,
    out Curve? side2)
  {
    baseCurve = null; side1 = null; side2 = null;

    var crvs = input.Select(c => c.Crv).ToArray();

    // Special case: single closed curve object already closed
    if (crvs.Length == 1 && crvs[0].IsClosed)
      return TryAnalyzeClosedCurve(doc, crvs[0], tol, out baseCurve, out side1, out side2);

    // Join into connected chains
    var joined = Curve.JoinCurves(crvs, tol * 10);
    if (joined == null || joined.Length == 0)
    {
      RhinoApp.WriteLine("vFacing: could not join selected curves — check connectivity.");
      return false;
    }

    joined = joined.Where(c => c.GetLength() > tol).ToArray();

    RhinoApp.WriteLine($"vFacing debug: JoinCurves → {joined.Length} chain(s)");
    for (var _di = 0; _di < joined.Length; _di++)
      RhinoApp.WriteLine($"  chain[{_di}]: type={joined[_di].GetType().Name} IsClosed={joined[_di].IsClosed} len={joined[_di].GetLength():F3}");

    if (joined.Length == 1 && joined[0].IsClosed)
      return TryAnalyzeClosedCurve(doc, joined[0], tol, out baseCurve, out side1, out side2);

    if (joined.Length == 1)
      return TryAnalyzeOpenChain(joined[0], tol, out baseCurve, out side1, out side2);

    if (joined.Length == 3)
      return TryAnalyzeThreeChains(joined, tol, out baseCurve, out side1, out side2);

    RhinoApp.WriteLine(
      $"vFacing: expected 1 connected chain or 3 separate chains, got {joined.Length}. " +
      "Select curves that form exactly base + two sides.");
    return false;
  }

  /// <summary>
  /// Open chain: find 2 corners ≥ 30°, middle segment = base.
  /// </summary>
  private static bool TryAnalyzeOpenChain(
    Curve chain, double tol,
    out Curve? baseCurve, out Curve? side1, out Curve? side2)
  {
    baseCurve = null; side1 = null; side2 = null;

    var corners = FindCornerParams(chain, 30.0);
    if (corners.Count != 2)
    {
      RhinoApp.WriteLine(
        $"vFacing: found {corners.Count} corner(s) ≥30° in the selected curves; " +
        "need exactly 2 (one at each end of the base). " +
        "Select base and sides as separate curve objects for cleaner detection.");
      return false;
    }

    corners.Sort();
    var pieces = chain.Split(corners.ToArray());
    if (pieces == null || pieces.Length != 3)
    {
      RhinoApp.WriteLine("vFacing: could not split the curve chain at detected corners.");
      return false;
    }

    // pieces[0] = before first corner (side1, going toward base), needs reversal
    // pieces[1] = between corners (base)
    // pieces[2] = after second corner (side2)
    baseCurve = pieces[1];

    var s1 = pieces[0].DuplicateCurve();
    s1.Reverse(); // now s1 starts at corner1 = base.PointAtStart, ends at free
    side1 = s1;
    side2 = pieces[2]; // starts at corner2 = base.PointAtEnd, ends at free

    return true;
  }

  /// <summary>
  /// Three separate chains: base = the one that connects to both others at its endpoints.
  /// </summary>
  private static bool TryAnalyzeThreeChains(
    Curve[] chains, double tol,
    out Curve? baseCurve, out Curve? side1, out Curve? side2)
  {
    baseCurve = null; side1 = null; side2 = null;

    int baseIdx = -1;
    for (var i = 0; i < 3; i++)
    {
      var j = (i + 1) % 3;
      var k = (i + 2) % 3;
      if (SharesEndpoint(chains[i], chains[j], tol) &&
          SharesEndpoint(chains[i], chains[k], tol))
      {
        baseIdx = i;
        break;
      }
    }

    if (baseIdx < 0)
    {
      RhinoApp.WriteLine("vFacing: could not identify the base curve — " +
                         "make sure base endpoints connect to both sides.");
      return false;
    }

    baseCurve = chains[baseIdx];
    side1     = chains[(baseIdx + 1) % 3];
    side2     = chains[(baseIdx + 2) % 3];

    OrientSides(ref baseCurve, ref side1, ref side2, tol);
    return true;
  }

  /// <summary>
  /// Closed curve: split at corners, show pieces, let user click the base edge.
  /// </summary>
  private static bool TryAnalyzeClosedCurve(
    RhinoDoc doc, Curve closed, double tol,
    out Curve? baseCurve, out Curve? side1, out Curve? side2)
  {
    baseCurve = null; side1 = null; side2 = null;

    var corners = FindCornerParams(closed, 30.0);
    if (corners.Count < 2)
    {
      RhinoApp.WriteLine("vFacing: need at least 2 corners (≥30°) in the closed curve.");
      return false;
    }

    corners.Sort();
    var pieces = closed.Split(corners.ToArray());
    if (pieces == null || pieces.Length < 2)
    {
      RhinoApp.WriteLine("vFacing: could not split closed curve at corner points.");
      return false;
    }

    // Add temporary highlighted objects for the user to click on
    var tempIds  = new List<Guid>();
    var baseAttr = new ObjectAttributes
    {
      ObjectColor  = Color.Yellow,
      ColorSource  = ObjectColorSource.ColorFromObject,
      PlotColorSource = ObjectPlotColorSource.PlotColorFromObject
    };
    foreach (var p in pieces)
      tempIds.Add(doc.Objects.AddCurve(p, baseAttr.Duplicate()));
    doc.Views.Redraw();

    var sizeOpt = new OptionDouble(_size, 0.001, double.MaxValue);
    var goBase  = new GetObject();
    goBase.SetCommandPrompt("Click on the base edge");
    goBase.GeometryFilter = ObjectType.Curve;
    goBase.SubObjectSelect = false;
    goBase.DeselectAllBeforePostSelect = false;
    goBase.EnablePreSelect(false, false);

    // Restrict selection to only our temporary objects
    goBase.SetCustomGeometryFilter((obj, _, _) => tempIds.Contains(obj.Id));

    GetResult goRes;
    do
    {
      goBase.ClearCommandOptions();
      goBase.AddOptionDouble("Size", ref sizeOpt);
      goRes = goBase.Get();
      _size = sizeOpt.CurrentValue;
    }
    while (goRes == GetResult.Option);

    // Remove temporary objects
    foreach (var id in tempIds) doc.Objects.Delete(id, true);
    doc.Views.Redraw();
    SaveOptions();

    if (goRes != GetResult.Object || goBase.ObjectCount == 0)
      return false;

    // Identify selected piece
    var selectedId = goBase.Object(0).ObjectId;
    var baseIdx    = tempIds.IndexOf(selectedId);
    if (baseIdx < 0) return false;

    var n         = pieces.Length;
    baseCurve     = pieces[baseIdx].DuplicateCurve();
    var side1Raw  = pieces[(baseIdx - 1 + n) % n].DuplicateCurve();
    var side2Raw  = pieces[(baseIdx + 1)     % n].DuplicateCurve();

    // Orient sides so they start at the respective base endpoints
    Curve b = baseCurve, s1 = side1Raw, s2 = side2Raw;
    OrientSides(ref b, ref s1, ref s2, tol);
    baseCurve = b; side1 = s1; side2 = s2;
    return true;
  }

  // ── Corner detection ─────────────────────────────────────────────────────

  /// <summary>
  /// Returns curve parameters at junctions where the direction changes by ≥ minAngleDeg.
  /// Works on PolyCurve (from JoinCurves). For a closed PolyCurve includes all junctions.
  /// </summary>
  private static List<double> FindCornerParams(Curve curve, double minAngleDeg)
  {
    var result = new List<double>();

    if (curve is not PolyCurve poly)
    {
      RhinoApp.WriteLine($"vFacing debug: FindCornerParams got {curve.GetType().Name}, not PolyCurve — no corners detected");
      return result;
    }

    var n     = poly.SegmentCount;
    var count = poly.IsClosed ? n : n - 1;
    RhinoApp.WriteLine($"vFacing debug: PolyCurve segments={n} IsClosed={poly.IsClosed} checking {count} junction(s) (min angle={minAngleDeg}°)");

    for (var i = 0; i < count; i++)
    {
      var j    = (i + 1) % n;
      var seg1 = poly.SegmentCurve(i);
      var seg2 = poly.SegmentCurve(j);
      if (seg1 == null || seg2 == null)
      {
        RhinoApp.WriteLine($"  junction[{i}→{j}]: null segment, skipped");
        continue;
      }

      var t1 = seg1.TangentAtEnd;
      var t2 = seg2.TangentAtStart;

      var angleDeg = Vector3d.VectorAngle(t1, t2) * (180.0 / Math.PI);
      RhinoApp.WriteLine($"  junction[{i}→{j}]: angle={angleDeg:F1}° {(angleDeg >= minAngleDeg ? "✓ CORNER" : "(skip)")} seg1={seg1.GetType().Name} seg2={seg2.GetType().Name}");

      if (angleDeg < minAngleDeg) continue;

      if (i < n - 1)
      {
        var dom = poly.SegmentDomain(i);
        result.Add(dom.Max);
      }
    }

    RhinoApp.WriteLine($"vFacing debug: FindCornerParams → {result.Count} corner(s) found");
    return result;
  }

  // ── Orientation helpers ──────────────────────────────────────────────────

  private static bool SharesEndpoint(Curve a, Curve b, double tol)
  {
    var eps = tol * 2;
    return a.PointAtStart.DistanceTo(b.PointAtStart) < eps ||
           a.PointAtStart.DistanceTo(b.PointAtEnd)   < eps ||
           a.PointAtEnd.DistanceTo(b.PointAtStart)   < eps ||
           a.PointAtEnd.DistanceTo(b.PointAtEnd)     < eps;
  }

  /// <summary>
  /// Orients base, side1, side2 so that:
  ///   side1.PointAtStart == base.PointAtStart  (junction A)
  ///   side2.PointAtStart == base.PointAtEnd    (junction B)
  /// </summary>
  private static void OrientSides(ref Curve baseCurve, ref Curve side1, ref Curve side2, double tol)
  {
    var eps = tol * 2;
    var b   = baseCurve.DuplicateCurve();
    var s1  = side1.DuplicateCurve();
    var s2  = side2.DuplicateCurve();

    // Find where side1 connects to base
    Point3d jA;
    if      (s1.PointAtStart.DistanceTo(b.PointAtStart) < eps) jA = b.PointAtStart;
    else if (s1.PointAtEnd.DistanceTo(b.PointAtStart)   < eps) jA = b.PointAtStart;
    else if (s1.PointAtStart.DistanceTo(b.PointAtEnd)   < eps) jA = b.PointAtEnd;
    else                                                         jA = b.PointAtEnd;   // best guess

    // If side1 connects at base.End, reverse base so jA == base.Start
    if (jA.DistanceTo(b.PointAtEnd) < eps)
      b.Reverse();

    // Orient s1 to start at jA (= b.PointAtStart now)
    if (s1.PointAtEnd.DistanceTo(b.PointAtStart) < eps)
      s1.Reverse();

    // Orient s2 to start at b.PointAtEnd
    if (s2.PointAtEnd.DistanceTo(b.PointAtEnd) < eps)
      s2.Reverse();
    // If s2 still starts near b.PointAtStart, reverse it
    if (s2.PointAtStart.DistanceTo(b.PointAtStart) < eps)
      s2.Reverse();

    baseCurve = b; side1 = s1; side2 = s2;
  }

  // ── Boundary construction ────────────────────────────────────────────────

  /// <summary>
  /// Offsets baseCurve toward the sides by size, extends each side to meet the
  /// offset, and joins the four pieces into a closed boundary:
  ///   A→B (base) + B→D' (side2 trimmed) + D'→C' (offset) + C'→A (side1 reversed).
  /// </summary>
  private static Curve? BuildFacingBoundary(
    Curve baseCurve, Curve side1, Curve side2,
    double size, double tol, Plane cplane)
  {
    // Offset base toward side free-end centroid
    var sideCenter = (side1.PointAtEnd + side2.PointAtEnd) / 2.0;
    var offsetCurve = OffsetTowardPoint(baseCurve, size, sideCenter, tol, cplane);
    if (offsetCurve == null)
    {
      RhinoApp.WriteLine("vFacing: could not offset base curve.");
      return null;
    }

    // Generous extension length
    var extLen = Math.Max(baseCurve.GetLength(), side1.GetLength() + side2.GetLength()) + size * 10;

    // Extend side1 beyond free end, extend side2 beyond free end, extend offset beyond both ends
    var s1Ext  = TryExtend(side1,       CurveEnd.End,  extLen) ?? side1.DuplicateCurve();
    var s2Ext  = TryExtend(side2,       CurveEnd.End,  extLen) ?? side2.DuplicateCurve();
    var offExt = TryExtend(offsetCurve, CurveEnd.Both, extLen) ?? offsetCurve.DuplicateCurve();

    // Intersect side1 with offset
    var ev1 = Intersection.CurveCurve(s1Ext, offExt, tol, tol);
    var ev2 = Intersection.CurveCurve(s2Ext, offExt, tol, tol);

    Point3d ptC, ptD;
    double  t1s1, t1off, t2s2, t2off;

    if (ev1 != null && ev1.Count > 0)
    {
      t1s1  = ev1[0].ParameterA;
      t1off = ev1[0].ParameterB;
      ptC   = ev1[0].PointA;
    }
    else
    {
      // Fallback: closest approach between extended side1 free end and offset curve
      if (!offExt.ClosestPoint(side1.PointAtEnd, out t1off))
        return null;
      ptC   = offExt.PointAt(t1off);
      if (!s1Ext.ClosestPoint(ptC, out t1s1))
        return null;
    }

    if (ev2 != null && ev2.Count > 0)
    {
      t2s2  = ev2[0].ParameterA;
      t2off = ev2[0].ParameterB;
      ptD   = ev2[0].PointA;
    }
    else
    {
      if (!offExt.ClosestPoint(side2.PointAtEnd, out t2off))
        return null;
      ptD   = offExt.PointAt(t2off);
      if (!s2Ext.ClosestPoint(ptD, out t2s2))
        return null;
    }

    // Trim side1: base.Start (A) → C'
    var s1Trimmed = s1Ext.Trim(s1Ext.Domain.Min, t1s1);
    if (s1Trimmed == null) return null;

    // Trim side2: base.End (B) → D'
    var s2Trimmed = s2Ext.Trim(s2Ext.Domain.Min, t2s2);
    if (s2Trimmed == null) return null;

    // Trim offset between C' and D' (order param range correctly)
    var offMin = Math.Min(t1off, t2off);
    var offMax = Math.Max(t1off, t2off);
    var offTrimmed = offExt.Trim(offMin, offMax);
    if (offTrimmed == null) return null;

    // Orient offset so it goes from D' to C' (closing side2 → offset → side1_reversed)
    var offForJoin = offTrimmed.DuplicateCurve();
    if (offForJoin.PointAtStart.DistanceTo(ptD) > offForJoin.PointAtEnd.DistanceTo(ptD))
      offForJoin.Reverse();

    // s1 reversed: C' → A
    var s1Rev = s1Trimmed.DuplicateCurve();
    s1Rev.Reverse();

    // Join: base(A→B) + side2(B→D') + offset(D'→C') + side1_rev(C'→A)
    var piecesToJoin = new[]
    {
      baseCurve.DuplicateCurve(),
      s2Trimmed,
      offForJoin,
      s1Rev
    };

    var joined = Curve.JoinCurves(piecesToJoin, tol * 10);
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return joined[0];

    joined = Curve.JoinCurves(piecesToJoin, tol * 100);
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return joined[0];

    return null;
  }

  private static Curve? TryExtend(Curve crv, CurveEnd end, double length)
  {
    try { return crv.Extend(end, length, CurveExtensionStyle.Line); }
    catch { return null; }
  }

  private static Curve? OffsetTowardPoint(
    Curve curve, double distance, Point3d targetPt, double tol, Plane plane)
  {
    var best     = (Curve?)null;
    var bestDist = double.MaxValue;

    foreach (var d in new[] { distance, -distance })
    {
      var offsets = curve.Offset(plane, d, tol, CurveOffsetCornerStyle.Sharp);
      if (offsets == null) continue;
      foreach (var c in offsets)
      {
        if (c == null) continue;
        var dist = c.PointAtNormalizedLength(0.5).DistanceTo(targetPt);
        if (dist < bestDist) { bestDist = dist; best = c; }
      }
    }

    return best;
  }

  // ── Inside-object collection (from vPart) ────────────────────────────────

  private static List<(GeometryBase Geom, ObjectAttributes Attr)> CollectInsideObjects(
    RhinoDoc doc, HashSet<Guid> excludeIds, Curve perimeter, Plane plane, double tol)
  {
    var result   = new List<(GeometryBase, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject,
      VisibleFilter    = true,
      DeletedObjects   = false,
      IncludeGrips     = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj.ObjectType == ObjectType.Grip) continue;
      if (excludeIds.Contains(obj.Id)) continue;
      var geom = obj.Geometry;
      if (geom == null) continue;
      var attr = obj.Attributes.Duplicate();

      if (geom is Curve crv)
      {
        var splitParams = new SortedSet<double>();
        var events = Intersection.CurveCurve(crv, perimeter, tol, tol);
        if (events != null)
          foreach (var ev in events)
          {
            if (ev.IsOverlap) { splitParams.Add(ev.OverlapA.T0); splitParams.Add(ev.OverlapA.T1); }
            else              splitParams.Add(ev.ParameterA);
          }

        if (splitParams.Count == 0)
        {
          if (IsInsideOrOn(crv.PointAtNormalizedLength(0.5), boundary, plane, tol))
            result.Add((crv.DuplicateCurve(), attr));
        }
        else
        {
          var segs = crv.Split(splitParams);
          if (segs == null) continue;
          foreach (var seg in segs)
          {
            if (seg.GetLength() < tol) continue;
            if (IsInsideOrOn(seg.PointAtNormalizedLength(0.5), boundary, plane, tol))
              result.Add((seg, attr));
          }
        }
      }
      else
      {
        var pt = RepresentativePoint(geom);
        if (pt.IsValid && IsInsideOrOn(pt, boundary, plane, tol))
          result.Add((geom.Duplicate()!, attr));
      }
    }

    return result;
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

  private static Point3d RepresentativePoint(GeometryBase geom) => geom switch
  {
    TextEntity te               => te.Plane.Origin,
    TextDot td                  => td.Point,
    Rhino.Geometry.Point pt     => pt.Location,
    _                           => geom.GetBoundingBox(true) is { IsValid: true } bb ? bb.Center : Point3d.Unset
  };

  // ── Doc / display helpers ────────────────────────────────────────────────

  private static void DrawPreview(DisplayPipeline display, GeometryBase geom, Color color)
  {
    switch (geom)
    {
      case Curve c:                       display.DrawCurve(c, color, 2);                      break;
      case TextEntity te:                 display.DrawAnnotation(te, color);                   break;
      case TextDot td:                    display.DrawDot(td, color, Color.Black, color);      break;
      case Rhino.Geometry.Point pt:       display.DrawPoint(pt.Location, color);               break;
      case Mesh m:                        display.DrawMeshWires(m, color);                     break;
      case Brep b:                        display.DrawBrepWires(b, color, 1);                  break;
    }
  }

  private static Guid AddToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr) => geom switch
  {
    Curve c                         => doc.Objects.AddCurve(c, attr),
    TextEntity te                   => doc.Objects.AddText(te, attr),
    TextDot td                      => doc.Objects.AddTextDot(td, attr),
    Rhino.Geometry.Point pt         => doc.Objects.AddPoint(pt.Location, attr),
    Hatch h                         => doc.Objects.AddHatch(h, attr),
    Brep b                          => doc.Objects.AddBrep(b, attr),
    Mesh m                          => doc.Objects.AddMesh(m, attr),
    _                               => doc.Objects.Add(geom, attr)
  };

  private static Plane ActiveCPlane(RhinoDoc doc)
  {
    var view = doc.Views.ActiveView;
    if (view == null) return Plane.WorldXY;
    try
    {
      var p = view.ActiveViewport.ConstructionPlane();
      if (p.IsValid) return p;
    }
    catch { }
    return Plane.WorldXY;
  }

  private static Plane ViewPlane(RhinoDoc doc)
  {
    var view = doc.Views.ActiveView;
    if (view?.ActiveViewport?.GetCameraFrame(out var frame) == true)
      return new Plane(Point3d.Origin, frame.XAxis, frame.YAxis);
    return Plane.WorldXY;
  }
}
