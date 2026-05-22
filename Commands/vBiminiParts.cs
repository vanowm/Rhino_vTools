using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
/// Generates bimini pattern parts (Finished/Seam boundary, FacingP/FacingS, main/secondary pockets)
/// from a selected closed boundary curve (or a set of open curves that form one).
///
/// Workflow:
///   Stage 1 — select boundary; detect or create Finished (PLOT) and Seam (CUT1) curves;
///             break both at corners → 4 segments each.
///   Stage 2 — pick 1–2 Main pocket curve(s).
///   Stage 3 — pick 0–2 Secondary pocket curve(s) (skipped if 2 Main selected).
///   Stage 4 — create FacingP / FacingS from side seam segments offset 3" inward.
///   Stage 5 — create Main pocket: zipper offset, side offsets, perpendicular lines.
/// </summary>
public sealed class vBiminiParts : Command
{
  // ── Constants ───────────────────────────────────────────────────────────────

  private const string SectionName     = "vBiminiParts";
  private const string PipeSizeKey     = "pipeSize";

  private const double SeamAllowance   = 0.5;   // seam offset from finished (inches)
  private const double FacingInset     = 3.0;   // facing: inset from seam side toward center
  private const double SidePktOutward  = 2.5;   // main pocket: side seam outward offset
  private const double MainPktSmall    = 4.0;   // main pocket depth, pipe ≤ 1"
  private const double MainPktLarge    = 5.0;   // main pocket depth, pipe 1-1/4"
  private const double CornerAngleDeg  = 30.0;  // minimum kink angle to split at (degrees)
  private const double FacingMoveOut   = 5.0;   // gap between moved facing and bimini seam edge

  private const string LayerPlot       = "PLOT";
  private const string LayerCut1       = "CUT1";

  // ── Persisted state ─────────────────────────────────────────────────────────

  private static double _pipeSize = 1.0;  // 1.0 or 1.25 inches

  // ── Debug logging ──────────────────────────────────────────────────────────

  private static StreamWriter? _log;

  private static string GetLogPath()
  {
    var dir = Path.Combine(Path.GetDirectoryName(typeof(vBiminiParts).Assembly.Location)!, "logs");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, "vBiminiParts.log");
  }

  internal static void InitLog()
  {
    try { File.WriteAllText(GetLogPath(), string.Empty); } catch { }
  }

  private static void L(string s) { _log?.WriteLine(s); }

  public override string EnglishName => "vBiminiParts";

  // ── Persistence ─────────────────────────────────────────────────────────────

  private static void LoadOptions()
  {
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetDouble(section, PipeSizeKey, out var ps)) _pipeSize = ps;
      return 0;
    });
  }

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section => { section[PipeSizeKey] = _pipeSize; });

  // ── Command ──────────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var tol = doc.ModelAbsoluteTolerance;

    // ── Stage 1: Select bimini boundary ─────────────────────────────────────

    var go = new GetObject();
    go.SetCommandPrompt("Select bimini boundary curves");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.AcceptNothing(false);

    // PipeSize option — current value shown via toggle label
    var pipeToggle = new OptionToggle(_pipeSize >= 1.25, "1inch", "1_25inch");
    go.AddOptionToggle("PipeSize", ref pipeToggle);

    while (true)
    {
      var r = go.GetMultiple(1, 0);
      if (r == GetResult.Option) continue;
      if (r != GetResult.Object) return Result.Cancel;
      break;
    }

    _pipeSize = pipeToggle.CurrentValue ? 1.25 : 1.0;
    SaveOptions();

    var selIds    = new HashSet<Guid>(Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId));
    var rawCurves = Enumerable.Range(0, go.ObjectCount)
                              .Select(i => go.Object(i).Curve())
                              .Where(c => c != null)
                              .Cast<Curve>()
                              .ToList();

    if (rawCurves.Count == 0) return Result.Cancel;

    // Join into one closed curve
    Curve boundary;
    if (rawCurves.Count == 1 && rawCurves[0].IsClosed)
    {
      boundary = rawCurves[0].DuplicateCurve();
    }
    else
    {
      var joined = Curve.JoinCurves(rawCurves, tol);
      if (joined == null || joined.Length != 1 || !joined[0].IsClosed)
      {
        RhinoApp.WriteLine("vBiminiParts: selected curves do not form a single closed boundary.");
        return Result.Failure;
      }
      boundary = joined[0];
    }

    // Centroid drives offset direction
    var amp = AreaMassProperties.Compute(boundary);
    if (amp == null)
    {
      RhinoApp.WriteLine("vBiminiParts: cannot compute boundary centroid.");
      return Result.Failure;
    }
    var centroid = amp.Centroid;

    // Determine Finished vs Seam
    // If a curve exists 0.5" inward from boundary → that is Finished, boundary is Seam.
    // Otherwise → boundary is Finished, offset 0.5" outward is Seam.
    Curve finishedCrv, seamCrv;
    RhinoObject? existingFinObj = null;
    var inwardCandidate = OffsetToward(boundary, centroid, SeamAllowance, tol);
    if (inwardCandidate != null)
    {
      existingFinObj = FindNearCurve(doc, inwardCandidate, selIds, tol * 20.0);
      if (existingFinObj != null)
      {
        seamCrv     = boundary.DuplicateCurve();
        finishedCrv = ((Curve)existingFinObj.Geometry).DuplicateCurve();
      }
      else
      {
        finishedCrv = boundary.DuplicateCurve();
        seamCrv     = OffsetAway(boundary, centroid, SeamAllowance, tol)
                      ?? boundary.DuplicateCurve();
      }
    }
    else
    {
      finishedCrv = boundary.DuplicateCurve();
      seamCrv     = OffsetAway(boundary, centroid, SeamAllowance, tol)
                    ?? boundary.DuplicateCurve();
    }

    // Ensure layers exist
    var plotIdx = EnsureLayer(doc, LayerPlot, Color.FromArgb(15, 138, 138));
    var cut1Idx = EnsureLayer(doc, LayerCut1, Color.FromArgb(204, 51, 51));

    // Break both curves at corners → 4 open segments each
    var finSegs  = BreakAtCorners(finishedCrv, CornerAngleDeg);
    var seamSegs = BreakAtCorners(seamCrv,     CornerAngleDeg);

    RhinoApp.WriteLine($"vBiminiParts: finished segments = {finSegs.Count}, seam segments = {seamSegs.Count}");

    var plotAttr   = MakeAttr(plotIdx);
    var cut1Attr   = MakeAttr(cut1Idx);
    var finIds     = new HashSet<Guid>();
    var seamIds    = new HashSet<Guid>();
    var seamDocIds = new List<Guid>();          // parallel to seamSegs — for picker highlighting
    foreach (var s in finSegs)  { var id = doc.Objects.AddCurve(s, plotAttr); if (id != Guid.Empty) finIds.Add(id); }
    foreach (var s in seamSegs) { var id = doc.Objects.AddCurve(s, cut1Attr); if (id != Guid.Empty) { seamIds.Add(id); seamDocIds.Add(id); } else { seamDocIds.Add(Guid.Empty); } }
    // Delete source curves — replaced by the broken segments added above
    foreach (var id in selIds) doc.Objects.Delete(id, false);
    if (existingFinObj != null) doc.Objects.Delete(existingFinObj.Id, false);
    doc.Views.Redraw();  // show seam/fin segments before stage-2 prompt

    // Exclude original selected curves + seam segments (seam boundary is added explicitly as facing edges).
    // Do NOT exclude finIds — finished curves inside the facing area must be collected into the facings.
    var excludeInterior = new HashSet<Guid>(selIds);
    excludeInterior.UnionWith(seamIds);

    // Classify segments as Top / Bottom / Left / Right
    var finParts  = Classify(finSegs,  centroid);
    var seamParts = Classify(seamSegs, centroid);

    // Color side seam doc objects distinctively so they're visually identifiable
    SetDocObjectColor(doc, seamSegs, seamDocIds, seamParts.Left,  Color.Magenta);
    SetDocObjectColor(doc, seamSegs, seamDocIds, seamParts.Right, Color.Magenta);
    doc.Views.Redraw();

    // Open log before picker so picker actions are captured
    var logPath = GetLogPath();
    _log = new StreamWriter(logPath, true, System.Text.Encoding.UTF8) { AutoFlush = true };
    L($"── vBiminiParts {DateTime.Now} ──");
    L($"tol={doc.ModelAbsoluteTolerance}  selIds={selIds.Count}  seamIds={seamIds.Count}  finIds={finIds.Count}  excludeInterior={excludeInterior.Count}");
    L($"seamCandidates: {seamSegs.Count} segs, {seamDocIds.Count} docIds");
    for (var _si = 0; _si < seamSegs.Count; _si++)
      L($"  seamSeg[{_si}]: id={(_si < seamDocIds.Count ? seamDocIds[_si] : Guid.Empty)}  len={seamSegs[_si].GetLength():F2}  start={seamSegs[_si].PointAtStart}  end={seamSegs[_si].PointAtEnd}");
    RhinoApp.WriteLine($"vBiminiParts: log → {logPath}");

    // ── Stage 2: Main pocket curve selection ────────────────────────────────

    var mainPicks = PickPocketCurves("Click near Main pocket center", 2, doc, seamSegs, seamDocIds);

    // ── Stage 3: Secondary pocket curve selection ────────────────────────────

    List<(Curve Curve, Point3d Center)> secPicks;
    if (mainPicks.Count >= 2)
    {
      secPicks = new List<(Curve, Point3d)>();
    }
    else
    {
      var maxSec = mainPicks.Count == 0 ? 2 : 1;
      secPicks = PickPocketCurves("Click near Secondary pocket center", maxSec, doc, seamSegs, seamDocIds);
    }

    L($"mainPicks={mainPicks.Count}  secPicks={secPicks.Count}");

    // ── Stage 4: Facing parts (FacingP = port/left, FacingS = stbd/right) ───

    BuildFacingParts(doc, seamParts, centroid, cut1Idx, excludeInterior, tol);

    // ── Stage 5: Main pocket geometry ───────────────────────────────────────

    // excludeInterior already contains seamIds (covers all global seam curves incl. Left/Right).
    // finIds are NOT excluded so finished-edge segments inside the pocket outline are collected.
    var pocketExclude = new HashSet<Guid>(excludeInterior);
    L($"pocketExclude: {pocketExclude.Count} ids (selIds + seamIds; finIds not excluded)");

    if (mainPicks.Count > 0)
      BuildMainPocket(doc, mainPicks, seamParts, finParts, centroid, cut1Idx, tol, pocketExclude);

    doc.Views.Redraw();
    _log?.Dispose();
    _log = null;
    return Result.Success;
  }

  // ── Pocket curve picker ─────────────────────────────────────────────────────

  private static List<(Curve Curve, Point3d Center)> PickPocketCurves(string prompt, int maxCount,
                                                                        RhinoDoc doc, List<Curve> candidates, List<Guid> candidateIds)
  {
    var list    = new List<(Curve, Point3d)>();
    var picked  = new HashSet<Curve>(ReferenceEqualityComparer.Instance);

    for (var i = 0; i < maxCount; i++)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt($"{prompt}. Press Enter to finish.");
      gp.AcceptNothing(true);

      var res = gp.Get();

      if (res != GetResult.Point) break;

      var pt   = gp.Point();
      Curve? best = null;
      double bestDist = double.MaxValue;
      foreach (var c in candidates)
      {
        if (picked.Contains(c)) continue;
        c.ClosestPoint(pt, out var t);
        var d = pt.DistanceTo(c.PointAt(t));
        if (d < bestDist) { bestDist = d; best = c; }
      }
      if (best == null) break;
      picked.Add(best);
      list.Add((best.DuplicateCurve(), pt));  // record the actual clicked point as center

      // Keep picked curve highlighted for subsequent prompts
      var bestIdx = candidates.FindIndex(c => ReferenceEquals(c, best));
      if (bestIdx >= 0 && bestIdx < candidateIds.Count && candidateIds[bestIdx] != Guid.Empty)
        doc.Objects.Select(candidateIds[bestIdx], true);
    }
    return list;
  }

  // ── Stage 4: Facing parts ───────────────────────────────────────────────────

  private static void BuildFacingParts(RhinoDoc doc, Parts seam,
                                        Point3d centroid, int cut1Idx,
                                        HashSet<Guid> excludeIds, double tol)
  {
    if (seam.Left != null)
      BuildOneFacing(doc, seam.Left, seam.Top, seam.Bottom, centroid, cut1Idx, excludeIds, tol, "FacingP");

    if (seam.Right != null)
      BuildOneFacing(doc, seam.Right, seam.Top, seam.Bottom, centroid, cut1Idx, excludeIds, tol, "FacingS");
  }

  private static void BuildOneFacing(RhinoDoc doc, Curve seamSide,
                                      Curve? seamTop, Curve? seamBot,
                                      Point3d centroid, int cut1Idx,
                                      HashSet<Guid> excludeIds, double tol,
                                      string label)
  {
    // 1. Offset seam side 3" toward centroid → inner facing edge (CUT1)
    var innerEdge = OffsetToward(seamSide, centroid, FacingInset, tol);
    if (innerEdge == null)
    {
      RhinoApp.WriteLine($"vBiminiParts: {label} — offset failed.");
      return;
    }

    // 2. Extend inner edge at both ends to meet seam top/bottom
    var extendTo = new List<GeometryBase>();
    if (seamTop != null) extendTo.Add(seamTop);
    if (seamBot != null) extendTo.Add(seamBot);
    if (extendTo.Count > 0)
    {
      var e1 = innerEdge.Extend(CurveEnd.Start, CurveExtensionStyle.Smooth, extendTo);
      if (e1 != null) innerEdge = e1;
      var e2 = innerEdge.Extend(CurveEnd.End, CurveExtensionStyle.Smooth, extendTo);
      if (e2 != null) innerEdge = e2;
    }

    // 3. Trim seamTop and seamBot to only the facing portion
    //    (between where seamSide endpoint meets them and where innerEdge meets them)
    var connTol      = Math.Max(tol * 200, 0.1);
    var facingCurves = new List<Curve> { innerEdge, seamSide.DuplicateCurve() };
    if (seamTop != null) TryAddTrimmedSeam(facingCurves, innerEdge, seamSide, seamTop, connTol);
    if (seamBot != null) TryAddTrimmedSeam(facingCurves, innerEdge, seamSide, seamBot, connTol);

    // 4. Collect objects inside the facing boundary (before move)
    var interiorObjects = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    {
      var boundaryTol = Math.Max(tol * 200, 0.1);
      Curve? closedBoundary = null;
      var joined = Curve.JoinCurves(facingCurves.Select(c => c.DuplicateCurve()).ToArray(), boundaryTol);
      if (joined?.Length == 1 && joined[0].IsClosed)
      {
        closedBoundary = joined[0];
      }
      else
      {
        // Fallback: connect innerEdge and seamSide with straight lines at the corners
        var ieS    = innerEdge.PointAtStart;
        var ieE    = innerEdge.PointAtEnd;
        var ssFlip = seamSide.PointAtEnd.DistanceTo(ieE) < seamSide.PointAtStart.DistanceTo(ieE);
        var ssCopy = seamSide.DuplicateCurve();
        if (ssFlip) ssCopy.Reverse();
        var pc = new PolyCurve();
        pc.Append(innerEdge.DuplicateCurve());
        pc.Append(new LineCurve(ieE, ssCopy.PointAtStart));
        pc.Append(ssCopy);
        pc.Append(new LineCurve(ssCopy.PointAtEnd, ieS));
        if (pc.IsClosed || pc.MakeClosed(boundaryTol))
          closedBoundary = pc;
      }
      if (closedBoundary != null)
        interiorObjects = CollectInsideObjects(doc, excludeIds, closedBoundary, Plane.WorldXY, tol);
      L($"  {label}: facingCurves={facingCurves.Count}  boundaryFormed={closedBoundary != null}  excludeIds={excludeIds.Count}  interiorFound={interiorObjects.Count}");
      if (closedBoundary == null)
        L($"  {label}: joined?.Length={joined?.Length}  joined[0].IsClosed={(joined?.Length > 0 ? joined![0].IsClosed.ToString() : "n/a")}");
      foreach (var (g, a) in interiorObjects)
        L($"    interior: {g.GetType().Name}  layer={a.LayerIndex}");
    }

    // 5. Move the whole facing outward so the nearest edge (innerEdge) ends up
    //    FacingMoveOut inches clear of the bimini seam (total = FacingMoveOut + FacingInset)
    var outDir = seamSide.PointAtNormalizedLength(0.5) - centroid;
    outDir.Unitize();
    var xf   = Transform.Translation(outDir * (FacingMoveOut + FacingInset));
    var attr = MakeAttr(cut1Idx);
    attr.Name = label;
    var addedIds = new List<Guid>();
    foreach (var c in facingCurves)
    {
      var copy = c.DuplicateCurve();
      copy.Transform(xf);
      var id = doc.Objects.AddCurve(copy, attr);
      if (id != Guid.Empty) addedIds.Add(id);
    }
    foreach (var (geom, geomAttr) in interiorObjects)
    {
      var copy = geom.Duplicate()!;
      copy.Transform(xf);
      geomAttr.RemoveFromAllGroups();
      var id = AddObjectToDoc(doc, copy, geomAttr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    // 6. Group all facing objects together
    if (addedIds.Count > 1)
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
  }

  /// <summary>
  /// Trims <paramref name="topBot"/> to the segment spanning from where
  /// <paramref name="seamSide"/> meets it to where <paramref name="innerEdge"/> meets it,
  /// then appends the trimmed piece to <paramref name="result"/>.
  /// </summary>
  private static void TryAddTrimmedSeam(List<Curve> result,
                                         Curve innerEdge, Curve seamSide,
                                         Curve topBot, double tol)
  {
    var tInner = NearestEndpointParam(innerEdge, topBot, tol);
    if (!tInner.HasValue) return;

    var tSide = NearestEndpointParam(seamSide, topBot, tol);
    if (!tSide.HasValue) return;

    var lo = Math.Min(tInner.Value, tSide.Value);
    var hi = Math.Max(tInner.Value, tSide.Value);
    if (hi - lo < RhinoMath.ZeroTolerance) return;

    var trimmed = topBot.Trim(lo, hi);
    if (trimmed != null) result.Add(trimmed);
  }

  /// <summary>
  /// Returns the parameter on <paramref name="onCurve"/> of the endpoint of
  /// <paramref name="source"/> (start or end) that lies within <paramref name="tol"/>,
  /// or null if neither endpoint qualifies.
  /// </summary>
  private static double? NearestEndpointParam(Curve source, Curve onCurve, double tol)
  {
    foreach (var pt in new[] { source.PointAtStart, source.PointAtEnd })
    {
      onCurve.ClosestPoint(pt, out var t);
      if (pt.DistanceTo(onCurve.PointAt(t)) < tol)
        return t;
    }
    return null;
  }

  // ── Stage 5: Main pocket ────────────────────────────────────────────────────

  private static void BuildMainPocket(RhinoDoc doc, List<(Curve Curve, Point3d Center)> mainPicks,
                                       Parts seam, Parts fin,
                                       Point3d centroid, int cut1Idx,
                                       double tol, HashSet<Guid> globalExclude)
  {
    double pocketDepth = _pipeSize >= 1.25 ? MainPktLarge : MainPktSmall;
    const double extLen  = 24.0;
    const double moveOut = 5.0;

    L($"BuildMainPocket: pocketDepth={pocketDepth}  picks={mainPicks.Count}");
    foreach (var (mc, pktCenter) in mainPicks)
    {
      var adjSeam = ClosestOf(mc, seam.Top, seam.Bottom, seam.Left, seam.Right);
      if (adjSeam == null) { L($"  mc: no adjSeam"); continue; }

      var zipperRaw = OffsetToward(adjSeam, centroid, pocketDepth, tol);
      if (zipperRaw == null) { L($"  mc: zipperRaw offset failed"); continue; }

      var offLeft  = seam.Left  != null ? OffsetAway(seam.Left,  centroid, SidePktOutward, tol) : null;
      var offRight = seam.Right != null ? OffsetAway(seam.Right, centroid, SidePktOutward, tol) : null;
      if (offLeft == null || offRight == null) { L($"  mc: offLeft={offLeft != null} offRight={offRight != null} seamL={seam.Left != null} seamR={seam.Right != null}"); continue; }

      // Build mirrored end flares at each corner where adjSeam meets the fin sides
      var adjFin   = ClosestOf(adjSeam, fin.Top, fin.Bottom, fin.Left, fin.Right);
      Curve? mirLeft = null, mirRight = null;
      if (adjFin != null)
      {
        if (fin.Left  != null) { var c = FindSharedEndpoint(adjFin, fin.Left);  if (c.IsValid) mirLeft  = BuildMirroredEnd(adjSeam, fin.Left,  c, pocketDepth, extLen); }
        if (fin.Right != null) { var c = FindSharedEndpoint(adjFin, fin.Right); if (c.IsValid) mirRight = BuildMirroredEnd(adjSeam, fin.Right, c, pocketDepth, extLen); }
      }
      L($"  mc: adjFin={adjFin != null}  mirLeft={mirLeft != null}  mirRight={mirRight != null}");

      // Build closed pocket outline: adjSeam (top) → mirRight → offRight → perpR
      //   → zipper (full width to finished sides) → perpL → offLeft → mirLeft → back
      var pocketOutline = BuildPocketOutline(adjSeam, mirLeft, mirRight,
                                             offLeft, offRight, zipperRaw, fin.Left, fin.Right, extLen, tol);

      // Collect objects inside the pocket boundary before moving
      var interiorObjects = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
      L($"  mc: pocketOutline={pocketOutline != null}  closed={(pocketOutline?.IsClosed)}  globalExclude={globalExclude.Count}");
      if (pocketOutline != null)
        L($"  mc: outline bbox={pocketOutline.GetBoundingBox(true)}");
      if (pocketOutline != null && pocketOutline.IsClosed)
        interiorObjects = CollectInsideObjects(doc, globalExclude, pocketOutline, Plane.WorldXY, tol);
      L($"  mc: interior collected={interiorObjects.Count}");

      // Translate all pocket geometry away from the bimini
      var outDir = adjSeam.PointAtNormalizedLength(0.5) - centroid;
      outDir.Unitize();
      var xf = Transform.Translation(outDir * moveOut);

      var addedIds = new List<Guid>();
      if (pocketOutline != null)
      {
        pocketOutline.Transform(xf);
        var id = doc.Objects.AddCurve(pocketOutline, MakeAttr(cut1Idx));
        if (id != Guid.Empty) addedIds.Add(id);
      }

      // Add pocket-height side seam reference lines — trimmed copies of seam.Left/Right
      // spanning only from adjSeam down to the zipper line within the pocket.
      foreach (var side in new[] { seam.Left, seam.Right })
      {
        if (side == null) continue;
        var sideRef = TrimToPocketHeight(side, adjSeam, zipperRaw, extLen, tol);
        if (sideRef == null) { L($"  mc: TrimToPocketHeight returned null for side"); continue; }
        sideRef.Transform(xf);
        var id = doc.Objects.AddCurve(sideRef, MakeAttr(cut1Idx));
        if (id != Guid.Empty) addedIds.Add(id);
      }
      foreach (var (geom, geomAttr) in interiorObjects)
      {
        var copy = geom.Duplicate()!;
        copy.Transform(xf);
        geomAttr.RemoveFromAllGroups();
        var id = AddObjectToDoc(doc, copy, geomAttr);
        if (id != Guid.Empty) addedIds.Add(id);
      }

      if (addedIds.Count > 1)
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
    }
  }

  /// <summary>
  /// Builds a closed pocket outline. The zipper extends full-width to the seam side curves.
  /// At each zip-seam corner a perpendicular line connects to the offset side wall.
  /// Offset sides run from perpendicular foot up to the mirrored end flares (8-segment outline).
  /// Falls back to a 4-sided rect when mirrored ends are unavailable.
  /// </summary>
  private static Curve? BuildPocketOutline(Curve adjSeam,
                                            Curve? mirLeft,  Curve? mirRight,
                                            Curve  offLeft,  Curve  offRight,
                                            Curve  zipper,
                                            Curve? seamLeft, Curve? seamRight,
                                            double extLen,   double tol)
  {
    var offLExt = offLeft.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? offLeft.DuplicateCurve();
    var offRExt = offRight.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? offRight.DuplicateCurve();
    var zipExt  = zipper.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)  ?? zipper.DuplicateCurve();

    // Bottom corners: zip extends to seam sides; perpendiculars connect zip-seam corners to offset side walls
    Curve?     zipSeg;
    LineCurve? perpL = null, perpR = null;
    double     tBotL_off, tBotR_off;  // parameters on offLExt/offRExt for perpendicular feet

    if (seamLeft != null && seamRight != null)
    {
      var seamLExt = seamLeft.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamLeft.DuplicateCurve();
      var seamRExt = seamRight.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamRight.DuplicateCurve();
      if (!FindIntersectionParam(seamLExt, zipExt, tol, out _, out var tSL_zip)) { L("  BuildPocketOutline: no seamLeft∩zipper"); return null; }
      if (!FindIntersectionParam(seamRExt, zipExt, tol, out _, out var tSR_zip)) { L("  BuildPocketOutline: no seamRight∩zipper"); return null; }

      zipSeg = zipExt.Trim(Math.Min(tSL_zip, tSR_zip), Math.Max(tSL_zip, tSR_zip));
      if (zipSeg == null) { L("  BuildPocketOutline: zipSeg trim null"); return null; }

      var ptBotL = zipExt.PointAt(tSL_zip);  // zip ∩ seamLeft
      var ptBotR = zipExt.PointAt(tSR_zip);  // zip ∩ seamRight
      if (!offLExt.ClosestPoint(ptBotL, out tBotL_off)) { L("  BuildPocketOutline: perpL closest-point failed"); return null; }
      if (!offRExt.ClosestPoint(ptBotR, out tBotR_off)) { L("  BuildPocketOutline: perpR closest-point failed"); return null; }

      perpL = new LineCurve(ptBotL, offLExt.PointAt(tBotL_off));
      perpR = new LineCurve(ptBotR, offRExt.PointAt(tBotR_off));
      L($"  BuildPocketOutline: seam corners  ptBotL={ptBotL}  ptBotR={ptBotR}");
    }
    else
    {
      // Fallback: bottom corners = offset sides ∩ zipper
      if (!FindIntersectionParam(offLExt, zipExt, tol, out tBotL_off, out var tOL_zip)) { L("  BuildPocketOutline: no offLeft∩zipper"); return null; }
      if (!FindIntersectionParam(offRExt, zipExt, tol, out tBotR_off, out var tOR_zip)) { L("  BuildPocketOutline: no offRight∩zipper"); return null; }
      zipSeg = zipExt.Trim(Math.Min(tOL_zip, tOR_zip), Math.Max(tOL_zip, tOR_zip));
      if (zipSeg == null) { L("  BuildPocketOutline: zipSeg trim null"); return null; }
    }

    Curve[] segments;
    if (mirLeft != null && mirRight != null)
    {
      // Top corners: mirrored flares ∩ offset sides
      if (!FindIntersectionParam(mirLeft,  offLExt, tol, out var tML_mir, out var tML_off)) { L("  BuildPocketOutline: no mirLeft∩offLeft"); return null; }
      if (!FindIntersectionParam(mirRight, offRExt, tol, out var tMR_mir, out var tMR_off)) { L("  BuildPocketOutline: no mirRight∩offRight"); return null; }

      // Trim flares from adjSeam endpoint (T0) to where they meet the offset side
      var mirLeftSeg  = mirLeft.Trim(mirLeft.Domain.T0,   tML_mir)
                      ?? mirLeft.Trim(tML_mir, mirLeft.Domain.T1);
      var mirRightSeg = mirRight.Trim(mirRight.Domain.T0, tMR_mir)
                      ?? mirRight.Trim(tMR_mir, mirRight.Domain.T1);
      if (mirLeftSeg == null || mirRightSeg == null) { L($"  BuildPocketOutline: flare trim null  mirLeftSeg={mirLeftSeg != null}  mirRightSeg={mirRightSeg != null}"); return null; }

      // Offset sides: flare intersection (top) to perpendicular foot (bottom)
      var offLSeg = offLExt.Trim(Math.Min(tML_off, tBotL_off), Math.Max(tML_off, tBotL_off));
      var offRSeg = offRExt.Trim(Math.Min(tMR_off, tBotR_off), Math.Max(tMR_off, tBotR_off));
      if (offLSeg == null || offRSeg == null) { L($"  BuildPocketOutline: offSide trim null  offLSeg={offLSeg != null}  offRSeg={offRSeg != null}"); return null; }

      // 8-seg: adjSeam → mirRight → offRight → perpR → zip → perpL → offLeft → mirLeft
      if (perpL != null && perpR != null)
        segments = new Curve[] { adjSeam.DuplicateCurve(), mirRightSeg, offRSeg, perpR, zipSeg, perpL, offLSeg, mirLeftSeg };
      else
        segments = new Curve[] { adjSeam.DuplicateCurve(), mirRightSeg, offRSeg, zipSeg, offLSeg, mirLeftSeg };
    }
    else
    {
      L("  BuildPocketOutline: fallback 4-sided (no mirLeft/mirRight)");
      var adjExt = adjSeam.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? adjSeam.DuplicateCurve();
      if (!FindIntersectionParam(adjExt, offLExt, tol, out var tAdj_L, out var tOL_adj)) { L("  BuildPocketOutline: no adjSeam∩offLeft"); return null; }
      if (!FindIntersectionParam(adjExt, offRExt, tol, out var tAdj_R, out var tOR_adj)) { L("  BuildPocketOutline: no adjSeam∩offRight"); return null; }

      var topSeg = adjExt.Trim(Math.Min(tAdj_L, tAdj_R), Math.Max(tAdj_L, tAdj_R));
      if (topSeg == null) { L("  BuildPocketOutline: topSeg trim null"); return null; }

      var offLSeg4 = offLExt.Trim(Math.Min(tOL_adj, tBotL_off), Math.Max(tOL_adj, tBotL_off));
      var offRSeg4 = offRExt.Trim(Math.Min(tOR_adj, tBotR_off), Math.Max(tOR_adj, tBotR_off));
      if (offLSeg4 == null || offRSeg4 == null) { L($"  BuildPocketOutline: 4-sided offSide trim null  offLSeg4={offLSeg4 != null}  offRSeg4={offRSeg4 != null}"); return null; }

      if (perpL != null && perpR != null)
        segments = new Curve[] { topSeg, offRSeg4, perpR, zipSeg, perpL, offLSeg4 };
      else
        segments = new Curve[] { topSeg, offRSeg4, zipSeg, offLSeg4 };
    }

    for (int i = 0; i < segments.Length; i++)
    {
      var s = segments[i];
      var sn = segments[(i + 1) % segments.Length];
      double gap = Math.Min(
        Math.Min(s.PointAtStart.DistanceTo(sn.PointAtStart), s.PointAtStart.DistanceTo(sn.PointAtEnd)),
        Math.Min(s.PointAtEnd.DistanceTo(sn.PointAtStart),   s.PointAtEnd.DistanceTo(sn.PointAtEnd)));
      L($"  seg[{i}]→seg[{(i+1)%segments.Length}]  gap={gap:F4}  s0={s.PointAtStart}  s1={s.PointAtEnd}  n0={sn.PointAtStart}  n1={sn.PointAtEnd}");
    }
    var joinTol = Math.Max(tol * 200, 0.1);
    var joined  = Curve.JoinCurves(segments, joinTol);
    L($"  BuildPocketOutline: joinTol={joinTol:F4}  segs={segments.Length}  joined={joined?.Length}  closed={(joined?.Length == 1 ? joined[0].IsClosed.ToString() : "n/a")}");
    return joined?.Length == 1 && joined[0].IsClosed ? joined[0] : null;
  }

  /// <summary>Returns the parameter on each curve at their first intersection, or false if none.</summary>
  private static bool FindIntersectionParam(Curve a, Curve b, double tol,
                                             out double tA, out double tB)
  {
    tA = tB = double.NaN;
    var events = Intersection.CurveCurve(a, b, tol, tol);
    if (events == null || events.Count == 0) return false;
    tA = events[0].ParameterA;
    tB = events[0].ParameterB;
    return true;
  }

  /// <summary>
  /// Mirrors a section of <paramref name="adjSeam"/> (length ≤ <paramref name="depth"/>)
  /// at the end nearest <paramref name="cornerPt"/> across <paramref name="finSide"/> at
  /// <paramref name="cornerPt"/>. Returns the mirrored curve with Start ≈ cornerPt,
  /// extended at the far end by <paramref name="extLen"/> so it overshoots the offset side.
  /// </summary>
  private static Curve? BuildMirroredEnd(Curve adjSeam, Curve finSide, Point3d cornerPt,
                                          double depth, double extLen)
  {
    var totalLen  = adjSeam.GetLength();
    var isAtStart = adjSeam.PointAtStart.DistanceTo(cornerPt)
                    <= adjSeam.PointAtEnd.DistanceTo(cornerPt);
    Curve? section;
    if (isAtStart)
    {
      if (!adjSeam.LengthParameter(Math.Min(depth, totalLen * 0.5), out var tEnd)) return null;
      section = adjSeam.Trim(adjSeam.Domain.T0, tEnd);
    }
    else
    {
      if (!adjSeam.LengthParameter(Math.Max(totalLen - depth, totalLen * 0.5), out var tStart)) return null;
      section = adjSeam.Trim(tStart, adjSeam.Domain.T1);
    }
    if (section == null) return null;

    var sideDir      = finSide.PointAtEnd - finSide.PointAtStart;
    sideDir.Unitize();
    var mirrorNormal = new Vector3d(-sideDir.Y, sideDir.X, 0);
    var mirrorOrigin = isAtStart ? adjSeam.PointAtStart : adjSeam.PointAtEnd;
    var mirrored     = section.DuplicateCurve();
    mirrored.Transform(Transform.Mirror(new Plane(mirrorOrigin, mirrorNormal)));

    // Ensure Start = mirrorOrigin so T0-based trim in BuildPocketOutline connects directly to adjSeam
    if (mirrored.PointAtEnd.DistanceTo(mirrorOrigin) < mirrored.PointAtStart.DistanceTo(mirrorOrigin))
      mirrored.Reverse();

    // Extend the far end (away from cornerPt) so it overshoots the offset side
    var extended = mirrored.Extend(CurveEnd.End, extLen, CurveExtensionStyle.Line);
    return extended ?? mirrored;
  }

  /// <summary>Returns the shared endpoint of two curves (within 0.5"), or <see cref="Point3d.Unset"/>.</summary>
  private static Point3d FindSharedEndpoint(Curve a, Curve b)
  {
    foreach (var pa in new[] { a.PointAtStart, a.PointAtEnd })
      foreach (var pb in new[] { b.PointAtStart, b.PointAtEnd })
        if (pa.DistanceTo(pb) < 0.5)
          return pa;
    return Point3d.Unset;
  }

  private static void DrawPerpLine(RhinoDoc doc, Point3d from, Curve? side, int layerIdx, double tol)
  {
    if (side == null) return;
    side.ClosestPoint(from, out var t);
    var foot = side.PointAt(t);
    if (from.DistanceTo(foot) < tol) return;
    doc.Objects.AddCurve(new Line(from, foot).ToNurbsCurve(), MakeAttr(layerIdx));
  }

  // ── Geometry utilities ──────────────────────────────────────────────────────

  private static int EnsureLayer(RhinoDoc doc, string name, Color color)
  {
    var idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
    if (idx >= 0) return idx;
    return doc.Layers.Add(new Layer { Name = name, Color = color });
  }

  private static ObjectAttributes MakeAttr(int layerIdx) =>
    new ObjectAttributes { LayerIndex = layerIdx };

  /// <summary>Offsets <paramref name="crv"/> by <paramref name="dist"/> toward <paramref name="target"/>.</summary>
  private static Curve? OffsetToward(Curve crv, Point3d target, double dist, double tol)
  {
    var a = TryOffset(crv,  dist, tol);
    var b = TryOffset(crv, -dist, tol);
    return PickByDistance(a, b, target, closer: true);
  }

  /// <summary>Offsets <paramref name="crv"/> by <paramref name="dist"/> away from <paramref name="target"/>.</summary>
  private static Curve? OffsetAway(Curve crv, Point3d target, double dist, double tol)
  {
    var a = TryOffset(crv,  dist, tol);
    var b = TryOffset(crv, -dist, tol);
    return PickByDistance(a, b, target, closer: false);
  }

  private static Curve? TryOffset(Curve crv, double dist, double tol)
  {
    var pieces = crv.Offset(Plane.WorldXY, dist, tol, CurveOffsetCornerStyle.Sharp);
    if (pieces == null || pieces.Length == 0) return null;
    if (pieces.Length == 1) return pieces[0];
    var joined = Curve.JoinCurves(pieces, tol);
    return joined?.Length > 0 ? joined[0] : null;
  }

  private static Curve? PickByDistance(Curve? a, Curve? b, Point3d target, bool closer)
  {
    if (a == null) return b;
    if (b == null) return a;
    // Use minimum distance from target to each curve (ClosestPoint), not area centroid.
    // For a closed curve that encloses the target, the inner offset has a smaller
    // minimum distance and the outer offset has a larger one.
    a.ClosestPoint(target, out var ta);
    b.ClosestPoint(target, out var tb);
    var da = a.PointAt(ta).DistanceTo(target);
    var db = b.PointAt(tb).DistanceTo(target);
    return closer ? (da <= db ? a : b) : (da >= db ? a : b);
  }

  private static Point3d CurveCentroid(Curve c) =>
    AreaMassProperties.Compute(c)?.Centroid ?? c.PointAtNormalizedLength(0.5);

  /// <summary>
  /// Returns the first doc curve (not in <paramref name="excludeIds"/>) whose points
  /// are all within <paramref name="threshold"/> of <paramref name="target"/>.
  /// Used to detect a pre-existing Finished curve inside the selected boundary.
  /// </summary>
  private static RhinoObject? FindNearCurve(RhinoDoc doc, Curve target, HashSet<Guid> excludeIds, double threshold)
  {
    const int samples = 20;
    var pts = Enumerable.Range(0, samples)
                        .Select(i => target.PointAtNormalizedLength(i / (double)(samples - 1)))
                        .ToArray();

    foreach (var ro in doc.Objects.GetObjectList(ObjectType.Curve))
    {
      if (ro == null || excludeIds.Contains(ro.Id)) continue;
      var c = ro.Geometry as Curve;
      if (c == null || !c.IsClosed) continue;
      if (pts.All(pt =>
      {
        c.ClosestPoint(pt, out var t);
        return pt.DistanceTo(c.PointAt(t)) < threshold;
      }))
        return ro;
    }
    return null;
  }

  /// <summary>
  /// Splits a closed curve at every G1-discontinuity that exceeds <paramref name="angleDeg"/>.
  /// </summary>
  private static List<Curve> BreakAtCorners(Curve crv, double angleDeg)
  {
    var dom = crv.Domain;
    var eps = Math.Max((dom.T1 - dom.T0) * 1e-6, RhinoMath.ZeroTolerance * 10.0);
    var cps = new List<double>();

    var seek = dom.T0 + eps;
    while (crv.GetNextDiscontinuity(Continuity.G1_continuous, seek, dom.T1, out var t))
    {
      if (IsSharpCorner(crv, t, angleDeg, dom, eps)) cps.Add(t);
      seek = t + eps;
      if (seek >= dom.T1) break;
    }

    // Closed-curve seam is never reported by GetNextDiscontinuity — test explicitly
    if (crv.IsClosed && IsSharpCorner(crv, dom.T0, angleDeg, dom, eps))
      cps.Add(dom.T0);

    if (cps.Count == 0)
      return new List<Curve> { crv.DuplicateCurve() };

    cps = cps.Distinct().OrderBy(v => v).ToList();
    var split = crv.Split(cps.ToArray());
    return split != null && split.Length > 0
           ? new List<Curve>(split)
           : new List<Curve> { crv.DuplicateCurve() };
  }

  private static bool IsSharpCorner(Curve crv, double t, double angleDeg, Interval dom, double eps)
  {
    Vector3d tA, tB;
    if (crv.IsClosed && Math.Abs(t - dom.T0) < eps * 10)
    {
      tA = crv.TangentAt(dom.T1 - eps);
      tB = crv.TangentAt(dom.T0 + eps);
    }
    else
    {
      tA = crv.TangentAt(Math.Max(dom.T0, t - eps));
      tB = crv.TangentAt(Math.Min(dom.T1, t + eps));
    }
    if (!tA.Unitize() || !tB.Unitize()) return false;
    var dot = Math.Max(-1.0, Math.Min(1.0, tA * tB));
    return RhinoMath.ToDegrees(Math.Acos(dot)) >= angleDeg;
  }

  // ── Segment classification ──────────────────────────────────────────────────

  private sealed class Parts
  {
    public Curve? Top, Bottom, Left, Right;
  }

  /// <summary>
  /// Classifies up to 4 segments as Top / Bottom / Left / Right using their
  /// bounding-box orientation relative to <paramref name="centroid"/>.
  /// Horizontal segments (wider than tall) → Top or Bottom.
  /// Vertical segments (taller than wide) → Left or Right.
  /// </summary>
  private static Parts Classify(List<Curve> segs, Point3d centroid)
  {
    var p = new Parts();
    if (segs.Count == 0) return p;

    var items = segs.Select(s =>
    {
      var bb = s.GetBoundingBox(false);
      return (Curve: s, BBox: bb, Mid: s.PointAtNormalizedLength(0.5));
    }).ToList();

    if (segs.Count == 4)
    {
      foreach (var item in items)
      {
        double w = item.BBox.Max.X - item.BBox.Min.X;
        double h = item.BBox.Max.Y - item.BBox.Min.Y;
        if (w >= h)  // horizontal → Top / Bottom
        {
          if (item.Mid.Y >= centroid.Y) p.Top    ??= item.Curve;
          else                          p.Bottom ??= item.Curve;
        }
        else         // vertical → Left / Right
        {
          if (item.Mid.X <= centroid.X) p.Left   ??= item.Curve;
          else                          p.Right  ??= item.Curve;
        }
      }
    }
    else
    {
      // Generic fallback for unexpected segment counts
      var byY = items.OrderByDescending(i => i.Mid.Y).ToList();
      p.Top    = byY[0].Curve;
      p.Bottom = byY[^1].Curve;
      var mid  = byY.Skip(1).Take(byY.Count - 2).OrderBy(i => i.Mid.X).ToList();
      p.Left   = mid.Count > 0 ? mid[0].Curve   : null;
      p.Right  = mid.Count > 1 ? mid[^1].Curve  : null;
    }

    return p;
  }

  /// <summary>Returns the candidate whose midpoint is closest to <paramref name="reference"/>'s midpoint.</summary>
  private static Curve? ClosestOf(Curve reference, params Curve?[] candidates)
  {
    var mid = reference.PointAtNormalizedLength(0.5);
    return candidates
           .Where(c => c != null)
           .Select(c => c!)
           .OrderBy(c => { c.ClosestPoint(mid, out var t); return mid.DistanceTo(c.PointAt(t)); })
           .FirstOrDefault();
  }

  // ── Interior object collection ──────────────────────────────────────────────

  private static List<(GeometryBase Geom, ObjectAttributes Attr)> CollectInsideObjects(
    RhinoDoc doc, HashSet<Guid> excludeIds, Curve perimeter, Plane plane, double tol)
  {
    var result   = new List<(GeometryBase, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };
    int nScanned = 0, nExcluded = 0, nOutside = 0;

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject,
      VisibleFilter    = true,
      DeletedObjects   = false,
      IncludeGrips     = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      nScanned++;
      if (obj.ObjectType == ObjectType.Grip) continue;
      if (excludeIds.Contains(obj.Id)) { nExcluded++; continue; }
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
            else               splitParams.Add(ev.ParameterA);
          }

        if (splitParams.Count == 0)
        {
          var inside = IsInsideOrOn(crv.PointAtNormalizedLength(0.5), boundary, plane, tol);
          L($"  CollectInside: crv id={obj.Id}  splitParams=0  inside={inside}  mid={crv.PointAtNormalizedLength(0.5)}");
          if (inside)
            result.Add((crv.DuplicateCurve(), attr));
          else
            nOutside++;
        }
        else
        {
          var segments = crv.Split(splitParams);
          if (segments == null) continue;
          foreach (var seg in segments)
          {
            if (seg.GetLength() < tol) continue;
            var inside = IsInsideOrOn(seg.PointAtNormalizedLength(0.5), boundary, plane, tol);
            L($"  CollectInside: crv id={obj.Id}  seg mid={seg.PointAtNormalizedLength(0.5)}  inside={inside}");
            if (inside)
              result.Add((seg, attr));
            else
              nOutside++;
          }
        }
      }
      else
      {
        var testPt = RepresentativePoint(geom);
        var inside = testPt.IsValid && IsInsideOrOn(testPt, boundary, plane, tol);
        L($"  CollectInside: other id={obj.Id}  type={obj.ObjectType}  inside={inside}  pt={testPt}");
        if (inside)
          result.Add((geom.Duplicate()!, attr));
        else
          nOutside++;
      }
    }

    L($"CollectInsideObjects done: scanned={nScanned}  excluded={nExcluded}  outside={nOutside}  collected={result.Count}");
    return result;
  }

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

  private static Guid AddObjectToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr)
  {
    switch (geom)
    {
      case Curve c:                       return doc.Objects.AddCurve(c, attr);
      case TextEntity te:                 return doc.Objects.AddText(te, attr);
      case TextDot td:                    return doc.Objects.AddTextDot(td, attr);
      case Rhino.Geometry.Point pt:       return doc.Objects.AddPoint(pt.Location, attr);
      case Hatch h:                       return doc.Objects.AddHatch(h, attr);
      case Brep b:                        return doc.Objects.AddBrep(b, attr);
      case Mesh m:                        return doc.Objects.AddMesh(m, attr);
      default:                            return doc.Objects.Add(geom, attr);
    }
  }

  // Sets a per-object display color on the doc object corresponding to the given
  // Curve reference within seamSegs / seamDocIds parallel lists.
  private static void SetDocObjectColor(RhinoDoc doc, List<Curve> segs, List<Guid> ids, Curve? c, Color color)
  {
    if (c == null) return;
    var idx = segs.FindIndex(s => ReferenceEquals(s, c));
    if (idx < 0 || idx >= ids.Count || ids[idx] == Guid.Empty) return;
    var obj = doc.Objects.FindId(ids[idx]);
    if (obj == null) return;
    obj.Attributes.ColorSource = ObjectColorSource.ColorFromObject;
    obj.Attributes.ObjectColor = color;
    obj.CommitChanges();
  }

  // Trims a seam side curve to the vertical range of the pocket:
  // from the endpoint adjacent to adjSeam down to the zipper intersection.
  private static Curve? TrimToPocketHeight(Curve seamSide, Curve adjSeam, Curve zipper,
                                            double extLen, double tol)
  {
    // Pocket top: endpoint of seamSide closest to EITHER endpoint of adjSeam
    // (they share a corner; comparing to adjSeam midpoint gave the wrong end when curve is long)
    var adjP0  = adjSeam.PointAtStart;
    var adjP1  = adjSeam.PointAtEnd;
    var dStart = Math.Min(seamSide.PointAtStart.DistanceTo(adjP0), seamSide.PointAtStart.DistanceTo(adjP1));
    var dEnd   = Math.Min(seamSide.PointAtEnd.DistanceTo(adjP0),   seamSide.PointAtEnd.DistanceTo(adjP1));
    var tTop   = dStart < dEnd ? seamSide.Domain.T0 : seamSide.Domain.T1;
    L($"  TrimToPocketHeight: seamStart={seamSide.PointAtStart}  seamEnd={seamSide.PointAtEnd}");
    L($"  adjP0={adjP0}  adjP1={adjP1}  dStart={dStart:F3}  dEnd={dEnd:F3}  tTop={tTop:F4} (={seamSide.PointAt(tTop)})");

    // Pocket bottom: intersection of extended seamSide with extended zipper
    var seamExt = seamSide.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? seamSide.DuplicateCurve();
    var zipExt  = zipper.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)   ?? zipper.DuplicateCurve();
    var xEvents = Intersection.CurveCurve(seamExt, zipExt, tol, tol);
    L($"  xEvents={(xEvents?.Count ?? 0)}  zipMid={zipper.PointAt(zipper.Domain.Mid)}");

    double tBot;
    if (xEvents != null && xEvents.Count > 0)
    {
      var xPt = xEvents[0].PointA;
      L($"  intersection xPt={xPt}");
      if (!seamSide.ClosestPoint(xPt, out tBot)) { L($"  ClosestPoint on seamSide failed — return null"); return null; }
    }
    else
    {
      // Fallback: project zipper midpoint onto seamSide
      L($"  no intersection — fallback: project zipper mid onto seamSide");
      if (!seamSide.ClosestPoint(zipper.PointAt(zipper.Domain.Mid), out tBot)) { L($"  fallback ClosestPoint failed — return null"); return null; }
    }

    L($"  tBot={tBot:F4} (={seamSide.PointAt(tBot)})");
    if (Math.Abs(tTop - tBot) < RhinoMath.ZeroTolerance) { L($"  tTop==tBot — return null"); return null; }
    var trimmed = seamSide.Trim(Math.Min(tTop, tBot), Math.Max(tTop, tBot));
    L($"  trimmed={(trimmed != null ? $"len={trimmed.GetLength():F2}" : "null")}");
    return trimmed;
  }
}
