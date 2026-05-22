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
    return Path.Combine(dir, $"vBiminiParts_{DateTime.Now:yyMMdd_HHmmss}.txt");
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

    var plotAttr = MakeAttr(plotIdx);
    var cut1Attr = MakeAttr(cut1Idx);
    var finIds  = new HashSet<Guid>();
    var seamIds = new HashSet<Guid>();
    foreach (var s in finSegs)  { var id = doc.Objects.AddCurve(s, plotAttr); if (id != Guid.Empty) finIds.Add(id); }
    foreach (var s in seamSegs) { var id = doc.Objects.AddCurve(s, cut1Attr); if (id != Guid.Empty) seamIds.Add(id); }
    // Delete source curves — replaced by the broken segments added above
    foreach (var id in selIds) doc.Objects.Delete(id, false);
    if (existingFinObj != null) doc.Objects.Delete(existingFinObj.Id, false);
    // Exclude original selected curves + seam segments (seam boundary is added explicitly as facing edges).
    // Do NOT exclude finIds — finished curves inside the facing area must be collected into the facings.
    var excludeInterior = new HashSet<Guid>(selIds);
    excludeInterior.UnionWith(seamIds);

    // Classify segments as Top / Bottom / Left / Right
    var finParts  = Classify(finSegs,  centroid);
    var seamParts = Classify(seamSegs, centroid);

    // ── Stage 2: Main pocket curve selection ────────────────────────────────

    var mainCurves = PickPocketCurves("Click on Main pocket curve(s)", 2);

    // ── Stage 3: Secondary pocket curve selection ────────────────────────────

    List<Curve> secCurves;
    if (mainCurves.Count >= 2)
    {
      secCurves = new List<Curve>();
    }
    else
    {
      var maxSec = mainCurves.Count == 0 ? 2 : 1;
      secCurves = PickPocketCurves($"Click on Secondary pocket curve(s) (max {maxSec})", maxSec);
    }

    // ── Stage 4: Facing parts (FacingP = port/left, FacingS = stbd/right) ───

    var logPath = GetLogPath();
    _log = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
    L($"vBiminiParts log — {DateTime.Now}");
    L($"tol={doc.ModelAbsoluteTolerance}  selIds={selIds.Count}  seamIds={seamIds.Count}  finIds={finIds.Count}  excludeInterior={excludeInterior.Count}");
    RhinoApp.WriteLine($"vBiminiParts: log → {logPath}");

    BuildFacingParts(doc, seamParts, centroid, cut1Idx, excludeInterior, tol);

    // ── Stage 5: Main pocket geometry ───────────────────────────────────────

    if (mainCurves.Count > 0)
      BuildMainPocket(doc, mainCurves, seamParts, finParts, centroid, cut1Idx, tol);

    doc.Views.Redraw();
    _log?.Dispose();
    _log = null;
    return Result.Success;
  }

  // ── Pocket curve picker ─────────────────────────────────────────────────────

  private static List<Curve> PickPocketCurves(string prompt, int maxCount)
  {
    var gm = new GetObject();
    gm.SetCommandPrompt(prompt + ". Press Enter to skip");
    gm.GeometryFilter = ObjectType.Curve;
    gm.SubObjectSelect = false;
    gm.AcceptNothing(true);
    gm.EnablePreSelect(false, false);
    gm.EnableClearObjectsOnEntry(false);
    gm.AlreadySelectedObjectSelect = true;

    var list = new List<Curve>();
    var res  = gm.GetMultiple(0, maxCount);
    if (res == GetResult.Object)
      for (var i = 0; i < gm.ObjectCount; i++)
      {
        var c = gm.Object(i).Curve();
        if (c != null) list.Add(c.DuplicateCurve());
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

  private static void BuildMainPocket(RhinoDoc doc, List<Curve> mainCurves,
                                       Parts seam, Parts fin,
                                       Point3d centroid, int cut1Idx, double tol)
  {
    double pocketDepth = _pipeSize >= 1.25 ? MainPktLarge : MainPktSmall;
    const double extLen  = 24.0;  // generous extension so all 4 curves overshoot their corners
    const double moveOut = 5.0;   // gap between pocket and bimini

    foreach (var mc in mainCurves)
    {
      var adjSeam = ClosestOf(mc, seam.Top, seam.Bottom, seam.Left, seam.Right);
      if (adjSeam == null) continue;

      // zipper = adjSeam offset toward centroid by pocketDepth
      var zipperRaw = OffsetToward(adjSeam, centroid, pocketDepth, tol);
      if (zipperRaw == null) continue;

      // offset sides outward (side walls of the pocket)
      var offLeft  = seam.Left  != null ? OffsetAway(seam.Left,  centroid, SidePktOutward, tol) : null;
      var offRight = seam.Right != null ? OffsetAway(seam.Right, centroid, SidePktOutward, tol) : null;
      if (offLeft == null || offRight == null) continue;

      // Extend all 4 curves so they overshoot, then trim at intersections (fillet radius=0)
      var topExt   = adjSeam.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)   ?? adjSeam.DuplicateCurve();
      var botExt   = zipperRaw.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line) ?? zipperRaw.DuplicateCurve();
      var leftExt  = offLeft.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)   ?? offLeft.DuplicateCurve();
      var rightExt = offRight.Extend(CurveEnd.Both, extLen, CurveExtensionStyle.Line)  ?? offRight.DuplicateCurve();

      // Build closed pocket outline: adjSeam (top) + offRight (right) + zipper (bottom) + offLeft (left)
      var pocketOutline = BuildFilletedRect(topExt, botExt, leftExt, rightExt, tol);

      // Mirrored end curves: section of adjSeam mirrored across each finished side at corner,
      // trimmed to meet the offset side curve
      var adjFin        = ClosestOf(adjSeam, fin.Top, fin.Bottom, fin.Left, fin.Right);
      var mirroredGeoms = new List<Curve>();
      if (adjFin != null)
      {
        if (fin.Left  != null) { var c = FindSharedEndpoint(adjFin, fin.Left);  if (c.IsValid) AddMirroredEndGeom(adjSeam, fin.Left,  c, offLeft,  pocketDepth, tol, mirroredGeoms); }
        if (fin.Right != null) { var c = FindSharedEndpoint(adjFin, fin.Right); if (c.IsValid) AddMirroredEndGeom(adjSeam, fin.Right, c, offRight, pocketDepth, tol, mirroredGeoms); }
      }

      // Translate all pocket geometry away from the bimini before adding to doc
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
      foreach (var m in mirroredGeoms)
      {
        m.Transform(xf);
        var id = doc.Objects.AddCurve(m, MakeAttr(cut1Idx));
        if (id != Guid.Empty) addedIds.Add(id);
      }

      // Group all pocket curves
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
  /// Builds a closed rectangular pocket outline from 4 extended curves by trimming each
  /// pair at their intersection (equivalent to fillet radius = 0 at all 4 corners).
  /// </summary>
  private static Curve? BuildFilletedRect(Curve top, Curve bot, Curve left, Curve right, double tol)
  {
    if (!FindIntersectionParam(top,  left,  tol, out var tTopL,  out var tLeftT))  return null;
    if (!FindIntersectionParam(top,  right, tol, out var tTopR,  out var tRightT)) return null;
    if (!FindIntersectionParam(bot,  left,  tol, out var tBotL,  out var tLeftB))  return null;
    if (!FindIntersectionParam(bot,  right, tol, out var tBotR,  out var tRightB)) return null;

    var topSeg   = top.Trim(Math.Min(tTopL,   tTopR),   Math.Max(tTopL,   tTopR));
    var botSeg   = bot.Trim(Math.Min(tBotL,   tBotR),   Math.Max(tBotL,   tBotR));
    var leftSeg  = left.Trim(Math.Min(tLeftT,  tLeftB),  Math.Max(tLeftT,  tLeftB));
    var rightSeg = right.Trim(Math.Min(tRightT, tRightB), Math.Max(tRightT, tRightB));

    if (topSeg == null || botSeg == null || leftSeg == null || rightSeg == null) return null;

    var joined = Curve.JoinCurves(new[] { topSeg, botSeg, leftSeg, rightSeg }, tol * 100);
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
  /// Mirrors a section of <paramref name="adjSeam"/> at the end nearest <paramref name="cornerPt"/>
  /// across <paramref name="finSide"/> at <paramref name="cornerPt"/>, then trims the result
  /// to where it first intersects <paramref name="offSide"/>. Appends result to <paramref name="result"/>.
  /// </summary>
  private static void AddMirroredEndGeom(Curve adjSeam, Curve finSide, Point3d cornerPt,
                                          Curve? offSide, double depth, double tol,
                                          List<Curve> result)
  {
    var totalLen  = adjSeam.GetLength();
    var isAtStart = adjSeam.PointAtStart.DistanceTo(cornerPt)
                    <= adjSeam.PointAtEnd.DistanceTo(cornerPt);
    Curve? section;
    if (isAtStart)
    {
      if (!adjSeam.LengthParameter(Math.Min(depth, totalLen * 0.5), out var tEnd)) return;
      section = adjSeam.Trim(adjSeam.Domain.T0, tEnd);
    }
    else
    {
      if (!adjSeam.LengthParameter(Math.Max(totalLen - depth, totalLen * 0.5), out var tStart)) return;
      section = adjSeam.Trim(tStart, adjSeam.Domain.T1);
    }
    if (section == null) return;

    var sideDir      = finSide.PointAtEnd - finSide.PointAtStart;
    sideDir.Unitize();
    var mirrorNormal = new Vector3d(-sideDir.Y, sideDir.X, 0);
    var mirrored     = section.DuplicateCurve();
    mirrored.Transform(Transform.Mirror(new Plane(cornerPt, mirrorNormal)));

    // Trim the mirrored curve to where it meets the offset side
    if (offSide != null && FindIntersectionParam(mirrored, offSide, tol, out var tM, out _))
    {
      var nearStart = mirrored.PointAtStart.DistanceTo(cornerPt) <= mirrored.PointAtEnd.DistanceTo(cornerPt);
      var trimmed   = nearStart
        ? mirrored.Trim(mirrored.Domain.T0, tM)
        : mirrored.Trim(tM, mirrored.Domain.T1);
      if (trimmed != null) mirrored = trimmed;
    }

    result.Add(mirrored);
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
        var testPt = RepresentativePoint(geom);
        if (testPt.IsValid && IsInsideOrOn(testPt, boundary, plane, tol))
          result.Add((geom.Duplicate()!, attr));
      }
    }

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
}
