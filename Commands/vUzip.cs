using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class vUzip : Command
{
  public override string EnglishName => "vUzip";

  private const string SettingsSection            = "vUzip";
  private const double DefaultLeft                = 2.375;
  private const double DefaultRight               = 2.375;
  private const double DefaultBottom              = 1.125;
  private const double DefaultRadius              = 12.0;
  private const double ZipperValue                = DefaultLeft;
  private const string ToolsConfigFileName        = "vTools.config.json";
  private const string DefaultLayerCutName        = "CUT1";
  private const string DefaultLayerPlotName       = "PLOT";
  private const string DefaultLayerReferenceName  = "Reference";
  private const string DefaultLayerCutColor       = "#CC3333";
  private const string DefaultLayerPlotColor      = "#0F8A8A";
  private const string DefaultLayerReferenceColor = "#FFFFFF";
  private const string DefaultLabel               = "";
  private const double DefaultTail                = 0.75;
  private const double PartGap                    = 0.5;
  private const double LabelOffsetAlong           = 0.125;
  private const double LabelOffsetPerp            = 0.125;

  private static string LayerCut       = DefaultLayerCutName;
  private static string LayerPlot      = DefaultLayerPlotName;
  private static string LayerReference = DefaultLayerReferenceName;

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
  };

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
    vToolsOptionStore.Read<UzipSettings>(SettingsSection, section =>
    {
      var s = new UzipSettings();
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
      if (vToolsOptionStore.TryGetString(section, "centerLayer", out var cl))  s.CenterLayer = cl;
      if (vToolsOptionStore.TryGetBool  (section, "parts",       out var pt))  s.Parts       = pt;
      if (vToolsOptionStore.TryGetString(section, "label",       out var lb))  s.Label       = lb;
      if (vToolsOptionStore.TryGetDouble(section, "tail",        out var tl))  s.Tail        = tl;
      return s;
    });

  private static void SaveSettings(UzipSettings s) =>
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
      section["centerLayer"] = s.CenterLayer;
      section["parts"]       = s.Parts;
      section["label"]       = s.Label;
      section["tail"]        = s.Tail;
    });
  // ── Parts config types ────────────────────────────────────────────────────

  private sealed class LayerConfigEntry
  {
    public string Name  { get; set; } = "";
    public string Color { get; set; } = "";
  }

  private sealed class UZipLayersConfig
  {
    public LayerConfigEntry Reference { get; set; } = new();
    public LayerConfigEntry Plot      { get; set; } = new();
    public LayerConfigEntry Cut       { get; set; } = new();
  }

  private sealed class UZipConfigSection
  {
    public string           Label  { get; set; } = DefaultLabel;
    public double           Tail   { get; set; } = DefaultTail;
    public UZipLayersConfig Layers { get; set; } = new();
    public List<PartSpec>   Parts  { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
  }

  private sealed class ToolsConfigRoot
  {
    public UZipConfigSection? vUzipParts { get; set; } = new();

    [JsonPropertyName("vUzip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UZipConfigSection? LegacyVUzip { get; set; }

    [JsonPropertyName("vUZIP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UZipConfigSection? LegacyVUZIP { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSections { get; set; }
  }

  private sealed class LayerRuntime
  {
    public string ReferenceName     { get; set; } = DefaultLayerReferenceName;
    public string PlotName          { get; set; } = DefaultLayerPlotName;
    public string CutName           { get; set; } = DefaultLayerCutName;
    public string ReferenceColorHex { get; set; } = DefaultLayerReferenceColor;
    public string PlotColorHex      { get; set; } = DefaultLayerPlotColor;
    public string CutColorHex       { get; set; } = DefaultLayerCutColor;

    public UZipLayersConfig ToConfig() => new()
    {
      Reference = new LayerConfigEntry { Name = ReferenceName, Color = ReferenceColorHex },
      Plot      = new LayerConfigEntry { Name = PlotName,      Color = PlotColorHex      },
      Cut       = new LayerConfigEntry { Name = CutName,       Color = CutColorHex       }
    };
  }

  private sealed record CurveItem(Curve Curve, string LayerName, Guid? ObjectId);

  private sealed class OffsetSpec
  {
    public string  Name   { get; set; } = "";
    public double  Offset { get; set; }
    public string? Layer  { get; set; }
  }

  private sealed class PartSpec
  {
    public string         Name           { get; set; } = "";
    public string?        CenterLayer    { get; set; }
    public string         CenterEndMode  { get; set; } = "";
    public string         Note           { get; set; } = "";
    public bool           MirrorPart     { get; set; }
    public List<OffsetSpec> Offsets      { get; set; } = new();
    public string         BandInsideName  { get; set; } = "";
    public string         BandOutsideName { get; set; } = "";
  }

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
    return null;
  }

  // ── Basic geometry helpers ────────────────────────────────────────────────

  // Domain-midpoint version (used by U-shape computation)
  private static Point3d CurveDomainMidpoint(Curve c)
  {
    double t = (c.Domain.Min + c.Domain.Max) * 0.5;
    return c.PointAt(t);
  }

  // Length-midpoint version (used by parts pipeline)
  private static Point3d CurveMidpoint(Curve curve)
  {
    var len = curve.GetLength();
    if (len <= RhinoMath.ZeroTolerance) return curve.PointAtStart;
    return curve.LengthParameter(0.5 * len, out var t) ? curve.PointAt(t) : curve.PointAt(curve.Domain.Mid);
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
      if (score < bestScore) { bestScore = score; bottomIdx = btm; armA = a; armB = b; }
    }
    var bottom = curves[bottomIdx];
    double tMid   = (bottom.Domain.Min + bottom.Domain.Max) * 0.5;
    var midBtm    = bottom.PointAt(tMid);
    var view      = doc.Views.ActiveView;
    var cplaneZ   = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    var refA      = (clickPts != null && clickPts.Length == 3) ? clickPts[armA] : CurveDomainMidpoint(curves[armA]);
    var cross     = Vector3d.CrossProduct(bottom.TangentAt(tMid), refA - midBtm);
    if (Vector3d.Multiply(cross, cplaneZ) >= 0) return (bottomIdx, armA, armB);
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

  private static Curve? OffsetSingle(RhinoDoc doc, Curve curve, Plane plane, double signedDistance)
  {
    var offsets = curve.Offset(plane, signedDistance, doc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
    if (offsets == null) return null;
    Curve? best = null; double bestLen = -1.0;
    foreach (var c in offsets) { var l = c.GetLength(); if (l > bestLen) { bestLen = l; best = c; } }
    return best;
  }

  private static Curve? OffsetAtSide(RhinoDoc doc, Curve centerCurve, Plane plane, int insideSign, double distanceAbs, string side)
  {
    if (distanceAbs <= RhinoMath.ZeroTolerance) return centerCurve.DuplicateCurve();
    var sign = side.Equals("inside", StringComparison.OrdinalIgnoreCase) ? insideSign : -insideSign;
    var curve = OffsetSingle(doc, centerCurve, plane, sign * distanceAbs);
    if (curve != null) return curve;
    return OffsetSingle(doc, centerCurve, plane, -sign * distanceAbs);
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

  private static (Point3d PtOnC1, Point3d PtOnC2) ArcTangentPts(Curve arc, Curve c1, Curve c2)
  {
    var pStart = arc.PointAtStart; var pEnd = arc.PointAtEnd;
    c1.ClosestPoint(pStart, out double t1s); c2.ClosestPoint(pStart, out double t2s);
    if (c1.PointAt(t1s).DistanceTo(pStart) <= c2.PointAt(t2s).DistanceTo(pStart)) return (pStart, pEnd);
    return (pEnd, pStart);
  }

  private static Curve? TrimKeepSide(Curve curve, Point3d splitPt, Point3d keepPt)
  {
    if (!curve.ClosestPoint(splitPt, out double t)) return null;
    var segs = curve.Split(t);
    if (segs == null || segs.Length == 0) return null;
    if (segs.Length == 1) return segs[0];
    return segs.OrderBy(seg => Math.Min(seg.PointAtStart.DistanceTo(keepPt), seg.PointAtEnd.DistanceTo(keepPt))).First();
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
    var (bottomIdx, leftIdx, rightIdx) = IdentifyUComponents(rawCurves, clickPts, doc);
    var btmCrv   = rawCurves[bottomIdx];
    var leftCrv  = rawCurves[leftIdx];
    var rightCrv = rawCurves[rightIdx];
    var view     = doc.Views.ActiveView;
    var cplaneNormal = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    var juncLeft  = JunctionPtOnBottom(leftCrv,  btmCrv);
    var juncRight = JunctionPtOnBottom(rightCrv, btmCrv);
    if (juncLeft == null || juncRight == null) return null;
    var clickL = (clickPts != null && clickPts.Length == 3) ? clickPts[leftIdx]  : CurveDomainMidpoint(leftCrv);
    var clickR = (clickPts != null && clickPts.Length == 3) ? clickPts[rightIdx] : CurveDomainMidpoint(rightCrv);
    if (leftCrv.ClosestPoints(btmCrv,  out var armPtL, out _)) { var seg = TrimArmToOpenSide(leftCrv,  armPtL, clickL); if (seg != null) leftCrv  = seg; }
    if (rightCrv.ClosestPoints(btmCrv, out var armPtR, out _)) { var seg = TrimArmToOpenSide(rightCrv, armPtR, clickR); if (seg != null) rightCrv = seg; }
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
    double tol     = doc.ModelAbsoluteTolerance;
    var offLeft    = OffsetCurveInward(leftCrv,  offL, insidePt, cplaneNormal, tol);
    var offRight   = OffsetCurveInward(rightCrv, offR, insidePt, cplaneNormal, tol);
    var offBottom  = OffsetCurveInward(btmCrv,   offB, insidePt, cplaneNormal, tol);
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
    var (isectL, _, _) = resL.Value; var (isectR, _, _) = resR.Value;
    var hintArmL = WalkAlongCurve(extLeft,   isectL, radius, hintLOpen);
    var hintBtmL = WalkAlongCurve(extBottom, isectL, radius, isectR);
    var hintArmR = WalkAlongCurve(extRight,  isectR, radius, hintROpen);
    var hintBtmR = WalkAlongCurve(extBottom, isectR, radius, isectL);
    double angleTol = doc.ModelAngleToleranceRadians;
    var arcL = FilletArcOnly(extLeft,  hintArmL, extBottom, hintBtmL, radius, tol, angleTol); if (arcL == null) return null;
    var arcR = FilletArcOnly(extRight, hintArmR, extBottom, hintBtmR, radius, tol, angleTol); if (arcR == null) return null;
    var (tanLArm, tanLBtm) = ArcTangentPts(arcL, extLeft,  extBottom);
    var (tanRArm, tanRBtm) = ArcTangentPts(arcR, extRight, extBottom);
    var trimmedLeft   = TrimKeepSide(extLeft,   tanLArm, hintLOpen);
    var trimmedRight  = TrimKeepSide(extRight,  tanRArm, hintROpen);
    var trimmedBottom = TrimBetween(extBottom, tanLBtm, tanRBtm);
    if (trimmedLeft == null || trimmedRight == null || trimmedBottom == null) return null;
    var pieces = new Curve[] { trimmedLeft, arcL, trimmedBottom, arcR, trimmedRight };
    var joined = Curve.JoinCurves(pieces, tol);
    return joined != null && joined.Length > 0 ? joined[0] : null;
  }

  // ── Parts config helpers ──────────────────────────────────────────────────

  private static string GetPluginDataDirectory()
  {
    var dir = Path.GetDirectoryName(typeof(vUzip).Assembly.Location) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(dir)) dir = ".";
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static string GetToolsConfigPath() => Path.Combine(GetPluginDataDirectory(), ToolsConfigFileName);

  private static ToolsConfigRoot LoadToolsConfig(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        var json = File.ReadAllText(path);
        if (!string.IsNullOrWhiteSpace(json))
        {
          var loaded = JsonSerializer.Deserialize<ToolsConfigRoot>(json, JsonOptions);
          if (loaded != null) return loaded;
        }
      }
    }
    catch { }
    return CreateDefaultToolsConfigRoot();
  }

  private static bool SaveToolsConfig(string path, ToolsConfigRoot config)
  {
    try
    {
      var parent = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
      var json = JsonSerializer.Serialize(config, JsonOptions);
      var tmp  = path + ".tmp";
      File.WriteAllText(tmp, json);
      File.Copy(tmp, path, true);
      File.Delete(tmp);
      return true;
    }
    catch { return false; }
  }

  private static LayerRuntime NormalizeLayerRuntime(UZipLayersConfig? layers) => new()
  {
    ReferenceName     = NormalizeLayerName(layers?.Reference?.Name,  DefaultLayerReferenceName),
    PlotName          = NormalizeLayerName(layers?.Plot?.Name,       DefaultLayerPlotName),
    CutName           = NormalizeLayerName(layers?.Cut?.Name,        DefaultLayerCutName),
    ReferenceColorHex = NormalizeHexColor(layers?.Reference?.Color,  DefaultLayerReferenceColor),
    PlotColorHex      = NormalizeHexColor(layers?.Plot?.Color,       DefaultLayerPlotColor),
    CutColorHex       = NormalizeHexColor(layers?.Cut?.Color,        DefaultLayerCutColor)
  };

  private static void ApplyLayerRuntime(LayerRuntime r)
  { LayerReference = r.ReferenceName; LayerPlot = r.PlotName; LayerCut = r.CutName; }

  private static string NormalizeLayerName(string? s, string fallback)
  { var n = (s ?? "").Trim(); return string.IsNullOrWhiteSpace(n) ? fallback : n; }

  private static string NormalizeHexColor(string? candidate, string fallback)
  {
    if (TryParseHexColor(candidate, out var p)) return $"#{p.R:X2}{p.G:X2}{p.B:X2}";
    if (TryParseHexColor(fallback,  out p))     return $"#{p.R:X2}{p.G:X2}{p.B:X2}";
    return "#FFFFFF";
  }

  private static bool TryParseHexColor(string? value, out System.Drawing.Color color)
  {
    color = System.Drawing.Color.White;
    if (string.IsNullOrWhiteSpace(value)) return false;
    var s = value.Trim();
    if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
    if (s.Length != 6) return false;
    if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
    color = System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    return true;
  }

  private static ToolsConfigRoot CreateDefaultToolsConfigRoot()
  {
    var lr = NormalizeLayerRuntime(null);
    return new ToolsConfigRoot { vUzipParts = new UZipConfigSection { Label = DefaultLabel, Tail = DefaultTail, Layers = lr.ToConfig(), Parts = CreateDefaultPartSpecs(lr) } };
  }

  private static UZipConfigSection EnsureUZipSection(ToolsConfigRoot root)
  {
    if (root.vUzipParts == null) root.vUzipParts = root.LegacyVUzip ?? root.LegacyVUZIP ?? new UZipConfigSection();
    root.LegacyVUzip = null; root.LegacyVUZIP = null;
    var section = root.vUzipParts!;
    section.Label = (section.Label ?? DefaultLabel).Trim();
    section.Tail  = Math.Max(0.0, section.Tail);
    if (section.Parts == null) section.Parts = new List<PartSpec>();
    var runtime = NormalizeLayerRuntime(section.Layers);
    section.Layers = runtime.ToConfig();
    if (section.Parts.Count == 0) section.Parts = CreateDefaultPartSpecs(runtime);
    return section;
  }

  private static System.Drawing.Color ParseHexColorOrDefault(string candidate, string fallback)
  {
    if (TryParseHexColor(candidate, out var p)) return p;
    if (TryParseHexColor(fallback,  out p))     return p;
    return System.Drawing.Color.White;
  }

  private static int EnsureLayer(RhinoDoc doc, string layerName, System.Drawing.Color? color = null)
  {
    var idx = doc.Layers.FindByFullPath(layerName, -1);
    if (idx >= 0)
    {
      if (color.HasValue) { var ex = doc.Layers[idx]; if (ex != null && ex.Color.ToArgb() != color.Value.ToArgb()) { ex.Color = color.Value; doc.Layers.Modify(ex, idx, true); } }
      return idx;
    }
    var layer = new Layer { Name = layerName };
    if (color.HasValue) layer.Color = color.Value;
    var added = doc.Layers.Add(layer);
    return added >= 0 ? added : doc.Layers.CurrentLayerIndex;
  }

  private static void EnsureCommandLayers(RhinoDoc doc, LayerRuntime r)
  {
    EnsureLayer(doc, r.ReferenceName, ParseHexColorOrDefault(r.ReferenceColorHex, DefaultLayerReferenceColor));
    EnsureLayer(doc, r.PlotName,      ParseHexColorOrDefault(r.PlotColorHex,      DefaultLayerPlotColor));
    EnsureLayer(doc, r.CutName,       ParseHexColorOrDefault(r.CutColorHex,       DefaultLayerCutColor));
  }

  private static Guid AddCurve(RhinoDoc doc, Curve? curve, string layerName)
  {
    if (curve == null) return Guid.Empty;
    var attr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, layerName) };
    return doc.Objects.AddCurve(curve, attr);
  }

  private static Guid AddText(RhinoDoc doc, TextEntity text, string layerName)
  {
    var attr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, layerName) };
    return doc.Objects.AddText(text, attr);
  }

  private static string ObjLayerName(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    if (obj == null) return doc.Layers.CurrentLayer.FullPath;
    var layer = doc.Layers[obj.Attributes.LayerIndex];
    return layer?.FullPath ?? doc.Layers.CurrentLayer.FullPath;
  }

  private static HashSet<string> ObjectGroups(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    if (obj == null) return new HashSet<string>();
    var names = obj.Attributes.GetGroupList()
      ?.Select(gi => doc.Groups.FindIndex(gi)?.Name)
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .Cast<string>().ToHashSet();
    return names ?? new HashSet<string>();
  }

  // ── Preselection + end-cap helpers ───────────────────────────────────────

  private static List<CurveItem> CollectPreselected(RhinoDoc doc, Guid centerId, Curve centerCurve, List<Guid> sourceIds, bool requireTouch)
  {
    var result = new List<CurveItem>();
    var centerGroups = ObjectGroups(doc, centerId);
    foreach (var id in sourceIds)
    {
      if (id == centerId) continue;
      if (centerGroups.Count > 0 && ObjectGroups(doc, id).Overlaps(centerGroups)) continue;
      var curve = CurveFromId(doc, id);
      if (curve == null) continue;
      if (requireTouch && !CurvesIntersect(centerCurve, curve, doc.ModelAbsoluteTolerance)) continue;
      result.Add(new CurveItem(curve.DuplicateCurve(), ObjLayerName(doc, id), id));
    }
    return result;
  }

  private static bool CurvesIntersect(Curve a, Curve b, double tolerance)
  {
    var ev = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, tolerance, tolerance);
    return ev != null && ev.Count > 0;
  }

  private static Curve? CurveFromId(RhinoDoc doc, Guid id)
  {
    var obj = doc.Objects.FindId(id);
    return obj?.Geometry as Curve;
  }

  private static Plane GetCurvePlane(Curve curve)
    => curve.TryGetPlane(out var plane) ? plane : Plane.WorldXY;

  private static int SolveInsideSign(RhinoDoc doc, Curve centerCurve, Plane plane)
  {
    var openingPt  = new Point3d((centerCurve.PointAtStart.X + centerCurve.PointAtEnd.X) * 0.5, (centerCurve.PointAtStart.Y + centerCurve.PointAtEnd.Y) * 0.5, (centerCurve.PointAtStart.Z + centerCurve.PointAtEnd.Z) * 0.5);
    var sampleDist = Math.Max(doc.ModelAbsoluteTolerance * 10.0, 0.01);
    var plus  = OffsetSingle(doc, centerCurve, plane,  sampleDist);
    var minus = OffsetSingle(doc, centerCurve, plane, -sampleDist);
    if (plus == null && minus == null) return 1;
    if (plus  == null) return -1;
    if (minus == null) return  1;
    var dPlus  = CurveMidpoint(plus).DistanceTo(openingPt);
    var dMinus = CurveMidpoint(minus).DistanceTo(openingPt);
    return dPlus <= dMinus ? 1 : -1;
  }

  private static double ClosestPointDistance(Curve curve, Point3d point)
  {
    if (!curve.ClosestPoint(point, out var t)) return double.PositiveInfinity;
    return curve.PointAt(t).DistanceTo(point);
  }

  private static double CenterDistanceToEnd(Curve centerCurve, double parameter, bool toStart)
  {
    var d0  = centerCurve.Domain.T0; var d1 = centerCurve.Domain.T1;
    var min = Math.Min(d0, d1);      var max = Math.Max(d0, d1);
    var p   = Math.Max(min, Math.Min(max, parameter));
    var span = toStart ? new Interval(d0, p) : new Interval(p, d1);
    if (Math.Abs(span.Length) <= RhinoMath.ZeroTolerance) return 0.0;
    try { var part = centerCurve.Trim(span); if (part != null) return Math.Max(0.0, part.GetLength()); } catch { }
    var pt = centerCurve.PointAt(p);
    return pt.DistanceTo(toStart ? centerCurve.PointAtStart : centerCurve.PointAtEnd);
  }

  private static List<double> IntersectionParams(RhinoDoc doc, Curve a, Curve b)
  {
    var values = new List<double>();
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
    if (events == null) return values;
    foreach (var ev in events) { if (ev.IsPoint) values.Add(ev.ParameterA); else if (ev.IsOverlap) { values.Add(ev.OverlapA.T0); values.Add(ev.OverlapA.T1); } }
    return values;
  }

  private static List<double> UniqueParams(IEnumerable<double> values, double tolerance)
  {
    var sorted = values.OrderBy(v => v).ToList(); var unique = new List<double>();
    foreach (var v in sorted) { if (unique.Count == 0 || Math.Abs(v - unique[^1]) > tolerance) unique.Add(v); }
    return unique;
  }

  private static CurveItem? SelectEndCapCurve(RhinoDoc doc, IReadOnlyList<CurveItem> items, Curve centerCurve, bool forStart, double tolerance)
  {
    CurveItem? best = null;
    var endPoint = forStart ? centerCurve.PointAtStart : centerCurve.PointAtEnd;
    var bestEndDist = double.PositiveInfinity; var tieSideDist = double.PositiveInfinity;
    foreach (var item in items)
    {
      var centerParams = UniqueParams(IntersectionParams(doc, centerCurve, item.Curve), tolerance);
      if (centerParams.Count == 0) continue;
      var itemBest = double.PositiveInfinity;
      foreach (var p in centerParams)
      {
        var sideDist     = CenterDistanceToEnd(centerCurve, p, toStart: forStart);
        var oppositeDist = CenterDistanceToEnd(centerCurve, p, toStart: !forStart);
        if (sideDist <= oppositeDist + tolerance) itemBest = Math.Min(itemBest, sideDist);
      }
      if (!double.IsFinite(itemBest)) continue;
      var itemEndDist = ClosestPointDistance(item.Curve, endPoint);
      if (best == null || itemEndDist < bestEndDist - tolerance || (Math.Abs(itemEndDist - bestEndDist) <= tolerance && itemBest < tieSideDist - tolerance))
      { best = item; bestEndDist = itemEndDist; tieSideDist = itemBest; }
    }
    return best;
  }

  private static double CurveDistanceScoreToCenterCurve(Curve testCurve, Curve centerCurve)
  {
    var samples = new[] { testCurve.Domain.T0, testCurve.Domain.Mid, testCurve.Domain.T1 };
    var dists = new List<double>();
    foreach (var t in samples) { var pt = testCurve.PointAt(t); if (centerCurve.ClosestPoint(pt, out var tc)) dists.Add(pt.DistanceTo(centerCurve.PointAt(tc))); }
    return dists.Count == 0 ? 0.0 : dists.Average();
  }

  private static Curve? OffsetAwayFromCenter(RhinoDoc doc, Curve curve, Plane plane, double distance, Curve centerCurve, Point3d centerMid, Point3d anchor)
  {
    var plus = OffsetSingle(doc, curve, plane,  distance);
    var minus = OffsetSingle(doc, curve, plane, -distance);
    if (plus == null && minus == null) return null;
    if (plus  == null) return minus;
    if (minus == null) return plus;
    double AnchorDist(Curve c) { if (!c.ClosestPoint(anchor, out var t)) return 0.0; return c.PointAt(t).DistanceTo(centerMid); }
    var tol = doc.ModelAbsoluteTolerance;
    var plusA = AnchorDist(plus); var minusA = AnchorDist(minus);
    if (plusA  > minusA + tol) return plus;
    if (minusA > plusA  + tol) return minus;
    return CurveDistanceScoreToCenterCurve(plus, centerCurve) >= CurveDistanceScoreToCenterCurve(minus, centerCurve) ? plus : minus;
  }

  private static (List<Curve> EndCurves, HashSet<Guid> EndParentIds) BuildEndCurves(RhinoDoc doc, List<CurveItem> preselected, Curve centerCurve, Plane plane, double tail)
  {
    var endCurves = new List<Curve>(); var parentIds = new HashSet<Guid>();
    if (preselected.Count == 0) return (endCurves, parentIds);
    var centerMid = CurveMidpoint(centerCurve);
    var tol = doc.ModelAbsoluteTolerance;
    var endSources = new List<(CurveItem Item, Point3d EndPoint)>();
    var start = SelectEndCapCurve(doc, preselected, centerCurve, forStart: true,  tol);
    if (start != null) endSources.Add((start, centerCurve.PointAtStart));
    var end   = SelectEndCapCurve(doc, preselected, centerCurve, forStart: false, tol);
    if (end   != null) endSources.Add((end,   centerCurve.PointAtEnd));
    var tailAbs = Math.Abs(tail);
    foreach (var (item, endPoint) in endSources)
    {
      var baseCurve = item.Curve.DuplicateCurve();
      if (baseCurve == null) continue;
      if (tailAbs > RhinoMath.ZeroTolerance)
      {
        var shifted = OffsetAwayFromCenter(doc, baseCurve, plane, tailAbs, centerCurve, centerMid, endPoint);
        if (shifted != null) { endCurves.Add(shifted); continue; }
      }
      else if (item.ObjectId.HasValue) parentIds.Add(item.ObjectId.Value);
      endCurves.Add(baseCurve);
    }
    return (endCurves, parentIds);
  }

  // ── Parts band-filter geometry ────────────────────────────────────────────

  private static Curve TrimToMiddleBetweenEndCurves(RhinoDoc doc, Curve curve, IEnumerable<Curve> endCurves, Point3d centerMid)
  {
    var endList = endCurves.ToList();
    if (endList.Count == 0) return curve.DuplicateCurve();
    var allParams = new List<double>();
    foreach (var end in endList) allParams.AddRange(IntersectionParams(doc, curve, end));
    var splitParams = UniqueParams(allParams, doc.ModelAbsoluteTolerance);
    if (splitParams.Count == 0) return curve.DuplicateCurve();
    var pieces = curve.Split(splitParams);
    if (pieces == null || pieces.Length == 0) return curve.DuplicateCurve();
    Curve? best = null; double? bestDist = null;
    foreach (var piece in pieces)
    {
      if (piece == null) continue;
      var dist = CurveMidpoint(piece).DistanceTo(centerMid);
      if (best == null || dist < bestDist) { best = piece; bestDist = dist; }
    }
    return best ?? curve.DuplicateCurve();
  }

  private static Curve ExtendCurveToEnd(RhinoDoc doc, Curve curve, IEnumerable<Curve> endCurves, Point3d centerMid)
  {
    var endList = endCurves.ToList();
    if (endList.Count == 0) return curve.DuplicateCurve();
    var original  = curve.DuplicateCurve();
    var candidate = curve.DuplicateCurve();
    for (var i = 0; i < 4; i++)
    {
      var allParams = new List<double>();
      foreach (var end in endList) allParams.AddRange(IntersectionParams(doc, candidate, end));
      if (UniqueParams(allParams, doc.ModelAbsoluteTolerance).Count >= 2) break;
      var extAmount = Math.Max(candidate.GetLength() * 2.0, 1.0);
      var extended  = candidate.Extend(CurveEnd.Both, extAmount, CurveExtensionStyle.Line);
      if (extended == null) break;
      candidate = extended;
    }
    var trimmed = TrimToMiddleBetweenEndCurves(doc, candidate, endList, centerMid);
    var checkParams = new List<double>();
    foreach (var end in endList) checkParams.AddRange(IntersectionParams(doc, trimmed, end));
    if (UniqueParams(checkParams, doc.ModelAbsoluteTolerance).Count < 2) return original;
    return trimmed;
  }

  private static bool CurveInsideOuterBoundary(RhinoDoc doc, Curve curve, Curve outerBoundary, Curve centerCurve)
  {
    var sampleParams = new[] { curve.Domain.T0, curve.Domain.Mid, curve.Domain.T1 };
    int insideHits = 0, sampleCount = 0;
    foreach (var t in sampleParams)
    {
      var pt = curve.PointAt(t);
      if (!centerCurve.ClosestPoint(pt, out var tc)) continue;
      var centerPt = centerCurve.PointAt(tc); var distToCenter = pt.DistanceTo(centerPt);
      if (!outerBoundary.ClosestPoint(centerPt, out var to)) continue;
      var outerLimit = centerPt.DistanceTo(outerBoundary.PointAt(to));
      sampleCount++;
      if (distToCenter <= outerLimit + doc.ModelAbsoluteTolerance) insideHits++;
    }
    return sampleCount > 0 && insideHits * 2 >= sampleCount;
  }

  private static bool CurveOutsideInnerBoundary(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    foreach (var t in new[] { curve.Domain.T0, curve.Domain.Mid, curve.Domain.T1 })
    {
      var pt = curve.PointAt(t);
      if (!centerCurve.ClosestPoint(pt, out var tc)) continue;
      var centerPt = centerCurve.PointAt(tc);
      if (!innerBoundary.ClosestPoint(centerPt, out var ti)) continue;
      if (pt.DistanceTo(centerPt) <= centerPt.DistanceTo(innerBoundary.PointAt(ti)) + doc.ModelAbsoluteTolerance) return false;
    }
    return true;
  }

  private static List<Curve> SplitCurveByBoundary(RhinoDoc doc, Curve curve, Curve boundary, IEnumerable<double>? precomputed = null)
  {
    var ps = precomputed?.ToList() ?? UniqueParams(IntersectionParams(doc, curve, boundary), doc.ModelAbsoluteTolerance);
    if (ps.Count == 0) return new List<Curve> { curve.DuplicateCurve() };
    var split = curve.Split(ps);
    if (split == null || split.Length == 0) return new List<Curve> { curve.DuplicateCurve() };
    return split.Where(c => c != null).Select(c => c.DuplicateCurve()).ToList();
  }

  private static (List<Curve> Pieces, List<Curve> Keep, string Mode) OutsidePiecesForInnerTrim(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve, IEnumerable<double>? precomputed = null)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var ps = precomputed?.ToList() ?? UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    var pieces = SplitCurveByBoundary(doc, curve, innerBoundary, ps);
    if (ps.Count >= 2 && pieces.Count >= 3)
    {
      var keep = new List<Curve>();
      if (pieces[0]  != null && pieces[0].GetLength()  > tol) keep.Add(pieces[0]);
      if (pieces[^1] != null && pieces[^1].GetLength() > tol) keep.Add(pieces[^1]);
      return (pieces, keep, "end-pieces");
    }
    return (pieces, pieces.Where(p => CurveOutsideInnerBoundary(doc, p, innerBoundary, centerCurve)).ToList(), "classified");
  }

  private static (Curve? Trimmed, bool TrimStart, bool TrimEnd, double SCenter, double SInner, double ECenter, double EInner)
    TrimCurveEndsInsideInnerDetailed(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance; var d0 = curve.Domain.T0; var d1 = curve.Domain.T1;
    var startPt = curve.PointAtStart; var endPt = curve.PointAtEnd;
    var ps = UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    if (ps.Count == 0) return (curve.DuplicateCurve(), false, false, 0, 0, 0, 0);
    if (!centerCurve.ClosestPoint(startPt, out var tcs) || !innerBoundary.ClosestPoint(startPt, out var tis)) return (curve.DuplicateCurve(), false, false, 0, 0, 0, 0);
    if (!centerCurve.ClosestPoint(endPt,   out var tce) || !innerBoundary.ClosestPoint(endPt,   out var tie)) return (curve.DuplicateCurve(), false, false, 0, 0, 0, 0);
    var sC = startPt.DistanceTo(centerCurve.PointAt(tcs)); var sI = startPt.DistanceTo(innerBoundary.PointAt(tis));
    var eC = endPt.DistanceTo(centerCurve.PointAt(tce));   var eI = endPt.DistanceTo(innerBoundary.PointAt(tie));
    var trimStart = sI + tol < sC; var trimEnd = eI + tol < eC;
    if (!trimStart && !trimEnd) return (curve.DuplicateCurve(), false, false, sC, sI, eC, eI);
    var keepStart = d0; var keepEnd = d1;
    if (trimStart) { var cand = ps.Where(p => p > d0 + tol).ToList(); if (cand.Count > 0) keepStart = cand.Min(); else trimStart = false; }
    if (trimEnd)   { var cand = ps.Where(p => p < d1 - tol).ToList(); if (cand.Count > 0) keepEnd   = cand.Max(); else trimEnd   = false; }
    if (!trimStart && !trimEnd) return (curve.DuplicateCurve(), false, false, sC, sI, eC, eI);
    if (keepEnd - keepStart <= tol) return (null, trimStart, trimEnd, sC, sI, eC, eI);
    return (curve.Trim(new Interval(keepStart, keepEnd)), trimStart, trimEnd, sC, sI, eC, eI);
  }

  private static Curve? PickPieceAttachedToEndpoint(IEnumerable<Curve> pieces, Point3d endpoint, double tolerance)
  {
    Curve? best = null; double? bestDist = null;
    foreach (var piece in pieces)
    {
      if (piece == null) continue;
      var d = Math.Min(piece.PointAtStart.DistanceTo(endpoint), piece.PointAtEnd.DistanceTo(endpoint));
      if (best == null || d < bestDist) { best = piece; bestDist = d; }
    }
    return best;
  }

  private static Curve? OutsidePieceForInnerOneCrossing(RhinoDoc doc, Curve curve, Curve innerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var ps = UniqueParams(IntersectionParams(doc, curve, innerBoundary), tol);
    if (ps.Count == 0) return null;
    List<Curve> pieces;
    if (ps.Count == 1)
    {
      pieces = new List<Curve>(); var t = ps[0]; var d0 = curve.Domain.T0; var d1 = curve.Domain.T1;
      if (t > d0 + tol) { var left  = curve.Trim(new Interval(d0, t)); if (left  != null && left.GetLength()  > tol) pieces.Add(left);  }
      if (t < d1 - tol) { var right = curve.Trim(new Interval(t, d1)); if (right != null && right.GetLength() > tol) pieces.Add(right); }
    }
    else pieces = SplitCurveByBoundary(doc, curve, innerBoundary);
    var ranked = pieces.Select(p =>
    {
      int outsideHits = 0; double avgMargin = 0.0; int count = 0;
      foreach (var tt in new[] { p.Domain.T0, p.Domain.Mid, p.Domain.T1 })
      {
        var pt = p.PointAt(tt);
        if (!centerCurve.ClosestPoint(pt, out var tc)) continue;
        var cpt = centerCurve.PointAt(tc); var dist = pt.DistanceTo(cpt);
        if (!innerBoundary.ClosestPoint(cpt, out var ti)) continue;
        var margin = dist - cpt.DistanceTo(innerBoundary.PointAt(ti));
        if (margin > tol) outsideHits++;
        avgMargin += margin; count++;
      }
      if (count > 0) avgMargin /= count;
      return new { Piece = p, OutsideHits = outsideHits, AvgMargin = avgMargin, Len = p.GetLength() };
    }).OrderByDescending(x => x.OutsideHits).ThenByDescending(x => x.AvgMargin).ThenByDescending(x => x.Len).FirstOrDefault();
    return ranked?.Piece;
  }

  private static Curve? TrimCurveEndsOutsideOuter(RhinoDoc doc, Curve curve, Curve outerBoundary, Curve centerCurve)
  {
    var tol = doc.ModelAbsoluteTolerance; var d0 = curve.Domain.T0; var d1 = curve.Domain.T1;
    var ps = UniqueParams(IntersectionParams(doc, curve, outerBoundary), tol);
    if (ps.Count == 0) return curve.DuplicateCurve();
    var startPt = curve.PointAtStart; var endPt = curve.PointAtEnd;
    if (!centerCurve.ClosestPoint(startPt, out var tcs) || !outerBoundary.ClosestPoint(startPt, out var tos)) return curve.DuplicateCurve();
    if (!centerCurve.ClosestPoint(endPt,   out var tce) || !outerBoundary.ClosestPoint(endPt,   out var toe)) return curve.DuplicateCurve();
    var trimStart = curve.PointAtStart.DistanceTo(outerBoundary.PointAt(tos)) + tol < curve.PointAtStart.DistanceTo(centerCurve.PointAt(tcs));
    var trimEnd   = curve.PointAtEnd.DistanceTo(outerBoundary.PointAt(toe))   + tol < curve.PointAtEnd.DistanceTo(centerCurve.PointAt(tce));
    if (!trimStart && !trimEnd) return curve.DuplicateCurve();
    var keepStart = d0; var keepEnd = d1;
    if (trimStart) { var cand = ps.Where(p => p > d0 + tol).ToList(); if (cand.Count > 0) keepStart = cand.Min(); else trimStart = false; }
    if (trimEnd)   { var cand = ps.Where(p => p < d1 - tol).ToList(); if (cand.Count > 0) keepEnd   = cand.Max(); else trimEnd   = false; }
    if (!trimStart && !trimEnd) return curve.DuplicateCurve();
    if (keepEnd - keepStart <= tol) return null;
    return curve.Trim(new Interval(keepStart, keepEnd));
  }

  private static bool CurvesNearlySame(RhinoDoc doc, Curve a, Curve b)
  {
    var tol = doc.ModelAbsoluteTolerance;
    if (Math.Abs(a.GetLength() - b.GetLength()) > tol * 2.0) return false;
    foreach (var t in new[] { a.Domain.T0, a.Domain.Mid, a.Domain.T1 })
    {
      var pt = a.PointAt(t);
      if (!b.ClosestPoint(pt, out var tb)) return false;
      if (pt.DistanceTo(b.PointAt(tb)) > tol * 2.0) return false;
    }
    return true;
  }

  private static List<Curve> SplitAndFilterForBand(RhinoDoc doc, Curve source, Curve innerBoundary, Curve outerBoundary, Curve centerCurve, bool useEndpointSideKeep)
  {
    var tol = doc.ModelAbsoluteTolerance; var final = new List<Curve>();
    var outParams = UniqueParams(IntersectionParams(doc, source, outerBoundary), tol);
    List<Curve> afterOuter;
    if (outParams.Count == 0) { afterOuter = new List<Curve> { source.DuplicateCurve() }; }
    else
    {
      var splitOuter = source.Split(outParams) ?? Array.Empty<Curve>();
      var classified = splitOuter.Where(c => c != null).Where(c => CurveInsideOuterBoundary(doc, c, outerBoundary, centerCurve)).Select(c => c.DuplicateCurve()).ToList();
      if (classified.Count > 0) { afterOuter = classified; }
      else
      {
        Curve? trimmedOuter = null;
        if (outParams.Count >= 2) { var dt = source.Trim(outParams.Min(), outParams.Max()); if (dt != null && dt.GetLength() > tol) trimmedOuter = dt; }
        if (trimmedOuter == null) trimmedOuter = TrimCurveEndsOutsideOuter(doc, source, outerBoundary, centerCurve);
        if (trimmedOuter != null && trimmedOuter.GetLength() > tol && !CurvesNearlySame(doc, trimmedOuter, source)) afterOuter = new List<Curve> { trimmedOuter };
        else afterOuter = new List<Curve>();
      }
    }
    foreach (var piece in afterOuter)
    {
      var inParams = UniqueParams(IntersectionParams(doc, piece, innerBoundary), tol);
      if (inParams.Count == 0) { final.Add(piece.DuplicateCurve()); continue; }
      if (inParams.Count >= 2) { var (_, keep, _) = OutsidePiecesForInnerTrim(doc, piece, innerBoundary, centerCurve, inParams); final.AddRange(keep.Where(c => c != null).Select(c => c.DuplicateCurve())); continue; }
      var (trimmedInner, trimStart, trimEnd, sC, sI, eC, eI) = TrimCurveEndsInsideInnerDetailed(doc, piece, innerBoundary, centerCurve);
      if (useEndpointSideKeep)
      {
        Curve? keepPiece = null;
        var splitPieces = SplitCurveByBoundary(doc, piece, innerBoundary, inParams);
        if (trimStart == trimEnd)
        {
          if (sC - sI > eC - eI) { trimStart = true; trimEnd = false; }
          else if (eC - eI > sC - sI) { trimStart = false; trimEnd = true; }
        }
        if (trimStart && !trimEnd) keepPiece = PickPieceAttachedToEndpoint(splitPieces, piece.PointAtEnd,   tol);
        else if (trimEnd && !trimStart) keepPiece = PickPieceAttachedToEndpoint(splitPieces, piece.PointAtStart, tol);
        if (keepPiece != null) trimmedInner = keepPiece;
        else trimmedInner = OutsidePieceForInnerOneCrossing(doc, piece, innerBoundary, centerCurve) ?? trimmedInner;
      }
      else if (trimmedInner == null) trimmedInner = OutsidePieceForInnerOneCrossing(doc, piece, innerBoundary, centerCurve);
      if (trimmedInner != null) { final.Add(trimmedInner); continue; }
      final.AddRange(piece.Split(inParams.ToArray())?.Where(c => c != null && CurveOutsideInnerBoundary(doc, c, innerBoundary, centerCurve)).Select(c => c.DuplicateCurve()) ?? Enumerable.Empty<Curve>());
    }
    return final.Where(c => c != null && c.IsValid && c.GetLength() > tol).ToList();
  }

  private static List<Curve> TrimCapToBandBoundaries(RhinoDoc doc, Curve cap, Curve? innerBoundary, Curve? outerBoundary)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var innerParams = innerBoundary != null ? UniqueParams(IntersectionParams(doc, cap, innerBoundary), tol) : new List<double>();
    var outerParams = outerBoundary != null ? UniqueParams(IntersectionParams(doc, cap, outerBoundary), tol) : new List<double>();
    if (innerParams.Count >= 2 && outerParams.Count >= 2)
    {
      var outerLo = outerParams.Min(); var outerHi = outerParams.Max();
      var innerLo = innerParams.Min(); var innerHi = innerParams.Max();
      var result = new List<Curve>();
      var left  = cap.Trim(new Interval(outerLo, innerLo)); if (left  != null && left.GetLength()  > tol) result.Add(left);
      var right = cap.Trim(new Interval(innerHi, outerHi)); if (right != null && right.GetLength() > tol) result.Add(right);
      if (result.Count > 0) return result;
    }
    var allPs = UniqueParams(innerParams.Concat(outerParams), tol);
    if (allPs.Count < 2) return new List<Curve> { cap.DuplicateCurve() };
    var trimmed = cap.Trim(new Interval(allPs.Min(), allPs.Max()));
    return new List<Curve> { trimmed ?? cap.DuplicateCurve() };
  }

  // ── Label + group helpers ─────────────────────────────────────────────────

  private static void LockTextTopLeftToPoint(RhinoDoc doc, Guid textId, Point3d topLeftTarget)
  {
    var ro = doc.Objects.FindId(textId);
    if (ro?.Geometry is not TextEntity te) return;
    var bb = te.GetBoundingBox(true); if (!bb.IsValid) return;
    var plane = te.Plane; double xMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
    foreach (var c in bb.GetCorners())
    {
      var v = c - plane.Origin;
      var x = Vector3d.Multiply(v, plane.XAxis); var y = Vector3d.Multiply(v, plane.YAxis);
      if (x < xMin) xMin = x; if (y > yMax) yMax = y;
    }
    if (!double.IsFinite(xMin) || !double.IsFinite(yMax)) return;
    var currentTopLeft = plane.Origin + plane.XAxis * xMin + plane.YAxis * yMax;
    var move = topLeftTarget - currentTopLeft;
    if (!move.IsValid || move.IsTiny()) return;
    var copy = te.Duplicate() as TextEntity; if (copy == null) return;
    if (!copy.Transform(Transform.Translation(move))) return;
    doc.Objects.Replace(textId, copy);
  }

  private static (Point3d EndsMid, Vector3d XRight, Vector3d YDown, Point3d LeftEnd, Point3d RightEnd)? CenterLocalFrame(Curve centerCurve)
  {
    var pStart = centerCurve.PointAtStart; var pEnd = centerCurve.PointAtEnd;
    var endsMid = new Point3d((pStart.X+pEnd.X)*0.5, (pStart.Y+pEnd.Y)*0.5, (pStart.Z+pEnd.Z)*0.5);
    var okPlane = centerCurve.TryGetPlane(out var plane);
    if (!okPlane) plane = Plane.WorldXY;
    var normal = plane.Normal; if (!normal.Unitize()) normal = Vector3d.ZAxis;
    if (Vector3d.Multiply(normal, Vector3d.ZAxis) < 0.0) normal = -normal;
    var chord = pEnd - pStart; chord -= normal * Vector3d.Multiply(chord, normal); if (!chord.Unitize()) return null;
    Vector3d? yDown = null; var bestDev = -1.0;
    var sample = centerCurve.DivideByCount(48, true);
    if (sample != null) foreach (var t in sample)
    {
      var p = centerCurve.PointAt(t); var v = p - endsMid;
      v -= normal * Vector3d.Multiply(v, normal);
      var perp = v - chord * Vector3d.Multiply(v, chord); var dev = perp.Length;
      if (dev > bestDev && dev > RhinoMath.ZeroTolerance && perp.Unitize()) { bestDev = dev; yDown = perp; }
    }
    if (!yDown.HasValue)
    {
      var yd = CurveMidpoint(centerCurve) - endsMid; yd -= normal * Vector3d.Multiply(yd, normal);
      if (!yd.Unitize()) { yd = Vector3d.CrossProduct(chord, normal); if (!yd.Unitize()) return null; }
      yDown = yd;
    }
    var xRight = Vector3d.CrossProduct(normal, yDown.Value); if (!xRight.Unitize()) return null;
    var sStart = Vector3d.Multiply(pStart - endsMid, xRight); var sEnd = Vector3d.Multiply(pEnd - endsMid, xRight);
    return (endsMid, xRight, yDown.Value, sStart <= sEnd ? pStart : pEnd, sStart <= sEnd ? pEnd : pStart);
  }

  private static List<Guid> AddPartLabel(RhinoDoc doc, string label, HashSet<Guid> endItemIds, Curve? outsideCurve, Curve? insideCurve, Curve? centerCurve)
  {
    var ids = new List<Guid>(); var text = (label ?? "").Trim();
    if (text.Length == 0 || outsideCurve == null || insideCurve == null || endItemIds.Count == 0) return ids;
    var endCurves = endItemIds.Select(id => CurveFromId(doc, id)).Where(c => c != null).Cast<Curve>().ToList();
    if (endCurves.Count == 0) return ids;
    var frame = centerCurve != null ? CenterLocalFrame(centerCurve) : null;
    Curve? left = null; double? bestS = null;
    foreach (var end in endCurves)
    {
      if (frame.HasValue)
      {
        var (endsMid, xRight, _, _, _) = frame.Value; double bestOnCurve = double.MaxValue;
        var div = end.DivideByCount(24, true) ?? new[] { end.Domain.Mid };
        foreach (var t in div) { var s = Vector3d.Multiply(end.PointAt(t) - endsMid, xRight); if (s < bestOnCurve) bestOnCurve = s; }
        if (left == null || bestOnCurve < bestS) { left = end; bestS = bestOnCurve; }
      }
      else { var mid = CurveMidpoint(end); if (left == null || mid.X < bestS) { left = end; bestS = mid.X; } }
    }
    if (left == null) return ids;
    var p0 = left.PointAtStart; var p1 = left.PointAtEnd;
    Point3d contact, otherEnd;
    if (frame.HasValue)
    {
      var (endsMid, xRight, _, _, _) = frame.Value;
      var s0 = Vector3d.Multiply(p0 - endsMid, xRight); var s1 = Vector3d.Multiply(p1 - endsMid, xRight);
      contact = s0 <= s1 ? p0 : p1; otherEnd = s0 <= s1 ? p1 : p0;
    }
    else { contact = p0.X <= p1.X ? p0 : p1; otherEnd = p0.X <= p1.X ? p1 : p0; }
    if (!outsideCurve.ClosestPoint(contact, out var to) || !insideCurve.ClosestPoint(contact, out var ti)) return ids;
    var outPt = outsideCurve.PointAt(to); var inPt = insideCurve.PointAt(ti);
    var bandMid = new Point3d((inPt.X+outPt.X)*0.5, (inPt.Y+outPt.Y)*0.5, (inPt.Z+outPt.Z)*0.5);
    var tangent = otherEnd - contact; if (!tangent.Unitize()) tangent = left.TangentAt(left.Domain.Mid); if (!tangent.Unitize()) tangent = Vector3d.XAxis;
    var yAxis = bandMid - contact; yAxis -= tangent * Vector3d.Multiply(yAxis, tangent); if (!yAxis.Unitize()) return ids;
    var curveLen = left.GetLength(); var alongDist = Math.Max(0.0, Math.Min(Math.Abs(LabelOffsetAlong), curveLen));
    bool useFromStart = !frame.HasValue || Vector3d.Multiply(left.PointAtStart - frame.Value.EndsMid, frame.Value.XRight) <= Vector3d.Multiply(left.PointAtEnd - frame.Value.EndsMid, frame.Value.XRight);
    double alongT = left.Domain.Mid;
    if (useFromStart) { if (!left.LengthParameter(alongDist, out alongT)) alongT = left.Domain.Mid; }
    else { if (!left.LengthParameter(Math.Max(0.0, curveLen - alongDist), out alongT)) alongT = left.Domain.Mid; }
    var alongPt = left.PointAt(alongT);
    var downRef = CurveMidpoint(outsideCurve) - alongPt; downRef -= tangent * Vector3d.Multiply(downRef, tangent);
    if (downRef.Unitize() && Vector3d.Multiply(yAxis, downRef) < 0.0) yAxis = -yAxis;
    var topLeftAnchor = alongPt + yAxis * Math.Abs(LabelOffsetPerp);
    var textYAxis = -yAxis; if (!textYAxis.Unitize()) textYAxis = Vector3d.YAxis;
    var labelPlane = new Plane(topLeftAnchor, tangent, textYAxis);
    if (Vector3d.Multiply(labelPlane.ZAxis, Vector3d.ZAxis) < 0.0) labelPlane = new Plane(topLeftAnchor, -labelPlane.XAxis, labelPlane.YAxis);
    var te = new TextEntity { Plane = labelPlane, PlainText = text, TextHeight = 0.5, Justification = TextJustification.TopLeft };
    var id = AddText(doc, te, LayerPlot);
    if (id != Guid.Empty) { LockTextTopLeftToPoint(doc, id, topLeftAnchor); ids.Add(id); }
    return ids;
  }

  private static Guid? AddFaceDownText(RhinoDoc doc, Curve? outerCurve, Curve? insideCurve, string note)
  {
    if (outerCurve == null || insideCurve == null) return null;
    var text = (note ?? "").Trim(); if (text.Length == 0) return null;
    var len = outerCurve.GetLength(); if (len <= RhinoMath.ZeroTolerance) return null;
    var t = outerCurve.LengthParameter(0.5 * len, out var tm) ? tm : outerCurve.Domain.Mid;
    var pMid = outerCurve.PointAt(t);
    if (!insideCurve.ClosestPoint(pMid, out var ti)) return null;
    var pIn = insideCurve.PointAt(ti); var tangent = outerCurve.TangentAt(t); if (!tangent.Unitize()) return null;
    var yAxis = pIn - pMid; yAxis -= tangent * Vector3d.Multiply(yAxis, tangent); if (!yAxis.Unitize()) return null;
    var center = pMid + yAxis * 1.5; var textPlane = new Plane(center, tangent, yAxis);
    var te = new TextEntity { Plane = textPlane, PlainText = text, TextHeight = 3.0, Justification = TextJustification.MiddleCenter };
    var id = AddText(doc, te, LayerReference);
    return id == Guid.Empty ? null : id;
  }

  private static string? AddPartGroup(RhinoDoc doc, string groupName, IEnumerable<Guid> objectIds)
  {
    var ids = objectIds.Where(id => id != Guid.Empty).Distinct().ToList();
    if (ids.Count == 0) return null;
    var finalName = groupName; var suffix = 2;
    while (doc.Groups.FindName(finalName) != null) { finalName = $"{groupName}_{suffix}"; suffix++; }
    var groupIndex = doc.Groups.Add(finalName); if (groupIndex < 0) return null;
    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id); if (obj == null) continue;
      var attr = obj.Attributes.Duplicate(); attr.AddToGroup(groupIndex);
      doc.Objects.ModifyAttributes(id, attr, false);
    }
    return finalName;
  }

  private static void MirrorPartAboutCenterMid(RhinoDoc doc, List<Guid> objectIds, Curve centerCurve)
  {
    var len = centerCurve.GetLength();
    var tm = centerCurve.LengthParameter(0.5 * len, out var t) ? t : centerCurve.Domain.Mid;
    var midPt = centerCurve.PointAt(tm);
    if (!centerCurve.PerpendicularFrameAt(tm, out var frame)) return;
    var tangent = frame.ZAxis; if (!tangent.Unitize()) return;
    TransformIdsInPlace(doc, objectIds, Transform.Mirror(new Plane(midPt, tangent)));
  }

  private static void TransformIdsInPlace(RhinoDoc doc, List<Guid> ids, Transform xform)
  {
    for (var i = 0; i < ids.Count; i++)
    {
      var newId = doc.Objects.Transform(ids[i], xform, true);
      if (newId != Guid.Empty) ids[i] = newId;
    }
  }

  private static void MirrorTextAboutOwnVerticalAxis(RhinoDoc doc, List<Guid> objectIds)
  {
    for (var i = 0; i < objectIds.Count; i++)
    {
      var obj = doc.Objects.FindId(objectIds[i]);
      if (obj?.Geometry is not TextEntity te) continue;
      var plane = te.Plane; var lb = te.GetBoundingBox(plane); if (!lb.IsValid) continue;
      var center = plane.PointAt((lb.Min.X+lb.Max.X)*0.5, (lb.Min.Y+lb.Max.Y)*0.5);
      var axisDir = plane.YAxis; if (!axisDir.Unitize()) continue;
      var normal = Vector3d.CrossProduct(axisDir, plane.ZAxis); if (!normal.Unitize()) continue;
      var newId = doc.Objects.Transform(objectIds[i], Transform.Mirror(new Plane(center, normal)), true);
      if (newId != Guid.Empty) objectIds[i] = newId;
    }
  }

  // ── Part specs ────────────────────────────────────────────────────────────

  private static string AutoOffsetName(double signedOffset)
  {
    var side = signedOffset > 0.0 ? "outside" : signedOffset < 0.0 ? "inside" : "center";
    var mag  = Math.Abs(signedOffset).ToString("0.####", CultureInfo.InvariantCulture).Replace('.', 'p');
    return $"{side}_{(string.IsNullOrWhiteSpace(mag) ? "0" : mag)}";
  }

  private static List<PartSpec> ClonePartSpecs(List<PartSpec>? source)
  {
    if (source == null || source.Count == 0) return new List<PartSpec>();
    try { var j = JsonSerializer.Serialize(source, JsonOptions); return JsonSerializer.Deserialize<List<PartSpec>>(j, JsonOptions) ?? new List<PartSpec>(); }
    catch { return new List<PartSpec>(); }
  }

  private static List<PartSpec> CreateDefaultPartSpecs(LayerRuntime lr) => new()
  {
    new() { Name = "zipper", CenterLayer = lr.PlotName,      CenterEndMode = "extend_trim", Note = "FACE DOWN", Offsets = { new OffsetSpec { Offset = -0.75 }, new OffsetSpec { Offset = 0.75  } } },
    new() { Name = "inner",  CenterLayer = lr.ReferenceName, CenterEndMode = "trim",         Offsets = { new OffsetSpec { Offset = -0.75 }, new OffsetSpec { Offset = 1.125 } } },
    new() { Name = "outer",  CenterLayer = lr.ReferenceName, CenterEndMode = "trim", MirrorPart = true,
            Offsets = { new OffsetSpec { Offset = -0.75, Layer = lr.PlotName }, new OffsetSpec { Offset = -1.5 }, new OffsetSpec { Offset = 1.125 } } }
  };

  private static List<PartSpec> ResolvePartSpecs(string centerItemLayer, List<PartSpec>? configuredParts, LayerRuntime layerRuntime)
  {
    var specs = ClonePartSpecs(configuredParts);
    if (specs.Count == 0) specs = CreateDefaultPartSpecs(layerRuntime);
    specs = specs.Where(s => s != null && s.Offsets != null && s.Offsets.Count > 0).ToList();
    foreach (var spec in specs)
    {
      spec.Name          = string.IsNullOrWhiteSpace(spec.Name) ? "part" : spec.Name.Trim();
      spec.Note          = spec.Note ?? "";
      spec.CenterLayer   = string.IsNullOrWhiteSpace(spec.CenterLayer) ? centerItemLayer : spec.CenterLayer!.Trim();
      spec.CenterEndMode = (spec.CenterEndMode ?? "").Trim().ToLowerInvariant();
      spec.CenterEndMode = spec.CenterEndMode is "trim" or "extend_trim" ? spec.CenterEndMode : "";
      var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var off in spec.Offsets)
      {
        var name = string.IsNullOrWhiteSpace(off.Name) ? AutoOffsetName(off.Offset) : off.Name.Trim();
        var baseName = name; var suffix = 2;
        while (!used.Add(name)) { name = $"{baseName}_{suffix}"; suffix++; }
        off.Name = name;
      }
      var inside  = spec.Offsets.Where(o => o.Offset < 0.0).OrderBy(o => o.Offset).FirstOrDefault()
                 ?? spec.Offsets.OrderBy(o => o.Offset).First();
      var outside = spec.Offsets.Where(o => o.Offset > 0.0).OrderByDescending(o => o.Offset).FirstOrDefault()
                 ?? spec.Offsets.OrderBy(o => o.Offset).Last();
      spec.BandInsideName  = inside.Name;
      spec.BandOutsideName = outside.Name;
      foreach (var off in spec.Offsets)
      {
        if (!string.IsNullOrWhiteSpace(off.Layer)) { off.Layer = off.Layer.Trim(); continue; }
        off.Layer = (off.Name == spec.BandInsideName || off.Name == spec.BandOutsideName) ? LayerCut : spec.CenterLayer;
      }
    }
    return specs;
  }

  // ── Build parts ───────────────────────────────────────────────────────────

  private static (List<Guid> PartObjects, string? GroupName) MakePart(RhinoDoc doc, int partIndex, string buildStamp, string label, CurveItem centerItem, List<CurveItem> allPreselected, HashSet<Guid> touchingIds, Curve centerCurve, Plane plane, int insideSign, List<Curve> endCurves, PartSpec spec)
  {
    var centerMid  = CurveMidpoint(centerCurve);
    var partObjects = new List<Guid>();
    var selected    = new List<CurveItem>(allPreselected);
    var endItemIds  = new HashSet<Guid>();
    var maxAbsOffset = spec.Offsets.Count > 0 ? spec.Offsets.Max(o => Math.Abs(o.Offset)) : 0.0;
    var deduplicated = new List<Curve>();
    var addedEndCurves = new List<Curve>();
    foreach (var end in endCurves)
    {
      if (deduplicated.Any(ex => CurvesNearlySame(doc, ex, end))) continue;
      deduplicated.Add(end);
      addedEndCurves.Add(maxAbsOffset > doc.ModelAbsoluteTolerance ? (end.Extend(CurveEnd.Both, maxAbsOffset, CurveExtensionStyle.Line) ?? end) : end);
    }
    var centerLayer  = string.IsNullOrWhiteSpace(spec.CenterLayer) ? centerItem.LayerName : spec.CenterLayer!;
    Curve centerForPart = spec.CenterEndMode == "extend_trim" ? ExtendCurveToEnd(doc, centerCurve, endCurves, centerMid)
                        : spec.CenterEndMode == "trim"        ? TrimToMiddleBetweenEndCurves(doc, centerCurve, endCurves, centerMid)
                        : centerCurve.DuplicateCurve();
    var centerId = AddCurve(doc, centerForPart, centerLayer);
    if (centerId != Guid.Empty) partObjects.Add(centerId);
    var offsets = new Dictionary<string, Curve>(StringComparer.OrdinalIgnoreCase);
    foreach (var off in spec.Offsets)
    {
      var side = off.Offset >= 0.0 ? "outside" : "inside";
      var oc   = OffsetAtSide(doc, centerCurve, plane, insideSign, Math.Abs(off.Offset), side);
      if (oc == null) continue;
      offsets[off.Name] = ExtendCurveToEnd(doc, oc, addedEndCurves, centerMid);
    }
    foreach (var off in spec.Offsets)
    {
      if (!offsets.TryGetValue(off.Name, out var curve)) continue;
      var id = AddCurve(doc, curve, string.IsNullOrWhiteSpace(off.Layer) ? centerItem.LayerName : off.Layer!);
      if (id != Guid.Empty) partObjects.Add(id);
    }
    offsets.TryGetValue(spec.BandInsideName,  out var insideBoundary);
    offsets.TryGetValue(spec.BandOutsideName, out var outsideBoundary);
    if (insideBoundary != null && outsideBoundary != null)
    {
      foreach (var item in selected)
      {
        var source = item.Curve.DuplicateCurve(); if (source == null) continue;
        var useEndpointSideKeep = !item.ObjectId.HasValue || !touchingIds.Contains(item.ObjectId.Value);
        var pieces = SplitAndFilterForBand(doc, source, insideBoundary, outsideBoundary, centerCurve, useEndpointSideKeep);
        foreach (var piece in pieces)
        {
          var id = AddCurve(doc, piece, item.LayerName); if (id == Guid.Empty) continue;
          partObjects.Add(id);
          if (!item.ObjectId.HasValue || touchingIds.Contains(item.ObjectId.Value)) { if (item.LayerName == LayerCut) endItemIds.Add(id); }
        }
      }
    }
    else foreach (var item in allPreselected) { var id = AddCurve(doc, item.Curve.DuplicateCurve(), item.LayerName); if (id != Guid.Empty) partObjects.Add(id); }
    foreach (var widenedCap in addedEndCurves)
    {
      foreach (var capPiece in TrimCapToBandBoundaries(doc, widenedCap, insideBoundary, outsideBoundary))
      {
        var capId = AddCurve(doc, capPiece, LayerCut);
        if (capId != Guid.Empty) { partObjects.Add(capId); endItemIds.Add(capId); }
      }
    }
    if (endItemIds.Count == 0)
      foreach (var id in partObjects) { var obj = doc.Objects.FindId(id); if (obj == null) continue; if (string.Equals(doc.Layers[obj.Attributes.LayerIndex]?.FullPath, LayerCut, StringComparison.OrdinalIgnoreCase)) endItemIds.Add(id); }
    partObjects.AddRange(AddPartLabel(doc, label, endItemIds, outsideBoundary, insideBoundary, centerForPart));
    var faceDown = AddFaceDownText(doc, outsideBoundary, insideBoundary, spec.Note);
    if (faceDown.HasValue) partObjects.Add(faceDown.Value);
    if (spec.MirrorPart) { MirrorPartAboutCenterMid(doc, partObjects, centerForPart); MirrorTextAboutOwnVerticalAxis(doc, partObjects); }
    var partToken = string.IsNullOrWhiteSpace(label) ? spec.Name.Replace(' ', '_') : $"{label.Trim().Replace(' ', '_')}_{spec.Name.Replace(' ', '_')}";
    return (partObjects, AddPartGroup(doc, $"U-Zip_{buildStamp}_{partToken}", partObjects));
  }

  private static void BuildAllParts(RhinoDoc doc, string stamp, string label, CurveItem centerItem, List<CurveItem> allPreselected, HashSet<Guid> touchingIds, Curve centerCurve, Plane plane, int insideSign, List<Curve> endCurves, List<PartSpec> specs, List<List<Guid>> allPartObjectIds, List<string> createdGroups)
  {
    doc.Views.RedrawEnabled = false;
    try
    {
      for (var i = 0; i < specs.Count; i++)
      {
        var (partIds, groupName) = MakePart(doc, i + 1, stamp, label, centerItem, allPreselected, touchingIds, centerCurve, plane, insideSign, endCurves, specs[i]);
        allPartObjectIds.Add(partIds);
        if (!string.IsNullOrWhiteSpace(groupName)) createdGroups.Add(groupName);
      }
      StackPartsDown(doc, allPartObjectIds, PartGap);
    }
    finally { doc.Views.RedrawEnabled = true; doc.Views.Redraw(); }
  }

  private static (double MinY, double MaxY)? BBoxYRange(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    BoundingBox? bbox = null;
    foreach (var id in objectIds.Where(id => id != Guid.Empty))
    {
      var obj = doc.Objects.FindId(id); if (obj?.Geometry == null) continue;
      var gb = obj.Geometry.GetBoundingBox(true); if (!gb.IsValid) continue;
      bbox = bbox.HasValue ? BoundingBox.Union(bbox.Value, gb) : gb;
    }
    if (!bbox.HasValue || !bbox.Value.IsValid) return null;
    return (bbox.Value.Min.Y, bbox.Value.Max.Y);
  }

  private static void StackPartsDown(RhinoDoc doc, List<List<Guid>> partObjectLists, double gap)
  {
    double? currentBottom = null;
    for (var i = 0; i < partObjectLists.Count; i++)
    {
      var ids = partObjectLists[i].Where(id => doc.Objects.FindId(id) != null).ToList();
      if (ids.Count == 0) continue;
      var range = BBoxYRange(doc, ids); if (!range.HasValue) continue;
      if (i == 0) { currentBottom = range.Value.MinY; continue; }
      if (!currentBottom.HasValue) { currentBottom = range.Value.MinY; continue; }
      var deltaY = currentBottom.Value - gap - range.Value.MaxY;
      if (Math.Abs(deltaY) > doc.ModelAbsoluteTolerance)
      {
        var xform = Transform.Translation(0.0, deltaY, 0.0);
        for (var k = 0; k < ids.Count; k++)
        {
          var newId = doc.Objects.Transform(ids[k], xform, true);
          if (newId != Guid.Empty) { ids[k] = newId; var oi = partObjectLists[i].FindIndex(id => id == ids[k]); if (oi < 0) { var oldId = partObjectLists[i].FindIndex(j => doc.Objects.FindId(j) == null); if (oldId >= 0) partObjectLists[i][oldId] = newId; } else partObjectLists[i][oi] = newId; }
        }
        currentBottom = BBoxYRange(doc, ids)?.MinY ?? (range.Value.MinY + deltaY);
      }
      else currentBottom = range.Value.MinY;
    }
  }

  // ── Placement loop + cleanup ──────────────────────────────────────────────

  private static void DeleteCreatedGroupsAndMembers(RhinoDoc doc, IEnumerable<string> groupNames)
  {
    var ids = new HashSet<Guid>();
    foreach (var name in groupNames)
    {
      if (string.IsNullOrWhiteSpace(name)) continue;
      var group = doc.Groups.FindName(name); if (group == null) continue;
      var members = doc.Groups.GroupMembers(group.Index);
      if (members != null) foreach (var m in members) if (m != null) ids.Add(m.Id);
    }
    foreach (var id in ids) doc.Objects.Delete(id, true);
    foreach (var name in groupNames)
    {
      if (string.IsNullOrWhiteSpace(name)) continue;
      var group = doc.Groups.FindName(name); if (group != null) doc.Groups.Delete(group.Index);
    }
  }

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

  // ── Preview conduit ───────────────────────────────────────────────────────

  private sealed class PreviewConduit : DisplayConduit
  {
    public Curve? Curve { get; set; }
    public Color  CenterColor { get; set; } = Color.Cyan;
    public List<(Curve Crv, Color Col)> SideCurves { get; } = new();

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (Curve != null) e.Display.DrawCurve(Curve, CenterColor, 2);
      foreach (var (crv, col) in SideCurves) e.Display.DrawCurve(crv, col, 1);
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

  // ── RunCommand ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var s          = LoadSettings();
    var configPath = GetToolsConfigPath();
    var configRoot = LoadToolsConfig(configPath);
    var section    = EnsureUZipSection(configRoot);
    var lr         = NormalizeLayerRuntime(section.Layers);
    ApplyLayerRuntime(lr);
    EnsureCommandLayers(doc, lr);

    double offL         = s.Left;
    double offR         = s.Right;
    double offB         = s.Bottom;
    double radius       = s.Radius;
    bool   glass        = s.Glass;
    bool   vis          = s.Vis;
    bool   parts        = s.Parts;
    var    currentLabel = s.Label;
    var    currentTail  = s.Tail;

    // Snapshot all preselected IDs before clearing selection
    var preselectedIds = doc.Objects.GetSelectedObjects(false, false)
      .Where(o => o != null)
      .Select(o => o.Id)
      .ToList();
    doc.Objects.UnselectAll();

    // ── Phase 1: collect 3 U-arm curves ──────────────────────────────────────
    // preCurveIds tracks the source object ID for each entry in preCurves (parallel list)
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
        var glassT = new OptionToggle(glass, "No", "Yes");
        var visT   = new OptionToggle(vis,   "No", "Yes");
        var partsT = new OptionToggle(parts, "No", "Yes");
        go.AddOptionToggle("Glass", ref glassT);
        go.AddOptionToggle("Vis",   ref visT);
        go.AddOptionToggle("Parts", ref partsT);
        go.AddOption("Options");
        var res = go.GetMultiple(need, need);
        if (res == GetResult.Cancel || go.CommandResult() != Result.Success) return Result.Cancel;
        if (res == GetResult.Option)
        {
          glass = glassT.CurrentValue; vis = visT.CurrentValue; parts = partsT.CurrentValue;
          var opt = go.Option()?.EnglishName ?? "";
          if      (opt == "Left")    { var v = GetDistSubprompt("Left arm offset",  offL); if (v == null) return Result.Cancel; offL = v.Value; }
          else if (opt == "Right")   { var v = GetDistSubprompt("Right arm offset", offR); if (v == null) return Result.Cancel; offR = v.Value; }
          else if (opt == "Bottom")  { var v = GetDistSubprompt("Bottom offset",    offB); if (v == null) return Result.Cancel; offB = v.Value; }
          else if (opt == "Radius")  { var v = GetDistSubprompt("Fillet radius",  radius); if (v == null) return Result.Cancel; radius = v.Value; }
          else if (opt == "Options") { var dlg = new OptionsDialog(doc, s); dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); if (dlg.Result) { dlg.ApplyTo(s); offL = s.Left; offR = s.Right; offB = s.Bottom; radius = s.Radius; glass = s.Glass; vis = s.Vis; } }
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

    // IDs that were consumed as the 3 U-arm curves — exclude from parts pipeline
    var uArmIds = new HashSet<Guid>(preCurveIds.Take(3));

    // Curves available for the parts pipeline = everything preselected except the 3 U-arms
    var partsSelectionIds = preselectedIds.Where(id => !uArmIds.Contains(id)).ToList();

    // ── Phase 2: preview + curve-selection (parts) or boundary (no parts) ─────
    var capCurves    = new List<Curve>(); // boundary curve used in parts=off mode
    Curve? displayCurve = null;
    var tol = doc.ModelAbsoluteTolerance;
    static Color Faded(Color c) => Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);
    Color FadedLayerColor(string name)
    {
      var idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
      return Faded(idx >= 0 ? doc.Layers[idx].Color : Color.Gray);
    }

    var conduit = new PreviewConduit { Enabled = true };
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
        displayCurve = finalCurve;

        // Derive trim caps: auto from partsSelection (parts=on) or explicit capCurves (parts=off)
        List<Curve> trimCaps;
        if (parts && partsSelectionIds.Count > 0)
        {
          var pvItems    = CollectPreselected(doc, Guid.Empty, finalCurve, partsSelectionIds, false);
          var pvTouching = pvItems.Where(i => CurvesIntersect(finalCurve, i.Curve, tol)).ToList();
          var pvPlane    = GetCurvePlane(finalCurve);
          var (pvEnds, _) = BuildEndCurves(doc, pvTouching, finalCurve, pvPlane, currentTail);
          trimCaps = pvEnds;
        }
        else
          trimCaps = capCurves;

        foreach (var cap in trimCaps) { var cl = TrimExtendToCurve(displayCurve, cap, tol); if (cl != null) displayCurve = cl; }

        conduit.SideCurves.Clear();
        var pvNormal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
        var clIdx    = doc.Layers.FindByFullPath(s.CenterLayer, RhinoMath.UnsetIntIndex);
        conduit.CenterColor = Faded(clIdx >= 0 ? doc.Layers[clIdx].Color : Color.Cyan);
        conduit.Curve = displayCurve;
        if (glass) foreach (var c in OffsetBothSides(displayCurve, s.GlassOffset, pvNormal, tol)) conduit.SideCurves.Add((c, FadedLayerColor(s.GlassLayer)));
        if (vis)   foreach (var c in OffsetBothSides(displayCurve, s.VisOffset,   pvNormal, tol)) conduit.SideCurves.Add((c, FadedLayerColor(s.VisLayer)));
        doc.Objects.UnselectAll(); doc.Views.Redraw();

        var gp2 = new GetObject();
        gp2.SetCommandPrompt(parts
          ? $"Click curves to include in parts, Enter to accept ({partsSelectionIds.Count} selected)"
          : "Enter to accept; click curve to trim/extend ends");
        gp2.GeometryFilter = ObjectType.Curve; gp2.SubObjectSelect = false;
        gp2.EnablePreSelect(false, true); gp2.DeselectAllBeforePostSelect = true;
        gp2.AcceptNothing(true);
        gp2.AddOption("Left",   FmtOpt(offL));
        gp2.AddOption("Right",  FmtOpt(offR));
        gp2.AddOption("Bottom", FmtOpt(offB));
        gp2.AddOption("Radius", FmtOpt(radius));
        var glassT2 = new OptionToggle(glass, "No", "Yes");
        var visT2   = new OptionToggle(vis,   "No", "Yes");
        var partsT2 = new OptionToggle(parts, "No", "Yes");
        gp2.AddOptionToggle("Glass", ref glassT2);
        gp2.AddOptionToggle("Vis",   ref visT2);
        gp2.AddOptionToggle("Parts", ref partsT2);
        if (parts)
        {
          gp2.AddOption("Label", string.IsNullOrEmpty(currentLabel) ? "none" : currentLabel);
          gp2.AddOption("Tail",  FmtOpt(currentTail));
        }
        gp2.AddOption("Options");

        var res2 = gp2.Get();
        if (res2 == GetResult.Nothing) break;
        if (res2 == GetResult.Object)
        {
          if (parts)
          {
            // Toggle clicked curve in/out of parts selection by object ID
            var clickedId = gp2.Object(0).ObjectId;
            if (partsSelectionIds.Contains(clickedId)) partsSelectionIds.Remove(clickedId);
            else partsSelectionIds.Add(clickedId);
          }
          else
          {
            var cap = gp2.Object(0).Curve()?.DuplicateCurve();
            if (cap != null) capCurves = new List<Curve> { cap };
          }
          doc.Objects.UnselectAll(); continue;
        }
        if (gp2.CommandResult() != Result.Success) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; }
        if (res2 == GetResult.Option)
        {
          glass = glassT2.CurrentValue; vis = visT2.CurrentValue; parts = partsT2.CurrentValue;
          var opt2 = gp2.Option()?.EnglishName ?? "";
          if      (opt2 == "Left")    { var v = GetDistSubprompt("Left arm offset",  offL); if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; } offL = v.Value; }
          else if (opt2 == "Right")   { var v = GetDistSubprompt("Right arm offset", offR); if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; } offR = v.Value; }
          else if (opt2 == "Bottom")  { var v = GetDistSubprompt("Bottom offset",    offB); if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; } offB = v.Value; }
          else if (opt2 == "Radius")  { var v = GetDistSubprompt("Fillet radius",  radius); if (v == null) { conduit.Enabled = false; doc.Views.Redraw(); return Result.Cancel; } radius = v.Value; }
          else if (opt2 == "Label")   { var nl = currentLabel; if (RhinoGet.GetString("Label", true, ref nl) == Result.Success) currentLabel = (nl ?? DefaultLabel).Trim(); }
          else if (opt2 == "Tail")    { var nt = currentTail;  if (RhinoGet.GetNumber("Tail length", true, ref nt) == Result.Success && nt >= 0.0) currentTail = nt; }
          else if (opt2 == "Options") { var dlg = new OptionsDialog(doc, s); dlg.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); if (dlg.Result) { dlg.ApplyTo(s); glass = s.Glass; vis = s.Vis; } }
          continue;
        }
      }
    }
    finally { conduit.Enabled = false; }

    if (displayCurve == null) return Result.Failure;

    // ── Derive final end curves for commit (glass/vis trim + parts) ───────────
    List<Curve> finalEndCurves;
    if (parts && partsSelectionIds.Count > 0)
    {
      var feItems    = CollectPreselected(doc, Guid.Empty, displayCurve, partsSelectionIds, false);
      var feTouching = feItems.Where(i => CurvesIntersect(displayCurve, i.Curve, tol)).ToList();
      var fePlane    = GetCurvePlane(displayCurve);
      var (feEnds, _) = BuildEndCurves(doc, feTouching, displayCurve, fePlane, currentTail);
      finalEndCurves = feEnds;
    }
    else
      finalEndCurves = capCurves;

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
        {
          var final = c;
          foreach (var cap in finalEndCurves) { var cl = TrimExtendToCurve(final, cap, tol); if (cl != null) final = cl; }
          AddCurve(doc, final, layerName);
        }
      }
      if (glass) AddOffsets(s.GlassOffset, s.GlassLayer);
      if (vis)   AddOffsets(s.VisOffset,   s.VisLayer);
    }

    // Parts pipeline
    if (parts)
    {
      var plane      = GetCurvePlane(displayCurve);
      var insideSign = SolveInsideSign(doc, displayCurve, plane);
      var specs      = ResolvePartSpecs(s.CenterLayer, section.Parts, lr);
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
      BuildAllParts(doc, stamp, currentLabel, centerItem, allPreItems, touchingIds, displayCurve, plane, insideSign, initEndCurves, specs, allPartObjectIds, createdGroups);

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
        BuildAllParts(doc, stamp, currentLabel, centerItem, rebuildAllPre, rebuildTouchingIds, displayCurve, plane, insideSign, rebuildEndCrvs, specs, allPartObjectIds, createdGroups);
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
