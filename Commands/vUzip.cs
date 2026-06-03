using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static vTools.Commands.UzipCommon;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vUzip : Command
{
  public override string EnglishName => "vUzip";

  private const string SettingsSection            = "vUzip";
  private const double DefaultLeft                = 2.375;
  private const double DefaultRight               = 2.375;
  private const double DefaultBottom              = 1.125;
  private const double DefaultRadius              = 12.0;
  private const double ZipperValue                = DefaultLeft;

  private static string LayerCut       = DefaultLayerCutName;
  private static string LayerPlot      = DefaultLayerPlotName;
  private static string LayerReference = DefaultLayerReferenceName;


  // ── Settings ──────────────────────────────────────────────────────────────

  private sealed class UzipSettings
  {
    public double Left        { get; set; } = DefaultLeft;
    public double Right       { get; set; } = DefaultRight;
    public double Bottom      { get; set; } = DefaultBottom;
    public double Radius      { get; set; } = DefaultRadius;
    public bool   Glass       { get; set; } = false;
    public double GlassOffset { get; set; } = 0.25;
    public string GlassLayer  { get; set; } = "Glass";
    public bool   Vis         { get; set; } = false;
    public double VisOffset   { get; set; } = 0.75;
    public string VisLayer    { get; set; } = "Vis-Line";
    public string CenterLayer { get; set; } = "Reference";
    public bool   Parts       { get; set; } = false;
    public string Label       { get; set; } = DefaultLabel;
    public double Tail        { get; set; } = DefaultTail;
  }

  private static UzipSettings LoadSettings() =>
    ToolsOptionStore.Read<UzipSettings>(SettingsSection, section =>
    {
      var s = new UzipSettings();
      if (ToolsOptionStore.TryGetDouble(section, "left",        out var l))   s.Left        = l;
      if (ToolsOptionStore.TryGetDouble(section, "right",       out var r))   s.Right       = r;
      if (ToolsOptionStore.TryGetDouble(section, "bottom",      out var b))   s.Bottom      = b;
      if (ToolsOptionStore.TryGetDouble(section, "radius",      out var rad)) s.Radius      = rad;
      if (ToolsOptionStore.TryGetBool  (section, "glass",       out var g))   s.Glass       = g;
      if (ToolsOptionStore.TryGetDouble(section, "glassOffset", out var go))  s.GlassOffset = go;
      if (ToolsOptionStore.TryGetString(section, "glassLayer",  out var gl))  s.GlassLayer  = gl;
      if (ToolsOptionStore.TryGetBool  (section, "vis",         out var v))   s.Vis         = v;
      if (ToolsOptionStore.TryGetDouble(section, "visOffset",   out var vo))  s.VisOffset   = vo;
      if (ToolsOptionStore.TryGetString(section, "visLayer",    out var vl))  s.VisLayer    = vl;
      if (ToolsOptionStore.TryGetString(section, "centerLayer", out var cl))  s.CenterLayer = cl;
      if (ToolsOptionStore.TryGetBool  (section, "parts",       out var pt))  s.Parts       = pt;
      if (ToolsOptionStore.TryGetString(section, "label",       out var lb))  s.Label       = lb;
      if (ToolsOptionStore.TryGetDouble(section, "tail",        out var tl))  s.Tail        = tl;
      return s;
    });

  private static void SaveSettings(UzipSettings s) =>
    ToolsOptionStore.Update(SettingsSection, section =>
    {
      section["left"]        = s.Left;
      section["right"]       = s.Right;
      section["bottom"]      = s.Bottom;
      section["radius"]      = s.Radius;
      section["glass"]       = s.Glass;
      section["glassOffset"] = s.GlassOffset;
      section["glassLayer"]  = s.GlassLayer;
      section["vis"]         = s.Vis;
      section["visOffset"]   = s.VisOffset;
      section["visLayer"]    = s.VisLayer;
      section["centerLayer"] = s.CenterLayer;
      section["parts"]       = s.Parts;
      section["label"]       = s.Label;
      section["tail"]        = s.Tail;
    });
  // ── Parts config types ────────────────────────────────────────────────────

  // ── Fractional formatting ──────────────────────────────────────────────────

  private static (int Whole, int Num, int Den) ToFraction(double v, int den = 16)
  {
    int whole = (int)v;
    int num = (int)Math.Round((v - whole) * den);
    if (num >= den) { whole++; num = 0; }
    int a = num, b = den;
    while (b != 0) { int tmp = b; b = a % b; a = tmp; }
    int gcd = a == 0 ? 1 : a;
    return (whole, num / gcd, den / gcd);
  }

  private static string FmtDist(double v)
  {
    var (w, n, d) = ToFraction(v);
    return n == 0 ? $"{w}\"" : $"{w} {n}/{d}\"";
  }

  private static string FmtOpt(double v)
  {
    var (w, n, d) = ToFraction(v);
    return n == 0 ? w.ToString(CultureInfo.InvariantCulture) : $"{w}-{n}/{d}";
  }

  private static double? ParseDist(string s)
  {
    s = s.Trim();
    if (string.IsNullOrEmpty(s)) return null;
    if ("zipper".StartsWith(s, StringComparison.OrdinalIgnoreCase)) return ZipperValue;
    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain)) return plain;
    var m = Regex.Match(s, @"^(\d+)[\s\-\+]+(\d+)/(\d+)$");
    if (m.Success)
    {
      int den = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
      if (den == 0) return null;
      return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
           + (double)int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / den;
    }
    m = Regex.Match(s, @"^(\d+)/(\d+)$");
    if (m.Success)
    {
      int den = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
      if (den == 0) return null;
      return (double)int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / den;
    }
    return null;
  }

  private static double? GetDistSubprompt(string prompt, double current, double defaultValue)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({FmtDist(current)})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return current;
    if (res == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (raw.Equals("d", StringComparison.OrdinalIgnoreCase) ||
          raw.Equals("r", StringComparison.OrdinalIgnoreCase))
        return defaultValue;
      var v = ParseDist(raw);
      if (v.HasValue && v.Value >= 0.0) return v.Value;
      return current;
    }
    return null;
  }

  // ── Basic geometry helpers ────────────────────────────────────────────────

  // Domain-midpoint version (used by U-shape computation)
  private static Point3d CurveDomainMidpoint(Curve c)
  {
    double t = (c.Domain.Min + c.Domain.Max) * 0.5;
    return c.PointAt(t);
  }

  private static Point3d? JunctionPtOnBottom(Curve arm, Curve bottom)
  {
    if (!arm.ClosestPoints(bottom, out _, out var ptBtm)) return null;
    return ptBtm;
  }

  private static Vector3d ArmInwardTangent(Curve arm, Point3d juncPt)
  {
    double dStart = arm.PointAtStart.DistanceTo(juncPt);
    double dEnd   = arm.PointAtEnd.DistanceTo(juncPt);
    Vector3d tan  = dStart <= dEnd ? arm.TangentAtStart : -arm.TangentAtEnd;
    tan.Unitize();
    return tan;
  }

  private static Point3d FarEndPt(Curve c, Point3d refPt)
  {
    double dStart = c.PointAtStart.DistanceTo(refPt);
    double dEnd   = c.PointAtEnd.DistanceTo(refPt);
    return dEnd >= dStart ? c.PointAtEnd : c.PointAtStart;
  }

  private static Point3d NearEndPt(Curve c, Point3d refPt)
  {
    double dStart = c.PointAtStart.DistanceTo(refPt);
    double dEnd   = c.PointAtEnd.DistanceTo(refPt);
    return dStart <= dEnd ? c.PointAtStart : c.PointAtEnd;
  }

  private static Curve? TrimArmToOpenSide(Curve crv, Point3d splitPt, Point3d clickPt)
  {
    if (!crv.ClosestPoint(splitPt, out double t)) return null;
    var segs = crv.Split(t);
    if (segs == null || segs.Length == 0) return null;
    if (segs.Length == 1) return segs[0];
    return segs.OrderBy(seg => { seg.ClosestPoint(clickPt, out double tc); return seg.PointAt(tc).DistanceTo(clickPt); }).First();
  }

  // ── U-component identification ────────────────────────────────────────────

  private static (int Bottom, int Left, int Right) IdentifyUComponents(Curve[] curves, Point3d[]? clickPts, RhinoDoc doc)
  {
    double bestScore = double.MaxValue;
    int bottomIdx = 0, armA = 1, armB = 2;
    for (int btm = 0; btm < 3; btm++)
    {
      var arms = new[] { 0, 1, 2 }.Where(i => i != btm).ToArray();
      int a = arms[0], b = arms[1];
      if (!curves[a].ClosestPoints(curves[btm], out var ptArmA, out var ptBtmA)) continue;
      if (!curves[b].ClosestPoints(curves[btm], out var ptArmB, out var ptBtmB)) continue;
      double score = ptArmA.DistanceTo(ptBtmA) + ptArmB.DistanceTo(ptBtmB);
      Dbg.Write($"  IdentifyU candidate btm={btm} a={a} b={b} score={score:F4} (armA-btm={ptArmA.DistanceTo(ptBtmA):F4} armB-btm={ptArmB.DistanceTo(ptBtmB):F4})");
      if (score < bestScore) { bestScore = score; bottomIdx = btm; armA = a; armB = b; }
    }
    var bottom = curves[bottomIdx];
    double tMid   = (bottom.Domain.Min + bottom.Domain.Max) * 0.5;
    var midBtm    = bottom.PointAt(tMid);
    var view      = doc.Views.ActiveView;
    var cplaneZ   = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    var refA      = (clickPts != null && clickPts.Length == 3) ? clickPts[armA] : CurveDomainMidpoint(curves[armA]);
    var cross     = Vector3d.CrossProduct(bottom.TangentAt(tMid), refA - midBtm);
    double dot    = Vector3d.Multiply(cross, cplaneZ);
    Dbg.Write($"  IdentifyU: bottomIdx={bottomIdx} (len={curves[bottomIdx].GetLength():F3}) bestScore={bestScore:F4}");
    Dbg.Write($"    armA={armA}(len={curves[armA].GetLength():F3}) armB={armB}(len={curves[armB].GetLength():F3}) cross·cplaneZ={dot:F6}");
    if (dot >= 0) { Dbg.Write($"    → left={armA} right={armB}"); return (bottomIdx, armA, armB); }
    Dbg.Write($"    → left={armB} right={armA}");
    return (bottomIdx, armB, armA);
  }

  // ── Offset helpers ────────────────────────────────────────────────────────

  private static List<Curve> OffsetBothSides(Curve curve, double distance, Vector3d normal, double tol)
  {
    var plane   = new Plane(curve.PointAtStart, normal);
    var pos     = curve.Offset(plane,  distance, tol, CurveOffsetCornerStyle.Sharp);
    var neg     = curve.Offset(plane, -distance, tol, CurveOffsetCornerStyle.Sharp);
    var results = new List<Curve>();
    if (pos != null) results.AddRange(pos);
    if (neg != null) results.AddRange(neg);
    return results;
  }

  private static Curve? OffsetCurveInward(Curve curve, double distance, Point3d centerPt, Vector3d normal, double tol)
  {
    if (distance <= RhinoMath.ZeroTolerance) return curve.DuplicateCurve();
    var pos = curve.Offset(centerPt, normal,  distance, tol, CurveOffsetCornerStyle.Sharp);
    var neg = curve.Offset(centerPt, normal, -distance, tol, CurveOffsetCornerStyle.Sharp);
    var candidates = new List<Curve>();
    if (pos != null) candidates.AddRange(pos);
    if (neg != null) candidates.AddRange(neg);
    if (candidates.Count == 0) return null;
    return candidates.OrderBy(c =>
    {
      if (c.ClosestPoint(centerPt, out double t)) return c.PointAt(t).DistanceTo(centerPt);
      return c.PointAt((c.Domain.Min + c.Domain.Max) * 0.5).DistanceTo(centerPt);
    }).First();
  }

  // ── Intersection / extension helpers ──────────────────────────────────────

  private static (Point3d Pt, double T1, double T2)? FindNearestIntersection(Curve c1, Curve c2, Point3d refPt, double tol)
  {
    var events = Intersection.CurveCurve(c1, c2, tol, 0.0);
    if (events == null || events.Count == 0) return null;
    (Point3d Pt, double T1, double T2)? best = null; double bestDist = double.MaxValue;
    foreach (var e in events) { double d = e.PointA.DistanceTo(refPt); if (d < bestDist) { bestDist = d; best = (e.PointA, e.ParameterA, e.ParameterB); } }
    return best;
  }

  private static Point3d WalkAlongCurve(Curve crv, Point3d fromPt, double distance, Point3d towardPt)
  {
    if (!crv.ClosestPoint(fromPt, out double tFrom)) return fromPt;
    if (!crv.ClosestPoint(towardPt, out double tTo)) return fromPt;
    double lenToFrom = crv.GetLength(new Interval(crv.Domain.Min, tFrom));
    double targetLen = tTo > tFrom ? lenToFrom + distance : lenToFrom - distance;
    if (targetLen < 0) return crv.PointAtStart;
    if (!crv.LengthParameter(targetLen, out double tTarget)) return fromPt;
    return crv.PointAt(tTarget);
  }

  private static Curve ExtendAtNearEnd(Curve curve, Point3d refPt, double amount)
  {
    var nurbs = curve.ToNurbsCurve();
    if (nurbs == null) return curve;
    double dStart = nurbs.PointAtStart.DistanceTo(refPt);
    double dEnd   = nurbs.PointAtEnd.DistanceTo(refPt);
    var end    = dStart <= dEnd ? CurveEnd.Start : CurveEnd.End;
    var result = nurbs.Extend(end, amount, CurveExtensionStyle.Line);
    return result ?? nurbs;
  }

  private static Curve ExtendBothEnds(Curve curve, double amount)
  {
    var nurbs = curve.ToNurbsCurve();
    if (nurbs == null) return curve;
    var r1    = nurbs.Extend(CurveEnd.Start, amount, CurveExtensionStyle.Line);
    var base1 = r1 ?? (NurbsCurve)nurbs;
    var r2    = base1.Extend(CurveEnd.End, amount, CurveExtensionStyle.Line);
    return r2 ?? base1;
  }

  private static Curve? FilletArcOnly(Curve c1, Point3d hint1, Curve c2, Point3d hint2, double radius, double tol, double angleTol)
  {
    var results = Curve.CreateFilletCurves(c1, hint1, c2, hint2, radius, false, false, false, tol, angleTol);
    return results != null && results.Length > 0 ? results[0] : null;
  }

  // Returns the largest radius ≤ maxRadius such that the fillet arc tangent on 'arm'
  // lands within arm's domain (not past openTip). Uses the interior corner angle at isect.
  private static double ClampFilletRadius(double maxRadius, Curve arm, Curve bottom,
    Point3d isect, Point3d openTip, Point3d towardOtherIsect)
  {
    if (!arm.ClosestPoint(isect,    out double tI)) return maxRadius;
    if (!arm.ClosestPoint(openTip,  out double tT)) return maxRadius;
    double armAvail = arm.GetLength(new Interval(Math.Min(tI, tT), Math.Max(tI, tT)));
    if (armAvail <= 1e-9) return maxRadius;
    if (!bottom.ClosestPoint(isect, out double tB)) return maxRadius;
    // Build unit vectors pointing away from the corner into each curve (interior angle).
    var armDir = arm.TangentAt(tI);
    if (Vector3d.Multiply(armDir, openTip - isect) < 0) armDir = -armDir;
    var btmDir = bottom.TangentAt(tB);
    if (Vector3d.Multiply(btmDir, towardOtherIsect - isect) < 0) btmDir = -btmDir;
    double cosA = armDir * btmDir;  // cos of interior corner angle
    double sinA = Math.Sqrt(Math.Max(0.0, 1.0 - cosA * cosA));
    if (sinA < 1e-6) return maxRadius;  // near-parallel; can't bound
    // tangent_length = R / tan(θ/2)  →  R_max = armAvail * tan(θ/2) = armAvail * sinA/(1+cosA)
    double rMax = armAvail * sinA / (1.0 + cosA) * 0.98;  // 2% margin
    return Math.Min(maxRadius, Math.Max(rMax, 1e-3));
  }

  private static (Point3d PtOnC1, Point3d PtOnC2) ArcTangentPts(Curve arc, Curve c1, Curve c2)
  {
    var pStart = arc.PointAtStart; var pEnd = arc.PointAtEnd;
    c1.ClosestPoint(pStart, out double t1s); c2.ClosestPoint(pStart, out double t2s);
    if (c1.PointAt(t1s).DistanceTo(pStart) <= c2.PointAt(t2s).DistanceTo(pStart)) return (pStart, pEnd);
    return (pEnd, pStart);
  }

  private static Curve? TrimKeepSide(Curve curve, Point3d splitPt, Point3d keepPt)
  {
    if (!curve.ClosestPoint(splitPt, out double tSplit)) { Dbg.Write($"  TrimKeepSide: ClosestPoint failed for splitPt={splitPt}"); return null; }
    if (!curve.ClosestPoint(keepPt,  out double tKeep))  { Dbg.Write($"  TrimKeepSide: ClosestPoint failed for keepPt={keepPt}");  return null; }
    double tLo = Math.Min(tSplit, tKeep);
    double tHi = Math.Max(tSplit, tKeep);
    if (tHi - tLo < RhinoMath.ZeroTolerance)
    {
      // splitPt is past the curve end and clamps to the same endpoint as keepPt.
      // Signal caller to use a bridge line instead.
      Dbg.Write($"  TrimKeepSide: splitPt past end (tSplit={tSplit:F6} tKeep={tKeep:F6}) → null for bridge");
      return null;
    }
    var result = curve.Trim(tLo, tHi);
    if (result == null) Dbg.Write($"  TrimKeepSide: Trim({tLo:F6},{tHi:F6}) returned null domain=[{curve.Domain.T0:F6},{curve.Domain.T1:F6}]");
    return result;
  }

  private static Curve? TrimBetween(Curve curve, Point3d ptA, Point3d ptB)
  {
    if (!curve.ClosestPoint(ptA, out double tA)) return null;
    if (!curve.ClosestPoint(ptB, out double tB)) return null;
    if (Math.Abs(tA - tB) < 1e-10) return null;
    double tLo = Math.Min(tA, tB), tHi = Math.Max(tA, tB);
    return curve.Trim(tLo, tHi);
  }

  // ── Trim/extend to boundary curve ─────────────────────────────────────────

  private static Curve? TrimExtendToCurve(Curve centerCrv, Curve clipCrv, double tol)
  {
    var ptStart = centerCrv.PointAtStart; var ptEnd = centerCrv.PointAtEnd;
    var nurbs   = centerCrv.ToNurbsCurve();
    if (nurbs == null) return null;
    var extended = nurbs.Extend(CurveEnd.Both, 1000.0, CurveExtensionStyle.Line) ?? (Curve)nurbs;
    if (!extended.ClosestPoint(ptStart, out double tOrigS)) return null;
    if (!extended.ClosestPoint(ptEnd,   out double tOrigE)) return null;
    static double SqDist(Point3d p, Point3d q) { var d = p - q; return d.X*d.X + d.Y*d.Y + d.Z*d.Z; }
    double tS, tE;
    var events = Intersection.CurveCurve(extended, clipCrv, tol, tol);
    if (events != null && events.Count > 0)
    {
      var all  = Enumerable.Range(0, events.Count).Select(i => (T: events[i].ParameterA, Pt: events[i].PointA)).ToList();
      double midT = (tOrigS + tOrigE) * 0.5;
      var sEv = all.Where(e => e.T <= midT).ToList();
      var eEv = all.Where(e => e.T >= midT).ToList();
      tS = sEv.Count > 0 ? sEv.OrderBy(e => SqDist(e.Pt, ptStart)).First().T : tOrigS;
      tE = eEv.Count > 0 ? eEv.OrderBy(e => SqDist(e.Pt, ptEnd)).First().T   : tOrigE;
    }
    else
    {
      if (!clipCrv.ClosestPoint(ptStart, out double tCs)) return null;
      if (!clipCrv.ClosestPoint(ptEnd,   out double tCe)) return null;
      if (!extended.ClosestPoint(clipCrv.PointAt(tCs), out tS)) return null;
      if (!extended.ClosestPoint(clipCrv.PointAt(tCe), out tE)) return null;
    }
    if (Math.Abs(tS - tE) < tol) return null;
    if (tS > tE) (tS, tE) = (tE, tS);
    return extended.Trim(tS, tE);
  }

  // ── ComputeResult (core U-shape) ──────────────────────────────────────────

  private static Curve? ComputeResult(Curve[] rawCurves, Point3d[]? clickPts, double offL, double offR, double offB, double radius, RhinoDoc doc)
  {
    Dbg.Write($"ComputeResult: offL={offL:F3} offR={offR:F3} offB={offB:F3} radius={radius:F3} curves={rawCurves.Length}");
    for (int i = 0; i < rawCurves.Length; i++)
      Dbg.Write($"  raw[{i}] len={rawCurves[i].GetLength():F3} domain={rawCurves[i].Domain}");
    var (bottomIdx, leftIdx, rightIdx) = IdentifyUComponents(rawCurves, clickPts, doc);
    var btmCrv   = rawCurves[bottomIdx];
    var leftCrv  = rawCurves[leftIdx];
    var rightCrv = rawCurves[rightIdx];
    var view     = doc.Views.ActiveView;
    var cplaneNormal = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    var juncLeft  = JunctionPtOnBottom(leftCrv,  btmCrv);
    var juncRight = JunctionPtOnBottom(rightCrv, btmCrv);
    if (juncLeft == null || juncRight == null) { Dbg.Write($"  → null: juncLeft={juncLeft.HasValue} juncRight={juncRight.HasValue}"); return null; }
    Dbg.Write($"  juncLeft={juncLeft.Value} juncRight={juncRight.Value}");
    var clickL = (clickPts != null && clickPts.Length == 3) ? clickPts[leftIdx]  : CurveDomainMidpoint(leftCrv);
    var clickR = (clickPts != null && clickPts.Length == 3) ? clickPts[rightIdx] : CurveDomainMidpoint(rightCrv);
    if (leftCrv.ClosestPoints(btmCrv,  out var armPtL, out _)) { var seg = TrimArmToOpenSide(leftCrv,  armPtL, clickL); if (seg != null) leftCrv  = seg; }
    if (rightCrv.ClosestPoints(btmCrv, out var armPtR, out _)) { var seg = TrimArmToOpenSide(rightCrv, armPtR, clickR); if (seg != null) rightCrv = seg; }
    Dbg.Write($"  trimmedArms: left.len={leftCrv.GetLength():F3} right.len={rightCrv.GetLength():F3}");
    Point3d insidePt;
    if (clickPts != null && clickPts.Length == 3)
    {
      var cl = clickPts[leftIdx]; var cr = clickPts[rightIdx];
      var clickMid = new Point3d((cl.X+cr.X)*0.5, (cl.Y+cr.Y)*0.5, (cl.Z+cr.Z)*0.5);
      var juncMid  = new Point3d((juncLeft.Value.X+juncRight.Value.X)*0.5, (juncLeft.Value.Y+juncRight.Value.Y)*0.5, (juncLeft.Value.Z+juncRight.Value.Z)*0.5);
      insidePt = new Point3d((juncMid.X+clickMid.X)*0.5, (juncMid.Y+clickMid.Y)*0.5, (juncMid.Z+clickMid.Z)*0.5);
    }
    else
    {
      double leftLen = Math.Max(leftCrv.GetLength(), doc.ModelAbsoluteTolerance * 10);
      double step    = Math.Max(doc.ModelAbsoluteTolerance * 100, leftLen * 0.1);
      var inwardTan  = ArmInwardTangent(leftCrv, juncLeft.Value);
      insidePt = new Point3d((juncLeft.Value.X+juncRight.Value.X)*0.5 + inwardTan.X*step, (juncLeft.Value.Y+juncRight.Value.Y)*0.5 + inwardTan.Y*step, (juncLeft.Value.Z+juncRight.Value.Z)*0.5 + inwardTan.Z*step);
    }
    Dbg.Write($"  insidePt={insidePt} cplaneNormal={cplaneNormal}");
    Dbg.Write($"  leftCrv: start={leftCrv.PointAtStart} end={leftCrv.PointAtEnd} mid={CurveDomainMidpoint(leftCrv)}");
    Dbg.Write($"  rightCrv: start={rightCrv.PointAtStart} end={rightCrv.PointAtEnd} mid={CurveDomainMidpoint(rightCrv)}");
    Dbg.Write($"  btmCrv: start={btmCrv.PointAtStart} end={btmCrv.PointAtEnd} mid={CurveDomainMidpoint(btmCrv)}");
    double tol     = doc.ModelAbsoluteTolerance;
    var offLeft    = OffsetCurveInward(leftCrv,  offL, insidePt, cplaneNormal, tol);
    var offRight   = OffsetCurveInward(rightCrv, offR, insidePt, cplaneNormal, tol);
    var offBottom  = OffsetCurveInward(btmCrv,   offB, insidePt, cplaneNormal, tol);
    if (offLeft == null || offRight == null || offBottom == null) { Dbg.Write($"  → null: offLeft={offLeft != null} offRight={offRight != null} offBottom={offBottom != null}"); return null; }
    Dbg.Write($"  offsets: left.len={offLeft.GetLength():F3} mid={CurveDomainMidpoint(offLeft)} right.len={offRight.GetLength():F3} mid={CurveDomainMidpoint(offRight)} bottom.len={offBottom.GetLength():F3} mid={CurveDomainMidpoint(offBottom)}");
    double extAmt  = 3.0 * radius + Math.Max(offL, Math.Max(offR, offB));
    // Compute open-tip hints BEFORE extension: FarEndPt on the original offset is always the un-extended end.
    var hintLOpen  = FarEndPt(offLeft,  juncLeft.Value);
    var hintROpen  = FarEndPt(offRight, juncRight.Value);
    Dbg.Write($"  offLeft: start={offLeft.PointAtStart} end={offLeft.PointAtEnd}  hintLOpen={hintLOpen}");
    Dbg.Write($"  offRight: start={offRight.PointAtStart} end={offRight.PointAtEnd}  hintROpen={hintROpen}");
    var extLeft    = ExtendAtNearEnd(offLeft,  juncLeft.Value,  extAmt);
    var extRight   = ExtendAtNearEnd(offRight, juncRight.Value, extAmt);
    var extBottom  = ExtendBothEnds(offBottom, extAmt);
    Dbg.Write($"  extLeft: start={extLeft.PointAtStart} end={extLeft.PointAtEnd} len={extLeft.GetLength():F3}");
    Dbg.Write($"  extRight: start={extRight.PointAtStart} end={extRight.PointAtEnd} len={extRight.GetLength():F3}");
    Dbg.Write($"  extended: left.len={extLeft.GetLength():F3} right.len={extRight.GetLength():F3} bottom.len={extBottom.GetLength():F3} extAmt={extAmt:F3}");
    var resL = FindNearestIntersection(extLeft,  extBottom, juncLeft.Value,  tol);
    var resR = FindNearestIntersection(extRight, extBottom, juncRight.Value, tol);
    if (resL == null || resR == null) { Dbg.Write($"  → null: resL={resL.HasValue} resR={resR.HasValue} (no arm-bottom intersection)"); return null; }
    var (isectL, _, _) = resL.Value; var (isectR, _, _) = resR.Value;
    Dbg.Write($"  intersections: isectL={isectL} isectR={isectR}");
    // Clamp fillet radius per arm so the arc tangent can land within the arm bounds.
    double radiusL = ClampFilletRadius(radius, extLeft,  extBottom, isectL, hintLOpen, isectR);
    double radiusR = ClampFilletRadius(radius, extRight, extBottom, isectR, hintROpen, isectL);
    Dbg.Write($"  radii: requested={radius:F3} radiusL={radiusL:F3} radiusR={radiusR:F3}");
    var hintArmL = WalkAlongCurve(extLeft,   isectL, radiusL, hintLOpen);
    var hintBtmL = WalkAlongCurve(extBottom, isectL, radiusL, isectR);
    var hintArmR = WalkAlongCurve(extRight,  isectR, radiusR, hintROpen);
    var hintBtmR = WalkAlongCurve(extBottom, isectR, radiusR, isectL);
    double angleTol = doc.ModelAngleToleranceRadians;
    var arcL = FilletArcOnly(extLeft,  hintArmL, extBottom, hintBtmL, radiusL, tol, angleTol); if (arcL == null) { Dbg.Write("  → null: arcL fillet failed"); return null; }
    var arcR = FilletArcOnly(extRight, hintArmR, extBottom, hintBtmR, radiusR, tol, angleTol); if (arcR == null) { Dbg.Write("  → null: arcR fillet failed"); return null; }
    var (tanLArm, tanLBtm) = ArcTangentPts(arcL, extLeft,  extBottom);
    var (tanRArm, tanRBtm) = ArcTangentPts(arcR, extRight, extBottom);
    Dbg.Write($"  tanPts: tanLArm={tanLArm} hintLOpen={hintLOpen} tanRArm={tanRArm} hintROpen={hintROpen}");
    var trimmedLeft   = TrimKeepSide(extLeft,   tanLArm, hintLOpen);
    var trimmedRight  = TrimKeepSide(extRight,  tanRArm, hintROpen);
    var trimmedBottom = TrimBetween(extBottom, tanLBtm, tanRBtm);
    if (trimmedLeft == null || trimmedRight == null || trimmedBottom == null) { Dbg.Write($"  → null: trimmedLeft={trimmedLeft != null} trimmedRight={trimmedRight != null} trimmedBottom={trimmedBottom != null}"); return null; }
    Dbg.Write($"  trimmed: left.len={trimmedLeft.GetLength():F3} right.len={trimmedRight.GetLength():F3} bottom.len={trimmedBottom.GetLength():F3}");
    var pieces = new Curve[] { trimmedLeft, arcL, trimmedBottom, arcR, trimmedRight };
    var joined = Curve.JoinCurves(pieces, tol);
    if (joined == null || joined.Length == 0) { Dbg.Write("  → null: JoinCurves failed"); return null; }
    Dbg.Write($"  → joined[0].len={joined[0].GetLength():F3} start={joined[0].PointAtStart} end={joined[0].PointAtEnd} domain=[{joined[0].Domain.T0:F6},{joined[0].Domain.T1:F6}]");
    return joined[0];
  }
  private static void ApplyLayerRuntime(LayerRuntime r)
  { LayerReference = r.ReferenceName; LayerPlot = r.PlotName; LayerCut = r.CutName; }

  private static Point3d? PartLeftEndOuterAnchor(RhinoDoc doc, string? groupName)
  {
    if (string.IsNullOrWhiteSpace(groupName)) return null;
    var group = doc.Groups.FindName(groupName); if (group == null) return null;
    var cutCurves = new List<Curve>();
    foreach (var member in doc.Groups.GroupMembers(group.Index))
    {
      if (member == null) continue;
      var obj = doc.Objects.FindId(member.Id); if (obj == null) continue;
      if (!string.Equals(doc.Layers[obj.Attributes.LayerIndex]?.FullPath, LayerCut, StringComparison.OrdinalIgnoreCase)) continue;
      if (obj.Geometry is Curve c) cutCurves.Add(c);
    }
    if (cutCurves.Count < 2) return null;
    var outer = cutCurves.OrderByDescending(c => c.GetLength()).FirstOrDefault(); if (outer == null) return null;
    return outer.PointAtStart.X <= outer.PointAtEnd.X ? outer.PointAtStart : outer.PointAtEnd;
  }

  private sealed class PlacementPreviewConduit : DisplayConduit
  {
    private readonly List<(GeometryBase Geom, System.Drawing.Color Color)> _items;
    public Point3d BasePoint; public Point3d CurrentPoint; public bool DrawEnabled;

    public PlacementPreviewConduit(IEnumerable<(GeometryBase Geom, System.Drawing.Color Color)> items, Point3d basePoint)
    { _items = new List<(GeometryBase, System.Drawing.Color)>(items); BasePoint = basePoint; CurrentPoint = basePoint; }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (!DrawEnabled) return;
      var xform = Transform.Translation(CurrentPoint - BasePoint);
      foreach (var (geom, color) in _items)
      {
        var draw = geom.Duplicate(); if (draw == null) continue;
        draw.Transform(xform);
        switch (draw)
        {
          case Curve c:       e.Display.DrawCurve(c, color, 1); break;
          case TextEntity te: e.Display.DrawAnnotation(te, color); break;
          case Rhino.Geometry.Point p: e.Display.DrawPoint(p.Location, Rhino.Display.PointStyle.Simple, 2, color); break;
        }
      }
    }
  }

  private static (bool NeedsRebuild, string Label, double Tail) PlaceGroupsWithPickOrDelete(RhinoDoc doc, List<string> groupNames, string? anchorGroupName, string label, double tail)
  {
    var selectedIds = new HashSet<Guid>();
    foreach (var name in groupNames)
    {
      var group = doc.Groups.FindName(name); if (group == null) continue;
      foreach (var member in doc.Groups.GroupMembers(group.Index)) if (member != null && doc.Objects.FindId(member.Id) != null) selectedIds.Add(member.Id);
    }
    if (selectedIds.Count == 0) return (false, label, tail);
    var bbox = BoundingBox.Empty;
    foreach (var id in selectedIds) { var obj = doc.Objects.FindId(id); if (obj?.Geometry == null) continue; var gb = obj.Geometry.GetBoundingBox(true); if (gb.IsValid) bbox = bbox.IsValid ? BoundingBox.Union(bbox, gb) : gb; }
    if (!bbox.IsValid) return (false, label, tail);
    var basePoint = PartLeftEndOuterAnchor(doc, anchorGroupName) ?? new Point3d(bbox.Min.X, bbox.Max.Y, 0.5 * (bbox.Min.Z + bbox.Max.Z));
    var previewItems = new List<(GeometryBase Geom, System.Drawing.Color Color)>();
    foreach (var id in selectedIds)
    {
      var obj = doc.Objects.FindId(id); if (obj?.Geometry == null) continue;
      var color = (doc.Layers[obj.Attributes.LayerIndex]?.Color) ?? System.Drawing.Color.Cyan;
      var dup = obj.Geometry.Duplicate(); if (dup != null) previewItems.Add((dup, color));
    }
    foreach (var id in selectedIds) doc.Objects.Hide(id, true);
    var conduit = new PlacementPreviewConduit(previewItems, basePoint); conduit.Enabled = true; doc.Views.Redraw();
    var gp = new GetPoint(); gp.SetCommandPrompt("Pick placement point for created parts (Esc to cancel and delete)");
    var labelOptIdx = gp.AddOption("Label", label); var tailOptIdx = gp.AddOption("Tail", tail.ToString("0.##"));
    gp.DynamicDraw += (_, e) =>
    {
      conduit.CurrentPoint = e.CurrentPoint; var move = e.CurrentPoint - basePoint; var xform = Transform.Translation(move);
      foreach (var (geom, color) in previewItems)
      { var draw = geom.Duplicate(); if (draw == null) continue; draw.Transform(xform); if (draw is Curve c) e.Display.DrawCurve(c, color, 1); else if (draw is TextEntity te) e.Display.DrawAnnotation(te, color); }
    };
    while (true)
    {
      var result = gp.Get();
      if (result == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt != null && opt.Index == labelOptIdx)
        {
          conduit.DrawEnabled = true; doc.Views.Redraw();
          var newLabel = label; RhinoGet.GetString("Label", true, ref newLabel); conduit.DrawEnabled = false; doc.Views.Redraw();
          var trimmed = (newLabel ?? DefaultLabel).Trim();
          if (trimmed != label) { conduit.Enabled = false; return (true, trimmed, tail); }
          gp.ClearCommandOptions(); labelOptIdx = gp.AddOption("Label", label); tailOptIdx = gp.AddOption("Tail", tail.ToString("0.##"));
        }
        else if (opt != null && opt.Index == tailOptIdx)
        {
          conduit.DrawEnabled = true; doc.Views.Redraw();
          var newTail = tail; RhinoGet.GetNumber("Tail length", true, ref newTail); conduit.DrawEnabled = false; doc.Views.Redraw();
          newTail = Math.Max(0.0, newTail);
          if (Math.Abs(newTail - tail) > RhinoMath.ZeroTolerance) { conduit.Enabled = false; return (true, label, newTail); }
          gp.ClearCommandOptions(); labelOptIdx = gp.AddOption("Label", label); tailOptIdx = gp.AddOption("Tail", tail.ToString("0.##"));
        }
        continue;
      }
      conduit.Enabled = false;
      if (result != GetResult.Point) { DeleteCreatedGroupsAndMembers(doc, groupNames); doc.Views.Redraw(); return (false, label, tail); }
      var target = gp.Point(); var moveVec = target - basePoint;
      if (moveVec.Length > doc.ModelAbsoluteTolerance)
      {
        var xform = Transform.Translation(moveVec);
        foreach (var id in selectedIds.ToList()) { var newId = doc.Objects.Transform(id, xform, true); if (newId != Guid.Empty) { selectedIds.Remove(id); selectedIds.Add(newId); } }
      }
      foreach (var id in selectedIds) doc.Objects.Show(id, true);
      doc.Objects.UnselectAll(); foreach (var id in selectedIds) doc.Objects.Select(id); doc.Views.Redraw();
      return (false, label, tail);
    }
  }

  // ── Debug log ──────────────────────────────────────────────────────────────

  private static class Dbg
  {
    private static string _path = string.Empty;

    public static void Init(string pluginDir)
    {
      try
      {
        var logsDir = Path.Combine(pluginDir, "logs");
        Directory.CreateDirectory(logsDir);
        _path = Path.Combine(logsDir, "vUzip-debug.log");
      }
      catch { _path = string.Empty; }
    }

    public static void Run()
      => Write($"\n=== vUzip run {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

    public static void Write(string msg)
    {
      if (string.IsNullOrEmpty(_path)) return;
      try { File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
      catch { }
    }
  }

  // ── Preview conduit ───────────────────────────────────────────────────────

  private sealed class PreviewConduit : DisplayConduit
  {
    public Curve? Curve { get; set; }
    public Color  CenterColor { get; set; } = Color.Cyan;
    public List<(Curve Crv, Color Col)> SideCurves { get; } = new();
    // Parts-selection highlight: curves drawn in bright yellow so they are visible
    // without relying on doc.Objects.Select (which blocks re-clicking selected objects)
    public List<Curve> PartsHighlight { get; } = new();
    private static readonly Color PartsHighlightColor = Color.FromArgb(255, 215, 0);

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (Curve != null) e.Display.DrawCurve(Curve, CenterColor, 2);
      foreach (var (crv, col) in SideCurves) e.Display.DrawCurve(crv, col, 1);
      foreach (var crv in PartsHighlight) e.Display.DrawCurve(crv, PartsHighlightColor, 2);
    }
  }

  // ── Options dialog ────────────────────────────────────────────────────────

  private sealed class OptionsDialog : Eto.Forms.Dialog<bool>
  {
    readonly Eto.Forms.DropDown _centerDrop;
    readonly Eto.Forms.DropDown _glassDrop;
    readonly Eto.Forms.TextBox  _glassOffBox;
    readonly Eto.Forms.DropDown _visDrop;
    readonly Eto.Forms.TextBox  _visOffBox;
    readonly Eto.Forms.TextBox  _labelBox;
    readonly Eto.Forms.TextBox  _tailBox;
    readonly List<(string Name, System.Drawing.Color Col)> _docLayers;

    public OptionsDialog(RhinoDoc doc, UzipSettings s)
    {
      Title = "vUzip Options"; Resizable = false; Result = false;
      _docLayers = doc.Layers.Where(l => !l.IsDeleted).Select(l => (Name: l.FullPath, Col: l.Color)).ToList();

      Eto.Forms.DropDown MakeDrop(string current)
      {
        var d = new Eto.Forms.DropDown { Width = 220 }; bool found = false;
        foreach (var (name, _) in _docLayers) { d.Items.Add(new Eto.Forms.ListItem { Text = name, Key = name }); if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) found = true; }
        if (!found && !string.IsNullOrWhiteSpace(current)) d.Items.Insert(0, new Eto.Forms.ListItem { Text = current + " (not in doc)", Key = current });
        d.SelectedKey = current; if (d.SelectedIndex < 0) d.SelectedIndex = 0; return d;
      }
      Eto.Forms.Drawable MakeSwatch(Eto.Forms.DropDown drop)
      {
        var sw = new Eto.Forms.Drawable { Size = new Eto.Drawing.Size(16, 16) };
        sw.Paint += (_, pe) =>
        {
          var key = drop.SelectedKey ?? ""; var hit = _docLayers.FirstOrDefault(l => string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase));
          var sc  = hit.Name != null ? hit.Col : System.Drawing.Color.Gray;
          pe.Graphics.FillRectangle(new Eto.Drawing.Color(sc.R/255f, sc.G/255f, sc.B/255f), new Eto.Drawing.RectangleF(0, 0, 16, 16));
        };
        drop.SelectedIndexChanged += (_, _) => sw.Invalidate(); return sw;
      }
      Eto.Forms.StackLayout SwatchRow(Eto.Forms.Drawable sw, Eto.Forms.DropDown dd) =>
        new Eto.Forms.StackLayout { Orientation = Eto.Forms.Orientation.Horizontal, Spacing = 4, VerticalContentAlignment = Eto.Forms.VerticalAlignment.Center, Items = { sw, new Eto.Forms.StackLayoutItem(dd, true) } };
      static Eto.Forms.Label Lbl(string t) => new Eto.Forms.Label { Text = t, VerticalAlignment = Eto.Forms.VerticalAlignment.Center };

      _centerDrop  = MakeDrop(s.CenterLayer);
      _glassDrop   = MakeDrop(s.GlassLayer);
      _glassOffBox = new Eto.Forms.TextBox { Text = s.GlassOffset.ToString(CultureInfo.InvariantCulture), Width = 80 };
      _visDrop     = MakeDrop(s.VisLayer);
      _visOffBox   = new Eto.Forms.TextBox { Text = s.VisOffset.ToString(CultureInfo.InvariantCulture),   Width = 80 };
      _labelBox    = new Eto.Forms.TextBox { Text = s.Label,                                              Width = 160 };
      _tailBox     = new Eto.Forms.TextBox { Text = s.Tail.ToString(CultureInfo.InvariantCulture),        Width = 80 };

      var centerSwatch = MakeSwatch(_centerDrop); var glassSwatch = MakeSwatch(_glassDrop); var visSwatch = MakeSwatch(_visDrop);
      var resetBtn = new Eto.Forms.Button { Text = "Reset to Defaults" };
      var okBtn    = new Eto.Forms.Button { Text = "OK",     Width = 80 };
      var cancelBtn = new Eto.Forms.Button { Text = "Cancel", Width = 80 };
      DefaultButton = okBtn; AbortButton = cancelBtn;
      resetBtn.Click += (_, _) =>
      {
        var d = new UzipSettings();
        _centerDrop.SelectedKey = d.CenterLayer; _glassDrop.SelectedKey = d.GlassLayer;
        _glassOffBox.Text = d.GlassOffset.ToString(CultureInfo.InvariantCulture);
        _visDrop.SelectedKey = d.VisLayer; _visOffBox.Text = d.VisOffset.ToString(CultureInfo.InvariantCulture);
        _labelBox.Text = d.Label; _tailBox.Text = d.Tail.ToString(CultureInfo.InvariantCulture);
        centerSwatch.Invalidate(); glassSwatch.Invalidate(); visSwatch.Invalidate();
      };
      okBtn.Click += (_, _) => Close(true); cancelBtn.Click += (_, _) => Close(false);
      var table = new Eto.Forms.TableLayout
      {
        Spacing = new Eto.Drawing.Size(8, 6),
        Rows =
        {
          new Eto.Forms.TableRow(Lbl("Center layer:"), new Eto.Forms.TableCell(SwatchRow(centerSwatch, _centerDrop), true)),
          new Eto.Forms.TableRow(Lbl("Glass offset:"), new Eto.Forms.TableCell(_glassOffBox)),
          new Eto.Forms.TableRow(Lbl("Glass layer:"),  new Eto.Forms.TableCell(SwatchRow(glassSwatch,  _glassDrop),  true)),
          new Eto.Forms.TableRow(Lbl("Vis offset:"),   new Eto.Forms.TableCell(_visOffBox)),
          new Eto.Forms.TableRow(Lbl("Vis layer:"),    new Eto.Forms.TableCell(SwatchRow(visSwatch,    _visDrop),    true)),
          new Eto.Forms.TableRow(Lbl("Label:"),        new Eto.Forms.TableCell(_labelBox)),
          new Eto.Forms.TableRow(Lbl("Tail:"),         new Eto.Forms.TableCell(_tailBox)),
        },
      };
      Content = new Eto.Forms.StackLayout
      {
        Padding = new Eto.Drawing.Padding(12), Spacing = 10,
        Items =
        {
          new Eto.Forms.StackLayoutItem(table, true),
          new Eto.Forms.StackLayout { Orientation = Eto.Forms.Orientation.Horizontal, Spacing = 6,
            Items = { resetBtn, new Eto.Forms.StackLayoutItem(new Eto.Forms.Panel(), true), cancelBtn, okBtn } }
        }
      };
    }

    public void ApplyTo(UzipSettings s)
    {
      if (!string.IsNullOrEmpty(_centerDrop.SelectedKey)) s.CenterLayer = _centerDrop.SelectedKey;
      if (!string.IsNullOrEmpty(_glassDrop.SelectedKey))  s.GlassLayer  = _glassDrop.SelectedKey;
      if (double.TryParse(_glassOffBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var go) && go > 0) s.GlassOffset = go;
      if (!string.IsNullOrEmpty(_visDrop.SelectedKey))    s.VisLayer    = _visDrop.SelectedKey;
      if (double.TryParse(_visOffBox.Text,  NumberStyles.Float, CultureInfo.InvariantCulture, out var vo) && vo > 0) s.VisOffset   = vo;
      s.Label = (_labelBox.Text ?? "").Trim();
      if (double.TryParse(_tailBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tl) && tl >= 0) s.Tail = tl;
    }
  }

  // ── Helpers: trim curve against multiple caps simultaneously ──────────────

  // Applies TrimExtendToCurve for each cap against the *original* full curve
  // (not chained), then keeps whichever trimmed result best reduces both ends.
  private static Curve TrimToBothCaps(Curve source, IReadOnlyList<Curve> caps, double tol)
  {
    if (caps.Count == 0) return source;
    var nurbs = source.ToNurbsCurve();
    if (nurbs == null) return source;
    Dbg.Write($"TrimToBothCaps: source.len={source.GetLength():F3} caps={caps.Count} tol={tol}");
    var extended = nurbs.Extend(CurveEnd.Both, 1000.0, CurveExtensionStyle.Line) ?? (Curve)nurbs;
    if (!extended.ClosestPoint(source.PointAtStart, out var tOrigS)) { Dbg.Write("  FAIL: no tOrigS"); return source; }
    if (!extended.ClosestPoint(source.PointAtEnd,   out var tOrigE)) { Dbg.Write("  FAIL: no tOrigE"); return source; }
    if (tOrigS > tOrigE) (tOrigS, tOrigE) = (tOrigE, tOrigS);
    double midT = (tOrigS + tOrigE) * 0.5;
    Dbg.Write($"  tOrigS={tOrigS:F6} tOrigE={tOrigE:F6} midT={midT:F6} extDomain=[{extended.Domain.T0:F6},{extended.Domain.T1:F6}]");
    double tS = double.MaxValue;
    double tE = double.MinValue;
    for (int ci = 0; ci < caps.Count; ci++)
    {
      var cap = caps[ci];
      var ev = Intersection.CurveCurve(extended, cap, tol, tol);
      if (ev != null && ev.Count > 0)
      {
        Dbg.Write($"  cap[{ci}] len={cap.GetLength():F3} → {ev.Count} intersections:");
        foreach (var e in ev)
        {
          var t = e.ParameterA;
          Dbg.Write($"    t={t:F6} side={(t <= midT ? "start" : "end")}");
          if (t <= midT) tS = Math.Min(tS, t);
          if (t >= midT) tE = Math.Max(tE, t);
        }
      }
      else
      {
        if (!extended.ClosestPoint(CurveMidpoint(cap), out var tc))
          { Dbg.Write($"  cap[{ci}] len={cap.GetLength():F3} → no int, proj FAILED"); continue; }
        Dbg.Write($"  cap[{ci}] len={cap.GetLength():F3} → no int, proj tc={tc:F6} side={(tc <= midT ? "start" : "end")}");
        if (tc <= midT) tS = Math.Min(tS, tc);
        if (tc >= midT) tE = Math.Max(tE, tc);
      }
    }
    if (tS > midT) { Dbg.Write("  tS not set, using tOrigS"); tS = tOrigS; }
    if (tE < midT) { Dbg.Write("  tE not set, using tOrigE"); tE = tOrigE; }
    Dbg.Write($"  final tS={tS:F6} tE={tE:F6}");
    if (tS >= tE || Math.Abs(tS - tE) < tol) { Dbg.Write("  DEGENERATE → returning source"); return source; }
    var result = extended.Trim(tS, tE) ?? source;
    Dbg.Write($"  result.len={result.GetLength():F3}");
    return result;
  }

  // ── RunCommand ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var s          = LoadSettings();
    var configPath = GetToolsConfigPath();
    var configRoot = LoadToolsConfig(configPath);
    var section    = EnsureUZipSection(configRoot);
    var lr         = NormalizeLayerRuntime(section.Layers);
    ApplyLayerRuntime(lr);
    var ctx = new UzipLayerContext(LayerCut, LayerPlot, LayerReference);
    EnsureCommandLayers(doc, lr);
    Dbg.Init(GetPluginDataDirectory());
    Dbg.Run();

    double offL         = s.Left;
    double offR         = s.Right;
    double offB         = s.Bottom;
    double radius       = s.Radius;
    bool   glass        = s.Glass;
    bool   vis          = s.Vis;
    bool   parts        = s.Parts;
    var    currentLabel = s.Label;
    var    currentTail  = s.Tail;

    void SaveCurrent() => SaveSettings(new UzipSettings
    {
      Left = offL, Right = offR, Bottom = offB, Radius = radius,
      Glass = glass, GlassOffset = s.GlassOffset, GlassLayer = s.GlassLayer,
      Vis   = vis,   VisOffset   = s.VisOffset,   VisLayer   = s.VisLayer,
      CenterLayer = s.CenterLayer, Parts = parts, Label = currentLabel ?? s.Label, Tail = currentTail,
    });

    // Snapshot all preselected IDs before clearing selection
    var preselectedIds = doc.Objects.GetSelectedObjects(false, false)
      .Where(o => o != null)
      .Select(o => o.Id)
      .ToList();
    doc.Objects.UnselectAll();

    // ── Phase 1: collect 3 U-arm curves ──────────────────────────────────────
    var preCurves   = new List<Curve>();
    var prePts      = new List<Point3d>();
    var preCurveIds = new List<Guid>();
    var jTol        = doc.ModelAbsoluteTolerance * 100.0;

    foreach (var id in preselectedIds.Take(3))
    {
      var c = CurveFromId(doc, id);
      if (c != null) { preCurves.Add(c.DuplicateCurve()); prePts.Add(CurveMidpoint(c)); preCurveIds.Add(id); }
    }
    if (preCurves.Count > 1)
    {
      var connected = new bool[preCurves.Count];
      for (int i = 0; i < preCurves.Count; i++)
        for (int j = 0; j < preCurves.Count; j++)
        {
          if (i == j) continue;
          if (preCurves[i].ClosestPoints(preCurves[j], out var pA, out var pB) && pA.DistanceTo(pB) <= jTol)
          { connected[i] = true; break; }
        }
      var nc = new List<Curve>(); var np = new List<Point3d>(); var ni = new List<Guid>();
      for (int i = 0; i < preCurves.Count; i++)
        if (connected[i]) { nc.Add(preCurves[i]); np.Add(prePts[i]); ni.Add(preCurveIds[i]); }
      preCurves = nc; prePts = np; preCurveIds = ni;
    }

    int need = 3 - preCurves.Count;
    if (need > 0)
    {
      var go = new GetObject();
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;
      while (true)
      {
        go.ClearCommandOptions();
        go.SetCommandPrompt($"Select {need} U-shape curve{(need == 1 ? "" : "s")}");
        go.AddOption("Left",   FmtOpt(offL));
        go.AddOption("Right",  FmtOpt(offR));
        go.AddOption("Bottom", FmtOpt(offB));
        go.AddOption("Radius", FmtOpt(radius));
        var glassT = new OptionToggle(glass, "No", "Yes");
        var visT   = new OptionToggle(vis,   "No", "Yes");
        var partsT = new OptionToggle(parts, "No", "Yes");
        go.AddOptionToggle("Glass", ref glassT);
        go.AddOptionToggle("Vis",   ref visT);
        go.AddOptionToggle("Parts", ref partsT);
        if (parts)
        {
          go.AddOption("Label", string.IsNullOrEmpty(currentLabel) ? "none" : currentLabel);
          go.AddOption("Tail",  FmtOpt(currentTail));
        }
        go.AddOption("Options");
        var res = go.GetMultiple(need, need);
        if (res == GetResult.Cancel || go.CommandResult() != Result.Success) return Result.Cancel;
        if (res == GetResult.Option)
        {
          // ClearCommandOptions() (called at the top of this loop) resets the GetObject's
          // accumulated object list, losing any curves the user picked before clicking an option.
          // Save partial picks now so they survive the continue.
          for (int i = 0; i < go.ObjectCount; i++)
          {
            var rf = go.Object(i);
            if (!preCurveIds.Contains(rf.ObjectId))
            {
              var crv = rf.Curve()?.DuplicateCurve();
              if (crv != null)
              {
                preCurves.Add(crv);
                var sp = rf.SelectionPoint();
                prePts.Add(sp.IsValid ? sp : CurveMidpoint(crv));
                preCurveIds.Add(rf.ObjectId);
                if (!preselectedIds.Contains(rf.ObjectId)) preselectedIds.Add(rf.ObjectId);
              }
            }
          }
          need = 3 - preCurves.Count;
          if (need == 0) { glass = glassT.CurrentValue; vis = visT.CurrentValue; parts = partsT.CurrentValue; SaveCurrent(); break; }
          glass = glassT.CurrentValue; vis = visT.CurrentValue; parts = partsT.CurrentValue;
          var opt = go.Option()?.EnglishName ?? "";
          if      (opt == "Left")    { var v = GetDistSubprompt("Left arm offset",  offL, DefaultLeft);    if (v == null) return Result.Cancel; offL = v.Value; }
          else if (opt == "Right")   { var v = GetDistSubprompt("Right arm offset", offR, DefaultRight);   if (v == null) return Result.Cancel; offR = v.Value; }
          else if (opt == "Bottom")  { var v = GetDistSubprompt("Bottom offset",    offB, DefaultBottom);  if (v == null) return Result.Cancel; offB = v.Value; }
          else if (opt == "Radius")  { var v = GetDistSubprompt("Fillet radius",  radius, DefaultRadius); if (v == null) return Result.Cancel; radius = v.Value; }
          else if (opt == "Label")   { var nl = currentLabel; if (RhinoGet.GetString("Label", true, ref nl) == Result.Success) currentLabel = (nl ?? DefaultLabel).Trim(); }
          else if (opt == "Tail")    { var nt = currentTail;  if (RhinoGet.GetNumber("Tail length", true, ref nt) == Result.Success && nt >= 0.0) currentTail = nt; }
          else if (opt == "Options") { var dlg = new OptionsDialog(doc, s); dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); if (dlg.Result) { dlg.ApplyTo(s); offL = s.Left; offR = s.Right; offB = s.Bottom; radius = s.Radius; glass = s.Glass; vis = s.Vis; } }
          SaveCurrent();
          // Re-select after the sub-prompt; any getter (GetString/GetNumber) deselects objects.
          foreach (var id in preCurveIds) doc.Objects.Select(id);
          continue;
        }
        if (res != GetResult.Object) return Result.Cancel;
        for (int i = 0; i < go.ObjectCount; i++)
        {
          var rf  = go.Object(i);
          var crv = rf.Curve()?.DuplicateCurve();
          if (crv == null) { RhinoApp.WriteLine("vUzip: could not extract curve."); return Result.Failure; }
          preCurves.Add(crv);
          var sp = rf.SelectionPoint();
          prePts.Add(sp.IsValid ? sp : CurveMidpoint(crv));
          preCurveIds.Add(rf.ObjectId);
          if (!preselectedIds.Contains(rf.ObjectId)) preselectedIds.Add(rf.ObjectId);
        }
        break;
      }
    }
    if (preCurves.Count < 3) return Result.Failure;

    var rawCurves = preCurves.Take(3).ToArray();
    var clickPts  = prePts.Take(3).ToArray();

    // IDs consumed as the 3 U-arm curves — always excluded from parts pipeline
    var uArmIds = new HashSet<Guid>(preCurveIds.Take(3));

    // Parts-pipeline curve set: everything preselected except U-arms
    var partsSelectionIds = preselectedIds.Where(id => !uArmIds.Contains(id)).ToList();

    // ── Phase 2: preview + curve-selection (parts) or boundary (no parts) ─────
    var capCurves   = new List<Curve>(); // single-cap used in parts=off mode
    Curve? displayCurve = null;
    var tol = doc.ModelAbsoluteTolerance;
    static Color Faded(Color c) => Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);
    Color FadedLayerColor(string name)
    {
      var idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
      return Faded(idx >= 0 ? doc.Layers[idx].Color : Color.Gray);
    }

    // Core cap computation — shared by GetPartsTrimCaps and the live selection event handler.
    List<Curve> ComputeCapCurvesForIds(Curve center, List<Guid> ids)
    {
      if (ids.Count == 0) return new List<Curve>();
      var pvItems = CollectPreselected(doc, Guid.Empty, center, ids, false);
      if (pvItems.Count == 0) return new List<Curve>();
      var domain = center.Domain; var span = domain.T1 - domain.T0;
      Dbg.Write($"ComputeCapCurvesForIds: center.len={center.GetLength():F3} domain=[{domain.T0:F6},{domain.T1:F6}] span={span:F6} start={center.PointAtStart} end={center.PointAtEnd} items={pvItems.Count}");
      CurveItem? startCap = null; var sNorm = double.MaxValue;
      CurveItem? endCap   = null; var eNorm = double.MinValue;
      foreach (var item in pvItems)
      {
        var ip = IntersectionParams(doc, center, item.Curve);
        if (ip.Count > 0)
        {
          Dbg.Write($"  item len={item.Curve.GetLength():F3} mid={CurveMidpoint(item.Curve)} intCount={ip.Count}");
          foreach (var p in ip) { var n = span > RhinoMath.ZeroTolerance ? (p - domain.T0) / span : 0.5; Dbg.Write($"    intP={p:F6} n={n:F6}"); if (n < sNorm) { sNorm = n; startCap = item; } if (n > eNorm) { eNorm = n; endCap = item; } }
        }
        else if (center.ClosestPoint(CurveMidpoint(item.Curve), out var tc))
        {
          var n = span > RhinoMath.ZeroTolerance ? (tc - domain.T0) / span : 0.5;
          Dbg.Write($"  item len={item.Curve.GetLength():F3} mid={CurveMidpoint(item.Curve)} no-int proj tc={tc:F6} n={n:F6}");
          if (n < sNorm) { sNorm = n; startCap = item; } if (n > eNorm) { eNorm = n; endCap = item; }
        }
        else
        {
          Dbg.Write($"  item len={item.Curve.GetLength():F3} mid={CurveMidpoint(item.Curve)} no-int no-proj");
        }
      }
      Dbg.Write($"  → startCap.n={sNorm:F4} endCap.n={eNorm:F4}");
      var caps = new List<Curve>();
      if (startCap != null) caps.Add(startCap.Curve);
      if (endCap   != null && !ReferenceEquals(endCap, startCap)) caps.Add(endCap.Curve);
      return caps;
    }

    List<Curve> GetPartsTrimCaps(Curve center)
    {
      Dbg.Write($"GetPartsTrimCaps: ids={partsSelectionIds.Count} center.len={center.GetLength():F3}");
      var result = ComputeCapCurvesForIds(center, partsSelectionIds);
      Dbg.Write($"  → {result.Count} caps");
      return result;
    }

    // caps stored at break time — used for glass/vis commit (no tail shift)
    List<Curve> commitCaps = new List<Curve>();
    // base uncapped curve from current loop iteration — needed by live event handler
    Curve? loopFinalCurve = null;

    var conduit = new PreviewConduit { Enabled = true };

    // Live conduit update: fires on each Rhino SelectObjects/DeselectObjects event during GetMultiple.
    // Reads the live Rhino selection to recompute caps and update the preview in real-time.
    void RefreshConduitFromLiveSel()
    {
      if (loopFinalCurve == null) return;
      var liveIds = doc.Objects.GetSelectedObjects(false, false)
        .Where(o => o?.Geometry is Curve && !uArmIds.Contains(o.Id))
        .Select(o => o.Id).ToList();
      var liveCaps = ComputeCapCurvesForIds(loopFinalCurve, liveIds);
      var liveDc   = TrimToBothCaps(loopFinalCurve, liveCaps, tol);
      conduit.Curve = liveDc;
      conduit.SideCurves.Clear();
      var ln = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
      if (glass) foreach (var c in OffsetBothSides(liveDc, s.GlassOffset, ln, tol))
        conduit.SideCurves.Add((TrimToBothCaps(c, liveCaps, tol), FadedLayerColor(s.GlassLayer)));
      if (vis) foreach (var c in OffsetBothSides(liveDc, s.VisOffset, ln, tol))
        conduit.SideCurves.Add((TrimToBothCaps(c, liveCaps, tol), FadedLayerColor(s.VisLayer)));
      doc.Views.Redraw();
    }
    void OnSelChanged(object? _, RhinoObjectSelectionEventArgs e) { if (e.Document == doc) RefreshConduitFromLiveSel(); }
    try
    {
      while (true)
      {
        var finalCurve = ComputeResult(rawCurves, clickPts, offL, offR, offB, radius, doc);
        if (finalCurve == null)
        {
          RhinoApp.WriteLine("vUzip: failed to compute center curve.");
          conduit.Enabled = false; doc.Views.Redraw(); return Result.Failure;
        }

        // Determine trim caps and apply all simultaneously
        loopFinalCurve = finalCurve;
        var trimCaps = parts ? GetPartsTrimCaps(finalCurve) : capCurves;
        displayCurve = TrimToBothCaps(finalCurve, trimCaps, tol);

        conduit.SideCurves.Clear();
        var pvNormal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
        var clIdx    = doc.Layers.FindByFullPath(s.CenterLayer, RhinoMath.UnsetIntIndex);
        conduit.CenterColor = Faded(clIdx >= 0 ? doc.Layers[clIdx].Color : Color.Cyan);
        conduit.Curve = displayCurve;
        if (glass) foreach (var c in OffsetBothSides(displayCurve, s.GlassOffset, pvNormal, tol))
          conduit.SideCurves.Add((TrimToBothCaps(c, trimCaps, tol), FadedLayerColor(s.GlassLayer)));
        if (vis) foreach (var c in OffsetBothSides(displayCurve, s.VisOffset, pvNormal, tol))
          conduit.SideCurves.Add((TrimToBothCaps(c, trimCaps, tol), FadedLayerColor(s.VisLayer)));

        // Pre-select current parts set so Rhino shows them highlighted natively.
        // This enables natural add (click/window) and remove (Shift+click/Shift+window).
        conduit.PartsHighlight.Clear();
        doc.Objects.UnselectAll();
        if (parts) foreach (var id in partsSelectionIds) doc.Objects.Select(id);
        doc.Views.Redraw();

        if (parts)
        {
          // EnablePreSelect(true,true) + DeselectAllBeforePostSelect=false: Rhino shows the
          // pre-selected curves highlighted and lets the user add or Shift-remove freely.
          // A single Enter accepts whatever is currently selected — no second prompt needed.
          var gm = new GetObject();
          gm.SetCommandPrompt($"Select boundary curves for parts ({partsSelectionIds.Count} selected)");
          gm.GeometryFilter = ObjectType.Curve;
          gm.SubObjectSelect = false;
          gm.EnablePreSelect(true, true);
          gm.DeselectAllBeforePostSelect = false;
          gm.AcceptNothing(true);
          var leftOptGm   = new OptionDouble(offL,        0.0, 1e300);
          var rightOptGm  = new OptionDouble(offR,        0.0, 1e300);
          var bottomOptGm = new OptionDouble(offB,        0.0, 1e300);
          var radiusOptGm = new OptionDouble(radius,      0.0, 1e300);
          var tailOptGm   = new OptionDouble(currentTail, 0.0, 1e300);
          gm.AddOptionDouble("Left",   ref leftOptGm);
          gm.AddOptionDouble("Right",  ref rightOptGm);
          gm.AddOptionDouble("Bottom", ref bottomOptGm);
          gm.AddOptionDouble("Radius", ref radiusOptGm);
          var glassT2p = new OptionToggle(glass, "No", "Yes");
          var visT2p   = new OptionToggle(vis,   "No", "Yes");
          var partsT2p = new OptionToggle(parts, "No", "Yes");
          gm.AddOptionToggle("Glass", ref glassT2p);
          gm.AddOptionToggle("Vis",   ref visT2p);
          gm.AddOptionToggle("Parts", ref partsT2p);
          gm.AddOption("Label", string.IsNullOrEmpty(currentLabel) ? "none" : currentLabel);
          gm.AddOptionDouble("Tail",  ref tailOptGm);
          gm.AddOption("Options");
          // Subscribe to selection events so the conduit updates in real-time as the user clicks.
          // Subscription is AFTER the pre-select calls so those don't trigger RefreshConduitFromLiveSel.
          RhinoDoc.SelectObjects   += OnSelChanged;
          RhinoDoc.DeselectObjects += OnSelChanged;
          var resM = gm.GetMultiple(0, 0);
          RhinoDoc.SelectObjects   -= OnSelChanged;
          RhinoDoc.DeselectObjects -= OnSelChanged;
          // Capture option values immediately — AddOptionDouble value confirmed via Enter
          // may return GetResult.Nothing in GetMultiple, so read before branching.
          glass = glassT2p.CurrentValue; vis = visT2p.CurrentValue; parts = partsT2p.CurrentValue;
          offL        = leftOptGm.CurrentValue;
          offR        = rightOptGm.CurrentValue;
          offB        = bottomOptGm.CurrentValue;
          radius      = radiusOptGm.CurrentValue;
          currentTail = tailOptGm.CurrentValue;
          SaveCurrent();
          // Read the actual Rhino selection state (pre-selected ± user changes).
          var newIds = doc.Objects.GetSelectedObjects(false, false)
            .Where(o => o?.Geometry is Curve && !uArmIds.Contains(o.Id))
            .Select(o => o.Id)
            .ToList();
          doc.Objects.UnselectAll();
          Dbg.Write($"Stage2 resM={resM} rawSelected={newIds.Count}");
          if (resM == GetResult.Nothing || resM == GetResult.Object)
          {
            partsSelectionIds = newIds;
            Dbg.Write($"  accepted: {partsSelectionIds.Count} boundary curves");
            // Recompute displayCurve with the final cap selection before breaking.
            // Without this, displayCurve is the uncapped pre-stage-2 value and the commit
            // phase (center curve, glass/vis, parts BuildEndCurves) will all be wrong.
            var fcFinal = ComputeResult(rawCurves, clickPts, offL, offR, offB, radius, doc);
            if (fcFinal != null)
            {
              var finalCaps2 = GetPartsTrimCaps(fcFinal);
              commitCaps = finalCaps2; // saved for glass/vis commit (no tail shift)
              displayCurve = TrimToBothCaps(fcFinal, finalCaps2, tol);
              conduit.Curve = displayCurve;
              conduit.SideCurves.Clear();
              var fnNormal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
              if (glass) foreach (var c in OffsetBothSides(displayCurve, s.GlassOffset, fnNormal, tol))
                conduit.SideCurves.Add((TrimToBothCaps(c, finalCaps2, tol), FadedLayerColor(s.GlassLayer)));
              if (vis) foreach (var c in OffsetBothSides(displayCurve, s.VisOffset, fnNormal, tol))
                conduit.SideCurves.Add((TrimToBothCaps(c, finalCaps2, tol), FadedLayerColor(s.VisLayer)));
              doc.Views.Redraw();
              Dbg.Write($"  displayCurve.len={displayCurve.GetLength():F3} caps={finalCaps2.Count}");
            }
            break;
          }
          if (resM == GetResult.Option)
          {
            // newIds is empty when an option is clicked because Rhino fires DeselectObjects
            // before returning GetResult.Option.  Read from gm.Objects() instead — it retains
            // the pre-selected + user-added objects through the option event.
            var gmIds = Enumerable.Range(0, gm.ObjectCount)
              .Select(i => gm.Object(i).ObjectId)
              .Where(id => !uArmIds.Contains(id))
              .ToList();
            if (gmIds.Count > 0) partsSelectionIds = gmIds;
            // Values already captured above; handle sub-prompts for Label/Options.
            var optM = gm.Option()?.EnglishName ?? "";
            if      (optM == "Label")   { var nl = currentLabel; if (RhinoGet.GetString("Label", true, ref nl) == Result.Success) { currentLabel = (nl ?? DefaultLabel).Trim(); SaveCurrent(); } }
            else if (optM == "Options") { var dlg = new OptionsDialog(doc, s); dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); if (dlg.Result) { dlg.ApplyTo(s); glass = s.Glass; vis = s.Vis; SaveCurrent(); } }
            continue;
          }
          if (gm.CommandResult() != Result.Success) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
          continue;
        }
        else
        {
          // parts=off: single curve click = boundary trim/extend
          var gp2 = new GetObject();
          gp2.SetCommandPrompt("Enter to accept; click curve to trim/extend ends");
          gp2.GeometryFilter = ObjectType.Curve; gp2.SubObjectSelect = false;
          gp2.EnablePreSelect(false, true); gp2.DeselectAllBeforePostSelect = true;
          gp2.AcceptNothing(true);
          var leftOptGp2   = new OptionDouble(offL,   0.0, 1e300);
          var rightOptGp2  = new OptionDouble(offR,   0.0, 1e300);
          var bottomOptGp2 = new OptionDouble(offB,   0.0, 1e300);
          var radiusOptGp2 = new OptionDouble(radius, 0.0, 1e300);
          gp2.AddOptionDouble("Left",   ref leftOptGp2);
          gp2.AddOptionDouble("Right",  ref rightOptGp2);
          gp2.AddOptionDouble("Bottom", ref bottomOptGp2);
          gp2.AddOptionDouble("Radius", ref radiusOptGp2);
          var glassT2 = new OptionToggle(glass, "No", "Yes");
          var visT2   = new OptionToggle(vis,   "No", "Yes");
          var partsT2 = new OptionToggle(parts, "No", "Yes");
          gp2.AddOptionToggle("Glass", ref glassT2);
          gp2.AddOptionToggle("Vis",   ref visT2);
          gp2.AddOptionToggle("Parts", ref partsT2);
          gp2.AddOption("Options");
          var res2 = gp2.Get();
          // Capture option values immediately before branching.
          glass  = glassT2.CurrentValue; vis = visT2.CurrentValue; parts = partsT2.CurrentValue;
          offL   = leftOptGp2.CurrentValue;
          offR   = rightOptGp2.CurrentValue;
          offB   = bottomOptGp2.CurrentValue;
          radius = radiusOptGp2.CurrentValue;
          SaveCurrent();
          if (res2 == GetResult.Nothing) break;
          if (res2 == GetResult.Object)
          {
            var cap = gp2.Object(0).Curve()?.DuplicateCurve();
            if (cap != null) capCurves = new List<Curve> { cap };
            doc.Objects.UnselectAll(); continue;
          }
          if (gp2.CommandResult() != Result.Success) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
          if (res2 == GetResult.Option)
          {
            // Values already captured above.
            var opt2 = gp2.Option()?.EnglishName ?? "";
            if (opt2 == "Options") { var dlg = new OptionsDialog(doc, s); dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); if (dlg.Result) { dlg.ApplyTo(s); glass = s.Glass; vis = s.Vis; SaveCurrent(); } }
            continue;
          }
        }
      }
    }
    finally { conduit.Enabled = false; doc.Objects.UnselectAll(); }

    if (displayCurve == null) return Result.Failure;

    // Glass/vis use commitCaps (raw selected boundary curves, no tail offset).
    // The parts pipeline does its own BuildEndCurves with currentTail internally.
    var glassVisCaps = parts ? commitCaps : capCurves;

    // ── Commit: center curve ──────────────────────────────────────────────────
    var centerId2  = AddCurve(doc, displayCurve, s.CenterLayer);
    var centerItem = new CurveItem(displayCurve.DuplicateCurve(), s.CenterLayer, centerId2 == Guid.Empty ? (Guid?)null : centerId2);

    // Glass / Vis
    if (glass || vis)
    {
      var normal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
      void AddOffsets(double offsetDist, string layerName)
      {
        foreach (var c in OffsetBothSides(displayCurve, offsetDist, normal, tol))
          AddCurve(doc, TrimToBothCaps(c, glassVisCaps, tol), layerName);
      }
      if (glass) AddOffsets(s.GlassOffset, s.GlassLayer);
      if (vis)   AddOffsets(s.VisOffset,   s.VisLayer);
    }

    // Parts pipeline
    if (parts)
    {
      var plane      = GetCurvePlane(displayCurve);
      var insideSign = SolveInsideSign(doc, displayCurve, plane);
      var specs      = ResolvePartSpecs(s.CenterLayer, section.Parts, lr, ctx);
      if (specs.Count == 0) { RhinoApp.WriteLine("vUzip: no part specs found in config."); return Result.Success; }

      // Use partsSelectionIds (U-arm curves excluded); center curve itself is filtered by centerId2
      var originalAllPre   = CollectPreselected(doc, centerId2, displayCurve, partsSelectionIds, false);
      var originalTouching = CollectPreselected(doc, centerId2, displayCurve, partsSelectionIds, true);
      var (initEndCurves, initEndParentIds) = BuildEndCurves(doc, originalTouching, displayCurve, plane, currentTail);

      var allPreItems   = originalAllPre;
      var touchingItems = originalTouching;
      if (Math.Abs(currentTail) <= RhinoMath.ZeroTolerance && initEndParentIds.Count > 0)
      {
        allPreItems   = originalAllPre.Where(i => !i.ObjectId.HasValue || !initEndParentIds.Contains(i.ObjectId.Value)).ToList();
        touchingItems = originalTouching.Where(i => !i.ObjectId.HasValue || !initEndParentIds.Contains(i.ObjectId.Value)).ToList();
      }
      var touchingIds = touchingItems.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).ToHashSet();

      var stamp            = DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
      var allPartObjectIds = new List<List<Guid>>();
      var createdGroups    = new List<string>();
      BuildAllParts(doc, stamp, currentLabel, centerItem, allPreItems, touchingIds, displayCurve, plane, insideSign, initEndCurves, specs, allPartObjectIds, createdGroups, ctx);

      while (true)
      {
        var anchor = createdGroups.Count > 0 ? createdGroups[0] : null;
        var (needsRebuild, newLabel, newTail) = PlaceGroupsWithPickOrDelete(doc, createdGroups, anchor, currentLabel, currentTail);
        if (!needsRebuild) break;
        DeleteCreatedGroupsAndMembers(doc, createdGroups);
        allPartObjectIds.Clear(); createdGroups.Clear();
        currentLabel = newLabel; currentTail = newTail;

        // Re-derive end curves with updated tail
        var rebuildAllPre   = CollectPreselected(doc, centerId2, displayCurve, partsSelectionIds, false);
        var rebuildTouching = CollectPreselected(doc, centerId2, displayCurve, partsSelectionIds, true);
        var (rebuildEndCrvs, rebuildEndParentIds) = BuildEndCurves(doc, rebuildTouching, displayCurve, plane, currentTail);
        if (Math.Abs(currentTail) <= RhinoMath.ZeroTolerance && rebuildEndParentIds.Count > 0)
        {
          rebuildAllPre   = rebuildAllPre.Where(i => !i.ObjectId.HasValue || !rebuildEndParentIds.Contains(i.ObjectId.Value)).ToList();
          rebuildTouching = rebuildTouching.Where(i => !i.ObjectId.HasValue || !rebuildEndParentIds.Contains(i.ObjectId.Value)).ToList();
        }
        var rebuildTouchingIds = rebuildTouching.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).ToHashSet();
        BuildAllParts(doc, stamp, currentLabel, centerItem, rebuildAllPre, rebuildTouchingIds, displayCurve, plane, insideSign, rebuildEndCrvs, specs, allPartObjectIds, createdGroups, ctx);
      }

      s.Label = currentLabel ?? DefaultLabel;
      s.Tail  = Math.Max(0.0, currentTail);
      section.Parts = specs;
      SaveToolsConfig(configPath, configRoot);
    }

    SaveSettings(new UzipSettings
    {
      Left = offL, Right = offR, Bottom = offB, Radius = radius,
      Glass = glass, GlassOffset = s.GlassOffset, GlassLayer = s.GlassLayer,
      Vis   = vis,   VisOffset   = s.VisOffset,   VisLayer   = s.VisLayer,
      CenterLayer = s.CenterLayer, Parts = parts, Label = s.Label, Tail = s.Tail,
    });
    doc.Views.Redraw();
    return Result.Success;
  }
}
