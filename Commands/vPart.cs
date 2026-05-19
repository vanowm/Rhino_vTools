using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
  private static StreamWriter? _log;

  private static void OpenLog()
  {
    try
    {
      _log?.Dispose();
      _log = null;
      var asmDir  = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
      var logsDir = Path.Combine(asmDir, "logs");
      Directory.CreateDirectory(logsDir);
      _log = new StreamWriter(Path.Combine(logsDir, "vPart_perim.log"), append: false) { AutoFlush = true };
    }
    catch { }
  }

  private static void L(string m)
  {
    try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {m}"); } catch { }
  }

  private static string Short(Guid id) => id.ToString()[..8];

  private static void LoadOptions() =>
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetBool(section, GroupKey,     out var g)) _group    = g;
      if (vToolsOptionStore.TryGetBool(section, JoinPerimKey, out var j)) _joinPerim = j;
      return 0;
    });

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
    {
      section[GroupKey]     = _group;
      section[JoinPerimKey] = _joinPerim;
    });

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var tol = doc.ModelAbsoluteTolerance;
    OpenLog();
    L($"=== vPart {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    L($"  tol={tol:G4}  group={_group}  joinPerim={_joinPerim}");

    // ── 1. Select perimeter curves ─────────────────────────────────────────
    // Single GetObject with EnableClearObjectsOnEntry(false) so the accumulated
    // list persists across calls — AlreadySelectedObjectSelect checks go's own
    // list, not raw doc selection, so clearing the list between calls would
    // prevent re-click detection entirely.
    //
    // GetMultiple(1,-1): immediate-return per user action (click or Enter).
    // Preselects arrive on the first return (ObjectsWerePreselected=true); we
    // capture them and call EnablePreSelect(false,true) to switch to interactive.
    // Each subsequent click appends one object to go's list; AlreadySelectedObjectSelect
    // causes a duplicate to be appended for already-accumulated objects, so the
    // most-recently-clicked object is always go.Object(go.ObjectCount-1).

    var groupToggle    = new OptionToggle(_group,    "No", "Yes");
    var joinPerimToggle = new OptionToggle(_joinPerim, "No", "Yes");

    var go = new GetObject();
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

    var objectsWerePreselected = false;
    var collectedIds = new HashSet<Guid>();
    var collectedMap = new Dictionary<Guid, ObjRef>();

    var selIter = 0;
    while (true)
    {
      var selRes = go.GetMultiple(1, -1);
      L($"sel iter {++selIter}: result={selRes}  cmdResult={go.CommandResult()}  count={go.ObjectCount}  preselected={go.ObjectsWerePreselected}");

      if (selRes == GetResult.Option)
      {
        _group = groupToggle.CurrentValue;
        _joinPerim = joinPerimToggle.CurrentValue;
        SaveOptions();
        continue;
      }

      if (selRes == GetResult.Nothing)
        break; // Enter = confirm

      if (go.CommandResult() != Result.Success)
      {
        foreach (var id in collectedIds) doc.Objects.FindId(id)?.Select(false);
        doc.Views.Redraw();
        L("cancelled");
        return Result.Cancel;
      }

      if (go.ObjectsWerePreselected)
      {
        objectsWerePreselected = true;
        for (var i = 0; i < go.ObjectCount; i++)
        {
          var r = go.Object(i);
          if (collectedIds.Add(r.ObjectId)) collectedMap[r.ObjectId] = r;
          L($"  pre[{i}]: {Short(r.ObjectId)}");
        }
        go.EnablePreSelect(false, true);
        L($"pre-selected: {collectedIds.Count}");
        continue;
      }

      if (selRes == GetResult.Object && go.ObjectCount > 0)
      {
        // go.Object(go.ObjectCount-1): new click appends; AlreadySelectedObjectSelect
        // appends a duplicate — so the last entry is always the most-recently-clicked.
        var r  = go.Object(go.ObjectCount - 1);
        var id = r.ObjectId;
        if (collectedIds.Contains(id))
        {
          collectedIds.Remove(id);
          collectedMap.Remove(id);
          doc.Objects.FindId(id)?.Select(false);
          L($"  removed: {Short(id)}");
        }
        else
        {
          collectedIds.Add(id);
          collectedMap[id] = r;
          L($"  added: {Short(id)}");
        }
      }
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
    var (perimeter, bridges) = BuildClosedPerimeter(perimList.Select(p => p.Crv).ToList(), plane, tol, perimLog);
    L($"BuildClosedPerimeter: {(perimeter != null ? "OK" : "FAILED")}  bridges={bridges.Count}");
    foreach (var entry in perimLog) L($"  perim: {entry}");
    if (perimeter == null)
    {
      RhinoApp.WriteLine("vPart: could not form a closed perimeter from selected curves.");
      L("EXIT: no closed perimeter");
      return Result.Nothing;
    }

    // ── 4. Collect inside objects (all visible types, trimmed for curves) ──

    var insideObjects = CollectInsideObjects(doc, perimIds, perimeter, plane, tol);
    L($"insideObjects: {insideObjects.Count}");

    // ── 5. Assemble Part items ─────────────────────────────────────────────────────
    //  • Original perimeter curves trimmed to the closed boundary — each on its own layer
    //  • Bridge segments (gap fillers) — on current doc layer
    //  • Inside objects — on their original layers

    var currentLayerAttr = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };

    var partItems = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    foreach (var (crv, attr) in TrimToPerimeter(perimList, perimeter!, tol))
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

    // Dale's pattern: deselect input objects if they were preselected so post-command
    // selection state is consistent regardless of pre/post-select mix.
    if (objectsWerePreselected)
    {
      foreach (var id in collectedIds) doc.Objects.FindId(id)?.Select(false);
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
    List<Curve> curves, Plane plane, double tol, List<string> log)
  {
    var bridges = new List<LineCurve>();

    // Single closed curve → nothing to do
    if (curves.Count == 1 && curves[0].IsClosed)
      return (curves[0].DuplicateCurve(), bridges);

    // Primary: CreateBooleanRegions — handles curves extending past corners
    var regions = Curve.CreateBooleanRegions(curves.ToArray(), plane, combineRegions: true, tol);
    if (regions != null)
    {
      for (var r = 0; r < regions.RegionCount; r++)
      {
        var rc = regions.RegionCurves(r);
        if (rc == null) continue;
        foreach (var c in rc)
          if (c?.IsClosed == true)
            return (c, bridges);  // success — no bridges needed
      }
      log.Add($"vPart[perim]: CreateBooleanRegions: {regions.RegionCount} region(s), none closed — trying endpoint join");
    }
    else
    {
      log.Add("vPart[perim]: CreateBooleanRegions returned null — trying endpoint join");
    }

    // Fallback: endpoint joining with gap bridging
    var pieces = curves.Select(c => c.DuplicateCurve()).ToList();

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
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return (joined[0], bridges);

    var final = Curve.JoinCurves(pieces.ToArray(), tol * 100);
    log.Add($"vPart[perim]: join@tol×100 → {(final == null ? "null" : $"{final.Length} result(s), closed={final.Any(c => c.IsClosed)}")} ");
    if (final != null && final.Length == 1 && final[0].IsClosed)
      return (final[0], bridges);

    log.Add("vPart[perim]: FAILED — endpoints not reachable or loop not closed");
    return (null, bridges);
  }

  /// <summary>  /// Splits each perimeter curve at its intersections with the other perimeter
  /// curves, then keeps only the segments whose midpoint lies on the closed
  /// boundary (within tol\u00d710).  For already-trimmed curves this returns them
  /// whole; for curves extending past corners, the protruding portions are
  /// discarded.
  /// </summary>
  private static IEnumerable<(Curve Crv, ObjectAttributes Attr)> TrimToPerimeter(
    List<(Curve Crv, ObjectAttributes Attr)> source, Curve boundary, double tol)
  {
    var crvs = source.Select(s => s.Crv).ToList();
    for (var si = 0; si < source.Count; si++)
    {
      var (crv, attr) = source[si];
      var splitParams  = new SortedSet<double>();
      for (var oi = 0; oi < crvs.Count; oi++)
      {
        if (oi == si) continue;
        var events = Intersection.CurveCurve(crv, crvs[oi], tol, tol);
        if (events == null) continue;
        foreach (var ev in events)
        {
          if (ev.IsOverlap) { splitParams.Add(ev.OverlapA.T0); splitParams.Add(ev.OverlapA.T1); }
          else               splitParams.Add(ev.ParameterA);
        }
      }

      if (splitParams.Count == 0)
      {
        // Use a broad IsOnCurve check for boundary-hugging curves, but also
        // include curves that are entirely inside the perimeter (e.g. accidentally
        // selected interior curves — they were in the user's selection so they
        // should appear in the output).
        var mid = crv.PointAtNormalizedLength(0.5);
        if (IsOnCurve(mid, boundary, tol * 10) || IsInsideOrOn(mid, new List<Curve> { boundary }, Plane.WorldXY, tol))
          yield return (crv.DuplicateCurve(), attr);
      }
      else
      {
        var segs = crv.Split(splitParams);
        if (segs == null) continue;
        foreach (var seg in segs)
        {
          if (seg.GetLength() < tol) continue;
          if (IsOnCurve(seg.PointAtNormalizedLength(0.5), boundary, tol * 10))
            yield return (seg, attr);
        }
      }
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
