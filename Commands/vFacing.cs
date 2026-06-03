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
/// Accepts multiple curves per role (base / side1 / side2) and a chamfer piece.
/// All output curves are placed on the same layer as their corresponding input.
/// The offset curve inherits the layer of the base.
/// </summary>
public sealed class vFacing : Command
{
  private const string SectionName = "vFacing";
  private const string SizeKey     = "size";

  private static double _size = 3.0;

  public override string EnglishName => "vFacing";

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, SizeKey, out var v)) _size = v;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section => { section[SizeKey] = _size; });

  // ── Entry point ──────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var tol = doc.ModelAbsoluteTolerance;

    // 1. Pick curves (each carries its original layer index)
    if (!TryPickCurves(doc, out var selectedCurves))
      return Result.Cancel;

    // 2. Analyze topology → base, side1, side2 (each as a list of curve+layer)
    if (!TryAnalyzeTopology(doc, selectedCurves, tol,
          out var baseParts, out var side1Parts, out var side2Parts))
    {
      foreach (var (id, _, _) in selectedCurves)
        doc.Objects.FindId(id)?.Select(false);
      doc.Views.Redraw();
      return Result.Nothing;
    }

    // 3. Build facing pieces; boundaryCurve is used only for inside-object collection
    var cplane = ActiveCPlane(doc);
    if (!BuildFacingPieces(baseParts!, side1Parts!, side2Parts!,
          _size, tol, cplane,
          out var outPieces, out var boundaryCurve))
    {
      RhinoApp.WriteLine("vFacing: could not build facing boundary. Check that sides are long enough.");
      return Result.Nothing;
    }

    // 4. Collect inside objects
    var excludeIds = new HashSet<Guid>(selectedCurves.Select(c => c.Id));
    var viewPlane = ViewPlane(doc);
    var inside    = CollectInsideObjects(doc, excludeIds, boundaryCurve!, viewPlane, tol);

    // 5. Build item list: boundary pieces (each on its original layer) + inside
    var items = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    foreach (var (crv, layerIdx) in outPieces!)
      items.Add((crv, new ObjectAttributes { LayerIndex = layerIdx }));
    items.AddRange(inside);

    // 6. Deselect source curves
    foreach (var (id, _, _) in selectedCurves)
      doc.Objects.FindId(id)?.Select(false);
    doc.Views.Redraw();

    // 7. Placement: DynamicDraw preview + pick point
    var bbox      = BoundingBox.Empty;
    foreach (var (crv, _) in outPieces!) bbox.Union(crv.GetBoundingBox(true));
    var basePoint = bbox.IsValid ? bbox.Center : outPieces![0].Crv.PointAtStart;

    var previewGeoms  = items
      .Select(p => (Geom: p.Geom.Duplicate()!,
                    Color: (p.Attr.LayerIndex < doc.Layers.Count
                            ? doc.Layers[p.Attr.LayerIndex]?.Color : null) ?? Color.Cyan))
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

  private static bool TryPickCurves(RhinoDoc doc, out List<(Guid Id, Curve Crv, int Layer)> result)
  {
    result = new List<(Guid, Curve, int)>();

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
      var r   = go.Object(i);
      var obj = r.Object();
      if (r.Curve() is { } crv)
        result.Add((r.ObjectId, crv.DuplicateCurve(),
                    obj?.Attributes.LayerIndex ?? doc.Layers.CurrentLayerIndex));
    }

    return result.Count > 0;
  }

  // ── Topology analysis ────────────────────────────────────────────────────

  private static bool TryAnalyzeTopology(
    RhinoDoc doc,
    List<(Guid Id, Curve Crv, int Layer)> input,
    double tol,
    out List<(Curve Crv, int Layer)>? baseParts,
    out List<(Curve Crv, int Layer)>? side1Parts,
    out List<(Curve Crv, int Layer)>? side2Parts)
  {
    baseParts = side1Parts = side2Parts = null;

    var crvs   = input.Select(t => t.Crv).ToArray();
    var layers = input.Select(t => t.Layer).ToArray();

    Log.Write("vFacing", $"TryAnalyzeTopology: {input.Count} input curve(s), tol={tol:G4}");
    for (var _i = 0; _i < input.Count; _i++)
      Log.Write("vFacing", $"  [{_i}] layer={layers[_i]} len={crvs[_i].GetLength():F3} " +
                            $"start=({crvs[_i].PointAtStart.X:F3},{crvs[_i].PointAtStart.Y:F3}) " +
                            $"end=({crvs[_i].PointAtEnd.X:F3},{crvs[_i].PointAtEnd.Y:F3})");

    // Single closed curve: split at corners, let user pick base
    if (crvs.Length == 1 && crvs[0].IsClosed)
    {
      if (!TryAnalyzeClosedCurve(doc, crvs[0], tol, out var bCrv, out var s1Crv, out var s2Crv))
        return false;
      baseParts  = new List<(Curve, int)> { (bCrv!,  layers[0]) };
      side1Parts = new List<(Curve, int)> { (s1Crv!, layers[0]) };
      side2Parts = new List<(Curve, int)> { (s2Crv!, layers[0]) };
      return true;
    }

    // PRIMARY: group curves by layer index.
    // Curves on the same layer belong to the same role (base / side1 / side2 / chamfer).
    var layerGroups = input
      .GroupBy(t => t.Layer)
      .Select(g => g.Select(t => (t.Crv, t.Layer)).ToList())
      .ToList();

    Log.Write("vFacing", $"Layer-based groups: {layerGroups.Count}");
    for (var _i = 0; _i < layerGroups.Count; _i++)
      Log.Write("vFacing", $"  group[{_i}] layer={layerGroups[_i][0].Layer} count={layerGroups[_i].Count}");

    // FALLBACK: all curves on the same layer.
    if (layerGroups.Count == 1)
    {
      if (input.Count == 3)
      {
        // Exactly 3 — split into individual groups and let topology detection work.
        Log.Write("vFacing", "All curves on same layer (3 curves) — treating each as separate group");
        layerGroups = input
          .Select(t => new List<(Curve Crv, int Layer)> { (t.Crv, t.Layer) })
          .ToList();
      }
      else if (input.Count > 3)
      {
        // More than 3 curves on the same layer — ask user to identify the 2 side curves.
        Log.Write("vFacing", $"All curves on same layer ({input.Count} curves) — asking user to pick 2 sides");
        if (!TryPickSideCurves(doc, input, out var sideIdxs))
          return false;
        var s1i = sideIdxs![0];
        var s2i = sideIdxs![1];
        var baseGroup = input
          .Where((_, i) => i != s1i && i != s2i)
          .Select(t => (t.Crv, t.Layer))
          .ToList();
        var s1Group = new List<(Curve Crv, int Layer)> { (input[s1i].Crv, input[s1i].Layer) };
        var s2Group = new List<(Curve Crv, int Layer)> { (input[s2i].Crv, input[s2i].Layer) };
        layerGroups = new List<List<(Curve Crv, int Layer)>> { baseGroup, s1Group, s2Group };
      }
      else
      {
        RhinoApp.WriteLine($"vFacing: need at least 3 curves, got {input.Count}.");
        return false;
      }
    }

    // 4 groups: one is likely a chamfer piece — merge the single-curve group that
    // shares an endpoint with exactly one of the non-chamfer groups.
    if (layerGroups.Count == 4)
    {
      var joinedForMerge = layerGroups.Select(g => JoinChain(g.Select(p => p.Crv).ToArray(), tol)).ToArray();
      var chamferIdx = -1;

      // Prefer: single-curve group whose joined chain is not the base (shares endpoint with only 1 other)
      for (var _i = 0; _i < 4 && chamferIdx < 0; _i++)
      {
        if (layerGroups[_i].Count != 1) continue;
        var shareCount = 0;
        for (var _j = 0; _j < 4; _j++)
          if (_j != _i && SharesEndpoint(joinedForMerge[_i], joinedForMerge[_j], tol))
            shareCount++;
        Log.Write("vFacing", $"  group[{_i}] single-curve, shareCount={shareCount}");
        if (shareCount == 1) chamferIdx = _i;
      }

      // Fallback: shortest group by total length
      if (chamferIdx < 0)
      {
        chamferIdx = Enumerable.Range(0, 4)
          .OrderBy(i => layerGroups[i].Sum(p => p.Crv.GetLength()))
          .First();
        Log.Write("vFacing", $"  chamfer fallback (shortest): group[{chamferIdx}]");
      }

      // Find the adjacent group (shares endpoint with chamfer)
      var adjIdx = -1;
      for (var _j = 0; _j < 4; _j++)
      {
        if (_j == chamferIdx) continue;
        if (SharesEndpoint(joinedForMerge[chamferIdx], joinedForMerge[_j], tol))
        { adjIdx = _j; break; }
      }

      if (adjIdx >= 0)
      {
        Log.Write("vFacing", $"  merging chamfer group[{chamferIdx}] into group[{adjIdx}]");
        layerGroups[adjIdx].AddRange(layerGroups[chamferIdx]);
        layerGroups.RemoveAt(chamferIdx);
      }
      else
      {
        Log.Write("vFacing", "  chamfer merge: no adjacent group found, proceeding with 4 groups");
      }
    }

    if (layerGroups.Count != 3)
    {
      RhinoApp.WriteLine(
        $"vFacing: need curves on exactly 3 layers (base + 2 sides), found {layerGroups.Count} layer group(s). " +
        "Place base curves on one layer and each side on its own layer.");
      return false;
    }

    // Build joined chain per group for topology detection
    var chains = layerGroups.Select(g => JoinChain(g.Select(p => p.Crv).ToArray(), tol)).ToArray();
    for (var _i = 0; _i < 3; _i++)
      Log.Write("vFacing", $"  chain[{_i}] layer={layerGroups[_i][0].Layer} " +
                            $"start=({chains[_i].PointAtStart.X:F3},{chains[_i].PointAtStart.Y:F3}) " +
                            $"end=({chains[_i].PointAtEnd.X:F3},{chains[_i].PointAtEnd.Y:F3})");

    // Base = chain whose endpoints each connect to one of the other two chains
    var baseIdx = -1;
    for (var i = 0; i < 3; i++)
    {
      var j = (i + 1) % 3;
      var k = (i + 2) % 3;
      var ij = SharesEndpoint(chains[i], chains[j], tol);
      var ik = SharesEndpoint(chains[i], chains[k], tol);
      Log.Write("vFacing", $"  SharesEndpoint([{i}],[{j}])={ij}  SharesEndpoint([{i}],[{k}])={ik}");
      if (ij && ik) { baseIdx = i; break; }
    }

    if (baseIdx < 0)
    {
      RhinoApp.WriteLine("vFacing: could not identify the base curve. " +
                         "Ensure its endpoints each connect to one side.");
      return false;
    }

    Log.Write("vFacing", $"  base=group[{baseIdx}]");
    var s1Idx = (baseIdx + 1) % 3;
    var s2Idx = (baseIdx + 2) % 3;

    var bChain  = chains[baseIdx];
    var s1Chain = chains[s1Idx];
    var s2Chain = chains[s2Idx];
    OrientSides(ref bChain, ref s1Chain, ref s2Chain, tol);

    baseParts  = OrderChain(layerGroups[baseIdx], bChain,  tol);
    side1Parts = OrderChain(layerGroups[s1Idx],   s1Chain, tol);
    side2Parts = OrderChain(layerGroups[s2Idx],   s2Chain, tol);
    return true;
  }

  /// <summary>
  /// Prompts the user to click 2 side curves from the already-selected set.
  /// Returns the indices into <paramref name="input"/> of the two chosen curves.
  /// </summary>
  private static bool TryPickSideCurves(
    RhinoDoc doc,
    List<(Guid Id, Curve Crv, int Layer)> input,
    out List<int>? sideIndices)
  {
    sideIndices = null;
    var allowedIds = new HashSet<Guid>(input.Select(t => t.Id));

    // Deselect all so the user starts from a clean state.
    foreach (var (id, _, _) in input)
      doc.Objects.FindId(id)?.Select(false);
    doc.Views.Redraw();

    var go = new GetObject();
    go.SetCommandPrompt("Select 2 side curves connected to the base");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.SetCustomGeometryFilter((rhObj, geom, componentIndex) => allowedIds.Contains(rhObj.Id));

    var res = go.GetMultiple(2, 2);

    // Re-select original curves regardless of result.
    foreach (var (id, _, _) in input)
      doc.Objects.FindId(id)?.Select(true);
    doc.Views.Redraw();

    if (go.CommandResult() != Result.Success || go.ObjectCount != 2)
      return false;

    var picked = new List<int>();
    for (var i = 0; i < 2; i++)
    {
      var idx = input.FindIndex(t => t.Id == go.Object(i).ObjectId);
      if (idx < 0) return false;
      picked.Add(idx);
    }

    sideIndices = picked;
    return true;
  }

  private static Curve JoinChain(Curve[] crvs, double tol)
  {
    if (crvs.Length == 1) return crvs[0].DuplicateCurve();
    var joined = Curve.JoinCurves(crvs, tol * 2);
    return (joined != null && joined.Length == 1) ? joined[0] : crvs[0].DuplicateCurve();
  }

  private static List<(Curve Crv, int Layer)> OrderChain(
    List<(Curve Crv, int Layer)> parts, Curve reference, double tol)
  {
    if (parts.Count == 1)
    {
      var (c, l) = parts[0];
      var dup = c.DuplicateCurve();
      if (dup.PointAtStart.DistanceTo(reference.PointAtStart) >
          dup.PointAtEnd.DistanceTo(reference.PointAtStart))
        dup.Reverse();
      return new List<(Curve, int)> { (dup, l) };
    }
    var eps       = tol * 2;
    var remaining = parts.Select(p => (Crv: p.Crv.DuplicateCurve(), p.Layer)).ToList();
    var ordered   = new List<(Curve, int)>();
    var current   = reference.PointAtStart;
    while (remaining.Count > 0)
    {
      var found = -1; var flip = false;
      for (var i = 0; i < remaining.Count; i++)
      {
        if (remaining[i].Crv.PointAtStart.DistanceTo(current) < eps) { found = i; flip = false; break; }
        if (remaining[i].Crv.PointAtEnd.DistanceTo(current)   < eps) { found = i; flip = true;  break; }
      }
      if (found < 0) break;
      var (crv, layer) = remaining[found];
      remaining.RemoveAt(found);
      if (flip) crv.Reverse();
      ordered.Add((crv, layer));
      current = crv.PointAtEnd;
    }
    ordered.AddRange(remaining);
    return ordered;
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
      Log.Write("vFacing", $"FindCornerParams got {curve.GetType().Name}, not PolyCurve — no corners detected");
      return result;
    }

    var n     = poly.SegmentCount;
    var count = poly.IsClosed ? n : n - 1;
    Log.Write("vFacing", $"PolyCurve segments={n} IsClosed={poly.IsClosed} checking {count} junction(s) (min angle={minAngleDeg}°)");

    for (var i = 0; i < count; i++)
    {
      var j    = (i + 1) % n;
      var seg1 = poly.SegmentCurve(i);
      var seg2 = poly.SegmentCurve(j);
      if (seg1 == null || seg2 == null)
      {
        Log.Write("vFacing", $"  junction[{i}→{j}]: null segment, skipped");
        continue;
      }

      var t1 = seg1.TangentAtEnd;
      var t2 = seg2.TangentAtStart;

      var angleDeg = Vector3d.VectorAngle(t1, t2) * (180.0 / Math.PI);
      Log.Write("vFacing", $"  junction[{i}→{j}]: angle={angleDeg:F1}° {(angleDeg >= minAngleDeg ? "✓ CORNER" : "(skip)")} seg1={seg1.GetType().Name} seg2={seg2.GetType().Name}");

      if (angleDeg < minAngleDeg) continue;

      if (i < n - 1)
      {
        var dom = poly.SegmentDomain(i);
        result.Add(dom.Max);
      }
    }

    Log.Write("vFacing", $"FindCornerParams → {result.Count} corner(s) found");
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

  // ── Boundary construction (multi-curve, layer-aware) ────────────────────

  /// <summary>
  /// Builds the 4-piece facing boundary from multi-curve base + side chains.
  /// Each output piece carries the layer of its corresponding input.
  /// Also returns a joined closed boundaryCurve for inside-object collection.
  /// </summary>
  private static bool BuildFacingPieces(
    List<(Curve Crv, int Layer)> baseParts,
    List<(Curve Crv, int Layer)> side1Parts,
    List<(Curve Crv, int Layer)> side2Parts,
    double size, double tol, Plane cplane,
    out List<(Curve Crv, int Layer)>? outPieces,
    out Curve? boundaryCurve)
  {
    outPieces = null; boundaryCurve = null;

    var baseLayer  = baseParts.Count  > 0 ? baseParts[0].Layer  : 0;
    var side1Layer = side1Parts.Count > 0 ? side1Parts[0].Layer : 0;
    var side2Layer = side2Parts.Count > 0 ? side2Parts[0].Layer : 0;

    var baseJoined = JoinChain(baseParts.Select(p  => p.Crv).ToArray(), tol);
    var s1Joined   = JoinChain(side1Parts.Select(p => p.Crv).ToArray(), tol);
    var s2Joined   = JoinChain(side2Parts.Select(p => p.Crv).ToArray(), tol);

    OrientSides(ref baseJoined, ref s1Joined, ref s2Joined, tol);

    var sideCenter  = (s1Joined.PointAtEnd + s2Joined.PointAtEnd) / 2.0;
    var offsetCurve = OffsetTowardPoint(baseJoined, size, sideCenter, tol, cplane);
    if (offsetCurve == null)
    {
      RhinoApp.WriteLine("vFacing: could not offset base curve.");
      return false;
    }

    var extLen = Math.Max(baseJoined.GetLength(), s1Joined.GetLength() + s2Joined.GetLength()) + size * 10;
    var s1Ext  = TryExtend(s1Joined,    CurveEnd.End,  extLen) ?? s1Joined.DuplicateCurve();
    var s2Ext  = TryExtend(s2Joined,    CurveEnd.End,  extLen) ?? s2Joined.DuplicateCurve();
    var offExt = TryExtend(offsetCurve, CurveEnd.Both, extLen) ?? offsetCurve.DuplicateCurve();

    double t1s1, t1off, t2s2, t2off;
    var ev1 = Intersection.CurveCurve(s1Ext, offExt, tol, tol);
    if (ev1 != null && ev1.Count > 0)
      (t1s1, t1off) = (ev1[0].ParameterA, ev1[0].ParameterB);
    else
    {
      if (!offExt.ClosestPoint(s1Joined.PointAtEnd, out t1off)) return false;
      if (!s1Ext.ClosestPoint(offExt.PointAt(t1off), out t1s1)) return false;
    }

    var ev2 = Intersection.CurveCurve(s2Ext, offExt, tol, tol);
    if (ev2 != null && ev2.Count > 0)
      (t2s2, t2off) = (ev2[0].ParameterA, ev2[0].ParameterB);
    else
    {
      if (!offExt.ClosestPoint(s2Joined.PointAtEnd, out t2off)) return false;
      if (!s2Ext.ClosestPoint(offExt.PointAt(t2off), out t2s2)) return false;
    }

    var s1Trimmed = s1Ext.Trim(s1Ext.Domain.Min, t1s1);
    var s2Trimmed = s2Ext.Trim(s2Ext.Domain.Min, t2s2);
    if (s1Trimmed == null || s2Trimmed == null) return false;

    var offTrimmed = offExt.Trim(Math.Min(t1off, t2off), Math.Max(t1off, t2off));
    if (offTrimmed == null) return false;

    if (offTrimmed.PointAtStart.DistanceTo(s2Trimmed.PointAtEnd) >
        offTrimmed.PointAtEnd.DistanceTo(s2Trimmed.PointAtEnd))
      offTrimmed.Reverse();

    var s1Rev = s1Trimmed.DuplicateCurve();
    s1Rev.Reverse();

    // Joined boundary for CollectInsideObjects only
    var bndPieces = new Curve[] { baseJoined, s2Trimmed!, offTrimmed!, s1Rev };
    var bnd = Curve.JoinCurves(bndPieces, tol * 10);
    if (bnd == null || bnd.Length != 1 || !bnd[0].IsClosed)
      bnd = Curve.JoinCurves(bndPieces, tol * 100);
    if (bnd == null || bnd.Length != 1 || !bnd[0].IsClosed) return false;
    boundaryCurve = bnd[0];

    // Output: base, side2 trimmed, offset (on base layer), side1 trimmed+reversed
    outPieces = new List<(Curve, int)>
    {
      (baseJoined,   baseLayer),
      (s2Trimmed!,   side2Layer),
      (offTrimmed!,  baseLayer),
      (s1Rev,        side1Layer),
    };
    return true;
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
