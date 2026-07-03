using System;
using System.Drawing;
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
/// Adds a chamfer line perpendicular to the angle bisector at a specified cut
/// length across a corner formed by two curves. The virtual corner is the
/// intersection of the tangent extensions from each curve's nearest endpoint â€”
/// works even when curves were previously chamfered and no longer share a point.
/// If a curve is too short to reach the chamfer point it is extended first.
///
/// Option Trim:
///   No  â€” only the chamfer line is added; curves are not modified.
///   Yes â€” both curves are trimmed to the chamfer points and the line is added.
///
/// Workflow:
///   Pick curve 1 â€” near the corner.
///   Pick curve 2 â€” near the same corner.
///   Length and Trim options are available at every prompt.
///   Press Enter to apply.
/// </summary>
public sealed class vChamfer : Command
{
  private const string SectionName = "vChamfer";
  private const string LengthKey   = "length";
  private const string TrimKey     = "trim";
  private const string JoinKey     = "join";

  private static double _length = 1.0;
  private static bool   _trim   = true;   // true = trim curves; false = add line only
  private static bool   _join   = true;   // only used when _trim = true

  public override string EnglishName => "vChamfer";

  // â”€â”€ Option persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, LengthKey, out var l) && l >= 0.0)
        _length = l;
      if (ToolsOptionStore.TryGetBool(section, TrimKey, out var t))
        _trim = t;
      if (ToolsOptionStore.TryGetBool(section, JoinKey, out var j))
        _join = j;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[LengthKey] = _length;
      section[TrimKey]   = _trim;
      section[JoinKey]   = _join;
    });

  // â”€â”€ Curve picking with options â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  private static (ObjRef? Ref, Curve? Crv) PickCurveWithOptions(string prompt)
  {
    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt(prompt);
      go.GeometryFilter              = ObjectType.Curve;
      go.SubObjectSelect             = false;
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;
      go.AcceptNumber(true, true);
      var lengthOpt = new OptionDouble(_length, 0.0, double.MaxValue);
      var idxLength = go.AddOptionDouble("Length", ref lengthOpt);
      var trimToggle = new OptionToggle(_trim, "No", "Yes");
      go.AddOptionToggle("Trim", ref trimToggle);
      var joinTogglePick = new OptionToggle(_join, "No", "Yes");
      if (_trim) go.AddOptionToggle("Join", ref joinTogglePick);

      var res = go.Get();

      if (res == GetResult.Object && go.ObjectCount >= 1)
      {
        var objRef = go.Object(0);
        if (objRef == null) return (null, null);
        var crv = objRef.Curve() ?? objRef.Geometry() as Curve;
        if (crv == null) return (null, null);
        return (objRef, crv.DuplicateCurve());
      }

      if (res == GetResult.Cancel || go.CommandResult() != Result.Success)
        return (null, null);

      if (res == GetResult.Number)
      {
        var v = go.Number();
        if (TrySetLength(v))
          SaveOptions();
        continue;
      }

      if (res == GetResult.Option)
      {
        if (go.Option()?.Index == idxLength)
          TrySetLength(lengthOpt.CurrentValue);
        _trim = trimToggle.CurrentValue;
        if (_trim) _join = joinTogglePick.CurrentValue;
        SaveOptions();
      }
    }
  }

  private static bool TrySetLength(double value)
  {
    if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
    {
      RhinoApp.WriteLine("vChamfer: length must be zero or greater.");
      return false;
    }

    _length = value;
    return true;
  }

  // â”€â”€ Corner detection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  /// <summary>
  /// Finds the closest endpoint pair and computes a virtual corner as the
  /// intersection of the tangent extensions â€” correct even when the curves do
  /// not share an endpoint (e.g. previously chamfered).
  /// </summary>
  private static (bool C1AtStart, bool C2AtStart, Point3d VirtualCorner) FindCorner(
    Curve c1, Curve c2)
  {
    bool bestC1s = true, bestC2s = true;
    double best = double.MaxValue;

    foreach (bool c1s in new[] { true, false })
    foreach (bool c2s in new[] { true, false })
    {
      double d = (c1s ? c1.PointAtStart : c1.PointAtEnd)
                 .DistanceTo(c2s ? c2.PointAtStart : c2.PointAtEnd);
      if (d < best) { best = d; bestC1s = c1s; bestC2s = c2s; }
    }

    var ep1 = bestC1s ? c1.PointAtStart : c1.PointAtEnd;
    var ep2 = bestC2s ? c2.PointAtStart : c2.PointAtEnd;

    // Tangents pointing toward the virtual corner (past the near endpoint).
    var t1 = bestC1s ? -c1.TangentAtStart : c1.TangentAtEnd;
    var t2 = bestC2s ? -c2.TangentAtStart : c2.TangentAtEnd;
    t1.Unitize(); t2.Unitize();

    var lineA = new Line(ep1, ep1 + t1 * 1e4);
    var lineB = new Line(ep2, ep2 + t2 * 1e4);

    if (Intersection.LineLine(lineA, lineB, out double a, out double b, 1e-6, false))
    {
      var vc = (lineA.PointAt(a) + lineB.PointAt(b)) * 0.5;
      Log.Write("vChamfer", $"corner  ep1={ep1:F4}  ep2={ep2:F4}  gap={best:F4}  c1AtStart={bestC1s}  c2AtStart={bestC2s}  corner={vc:F4}");
      return (bestC1s, bestC2s, vc);
    }

    // Parallel tangents — fall back to endpoint midpoint.
    var mid = (ep1 + ep2) * 0.5;
    Log.Write("vChamfer", $"corner  parallel tangents fallback  ep1={ep1:F4}  ep2={ep2:F4}  mid={mid:F4}");
    return (bestC1s, bestC2s, mid);
  }

  // â”€â”€ Extension â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  /// <summary>
  /// Extends the corner end of a working-copy curve to the virtual corner point.
  /// This allows chamfering when the curve is too short to reach the chamfer point.
  /// Returns the (possibly extended) curve.
  /// </summary>
  private static Curve ExtendToCorner(Curve c, bool atStart, Point3d virtualCorner)
  {
    var ep   = atStart ? c.PointAtStart : c.PointAtEnd;
    double d = ep.DistanceTo(virtualCorner);
    if (d < 1e-6) return c;

    var end      = atStart ? CurveEnd.Start : CurveEnd.End;
    var extended = c.Extend(end, d + 1e-3, CurveExtensionStyle.Line);
    return extended ?? c;
  }

  // â”€â”€ Chamfer computation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  /// <summary>
  /// Computes chamfer endpoints on working curves (already extended to corner).
  /// Returns false when geometry is degenerate.
  /// </summary>
  // ── Plane inference ────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a plane from the two corner tangents so that the chamfer angle is computed
  /// in the plane of the curves, not the viewport CPlane. Falls back to <paramref name="fallback"/>
  /// when the tangents are parallel (no unique plane).
  /// </summary>
  private static Plane InferChamferPlane(
    Curve c1, bool c1AtStart, Curve c2, bool c2AtStart,
    Point3d corner, Plane fallback)
  {
    var t1 = c1AtStart ?  c1.TangentAtStart : -c1.TangentAtEnd;
    var t2 = c2AtStart ?  c2.TangentAtStart : -c2.TangentAtEnd;
    if (!t1.Unitize() || !t2.Unitize()) return fallback;

    var zAxis = Vector3d.CrossProduct(t1, t2);
    if (zAxis.Length < 1e-6)
    {
      Log.Write("vChamfer", $"plane  parallel tangents — using CPlane fallback  t1={t1:F4}  t2={t2:F4}");
      return fallback;
    }
    zAxis.Unitize();

    var xAxis = t1;
    var yAxis = Vector3d.CrossProduct(zAxis, xAxis);
    if (!yAxis.Unitize())
    {
      Log.Write("vChamfer", $"plane  yAxis degenerate — using CPlane fallback");
      return fallback;
    }

    var inferred = new Plane(corner, xAxis, yAxis);
    Log.Write("vChamfer", $"plane  t1={t1:F4}  t2={t2:F4}  normal={zAxis:F4}  xAxis={xAxis:F4}  yAxis={yAxis:F4}");
    return inferred;
  }

  // ── Chamfer computation ────────────────────────────────────────────────────

  /// <summary>
  /// Computes chamfer endpoints on working curves (already extended to corner).\n  /// Returns false when geometry is degenerate.
  /// </summary>
  private static bool ComputeChamfer(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    Point3d corner, double length, Plane cplane,
    out Point3d ptA, out Point3d ptB,
    out double  tA,  out double  tB)
  {
    ptA = ptB = Point3d.Unset;
    tA  = tB  = double.NaN;

    if (length < 0.0)
      return false;

    if (length <= RhinoMath.ZeroTolerance)
    {
      if (!c1.ClosestPoint(corner, out tA)) return false;
      if (!c2.ClosestPoint(corner, out tB)) return false;

      ptA = c1.PointAt(tA);
      ptB = c2.PointAt(tB);
      return ptA.IsValid && ptB.IsValid;
    }

    // Tangents pointing AWAY from the corner along each curve.
    var rawT1 = c1AtStart ?  c1.TangentAtStart : -c1.TangentAtEnd;
    var rawT2 = c2AtStart ?  c2.TangentAtStart : -c2.TangentAtEnd;

    if (rawT1.IsTiny() || rawT2.IsTiny())
    {
      Log.Write("vChamfer", $"compute  degenerate tangent  rawT1={rawT1:F4}  rawT2={rawT2:F4}");
      return false;
    }
    rawT1.Unitize(); rawT2.Unitize();

    // Project tangents onto the active CPlane (drop out-of-plane component).
    double t1x = rawT1 * cplane.XAxis, t1y = rawT1 * cplane.YAxis;
    double t2x = rawT2 * cplane.XAxis, t2y = rawT2 * cplane.YAxis;

    double len1 = Math.Sqrt(t1x * t1x + t1y * t1y);
    double len2 = Math.Sqrt(t2x * t2x + t2y * t2y);
    if (len1 < 1e-12 || len2 < 1e-12)
    {
      Log.Write("vChamfer", $"compute  projection too small  t1=({t1x:F4},{t1y:F4}) len={len1:G4}  t2=({t2x:F4},{t2y:F4}) len={len2:G4}");
      return false;
    }

    t1x /= len1; t1y /= len1;
    t2x /= len2; t2y /= len2;

    // Angle bisector in CPlane 2-D.
    double bx = t1x + t2x, by = t1y + t2y;
    double blen = Math.Sqrt(bx * bx + by * by);
    if (blen < 1e-12) { bx = -t1y; by = t1x; }   // anti-parallel: use perp to t1
    else              { bx /= blen; by /= blen; }

    // Perpendicular to bisector (90Â° CCW).
    double px = -by, py = bx;

    // Solve for arc-length distances s1, s2 from corner to each chamfer point.
    double det = t1x * t2y - t1y * t2x;
    // Exactly-parallel tangents → division by zero.
    if (Math.Abs(det) < 1e-12)
    {
      Log.Write("vChamfer", $"compute  exactly-parallel tangents  det={det:G4}");
      return false;
    }

    double s1 = length * (t2x * py - t2y * px) / det;
    double s2 = length * (py  * t1x - px  * t1y) / det;

    Log.Write("vChamfer", $"compute  length={length:G4}  bisector=({bx:F4},{by:F4})  det={det:G4}  s1={s1:G4}  s2={s2:G4}");

    if (s1 < 0 && s2 < 0) { s1 = -s1; s2 = -s2; }
    if (s1 <= 1e-12 || s2 <= 1e-12)
    {
      Log.Write("vChamfer", $"compute  s1/s2 non-positive after flip  s1={s1:G4}  s2={s2:G4}");
      return false;
    }

    // Walk arc-length s1/s2 from each corner endpoint.
    // When s1 > curve length, Max(0,...) clamps d to 0 → LengthParameter returns the
    // far-end parameter, which is where the chamfer line intersects the short curve.
    {
      double arcLen1 = c1.GetLength();
      double d1 = c1AtStart ? s1 : Math.Max(0.0, arcLen1 - s1);
      if (!c1.LengthParameter(d1, out tA))
        if (!c1.ClosestPoint(corner + s1 * rawT1, out tA)) return false;
      Log.Write("vChamfer", $"compute  c1  arcLen={arcLen1:G4}  d1={d1:G4}  tA={tA:G4}");
    }
    {
      double arcLen2 = c2.GetLength();
      double d2 = c2AtStart ? s2 : Math.Max(0.0, arcLen2 - s2);
      if (!c2.LengthParameter(d2, out tB))
        if (!c2.ClosestPoint(corner + s2 * rawT2, out tB)) return false;
      Log.Write("vChamfer", $"compute  c2  arcLen={arcLen2:G4}  d2={d2:G4}  tB={tB:G4}");
    }

    ptA = c1.PointAt(tA);
    ptB = c2.PointAt(tB);

    // Cut must lie strictly inside the curve (not at either endpoint).\n    const double tol = 1e-10;\n    // Corner end: zero chamfer\n    if ( c1AtStart && tA <= c1.Domain.Min + tol) { Log.Write("vChamfer", $"compute  tA at corner start  tA={tA:G6}  min={c1.Domain.Min:G6}"); return false; }\n    if (!c1AtStart && tA >= c1.Domain.Max - tol) { Log.Write("vChamfer", $"compute  tA at corner end  tA={tA:G6}  max={c1.Domain.Max:G6}");   return false; }\n    // Far end: chamfer would consume the entire curve (curves too parallel/short)\n    if ( c1AtStart && tA >= c1.Domain.Max - tol) { Log.Write("vChamfer", $"compute  tA at far end — curves too short/parallel  tA={tA:G6}  max={c1.Domain.Max:G6}"); return false; }\n    if (!c1AtStart && tA <= c1.Domain.Min + tol) { Log.Write("vChamfer", $"compute  tA at far end — curves too short/parallel  tA={tA:G6}  min={c1.Domain.Min:G6}"); return false; }\n    if ( c2AtStart && tB <= c2.Domain.Min + tol) { Log.Write("vChamfer", $"compute  tB at corner start  tB={tB:G6}  min={c2.Domain.Min:G6}"); return false; }\n    if (!c2AtStart && tB >= c2.Domain.Max - tol) { Log.Write("vChamfer", $"compute  tB at corner end  tB={tB:G6}  max={c2.Domain.Max:G6}");   return false; }\n    if ( c2AtStart && tB >= c2.Domain.Max - tol) { Log.Write("vChamfer", $"compute  tB at far end — curves too short/parallel  tB={tB:G6}  max={c2.Domain.Max:G6}"); return false; }\n    if (!c2AtStart && tB <= c2.Domain.Min + tol) { Log.Write("vChamfer", $"compute  tB at far end — curves too short/parallel  tB={tB:G6}  min={c2.Domain.Min:G6}"); return false; }

    Log.Write("vChamfer", $"compute  OK  ptA={ptA:F4}  ptB={ptB:F4}  chamferLen={ptA.DistanceTo(ptB):G4}");
    return true;
  }

  private static bool TryBuildChamferDirections(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    Plane cplane,
    out Vector3d bisDir,
    out Vector3d chamferDir)
  {
    bisDir = Vector3d.Unset;
    chamferDir = Vector3d.Unset;

    var rawT1 = c1AtStart ?  c1.TangentAtStart : -c1.TangentAtEnd;
    var rawT2 = c2AtStart ?  c2.TangentAtStart : -c2.TangentAtEnd;

    if (rawT1.IsTiny() || rawT2.IsTiny()) return false;
    rawT1.Unitize(); rawT2.Unitize();

    double t1x = Vector3d.Multiply(rawT1, cplane.XAxis);
    double t1y = Vector3d.Multiply(rawT1, cplane.YAxis);
    double t2x = Vector3d.Multiply(rawT2, cplane.XAxis);
    double t2y = Vector3d.Multiply(rawT2, cplane.YAxis);

    double len1 = Math.Sqrt(t1x * t1x + t1y * t1y);
    double len2 = Math.Sqrt(t2x * t2x + t2y * t2y);
    if (len1 < 1e-12 || len2 < 1e-12) return false;

    t1x /= len1; t1y /= len1;
    t2x /= len2; t2y /= len2;

    double bx = t1x + t2x, by = t1y + t2y;
    double blen = Math.Sqrt(bx * bx + by * by);
    if (blen < 1e-12) { bx = -t1y; by = t1x; }
    else              { bx /= blen; by /= blen; }

    double px = -by, py = bx;
    bisDir = (cplane.XAxis * bx) + (cplane.YAxis * by);
    chamferDir = (cplane.XAxis * px) + (cplane.YAxis * py);

    return bisDir.Unitize() && chamferDir.Unitize();
  }

  private static bool ComputeChamferThroughPoint(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    Point3d corner, Point3d pickedPt, Plane cplane, double tolerance,
    out Point3d ptA, out Point3d ptB,
    out double tA, out double tB)
  {
    ptA = ptB = Point3d.Unset;
    tA = tB = double.NaN;

    tolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);

    if (!TryBuildChamferDirections(c1, c1AtStart, c2, c2AtStart, cplane, out var bisDir, out var chamferDir))
      return false;

    if (Vector3d.Multiply(pickedPt - corner, bisDir) <= tolerance)
      return false;

    var span = Math.Max(corner.DistanceTo(pickedPt) + c1.GetLength() + c2.GetLength(), 1.0) * 4.0;
    span = Math.Max(span, tolerance * 100.0);
    var targetLine = new Line(pickedPt - chamferDir * span, pickedPt + chamferDir * span);

    if (!TryIntersectChamferLine(c1, c1AtStart, corner, targetLine, tolerance, out tA, out ptA))
      return false;
    if (!TryIntersectChamferLine(c2, c2AtStart, corner, targetLine, tolerance, out tB, out ptB))
      return false;

    if (ptA.DistanceTo(ptB) <= tolerance)
      return false;

    var chamferSegment = new Line(ptA, ptB);
    var closest = chamferSegment.ClosestPoint(pickedPt, true);
    return closest.DistanceTo(pickedPt) <= Math.Max(tolerance * 10.0, 1e-6);
  }

  private static bool TryIntersectChamferLine(
    Curve curve, bool atStart, Point3d corner, Line targetLine, double tolerance,
    out double t, out Point3d point)
  {
    t = double.NaN;
    point = Point3d.Unset;

    var events = Intersection.CurveLine(curve, targetLine, tolerance, tolerance);
    if (events == null || events.Count == 0)
      return false;

    var bestScore = double.MaxValue;
    for (var i = 0; i < events.Count; i++)
    {
      var candidateT = events[i].ParameterA;
      if (double.IsNaN(candidateT))
        continue;
      if (!IsInsideChamferParameter(curve, atStart, candidateT))
        continue;

      var candidate = curve.PointAt(candidateT);
      if (!candidate.IsValid)
        continue;

      var cornerDistance = candidate.DistanceTo(corner);
      if (cornerDistance <= tolerance)
        continue;

      var lineDistance = candidate.DistanceTo(targetLine.ClosestPoint(candidate, false));
      var score = cornerDistance + lineDistance * 1000.0;
      if (score >= bestScore)
        continue;

      bestScore = score;
      t = candidateT;
      point = candidate;
    }

    return point.IsValid;
  }

  private static bool IsInsideChamferParameter(Curve curve, bool atStart, double t)
  {
    const double tol = 1e-10;
    return atStart
      ? t > curve.Domain.Min + tol
      : t < curve.Domain.Max - tol;
  }

  // â”€â”€ Preview conduit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  private sealed class ChamferPreviewConduit : DisplayConduit
  {
    public Line?  ChamferLine { get; set; }
    /// <summary>Straight extension added to work1 to reach virtual corner.</summary>
    public Line?  Ext1        { get; set; }
    /// <summary>Straight extension added to work2 to reach virtual corner.</summary>
    public Line?  Ext2        { get; set; }
    /// <summary>Corner piece trimmed from work1 (corner end → chamfer point).</summary>
    public Curve? CutOff1     { get; set; }
    /// <summary>Corner piece trimmed from work2 (corner end → chamfer point).</summary>
    public Curve? CutOff2     { get; set; }
    /// <summary>Whether to draw cut-off geometry in red (Trim=Yes).</summary>
    public bool   ShowTrim    { get; set; }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      // Extensions: cyan (same as chamfer line)
      if (Ext1 is { } ext1)
        e.Display.DrawLine(ext1, Color.Cyan, 2);
      if (Ext2 is { } ext2)
        e.Display.DrawLine(ext2, Color.Cyan, 2);

      // Corner pieces removed by trim — red
      if (ShowTrim)
      {
        if (CutOff1 != null)
          e.Display.DrawCurve(CutOff1, Color.Red, 2);
        if (CutOff2 != null)
          e.Display.DrawCurve(CutOff2, Color.Red, 2);
      }

      // Chamfer line — cyan, drawn on top
      if (ChamferLine is { } line)
        e.Display.DrawLine(line, Color.Cyan, 2);
    }
  }


  // -- Conduit update helper --------------------------------------------------

  /// <summary>
  /// Recomputes all conduit preview geometry from current state.
  /// </summary>
  private static void UpdateConduit(
    ChamferPreviewConduit conduit,
    Curve crv1, Curve work1, bool c1AtStart,
    Curve crv2, Curve work2, bool c2AtStart,
    double tA, double tB, Point3d ptA, Point3d ptB)
  {
    // Cut-off curve pieces (red when Trim=Yes): original curve corner end → chamfer point.
    // Computed first so extension-line gating can reference them.
    conduit.CutOff1 = null;
    if (crv1.ClosestPoint(ptA, out var tAorig))
    {
      // Only show cut-off when ptA is on the curve body, not when it's in the extension
      // zone (where the closest point on the original curve is its corner endpoint).
      bool atEndpoint1 = c1AtStart
        ? tAorig <= crv1.Domain.Min + 1e-6
        : tAorig >= crv1.Domain.Max - 1e-6;
      if (!atEndpoint1)
        conduit.CutOff1 = c1AtStart
          ? crv1.Trim(crv1.Domain.Min, tAorig)
          : crv1.Trim(tAorig, crv1.Domain.Max);
    }

    conduit.CutOff2 = null;
    if (crv2.ClosestPoint(ptB, out var tBorig))
    {
      bool atEndpoint2 = c2AtStart
        ? tBorig <= crv2.Domain.Min + 1e-6
        : tBorig >= crv2.Domain.Max - 1e-6;
      if (!atEndpoint2)
        conduit.CutOff2 = c2AtStart
          ? crv2.Trim(crv2.Domain.Min, tBorig)
          : crv2.Trim(tBorig, crv2.Domain.Max);
    }

    // Extension lines: drawn from original corner tip toward the virtual corner,
    // clipped at the chamfer cut point when ptA lands in the extension zone.
    //   ptA in extension zone (CutOff null):  Line(crv1End, ptA)      — stops at cut, not past it
    //   ptA in original body  (CutOff non-null): Line(crv1End, work1End) — full extension; CutOff
    //     goes the opposite direction from crv1End so there is no cyan-over-red overlap.
    var crv1End  = c1AtStart ? crv1.PointAtStart : crv1.PointAtEnd;
    var work1End = c1AtStart ? work1.PointAtStart : work1.PointAtEnd;
    if (crv1End.DistanceTo(work1End) > 1e-6)
    {
      var extPt1 = conduit.CutOff1 != null ? work1End : ptA;
      conduit.Ext1 = crv1End.DistanceTo(extPt1) > 1e-6 ? new Line(crv1End, extPt1) : (Line?)null;
    }
    else conduit.Ext1 = null;

    var crv2End  = c2AtStart ? crv2.PointAtStart : crv2.PointAtEnd;
    var work2End = c2AtStart ? work2.PointAtStart : work2.PointAtEnd;
    if (crv2End.DistanceTo(work2End) > 1e-6)
    {
      var extPt2 = conduit.CutOff2 != null ? work2End : ptB;
      conduit.Ext2 = crv2End.DistanceTo(extPt2) > 1e-6 ? new Line(crv2End, extPt2) : (Line?)null;
    }
    else conduit.Ext2 = null;

    conduit.ChamferLine = ptA.DistanceTo(ptB) > 1e-10 ? new Line(ptA, ptB) : (Line?)null;
    conduit.ShowTrim    = _trim;
  }
  // â”€â”€ Command â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    var (ref1, crv1) = PickCurveWithOptions("Select first curve at corner");
    if (ref1 == null || crv1 == null) return Result.Cancel;

    var (ref2, crv2) = PickCurveWithOptions("Select second curve at corner");
    if (ref2 == null || crv2 == null) return Result.Cancel;

    if (ref1.ObjectId == ref2.ObjectId)
    {
      RhinoApp.WriteLine("vChamfer: select two different curves.");
      return Result.Failure;
    }

    var (c1AtStart, c2AtStart, corner) = FindCorner(crv1, crv2);
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    // Extend working copies to the virtual corner so chamfering always works
    // even when the curves are too short (e.g. previously chamfered corner).
    Curve work1 = ExtendToCorner(crv1, c1AtStart, corner);
    Curve work2 = ExtendToCorner(crv2, c2AtStart, corner);

    // Auto-detect the corner plane from the actual tangent directions.
    // This makes the chamfer angle correct for 3D curves not lying in the active CPlane.
    cplane = InferChamferPlane(work1, c1AtStart, work2, c2AtStart, corner, cplane);

    if (!ComputeChamfer(work1, c1AtStart, work2, c2AtStart, corner, _length, cplane,
          out var ptA, out var ptB, out var tA, out var tB))
    {
      RhinoApp.WriteLine("vChamfer: cannot compute chamfer for these curves.");
      return Result.Failure;
    }

    var conduit = new ChamferPreviewConduit();
    UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
    conduit.Enabled = true;
    doc.Views.Redraw();

    bool pointActive = false;

    try
    {
      while (true)
      {
        var get = new GetPoint();
        get.SetCommandPrompt(pointActive
          ? "Chamfer placed at point — Enter to apply"
          : "Press Enter to apply chamfer; pick a point to place at Length distance from point");
        get.AcceptNothing(true);
        get.AcceptNumber(true, true);
        var lengthOpt = new OptionDouble(_length, 0.0, double.MaxValue);
        var idxLength = get.AddOptionDouble("Length", ref lengthOpt);
        var trimOpt = new OptionToggle(_trim, "No", "Yes");
        get.AddOptionToggle("Trim", ref trimOpt);
        var joinOpt = new OptionToggle(_join, "No", "Yes");
        if (_trim) get.AddOptionToggle("Join", ref joinOpt);
        int idxClearPoint = pointActive ? get.AddOption("ClearPoint") : -1;

        var res = get.Get();

        if (res == GetResult.Cancel)
          return Result.Cancel;

        if (res == GetResult.Point)
        {
          var pickedPt = get.Point();

          if (_length <= RhinoMath.ZeroTolerance)
          {
            if (!ComputeChamferThroughPoint(work1, c1AtStart, work2, c2AtStart, corner, pickedPt, cplane,
                  doc.ModelAbsoluteTolerance, out ptA, out ptB, out tA, out tB))
            {
              RhinoApp.WriteLine("vChamfer: cannot create a chamfer through that point.");
              continue;
            }

            pointActive = true;
            UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
            doc.Views.Redraw();
            continue;
          }

          // The chamfer line is always perpendicular to the corner bisector, and its
          // bisector-distance from the corner scales linearly with cut-length.
          // Derive that scale factor from the current valid chamfer geometry, then
          // solve for the cut-length that places the chamfer at _length distance
          // perpendicular to the picked point.
          var infLine  = new Line(ptA, ptB);
          var foot     = infLine.ClosestPoint(corner, false);
          double dCur  = corner.DistanceTo(foot);
          var bisDir   = foot - corner;
          if (dCur < 1e-12 || !bisDir.Unitize())
          {
            RhinoApp.WriteLine("vChamfer: degenerate corner geometry, cannot use point-based placement.");
            continue;
          }

          double d1   = dCur / _length;                   // bisector-dist per unit of cut-length
          double dP   = (pickedPt - corner) * bisDir;     // projection of point onto bisector
          double dNew = dP - _length;                     // desired chamfer bisector-dist

          if (dNew <= 1e-12)
          {
            RhinoApp.WriteLine($"vChamfer: picked point is closer to corner than Length ({_length:0.##}); move it farther out.");
            continue;
          }

          double newCutLength = dNew / d1;

          if (!ComputeChamfer(work1, c1AtStart, work2, c2AtStart, corner, newCutLength, cplane,
                out ptA, out ptB, out tA, out tB))
          {
            RhinoApp.WriteLine("vChamfer: point-based chamfer exceeds available curve length.");
            continue;
          }

          pointActive = true;
          UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
          doc.Views.Redraw();
          continue;
        }

        if (res == GetResult.Nothing)
          break;

        if (res == GetResult.Option && idxClearPoint >= 0 && get.Option()?.Index == idxClearPoint)
        {
          pointActive = false;
          if (ComputeChamfer(work1, c1AtStart, work2, c2AtStart, corner, _length, cplane,
                out ptA, out ptB, out tA, out tB))
            UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
          doc.Views.Redraw();
          continue;
        }

        if (res == GetResult.Number)
        {
          var v = get.Number();
          if (TrySetLength(v))
          {
            pointActive = false;
            SaveOptions();
          }
        }
        else if (res == GetResult.Option)
        {
          var option = get.Option();
          _trim = trimOpt.CurrentValue;
          if (_trim) _join = joinOpt.CurrentValue;

          if (option?.Index == idxLength && TrySetLength(lengthOpt.CurrentValue))
            pointActive = false;

          SaveOptions();
        }

        if (res == GetResult.Number || res == GetResult.Option)
        {
          if (ComputeChamfer(work1, c1AtStart, work2, c2AtStart, corner, _length, cplane,
                out ptA, out ptB, out tA, out tB))
            UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
          else
          {
            conduit.ShowTrim = _trim;   // still reflect trim toggle even when invalid
            RhinoApp.WriteLine("vChamfer: length too large for this corner.");
          }

          doc.Views.Redraw();
        }
      }
    }
    finally
    {
      conduit.Enabled = false;
      doc.Views.Redraw();
    }

    // Apply.
    var hasChamferLine = ptA.DistanceTo(ptB) > doc.ModelAbsoluteTolerance;
    var chamferLineId = hasChamferLine ? doc.Objects.AddLine(ptA, ptB) : Guid.Empty;

    if (_trim)
    {
      var trimmedC1 = c1AtStart
        ? work1.Trim(tA, work1.Domain.Max)
        : work1.Trim(work1.Domain.Min, tA);

      var trimmedC2 = c2AtStart
        ? work2.Trim(tB, work2.Domain.Max)
        : work2.Trim(work2.Domain.Min, tB);

      if (trimmedC1 == null || trimmedC2 == null)
      {
        RhinoApp.WriteLine("vChamfer: curve trim failed.");
        return Result.Failure;
      }

      doc.Objects.Replace(ref1.ObjectId, trimmedC1);
      doc.Objects.Replace(ref2.ObjectId, trimmedC2);

      if (_join)
      {
        var tol = doc.ModelAbsoluteTolerance;
        var joinCurves = hasChamferLine
          ? new Curve[] { trimmedC1, new LineCurve(ptA, ptB), trimmedC2 }
          : new Curve[] { trimmedC1, trimmedC2 };
        var joined = Curve.JoinCurves(joinCurves, tol);
        if (joined != null && joined.Length == 1)
        {
          // Replace the chamfer line and both trimmed curves with the single joined result.
          if (hasChamferLine)
            doc.Objects.Delete(chamferLineId, quiet: true);
          doc.Objects.Replace(ref1.ObjectId, joined[0]);
          doc.Objects.Delete(ref2.ObjectId, quiet: true);
        }
        // If join failed (not contiguous), leave the three separate objects.
      }
    }

    SaveOptions();
    doc.Views.Redraw();
    return Result.Success;
  }
}
