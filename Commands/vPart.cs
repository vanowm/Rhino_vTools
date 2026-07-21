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

  private const string SectionName  = "vPart";
  private const string GroupKey     = "group";
  private const string JoinPerimKey = "joinPerim";

  // Persisted option defaults
  private static bool _group    = false;
  private static bool _joinPerim = false;

  // ── Logging ────────────────────────────────────────────────────────────
  private static void L(string message) => vTools.Log.Write("vPart", message);

  private static string Short(Guid id) => id.ToString()[..8];

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetBool(section, GroupKey,     out var g)) _group    = g;
      if (ToolsOptionStore.TryGetBool(section, JoinPerimKey, out var j)) _joinPerim = j;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[GroupKey]     = _group;
      section[JoinPerimKey] = _joinPerim;
    });

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var tol = doc.ModelAbsoluteTolerance;
    L($"=== vPart {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    L($"  tol={tol:G4}  group={_group}  joinPerim={_joinPerim}");

    // ── 1. Select perimeter curves ─────────────────────────────────────────
    // Dale's pattern: single GetObject instance with EnableClearObjectsOnEntry(false)
    // so the accumulated list persists across GetMultiple calls.
    // AlreadySelectedObjectSelect = true lets the user re-click a deselected object
    // within the same session to add it back.

    var groupToggle    = new OptionToggle(_group,    "No", "Yes");
    var joinPerimToggle = new OptionToggle(_joinPerim, "No", "Yes");

    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select perimeter curves. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = false;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.AlreadySelectedObjectSelect = true;
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);
    go.AddOptionToggle("Group",         ref groupToggle);
    go.AddOptionToggle("JoinPerimeter", ref joinPerimToggle);

    var selIter = 0;
    while (true)
    {
      var selRes = go.GetMultiple(0, 0);
      L($"sel iter {++selIter}: result={selRes}  cmdResult={go.CommandResult()}  count={go.ObjectCount}  preselected={go.ObjectsWerePreselected}");

      if (selRes == GetResult.Option)
      {
        _group = groupToggle.CurrentValue;
        _joinPerim = joinPerimToggle.CurrentValue;
        SaveOptions();
        continue;
      }

      if (go.CommandResult() != Result.Success)
      {
        for (var i = 0; i < go.ObjectCount; i++) go.Object(i).Object()?.Select(false);
        doc.Views.Redraw();
        L("cancelled");
        return Result.Cancel;
      }

      if (go.ObjectsWerePreselected)
      {
        go.EnablePreSelect(false, false);
        continue;
      }

      break;
    }

    var collectedIds = new HashSet<Guid>();
    var collectedMap = new Dictionary<Guid, ObjRef>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      if (collectedIds.Add(r.ObjectId)) collectedMap[r.ObjectId] = r;
      L($"  sel[{i}]: {Short(r.ObjectId)}");
    }

    L($"final collection: {collectedIds.Count} curve(s)");
    foreach (var id in collectedIds) L($"  collected: {Short(id)}");

    // Collect perimeter curves — keep each with its own attributes
    var perimIds  = new HashSet<Guid>();
    var perimList = new List<(Curve Crv, ObjectAttributes Attr)>();

    foreach (var (id, r) in collectedMap)
    {
      if (r.Curve() is { } crv)
      {
        perimIds.Add(id);
        perimList.Add((crv.DuplicateCurve(), r.Object()?.Attributes?.Duplicate() ?? new ObjectAttributes()));
      }
    }

    L($"perimList: {perimList.Count} curve(s)");
    if (perimList.Count == 0)
    {
      RhinoApp.WriteLine("vPart: no curves selected.");
      L("EXIT: no curves");
      return Result.Nothing;
    }

    // Deselect source curves now that we have them captured
    foreach (var r in collectedMap.Values) r.Object()?.Select(false);
    doc.Views.Redraw();

    // ── 2. View plane (needed for perimeter detection and containment testing) ──

    var vp    = doc.Views.ActiveView?.ActiveViewport;
    var plane = Plane.WorldXY;
    if (vp != null && vp.GetCameraFrame(out var camFrame))
      plane = new Plane(Point3d.Origin, camFrame.XAxis, camFrame.YAxis);

    // ── 3. Build closed perimeter for containment testing ─────────────────────
    //  Uses CreateBooleanRegions first (handles curves extending past corners),
    //  then falls back to endpoint joining + gap bridging.

    var perimLog = new List<string>();
    var (perimeter, bridges, perimeterCurves, boundaryTolerance) = BuildClosedPerimeter(
      perimList.Select(p => p.Crv).ToList(), plane, tol, perimLog);
    L($"BuildClosedPerimeter: {(perimeter != null ? "OK" : "FAILED")}  bridges={bridges.Count}  boundaryTol={boundaryTolerance:G6}");
    foreach (var entry in perimLog) L($"  perim: {entry}");
    if (perimeter == null)
    {
      RhinoApp.WriteLine("vPart: could not form a closed perimeter from selected curves.");
      L("EXIT: no closed perimeter");
      return Result.Nothing;
    }

    // ── 4. Collect inside objects (all visible types, trimmed for curves) ──

    var insideObjects = CollectInsideObjects(doc, perimIds, perimeter, plane, boundaryTolerance);
    L($"insideObjects: {insideObjects.Count}");

    // ── 5. Assemble Part items ─────────────────────────────────────────────────────
    //  • Original perimeter curves trimmed to the closed boundary — each on its own layer
    //  • Bridge segments (gap fillers) — on current doc layer
    //  • Inside objects — on their original layers

    var currentLayerAttr = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };

    var effectivePerimeter = perimList
      .Select((item, index) => (
        Crv: index < perimeterCurves.Count ? perimeterCurves[index] : item.Crv,
        item.Attr))
      .ToList();

    var trimmedPerimeter = TrimToPerimeter(
      effectivePerimeter, perimeter!, plane, boundaryTolerance).ToList();
    L($"trimmedPerimeter: {trimmedPerimeter.Count} piece(s) from {effectivePerimeter.Count} selected curve(s)");

    var partItems = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    foreach (var (crv, attr) in trimmedPerimeter)
      partItems.Add((crv, attr));
    foreach (var bridge in bridges)
      partItems.Add((bridge, currentLayerAttr.Duplicate()));
    partItems.AddRange(insideObjects);
    L($"partItems: {partItems.Count} ({perimList.Count} perim + {bridges.Count} bridges + {insideObjects.Count} inside)");

    // ── 6. Base point = bounding box center of perimeter ─────────────────

    var bbox = perimeter.GetBoundingBox(true);
    foreach (var (geom, _) in insideObjects)
    {
      var b = geom.GetBoundingBox(true);
      if (b.IsValid) bbox = bbox.IsValid ? BoundingBox.Union(bbox, b) : b;
    }
    var basePoint = bbox.IsValid ? bbox.Center : perimeter.PointAtStart;

    // ── 7. DynamicDraw preview + placement ────────────────────────────────

    // Precompute preview lists for both join states; DynamicDraw reads the toggle live.
    var currentColor = doc.Layers[doc.Layers.CurrentLayerIndex]?.Color ?? Color.Cyan;

    var splitPreview = partItems
      .Select(p => (Geom: p.Geom.Duplicate()!, Color: doc.Layers[p.Attr.LayerIndex]?.Color ?? Color.Cyan))
      .Where(p => p.Geom != null)
      .ToList();

    var joinedPreview = new List<(GeometryBase Geom, Color Color)>();
    joinedPreview.Add((perimeter!.Duplicate()!, currentColor));
    foreach (var bridge in bridges)
      joinedPreview.Add((bridge.Duplicate()!, currentColor));
    foreach (var (geom, attr) in insideObjects)
      joinedPreview.Add((geom.Duplicate()!, doc.Layers[attr.LayerIndex]?.Color ?? Color.Cyan));

    var gp = new GetPoint();
    gp.EnableTransparentCommands(true);
    gp.SetCommandPrompt("Pick placement point for Part");
    gp.AddOptionToggle("Group",         ref groupToggle);
    gp.AddOptionToggle("JoinPerimeter", ref joinPerimToggle);
    gp.DynamicDraw += (_, e) =>
    {
      var xform = Transform.Translation(e.CurrentPoint - basePoint);
      var items = joinPerimToggle.CurrentValue ? joinedPreview : splitPreview;
      foreach (var (geom, color) in items)
      {
        var draw = geom.Duplicate();
        if (draw == null) continue;
        draw.Transform(xform);
        DrawPreview(e.Display, draw, color);
      }
    };

    GetResult gpResult;
    do
    {
      gpResult = gp.Get();
      if (gpResult == GetResult.Option)
      { _group = groupToggle.CurrentValue; _joinPerim = joinPerimToggle.CurrentValue; SaveOptions(); }
    }
    while (gpResult == GetResult.Option);

    if (gpResult != GetResult.Point)
    {
      L($"EXIT: placement cancelled (gpResult={gpResult})");
      return Result.Cancel;
    }
    L($"placement point: {gp.Point()}");

    // ── 8. Commit ─────────────────────────────────────────────────────────

    var translation = Transform.Translation(gp.Point() - basePoint);
    var addedIds    = new List<Guid>();

    var commitItems = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    if (_joinPerim)
    {
      // Single joined closed perimeter on current layer, then inside objects
      commitItems.Add(((GeometryBase)perimeter!, currentLayerAttr.Duplicate()));
      commitItems.AddRange(insideObjects);
    }
    else
    {
      commitItems.AddRange(partItems);
    }

    foreach (var (geom, attr) in commitItems)
    {
      var placed = geom.Duplicate();
      if (placed == null) continue;
      placed.Transform(translation);
      attr.RemoveFromAllGroups();
      var id = AddObjectToDoc(doc, placed, attr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    if (_group && addedIds.Count > 1)
    {
      var grpIdx = doc.Groups.Add();
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        obj.Attributes.AddToGroup(grpIdx);
        obj.CommitChanges();
      }
    }

    L($"committed: {addedIds.Count} object(s)  grouped={_group && addedIds.Count > 1}");
    L("=== done ===");
    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a single closed loop for containment testing. The source document
  /// geometry is untouched; working copies may be extended to selected curves.
  /// Gap-bridging segments are returned separately for the output Part.
  /// </summary>
  private static (Curve? Closed, List<LineCurve> Bridges, List<Curve> PerimeterCurves, double BoundaryTolerance) BuildClosedPerimeter(
    List<Curve> curves, Plane plane, double tol, List<string> log)
  {
    var bridges = new List<LineCurve>();
    var workingCurves = curves.Select(c => c.DuplicateCurve()).ToList();

    // Single closed curve → nothing to do
    if (curves.Count == 1 && curves[0].IsClosed)
      return (curves[0].DuplicateCurve(), bridges, workingCurves, tol);

    // Primary: CreateBooleanRegions handles curves that already meet or cross.
    var boundary = TryCreateBooleanBoundary(
      workingCurves, plane, tol, "original", log, out var boundaryTolerance);
    if (boundary != null)
      return (boundary, bridges, workingCurves, boundaryTolerance);

    // If an open end stops short, extend it in its own end direction until it
    // reaches another selected perimeter curve. Only keep these working copies
    // when they form a genuine Boolean region.
    var extendedEnds = ExtendDisconnectedEndsToSelectedCurves(workingCurves, tol, log);
    if (extendedEnds > 0)
    {
      boundary = TryCreateBooleanBoundary(
        workingCurves, plane, tol, "extended", log, out boundaryTolerance);
      if (boundary != null)
        return (boundary, bridges, workingCurves, boundaryTolerance);
    }

    log.Add("vPart[perim]: no closed Boolean region after end extension — trying endpoint join");

    // Fallback: endpoint joining with gap bridging
    var pieces = workingCurves.Select(c => c.DuplicateCurve()).ToList();

    // Greedily bridge nearest open-endpoint pairs across different curves
    var openEnds = new List<(int CrvIdx, bool IsStart, Point3d Pt)>();
    for (var i = 0; i < pieces.Count; i++)
    {
      if (pieces[i].IsClosed) continue;
      openEnds.Add((i, true,  pieces[i].PointAtStart));
      openEnds.Add((i, false, pieces[i].PointAtEnd));
    }

    // Log gap distances before bridging
    log.Add($"vPart[perim]: {curves.Count} curve(s) — {openEnds.Count} open endpoints");
    for (var di = 0; di < openEnds.Count; di++)
      for (var dj = di + 1; dj < openEnds.Count; dj++)
      {
        if (openEnds[dj].CrvIdx == openEnds[di].CrvIdx) continue;
        var gapDist = openEnds[di].Pt.DistanceTo(openEnds[dj].Pt);
        var tag = gapDist <= tol      ? "(coincident)"
                : gapDist < tol * 200 ? "(will bridge)"
                : $"(TOO FAR — {gapDist / tol:F0}× tol, not bridged)";
        log.Add($"  [{openEnds[di].CrvIdx}/{(openEnds[di].IsStart ? "S" : "E")}]↔[{openEnds[dj].CrvIdx}/{(openEnds[dj].IsStart ? "S" : "E")}] dist={gapDist:F4} {tag}");
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

    log.Add($"vPart[perim]: {bridges.Count} bridge(s) added — {pieces.Count} pieces for join");

    // Join copies (only for containment testing)
    var joined = Curve.JoinCurves(pieces.ToArray(), tol * 10);
    log.Add($"vPart[perim]: join@tol×10 → {(joined == null ? "null" : $"{joined.Length} result(s), closed={joined.Any(c => c.IsClosed)}")} ");
    var joinedClosed = joined?.Where(c => c.IsClosed).OrderByDescending(ClosedCurveArea).FirstOrDefault();
    if (joinedClosed != null)
      return (joinedClosed, bridges, workingCurves, tol * 10.0);

    var final = Curve.JoinCurves(pieces.ToArray(), tol * 100);
    log.Add($"vPart[perim]: join@tol×100 → {(final == null ? "null" : $"{final.Length} result(s), closed={final.Any(c => c.IsClosed)}")} ");
    var finalClosed = final?.Where(c => c.IsClosed).OrderByDescending(ClosedCurveArea).FirstOrDefault();
    if (finalClosed != null)
      return (finalClosed, bridges, workingCurves, tol * 100.0);

    log.Add("vPart[perim]: FAILED — endpoints not reachable or loop not closed");
    return (null, bridges, workingCurves, tol);
  }

  private static Curve? TryCreateBooleanBoundary(
    List<Curve> curves,
    Plane plane,
    double tol,
    string stage,
    List<string> log,
    out double usedTolerance)
  {
    usedTolerance = tol;
    foreach (var multiplier in new[] { 1.0, 10.0, 100.0 })
    {
      var regionTolerance = tol * multiplier;
      using var regions = Curve.CreateBooleanRegions(
        curves.ToArray(), plane, combineRegions: true, regionTolerance);
      if (regions == null)
      {
        log.Add($"vPart[perim]: {stage}@tol×{multiplier:G}: CreateBooleanRegions returned null");
        continue;
      }

      Curve? bestBoundary = null;
      var bestArea = double.NegativeInfinity;

      for (var r = 0; r < regions.RegionCount; r++)
      {
        var regionCurves = regions.RegionCurves(r);
        if (regionCurves == null || regionCurves.Length == 0)
          continue;

        var outerCandidates = regionCurves[0].IsClosed
          ? new[] { regionCurves[0] }
          : Curve.JoinCurves(regionCurves, regionTolerance * 10.0);

        foreach (var candidate in outerCandidates)
        {
          if (candidate?.IsClosed != true)
            continue;

          var area = ClosedCurveArea(candidate);
          if (area <= bestArea)
            continue;

          bestArea = area;
          bestBoundary = candidate.DuplicateCurve();
        }
      }

      log.Add($"vPart[perim]: {stage}@tol×{multiplier:G}: {regions.RegionCount} region(s), closed={bestBoundary != null}, area={Math.Max(0.0, bestArea):G6}");
      if (bestBoundary != null)
      {
        usedTolerance = regionTolerance;
        return bestBoundary;
      }
    }

    return null;
  }

  private static int ExtendDisconnectedEndsToSelectedCurves(
    List<Curve> curves, double tol, List<string> log)
  {
    var extendedEnds = 0;
    var touchTolerance = tol * 10.0;
    var bounds = BoundingBox.Empty;
    foreach (var curve in curves)
      bounds.Union(curve.GetBoundingBox(true));
    var probeLength = Math.Max(bounds.IsValid ? bounds.Diagonal.Length * 4.0 : 0.0, tol * 1000.0);

    // Two passes allow one newly extended curve to become a target for another.
    for (var pass = 0; pass < 2; pass++)
    {
      var changed = false;
      for (var i = 0; i < curves.Count; i++)
      {
        if (curves[i].IsClosed)
          continue;

        foreach (var end in new[] { CurveEnd.Start, CurveEnd.End })
        {
          var curve = curves[i];
          var endpoint = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
          if (EndpointTouchesAnotherCurve(endpoint, curves, i, touchTolerance))
            continue;

          var drivers = curves
            .Where((_, index) => index != i)
            .Cast<GeometryBase>()
            .ToArray();
          Curve? extended;
          try
          {
            extended = curve.Extend(end, CurveExtensionStyle.Line, drivers);
          }
          catch (Exception ex)
          {
            extended = null;
            log.Add($"vPart[perim]: curve {i} {end} boundary extension threw {ex.GetType().Name}");
          }

          var extensionMethod = "boundary";
          var extensionDistance = 0.0;
          if (extended != null)
          {
            var extendedEndpoint = end == CurveEnd.Start ? extended.PointAtStart : extended.PointAtEnd;
            if (!EndpointTouchesAnotherCurve(extendedEndpoint, curves, i, touchTolerance))
              extended = null;
          }

          if (extended == null)
          {
            extended = ExtendEndToNearestForwardIntersection(
              curve, end, curves, i, probeLength, touchTolerance, out extensionDistance);
            extensionMethod = "ray";
          }

          if (extended == null)
          {
            log.Add($"vPart[perim]: curve {i} {end} has no forward selected-curve hit within {probeLength:G6}");
            continue;
          }

          var addedLength = extended.GetLength() - curve.GetLength();
          if (addedLength <= tol)
            continue;

          curves[i] = extended;
          extendedEnds++;
          changed = true;
          var hit = extensionDistance > 0.0 ? $" hit={extensionDistance:G6}" : string.Empty;
          log.Add($"vPart[perim]: extended curve {i} {end} by {addedLength:G6} via {extensionMethod}{hit}");
        }
      }

      if (!changed)
        break;
    }

    log.Add($"vPart[perim]: extended {extendedEnds} disconnected end(s)");
    return extendedEnds;
  }

  private static Curve? ExtendEndToNearestForwardIntersection(
    Curve curve,
    CurveEnd end,
    List<Curve> curves,
    int curveIndex,
    double probeLength,
    double tol,
    out double hitDistance)
  {
    hitDistance = double.PositiveInfinity;
    var bestDistance = double.PositiveInfinity;

    var endpoint = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
    var direction = end == CurveEnd.Start ? -curve.TangentAtStart : curve.TangentAtEnd;
    if (!direction.Unitize())
      return null;

    Curve? probe;
    try
    {
      probe = curve.Extend(end, probeLength, CurveExtensionStyle.Line);
    }
    catch
    {
      probe = null;
    }

    if (probe == null)
      return null;

    var bestPoint = Point3d.Unset;

    void Consider(Point3d point)
    {
      if (!point.IsValid)
        return;

      var delta = point - endpoint;
      var forward = Vector3d.Multiply(delta, direction);
      if (forward <= tol || forward >= bestDistance)
        return;

      // Ignore intersections on the original portion of the curve.
      if (curve.ClosestPoint(point, out var originalT)
          && point.DistanceTo(curve.PointAt(originalT)) <= tol)
        return;

      bestPoint = point;
      bestDistance = forward;
    }

    for (var i = 0; i < curves.Count; i++)
    {
      if (i == curveIndex)
        continue;

      var events = Intersection.CurveCurve(probe, curves[i], tol, tol);
      if (events == null)
        continue;

      foreach (var intersection in events)
      {
        Consider(intersection.PointA);
        if (intersection.IsOverlap)
          Consider(intersection.PointA2);
      }
    }

    if (!bestPoint.IsValid || double.IsPositiveInfinity(bestDistance))
      return null;

    hitDistance = bestDistance;

    try
    {
      var toPoint = curve.Extend(end, CurveExtensionStyle.Line, bestPoint);
      if (toPoint != null)
        return toPoint;
    }
    catch
    {
    }

    if (!probe.ClosestPoint(bestPoint, out var hitT))
      return null;

    var oppositePoint = end == CurveEnd.Start ? curve.PointAtEnd : curve.PointAtStart;
    if (!probe.ClosestPoint(oppositePoint, out var oppositeT))
      return null;

    var interval = new Interval(Math.Min(hitT, oppositeT), Math.Max(hitT, oppositeT));
    return probe.Trim(interval);
  }

  private static bool EndpointTouchesAnotherCurve(
    Point3d endpoint, List<Curve> curves, int curveIndex, double tol)
  {
    for (var i = 0; i < curves.Count; i++)
    {
      if (i == curveIndex || !curves[i].ClosestPoint(endpoint, out var t))
        continue;

      if (endpoint.DistanceTo(curves[i].PointAt(t)) <= tol)
        return true;
    }

    return false;
  }

  private static double ClosedCurveArea(Curve curve)
  {
    using var properties = AreaMassProperties.Compute(curve);
    return properties?.Area ?? 0.0;
  }

  /// <summary>  /// Splits each perimeter curve at its intersections with the other perimeter
  /// curves, then keeps only the segments whose midpoint lies on the closed
  /// boundary (within tol\u00d710).  For already-trimmed curves this returns them
  /// whole; for curves extending past corners, the protruding portions are
  /// discarded.
  /// </summary>
  private static IEnumerable<(Curve Crv, ObjectAttributes Attr)> TrimToPerimeter(
    List<(Curve Crv, ObjectAttributes Attr)> source,
    Curve boundary,
    Plane plane,
    double tol)
  {
    var crvs = source.Select(s => s.Crv).ToList();
    var boundaryList = new List<Curve> { boundary };
    var boundaryMatchTolerance = tol * 2.0;
    for (var si = 0; si < source.Count; si++)
    {
      var (crv, attr) = source[si];
      var splitParams  = new SortedSet<double>();
      var keptCount = 0;
      var parameterTolerance = Math.Max(RhinoMath.ZeroTolerance, tol * 0.1);

      void AddSplitParameter(double parameter)
      {
        if (parameter <= crv.Domain.T0 + parameterTolerance
            || parameter >= crv.Domain.T1 - parameterTolerance)
          return;

        splitParams.Add(parameter);
      }

      for (var oi = 0; oi < crvs.Count; oi++)
      {
        if (oi == si) continue;
        var events = Intersection.CurveCurve(crv, crvs[oi], tol, tol);
        if (events == null) continue;
        foreach (var ev in events)
        {
          if (ev.IsOverlap) { AddSplitParameter(ev.OverlapA.T0); AddSplitParameter(ev.OverlapA.T1); }
          else               AddSplitParameter(ev.ParameterA);
        }
      }

      var boundaryEvents = Intersection.CurveCurve(crv, boundary, tol, tol);
      if (boundaryEvents != null)
        foreach (var ev in boundaryEvents)
        {
          if (ev.IsOverlap) { AddSplitParameter(ev.OverlapA.T0); AddSplitParameter(ev.OverlapA.T1); }
          else               AddSplitParameter(ev.ParameterA);
        }

      if (splitParams.Count == 0)
      {
        // Use a broad IsOnCurve check for boundary-hugging curves, but also
        // include curves that are entirely inside the perimeter (e.g. accidentally
        // selected interior curves — they were in the user's selection so they
        // should appear in the output).
        var mid = crv.PointAtNormalizedLength(0.5);
        if (IsOnCurve(mid, boundary, boundaryMatchTolerance) || IsInsideOrOn(mid, boundaryList, plane, tol))
        {
          yield return (crv.DuplicateCurve(), attr);
          keptCount++;
        }
      }
      else
      {
        var segs = crv.Split(splitParams);
        if (segs == null) continue;
        foreach (var seg in segs)
        {
          if (seg.GetLength() < tol) continue;
          var mid = seg.PointAtNormalizedLength(0.5);
          if (IsOnCurve(mid, boundary, boundaryMatchTolerance)
              || IsInsideOrOn(mid, boundaryList, plane, tol))
          {
            yield return (seg, attr);
            keptCount++;
          }
        }
      }


      L($"trim source {si}: splitParams={splitParams.Count} kept={keptCount} length={crv.GetLength():G6}");
    }
  }

  private static bool IsOnCurve(Point3d pt, Curve crv, double tol)
  {
    if (!pt.IsValid || !crv.ClosestPoint(pt, out var t)) return false;
    return pt.DistanceTo(crv.PointAt(t)) <= tol;
  }

  /// <summary>  /// Returns all visible objects (excluding selected perimeter IDs) whose
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
        // Curves: split at perimeter and keep only inside pieces.
        // If there are no split points, include the whole curve only when the
        // whole sampled curve is inside. This prevents a curve with midpoint
        // inside but ends sticking out from being copied untrimmed.
        foreach (var insidePiece in TrimCurveInsidePerimeter(crv, perimeter, plane, tol))
          result.Add((insidePiece, attr));
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

  private static IEnumerable<Curve> TrimCurveInsidePerimeter(Curve crv, Curve perimeter, Plane plane, double tol)
  {
    var boundary = new List<Curve> { perimeter };

    // Fast path: when intersection parameters are available, split directly.
    // The slower sampling fallback is only used when intersections fail.
    var splitParams = CollectPerimeterSplitParams(crv, perimeter, plane, tol);
    if (splitParams.Count > 0)
    {
      var pieces = crv.IsClosed
        ? SplitClosedCurveByParameters(crv, splitParams, tol)
        : crv.Split(splitParams.ToArray()) ?? Enumerable.Empty<Curve>();

      foreach (var piece in pieces)
      {
        if (piece.GetLength() < tol)
          continue;

        if (IsInsideOrOn(piece.PointAtNormalizedLength(0.5), boundary, plane, tol))
          yield return piece;
      }

      yield break;
    }

    // No intersection points. Keep/discard most curves from a small sample and
    // only run the expensive bisection sampler for mixed inside/outside curves.
    var quickSamples = SampleCurveInsideState(crv, boundary, plane, tol, 17);
    if (quickSamples.All(s => s.Inside))
    {
      yield return crv.DuplicateCurve();
      yield break;
    }

    if (quickSamples.All(s => !s.Inside))
      yield break;

    var intervals = FindInsideCurveIntervals(crv, boundary, plane, tol, quickSamples);
    foreach (var (a, b) in intervals)
    {
      var piece = TrimCurveInterval(crv, a, b, tol);
      if (piece != null && piece.GetLength() >= tol)
        yield return piece;
    }
  }

  private static List<(double T, bool Inside)> SampleCurveInsideState(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    int sampleCount)
  {
    var samples = new List<(double T, bool Inside)>();
    var domain = crv.Domain;
    sampleCount = Math.Max(1, sampleCount);

    for (var i = 0; i <= sampleCount; i++)
    {
      var t = domain.ParameterAt(i / (double)sampleCount);
      samples.Add((t, IsInsideOrOn(crv.PointAt(t), boundary, plane, tol)));
    }

    return samples;
  }

  private static List<(double A, double B)> FindInsideCurveIntervals(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    List<(double T, bool Inside)> initialSamples)
  {
    var result = new List<(double A, double B)>();
    var domain = crv.Domain;
    var parameterSpan = domain.T1 - domain.T0;
    if (parameterSpan <= RhinoMath.ZeroTolerance || initialSamples.Count < 2)
      return result;

    double? insideStart = initialSamples[0].Inside ? domain.T0 : null;

    for (var i = 1; i < initialSamples.Count; i++)
    {
      var prev = initialSamples[i - 1];
      var cur = initialSamples[i];

      if (prev.Inside == cur.Inside)
        continue;

      var crossing = FindContainmentChangeParameter(crv, boundary, plane, tol, prev.T, cur.T, prev.Inside);

      if (!prev.Inside && cur.Inside)
      {
        insideStart = crossing;
      }
      else if (prev.Inside && !cur.Inside && insideStart.HasValue)
      {
        if (crossing - insideStart.Value > RhinoMath.ZeroTolerance)
          result.Add((insideStart.Value, crossing));
        insideStart = null;
      }
    }

    if (insideStart.HasValue && domain.T1 - insideStart.Value > RhinoMath.ZeroTolerance)
      result.Add((insideStart.Value, domain.T1));

    return result;
  }

  private static double FindContainmentChangeParameter(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    double a,
    double b,
    bool insideAtA)
  {
    for (var i = 0; i < 40; i++)
    {
      var mid = 0.5 * (a + b);
      var insideAtMid = IsInsideOrOn(crv.PointAt(mid), boundary, plane, tol);

      if (insideAtMid == insideAtA)
        a = mid;
      else
        b = mid;
    }

    return 0.5 * (a + b);
  }

  private static Curve? TrimCurveInterval(Curve crv, double a, double b, double tol)
  {
    if (b - a <= RhinoMath.ZeroTolerance)
      return null;

    return crv.IsClosed
      ? TrimClosedInterval(crv, a, b, crv.Domain, tol)
      : crv.Trim(a, b);
  }

  private static List<double> CollectPerimeterSplitParams(Curve crv, Curve perimeter, Plane plane, double tol)
  {
    var splitParams = new List<double>();

    var events = Intersection.CurveCurve(crv, perimeter, tol, tol);
    if (events == null || events.Count == 0)
      events = Intersection.CurveCurve(crv, perimeter, tol * 10.0, tol * 10.0);

    // Fallback to view-plane 2D intersections. This catches curves that cross the
    // boundary in the active view plane but do not intersect in 3D exactly.
    if (events == null || events.Count == 0)
    {
      var crv2d = crv.DuplicateCurve();
      var perimeter2d = perimeter.DuplicateCurve();
      var toWorldXY = Transform.PlaneToPlane(plane, Plane.WorldXY);

      crv2d.Transform(toWorldXY);
      perimeter2d.Transform(toWorldXY);

      events = Intersection.CurveCurve(crv2d, perimeter2d, tol, tol);
      if (events == null || events.Count == 0)
        events = Intersection.CurveCurve(crv2d, perimeter2d, tol * 10.0, tol * 10.0);
    }

    if (events == null)
      return splitParams;

    foreach (var ev in events)
    {
      if (ev.IsOverlap)
      {
        AddCurveSplitParam(crv, splitParams, ev.OverlapA.T0, tol);
        AddCurveSplitParam(crv, splitParams, ev.OverlapA.T1, tol);
      }
      else
      {
        AddCurveSplitParam(crv, splitParams, ev.ParameterA, tol);
      }
    }

    return splitParams.OrderBy(t => t).ToList();
  }

  private static void AddCurveSplitParam(Curve crv, List<double> splitParams, double t, double tol)
  {
    if (crv.IsClosed)
    {
      t = NormalizeClosedParameter(t, crv.Domain);
    }
    else
    {
      var d = crv.Domain;
      var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
      if (t <= d.T0 + paramTol || t >= d.T1 - paramTol)
        return;
    }

    var duplicateTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
    if (!splitParams.Any(existing => Math.Abs(existing - t) <= duplicateTol))
      splitParams.Add(t);
  }

  private static IEnumerable<Curve> SplitClosedCurveByParameters(Curve crv, List<double> splitParams, double tol)
  {
    var domain = crv.Domain;
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      yield break;

    var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
    var parameters = splitParams
      .Select(t => NormalizeClosedParameter(t, domain))
      .OrderBy(t => t)
      .ToList();

    if (parameters.Count < 2)
      yield break;

    for (var i = 0; i < parameters.Count; i++)
    {
      var a = parameters[i];
      var b = i == parameters.Count - 1 ? parameters[0] + period : parameters[i + 1];
      if (b - a <= paramTol)
        continue;

      var piece = TrimClosedInterval(crv, a, b, domain, tol);
      if (piece != null && piece.GetLength() >= tol)
        yield return piece;
    }
  }

  private static Curve? TrimClosedInterval(Curve crv, double a, double b, Interval domain, double tol)
  {
    var period = domain.T1 - domain.T0;

    while (a < domain.T0) a += period;
    while (a > domain.T1) a -= period;
    while (b < domain.T0) b += period;

    if (b <= domain.T1)
      return crv.Trim(a, b);

    var first = crv.Trim(a, domain.T1);
    var second = crv.Trim(domain.T0, b - period);

    if (first == null) return second;
    if (second == null) return first;

    var joined = Curve.JoinCurves(new[] { first, second }, tol);
    if (joined != null && joined.Length == 1)
      return joined[0];

    var pc = new PolyCurve();
    pc.Append(first);
    pc.Append(second);
    return pc;
  }

  private static double NormalizeClosedParameter(double t, Interval domain)
  {
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      return t;

    while (t < domain.T0) t += period;
    while (t > domain.T1) t -= period;
    return t;
  }

  private static bool AllSamplesInsideOrOn(Curve crv, List<Curve> boundary, Plane plane, double tol)
  {
    foreach (var pt in SampleCurve(crv, 17))
      if (!IsInsideOrOn(pt, boundary, plane, tol))
        return false;
    return true;
  }

  private static IEnumerable<Point3d> SampleCurve(Curve curve, int count)
  {
    count = Math.Max(1, count);

    for (var i = 0; i < count; i++)
    {
      var s = (i + 0.5) / count;
      yield return curve.PointAtNormalizedLength(s);
    }
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

  private static Guid AddObjectToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr)
  {
    switch (geom)
    {
      case Curve c:                          return doc.Objects.AddCurve(c, attr);
      case TextEntity te:                    return doc.Objects.AddText(te, attr);
      case TextDot td:                       return doc.Objects.AddTextDot(td, attr);
      case Rhino.Geometry.Point pt:          return doc.Objects.AddPoint(pt.Location, attr);
      case Hatch h:                          return doc.Objects.AddHatch(h, attr);
      case Brep b:                           return doc.Objects.AddBrep(b, attr);
      case Mesh m:                           return doc.Objects.AddMesh(m, attr);
      default:                               return doc.Objects.Add(geom, attr);
    }
  }
}
