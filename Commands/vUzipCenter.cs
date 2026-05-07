using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// Offsets the three curves of a U-shape (left arm, right arm, bottom) inward
/// by configurable amounts, fillets the two inside corners, and produces a
/// single joined open curve. Ported from vUzipCenter.py.
/// </summary>
public sealed class vUzipCenter : Command
{
  public override string EnglishName => "vUzipCenter";

  private const string SettingsSection = "vUzipCenter";
  private const double DefaultLeft   = 2.375;
  private const double DefaultRight  = 2.375;
  private const double DefaultBottom = 1.125;
  private const double DefaultRadius = 12.0;
  private const double ZipperValue   = DefaultLeft;

  // ── Settings ──────────────────────────────────────────────────────────────

  private sealed class UzipCenterSettings
  {
    public double Left   { get; set; } = DefaultLeft;
    public double Right  { get; set; } = DefaultRight;
    public double Bottom { get; set; } = DefaultBottom;
    public double Radius { get; set; } = DefaultRadius;
    public bool   Glass       { get; set; } = false;
    public double GlassOffset { get; set; } = 0.25;
    public string GlassLayer  { get; set; } = "Glass";
    public bool   Vis         { get; set; } = false;
    public double VisOffset   { get; set; } = 0.75;
    public string VisLayer    { get; set; } = "Vis-Line";
  }

  private static UzipCenterSettings LoadSettings() =>
    vToolsOptionStore.Read<UzipCenterSettings>(SettingsSection, section =>
    {
      var s = new UzipCenterSettings();
      if (vToolsOptionStore.TryGetDouble(section, "left",        out var l))   s.Left        = l;
      if (vToolsOptionStore.TryGetDouble(section, "right",       out var r))   s.Right       = r;
      if (vToolsOptionStore.TryGetDouble(section, "bottom",      out var b))   s.Bottom      = b;
      if (vToolsOptionStore.TryGetDouble(section, "radius",      out var rad)) s.Radius      = rad;
      if (vToolsOptionStore.TryGetBool  (section, "glass",       out var g))   s.Glass       = g;
      if (vToolsOptionStore.TryGetDouble(section, "glassOffset", out var go))  s.GlassOffset = go;
      if (vToolsOptionStore.TryGetString(section, "glassLayer",  out var gl))  s.GlassLayer  = gl;
      if (vToolsOptionStore.TryGetBool  (section, "vis",         out var v))   s.Vis         = v;
      if (vToolsOptionStore.TryGetDouble(section, "visOffset",   out var vo))  s.VisOffset   = vo;
      if (vToolsOptionStore.TryGetString(section, "visLayer",    out var vl))  s.VisLayer    = vl;
      return s;
    });

  private static void SaveSettings(UzipCenterSettings s) =>
    vToolsOptionStore.Update(SettingsSection, section =>
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
    });

  // ── Fractional formatting ─────────────────────────────────────────────────

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

  /// <summary>Format as 2" or 2 3/8"</summary>
  private static string FmtDist(double v)
  {
    var (w, n, d) = ToFraction(v);
    return n == 0 ? $"{w}\"" : $"{w} {n}/{d}\"";
  }

  /// <summary>Format as option current-value: 2 or 2-3/8</summary>
  private static string FmtOpt(double v)
  {
    var (w, n, d) = ToFraction(v);
    return n == 0 ? w.ToString(CultureInfo.InvariantCulture) : $"{w}-{n}/{d}";
  }

  // ── Parsing ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Parse fractional inch string: "2 3/8", "2-3/8", "3/8", plain float, or "zipper"/"z".
  /// </summary>
  private static double? ParseDist(string s)
  {
    s = s.Trim();
    if (string.IsNullOrEmpty(s)) return null;
    if ("zipper".StartsWith(s, StringComparison.OrdinalIgnoreCase))
      return ZipperValue;
    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
      return plain;
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

  /// <summary>
  /// Sub-prompt for a distance value; accepts fractions.
  /// Returns new value, original value on Enter, or null on cancel.
  /// </summary>
  private static double? GetDistSubprompt(string prompt, double current)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({FmtDist(current)})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return current;
    if (res == GetResult.String)
    {
      var v = ParseDist(gs.StringResult());
      if (v.HasValue && v.Value > 0.0) return v.Value;
      return current;
    }
    return null; // cancel
  }

  // ── Geometry helpers ──────────────────────────────────────────────────────

  private static Point3d CurveMidpoint(Curve c)
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

  private static Curve? TrimArmToOpenSide(Curve crv, Point3d splitPt, Point3d clickPt)
  {
    if (!crv.ClosestPoint(splitPt, out double t)) return null;
    var segs = crv.Split(t);
    if (segs == null || segs.Length == 0) return null;
    if (segs.Length == 1) return segs[0];
    return segs.OrderBy(seg =>
    {
      seg.ClosestPoint(clickPt, out double tc);
      return seg.PointAt(tc).DistanceTo(clickPt);
    }).First();
  }

  // ── U-component identification ────────────────────────────────────────────

  private static (int Bottom, int Left, int Right) IdentifyUComponents(
    Curve[] curves, Point3d[]? clickPts, RhinoDoc doc)
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
      if (score < bestScore)
      {
        bestScore = score;
        bottomIdx = btm; armA = a; armB = b;
      }
    }

    var bottom = curves[bottomIdx];
    double tMid  = (bottom.Domain.Min + bottom.Domain.Max) * 0.5;
    var midBtm   = bottom.PointAt(tMid);

    var view    = doc.Views.ActiveView;
    var cplaneZ = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;

    var refA  = (clickPts != null && clickPts.Length == 3)
                ? clickPts[armA]
                : CurveMidpoint(curves[armA]);
    var cross = Vector3d.CrossProduct(bottom.TangentAt(tMid), refA - midBtm);

    if (Vector3d.Multiply(cross, cplaneZ) >= 0)
      return (bottomIdx, armA, armB);
    return (bottomIdx, armB, armA);
  }

  // ── Offset ────────────────────────────────────────────────────────────────

  /// <summary>Offsets a curve both sides; returns up to 2 results.</summary>
  private static List<Curve> OffsetBothSides(Curve curve, double distance, Vector3d normal, double tol)
  {
    var plane = new Plane(curve.PointAtStart, normal);
    var pos = curve.Offset(plane, +distance, tol, CurveOffsetCornerStyle.Sharp);
    var neg = curve.Offset(plane, -distance, tol, CurveOffsetCornerStyle.Sharp);
    var results = new List<Curve>();
    if (pos != null) results.AddRange(pos);
    if (neg != null) results.AddRange(neg);
    return results;
  }

  private static Curve? OffsetCurveInward(
    Curve curve, double distance, Point3d centerPt, Vector3d normal, double tol)
  {
    var pos = curve.Offset(centerPt, normal,  distance, tol, CurveOffsetCornerStyle.Sharp);
    var neg = curve.Offset(centerPt, normal, -distance, tol, CurveOffsetCornerStyle.Sharp);
    var candidates = new List<Curve>();
    if (pos != null) candidates.AddRange(pos);
    if (neg != null) candidates.AddRange(neg);
    if (candidates.Count == 0) return null;
    return candidates.OrderBy(c =>
    {
      if (c.ClosestPoint(centerPt, out double t))
        return c.PointAt(t).DistanceTo(centerPt);
      double tm = (c.Domain.Min + c.Domain.Max) * 0.5;
      return c.PointAt(tm).DistanceTo(centerPt);
    }).First();
  }

  // ── Intersection ──────────────────────────────────────────────────────────

  private static (Point3d Pt, double T1, double T2)? FindNearestIntersection(
    Curve c1, Curve c2, Point3d refPt, double tol)
  {
    var events = Intersection.CurveCurve(c1, c2, tol, 0.0);
    if (events == null || events.Count == 0) return null;
    (Point3d Pt, double T1, double T2)? best = null;
    double bestDist = double.MaxValue;
    foreach (var e in events)
    {
      double d = e.PointA.DistanceTo(refPt);
      if (d < bestDist) { bestDist = d; best = (e.PointA, e.ParameterA, e.ParameterB); }
    }
    return best;
  }

  // ── Walk along curve ──────────────────────────────────────────────────────

  private static Point3d WalkAlongCurve(Curve crv, Point3d fromPt, double distance, Point3d towardPt)
  {
    if (!crv.ClosestPoint(fromPt,    out double tFrom))    return fromPt;
    if (!crv.ClosestPoint(towardPt,  out double tTo))      return fromPt;
    double lenToFrom = crv.GetLength(new Interval(crv.Domain.Min, tFrom));
    double targetLen = tTo > tFrom ? lenToFrom + distance : lenToFrom - distance;
    if (targetLen < 0) return crv.PointAtStart;
    if (!crv.LengthParameter(targetLen, out double tTarget)) return fromPt;
    return crv.PointAt(tTarget);
  }

  // ── Extension ─────────────────────────────────────────────────────────────

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
    var r1   = nurbs.Extend(CurveEnd.Start, amount, CurveExtensionStyle.Line);
    var base1 = r1 ?? (NurbsCurve)nurbs;
    var r2   = base1.Extend(CurveEnd.End, amount, CurveExtensionStyle.Line);
    return r2 ?? base1;
  }

  // ── Fillet ────────────────────────────────────────────────────────────────

  private static Curve? FilletArcOnly(
    Curve c1, Point3d hint1, Curve c2, Point3d hint2,
    double radius, double tol, double angleTol)
  {
    var results = Curve.CreateFilletCurves(
      c1, hint1, c2, hint2, radius,
      false, false, false, tol, angleTol);
    return results != null && results.Length > 0 ? results[0] : null;
  }

  private static (Point3d PtOnC1, Point3d PtOnC2) ArcTangentPts(Curve arc, Curve c1, Curve c2)
  {
    var pStart = arc.PointAtStart;
    var pEnd   = arc.PointAtEnd;
    c1.ClosestPoint(pStart, out double t1s);
    c2.ClosestPoint(pStart, out double t2s);
    if (c1.PointAt(t1s).DistanceTo(pStart) <= c2.PointAt(t2s).DistanceTo(pStart))
      return (pStart, pEnd);
    return (pEnd, pStart);
  }

  // ── Trim helpers ──────────────────────────────────────────────────────────

  private static Curve? TrimKeepSide(Curve curve, Point3d splitPt, Point3d keepPt)
  {
    if (!curve.ClosestPoint(splitPt, out double t)) return null;
    var segs = curve.Split(t);
    if (segs == null || segs.Length == 0) return null;
    if (segs.Length == 1) return segs[0];
    return segs.OrderBy(seg =>
      Math.Min(seg.PointAtStart.DistanceTo(keepPt), seg.PointAtEnd.DistanceTo(keepPt))
    ).First();
  }

  private static Curve? TrimBetween(Curve curve, Point3d ptA, Point3d ptB)
  {
    if (!curve.ClosestPoint(ptA, out double tA)) return null;
    if (!curve.ClosestPoint(ptB, out double tB)) return null;
    if (Math.Abs(tA - tB) < 1e-10) return null;
    double tLo = Math.Min(tA, tB), tHi = Math.Max(tA, tB);
    return curve.Trim(tLo, tHi);
  }

  // ── Trim/extend to boundary curve ────────────────────────────────────────

  private static Curve? TrimExtendToCurve(Curve centerCrv, Curve clipCrv, double tol)
  {
    var ptStart = centerCrv.PointAtStart;
    var ptEnd   = centerCrv.PointAtEnd;

    var nurbs = centerCrv.ToNurbsCurve();
    if (nurbs == null) return null;
    var extended = nurbs.Extend(CurveEnd.Both, 1000.0, CurveExtensionStyle.Line) ?? (Curve)nurbs;

    if (!extended.ClosestPoint(ptStart, out double tOrigS)) return null;
    if (!extended.ClosestPoint(ptEnd,   out double tOrigE)) return null;

    static double SqDist(Point3d p, Point3d q)
    { var d = p - q; return d.X * d.X + d.Y * d.Y + d.Z * d.Z; }

    double tS, tE;
    var events = Intersection.CurveCurve(extended, clipCrv, tol, tol);
    if (events != null && events.Count > 0)
    {
      var all = Enumerable.Range(0, events.Count)
                          .Select(i => (T: events[i].ParameterA, Pt: events[i].PointA))
                          .ToList();
      double midT = (tOrigS + tOrigE) * 0.5;
      var sEv = all.Where(e => e.T <= midT).ToList();
      var eEv = all.Where(e => e.T >= midT).ToList();
      tS = sEv.Count > 0 ? sEv.OrderBy(e => SqDist(e.Pt, ptStart)).First().T : tOrigS;
      tE = eEv.Count > 0 ? eEv.OrderBy(e => SqDist(e.Pt, ptEnd  )).First().T : tOrigE;
    }
    else
    {
      if (!clipCrv.ClosestPoint(ptStart, out double tCs)) return null;
      if (!clipCrv.ClosestPoint(ptEnd,   out double tCe)) return null;
      var clipPtS = clipCrv.PointAt(tCs);
      var clipPtE = clipCrv.PointAt(tCe);
      if (!extended.ClosestPoint(clipPtS, out tS)) return null;
      if (!extended.ClosestPoint(clipPtE, out tE)) return null;
    }

    if (Math.Abs(tS - tE) < tol) return null;
    if (tS > tE) (tS, tE) = (tE, tS);
    return extended.Trim(tS, tE);
  }

  // ── Core computation ──────────────────────────────────────────────────────

  private static Curve? ComputeResult(
    Curve[] rawCurves, Point3d[]? clickPts,
    double offL, double offR, double offB, double radius,
    RhinoDoc doc)
  {
    var (bottomIdx, leftIdx, rightIdx) = IdentifyUComponents(rawCurves, clickPts, doc);

    var btmCrv   = rawCurves[bottomIdx];
    var leftCrv  = rawCurves[leftIdx];
    var rightCrv = rawCurves[rightIdx];

    var view         = doc.Views.ActiveView;
    var cplaneNormal = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;

    var juncLeft  = JunctionPtOnBottom(leftCrv,  btmCrv);
    var juncRight = JunctionPtOnBottom(rightCrv, btmCrv);
    if (juncLeft == null || juncRight == null) return null;

    var clickL = (clickPts != null && clickPts.Length == 3)
                 ? clickPts[leftIdx]  : CurveMidpoint(leftCrv);
    var clickR = (clickPts != null && clickPts.Length == 3)
                 ? clickPts[rightIdx] : CurveMidpoint(rightCrv);

    // Trim each arm to its open side (junction → open end).
    if (leftCrv.ClosestPoints(btmCrv, out var armPtL, out _))
    {
      var seg = TrimArmToOpenSide(leftCrv, armPtL, clickL);
      if (seg != null) leftCrv = seg;
    }
    if (rightCrv.ClosestPoints(btmCrv, out var armPtR, out _))
    {
      var seg = TrimArmToOpenSide(rightCrv, armPtR, clickR);
      if (seg != null) rightCrv = seg;
    }

    // Compute inside_pt: midpoint of junction-midpoint and click-midpoint.
    Point3d insidePt;
    if (clickPts != null && clickPts.Length == 3)
    {
      var cl = clickPts[leftIdx];
      var cr = clickPts[rightIdx];
      var clickMid = new Point3d((cl.X + cr.X) * 0.5, (cl.Y + cr.Y) * 0.5, (cl.Z + cr.Z) * 0.5);
      var juncMid  = new Point3d(
        (juncLeft.Value.X + juncRight.Value.X) * 0.5,
        (juncLeft.Value.Y + juncRight.Value.Y) * 0.5,
        (juncLeft.Value.Z + juncRight.Value.Z) * 0.5);
      insidePt = new Point3d(
        (juncMid.X + clickMid.X) * 0.5,
        (juncMid.Y + clickMid.Y) * 0.5,
        (juncMid.Z + clickMid.Z) * 0.5);
    }
    else
    {
      double leftLen = Math.Max(leftCrv.GetLength(), doc.ModelAbsoluteTolerance * 10);
      double step    = Math.Max(doc.ModelAbsoluteTolerance * 100, leftLen * 0.1);
      var inwardTan  = ArmInwardTangent(leftCrv, juncLeft.Value);
      insidePt = new Point3d(
        (juncLeft.Value.X + juncRight.Value.X) * 0.5 + inwardTan.X * step,
        (juncLeft.Value.Y + juncRight.Value.Y) * 0.5 + inwardTan.Y * step,
        (juncLeft.Value.Z + juncRight.Value.Z) * 0.5 + inwardTan.Z * step);
    }

    double tol = doc.ModelAbsoluteTolerance;

    var offLeft   = OffsetCurveInward(leftCrv,  offL, insidePt, cplaneNormal, tol);
    var offRight  = OffsetCurveInward(rightCrv, offR, insidePt, cplaneNormal, tol);
    var offBottom = OffsetCurveInward(btmCrv,   offB, insidePt, cplaneNormal, tol);
    if (offLeft == null || offRight == null || offBottom == null) return null;

    double extAmt  = 3.0 * radius + Math.Max(offL, Math.Max(offR, offB));
    var extLeft    = ExtendAtNearEnd(offLeft,  juncLeft.Value,  extAmt);
    var extRight   = ExtendAtNearEnd(offRight, juncRight.Value, extAmt);
    var extBottom  = ExtendBothEnds(offBottom, extAmt);

    var hintLOpen  = FarEndPt(extLeft,  juncLeft.Value);
    var hintROpen  = FarEndPt(extRight, juncRight.Value);

    var resL = FindNearestIntersection(extLeft,  extBottom, juncLeft.Value,  tol);
    var resR = FindNearestIntersection(extRight, extBottom, juncRight.Value, tol);
    if (resL == null || resR == null) return null;

    var (isectL, _, _) = resL.Value;
    var (isectR, _, _) = resR.Value;

    var hintArmL = WalkAlongCurve(extLeft,   isectL, radius, hintLOpen);
    var hintBtmL = WalkAlongCurve(extBottom, isectL, radius, isectR);
    var hintArmR = WalkAlongCurve(extRight,  isectR, radius, hintROpen);
    var hintBtmR = WalkAlongCurve(extBottom, isectR, radius, isectL);

    double angleTol = doc.ModelAngleToleranceRadians;
    var arcL = FilletArcOnly(extLeft, hintArmL, extBottom, hintBtmL, radius, tol, angleTol);
    if (arcL == null) return null;
    var (tanLArm, tanLBtm) = ArcTangentPts(arcL, extLeft, extBottom);

    var arcR = FilletArcOnly(extRight, hintArmR, extBottom, hintBtmR, radius, tol, angleTol);
    if (arcR == null) return null;
    var (tanRArm, tanRBtm) = ArcTangentPts(arcR, extRight, extBottom);

    var trimmedLeft   = TrimKeepSide(extLeft,   tanLArm, hintLOpen);
    var trimmedRight  = TrimKeepSide(extRight,  tanRArm, hintROpen);
    var trimmedBottom = TrimBetween(extBottom, tanLBtm, tanRBtm);
    if (trimmedLeft == null || trimmedRight == null || trimmedBottom == null) return null;

    var pieces = new Curve[] { trimmedLeft, arcL, trimmedBottom, arcR, trimmedRight };
    var joined = Curve.JoinCurves(pieces, tol);
    return joined != null && joined.Length > 0 ? joined[0] : null;
  }

  // ── Preview conduit ───────────────────────────────────────────────────────

  private sealed class PreviewConduit : DisplayConduit
  {
    public Curve?              Curve       { get; set; }
    public Color               CenterColor { get; set; } = Color.Cyan;
    public List<(Curve Crv, Color Col)> SideCurves { get; } = new();

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (Curve != null)
        e.Display.DrawCurve(Curve, CenterColor, 2);
      foreach (var (crv, col) in SideCurves)
        e.Display.DrawCurve(crv, col, 1);
    }
  }

  // ── RunCommand ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var settings = LoadSettings();
    double offL   = settings.Left;
    double offR   = settings.Right;
    double offB   = settings.Bottom;
    double radius = settings.Radius;
    bool   glass  = settings.Glass;
    bool   vis    = settings.Vis;

    // Harvest preselected curves (first 3 = U arms+bottom; optional 4th = boundary).
    var presel = doc.Objects.GetSelectedObjects(false, false)
                            .Where(o => o.ObjectType == ObjectType.Curve)
                            .Take(4)
                            .ToList();
    doc.Objects.UnselectAll();

    Curve? initialBoundary = null;
    if (presel.Count >= 4)
      initialBoundary = (presel[3].Geometry as Curve)?.DuplicateCurve();

    var preCurves = presel.Take(3)
                          .Select(o => (o.Geometry as Curve)?.DuplicateCurve())
                          .Where(c => c != null)
                          .Cast<Curve>()
                          .ToList();
    var prePts = preCurves.Select(c => CurveMidpoint(c)).ToList();

    int need = 3 - preCurves.Count;

    // ── Interactive selection (if needed) ────────────────────────────────────
    if (need > 0)
    {
      var go = new GetObject();
      go.SetCommandPrompt($"Select {need} U-shape curve{(need == 1 ? "" : "s")}");
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;

      while (true)
      {
        go.ClearCommandOptions();
        go.AddOption("Left",   FmtOpt(offL));
        go.AddOption("Right",  FmtOpt(offR));
        go.AddOption("Bottom", FmtOpt(offB));
        go.AddOption("Radius", FmtOpt(radius));
        var glassToggle1 = new OptionToggle(glass, "No", "Yes");
        var visToggle1   = new OptionToggle(vis,   "No", "Yes");
        go.AddOptionToggle("Glass", ref glassToggle1);
        go.AddOptionToggle("Vis",   ref visToggle1);

        var res = go.GetMultiple(need, need);

        if (res == GetResult.Cancel || go.CommandResult() != Result.Success)
          return Result.Cancel;

        if (res == GetResult.Option)
        {
          glass = glassToggle1.CurrentValue;
          vis   = visToggle1.CurrentValue;
          var name = go.Option()?.EnglishName ?? string.Empty;
          if (name == "Left")
          {
            var v = GetDistSubprompt("Left arm offset", offL);
            if (v == null) return Result.Cancel;
            offL = v.Value;
          }
          else if (name == "Right")
          {
            var v = GetDistSubprompt("Right arm offset", offR);
            if (v == null) return Result.Cancel;
            offR = v.Value;
          }
          else if (name == "Bottom")
          {
            var v = GetDistSubprompt("Bottom offset", offB);
            if (v == null) return Result.Cancel;
            offB = v.Value;
          }
          else if (name == "Radius")
          {
            var v = GetDistSubprompt("Fillet radius", radius);
            if (v == null) return Result.Cancel;
            radius = v.Value;
          }
          // Glass/Vis are toggles; already read above.
          continue;
        }

        if (res != GetResult.Object) return Result.Cancel;

        for (int i = 0; i < go.ObjectCount; i++)
        {
          var rf  = go.Object(i);
          var crv = rf.Curve()?.DuplicateCurve();
          if (crv == null) { RhinoApp.WriteLine("vUzipCenter: could not extract curve geometry."); return Result.Failure; }
          preCurves.Add(crv);
          var sp = rf.SelectionPoint();
          prePts.Add(sp.IsValid ? sp : CurveMidpoint(crv));
        }
        break;
      }
    }

    if (preCurves.Count < 3) return Result.Failure;

    var rawCurves = preCurves.Take(3).ToArray();
    var clickPts  = prePts.Take(3).ToArray();
    var boundaryCrv = initialBoundary;

    // ── Preview / confirm loop ────────────────────────────────────────────────
    var conduit = new PreviewConduit();
    conduit.Enabled = true;

    Curve? displayCurve = null;

    static Color Faded(Color c) =>
      Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);
    Color FadedLayerColor(string name)
    {
      var idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
      return Faded(idx >= 0 ? doc.Layers[idx].Color : Color.Gray);
    }

    try
    {
      while (true)
      {
        var finalCurve = ComputeResult(rawCurves, clickPts, offL, offR, offB, radius, doc);
        if (finalCurve == null)
        {
          RhinoApp.WriteLine("vUzipCenter: failed to compute result curve.");
          conduit.Enabled = false;
          doc.Views.Redraw();
          return Result.Failure;
        }

        displayCurve = finalCurve;
        double tol = doc.ModelAbsoluteTolerance;
        if (boundaryCrv != null)
        {
          var clipped = TrimExtendToCurve(finalCurve, boundaryCrv, tol);
          if (clipped != null) displayCurve = clipped;
        }

        conduit.SideCurves.Clear();
        var pvNormal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
        double pvTol = doc.ModelAbsoluteTolerance;
        conduit.CenterColor = Faded(doc.Layers[doc.Layers.CurrentLayerIndex].Color);
        if (glass)
          foreach (var c in OffsetBothSides(displayCurve, settings.GlassOffset, pvNormal, pvTol))
            conduit.SideCurves.Add((boundaryCrv != null ? TrimExtendToCurve(c, boundaryCrv, pvTol) ?? c : c, FadedLayerColor(settings.GlassLayer)));
        if (vis)
          foreach (var c in OffsetBothSides(displayCurve, settings.VisOffset, pvNormal, pvTol))
            conduit.SideCurves.Add((boundaryCrv != null ? TrimExtendToCurve(c, boundaryCrv, pvTol) ?? c : c, FadedLayerColor(settings.VisLayer)));
        conduit.Curve = displayCurve;
        doc.Objects.UnselectAll();
        doc.Views.Redraw();

        var gp = new GetObject();
        gp.SetCommandPrompt("Enter to accept, click curve to trim/extend ends");
        gp.GeometryFilter  = ObjectType.Curve;
        gp.SubObjectSelect = false;
        gp.EnablePreSelect(false, true);
        gp.DeselectAllBeforePostSelect = true;
        gp.AcceptNothing(true);
        gp.AddOption("Left",   FmtOpt(offL));
        gp.AddOption("Right",  FmtOpt(offR));
        gp.AddOption("Bottom", FmtOpt(offB));
        gp.AddOption("Radius", FmtOpt(radius));
        var glassToggle2 = new OptionToggle(glass, "No", "Yes");
        var visToggle2   = new OptionToggle(vis,   "No", "Yes");
        gp.AddOptionToggle("Glass", ref glassToggle2);
        gp.AddOptionToggle("Vis",   ref visToggle2);

        var res = gp.Get();

        if (res == GetResult.Nothing)
          break; // accepted

        if (res == GetResult.Object)
        {
          var bndCrv = gp.Object(0).Curve();
          if (bndCrv != null) boundaryCrv = bndCrv.DuplicateCurve();
          doc.Objects.UnselectAll();
          continue;
        }

        if (gp.CommandResult() != Result.Success)
        {
          conduit.Enabled = false;
          doc.Views.Redraw();
          return Result.Cancel;
        }

        if (res == GetResult.Option)
        {
          glass = glassToggle2.CurrentValue;
          vis   = visToggle2.CurrentValue;
          var name = gp.Option()?.EnglishName ?? string.Empty;
          if (name == "Left")
          {
            var v = GetDistSubprompt("Left arm offset", offL);
            if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
            offL = v.Value;
          }
          else if (name == "Right")
          {
            var v = GetDistSubprompt("Right arm offset", offR);
            if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
            offR = v.Value;
          }
          else if (name == "Bottom")
          {
            var v = GetDistSubprompt("Bottom offset", offB);
            if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
            offB = v.Value;
          }
          else if (name == "Radius")
          {
            var v = GetDistSubprompt("Fillet radius", radius);
            if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
            radius = v.Value;
          }
          continue;
        }
      }
    }
    finally
    {
      conduit.Enabled = false;
    }

    // ── Commit ────────────────────────────────────────────────────────────────
    if (displayCurve != null)
      doc.Objects.AddCurve(displayCurve);

    // Glass / Vis side offsets
    if (displayCurve != null && (glass || vis))
    {
      var normal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
      double tol = doc.ModelAbsoluteTolerance;

      void AddOffsets(double offsetDist, string layerName)
      {
        var curves = OffsetBothSides(displayCurve, offsetDist, normal, tol);
        var attr   = new ObjectAttributes();
        var layerIdx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
        if (layerIdx >= 0)
          attr.LayerIndex = layerIdx;
        foreach (var c in curves)
        {
          var final = boundaryCrv != null ? TrimExtendToCurve(c, boundaryCrv, tol) ?? c : c;
          doc.Objects.AddCurve(final, attr);
        }
      }

      if (glass) AddOffsets(settings.GlassOffset, settings.GlassLayer);
      if (vis)   AddOffsets(settings.VisOffset,   settings.VisLayer);
    }

    SaveSettings(new UzipCenterSettings
    {
      Left = offL, Right = offR, Bottom = offB, Radius = radius,
      Glass = glass, GlassOffset = settings.GlassOffset, GlassLayer = settings.GlassLayer,
      Vis   = vis,   VisOffset   = settings.VisOffset,   VisLayer   = settings.VisLayer,
    });
    doc.Views.Redraw();
    return Result.Success;
  }
}
