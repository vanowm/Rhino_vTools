using System;
using System.Drawing;
using System.Globalization;
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
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetDouble(section, LengthKey, out var l) && l > 0)
        _length = l;
      if (vToolsOptionStore.TryGetBool(section, TrimKey, out var t))
        _trim = t;
      if (vToolsOptionStore.TryGetBool(section, JoinKey, out var j))
        _join = j;
      return 0;
    });

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
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
      go.AcceptNumber(true, false);
      go.AddOption("Length", _length.ToString("0.##", CultureInfo.InvariantCulture));
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
        if (v > 0) { _length = v; SaveOptions(); }
        continue;
      }

      if (res == GetResult.Option)
      {
        _trim = trimToggle.CurrentValue;
        if (_trim) _join = joinTogglePick.CurrentValue;
        SaveOptions();
        if (go.Option()?.EnglishName == "Length")
          HandleLengthSubprompt();
      }
    }
  }

  private static void HandleLengthSubprompt()
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"Chamfer cut length ({_length:0.##})");
    gs.AcceptNothing(true);
    if (gs.Get() == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
      {
        _length = v;
        SaveOptions();
      }
    }
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
      return (bestC1s, bestC2s, (lineA.PointAt(a) + lineB.PointAt(b)) * 0.5);

    // Parallel tangents â€” fall back to endpoint midpoint.
    return (bestC1s, bestC2s, (ep1 + ep2) * 0.5);
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
  private static bool ComputeChamfer(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    Point3d corner, double length, Plane cplane,
    out Point3d ptA, out Point3d ptB,
    out double  tA,  out double  tB)
  {
    ptA = ptB = Point3d.Unset;
    tA  = tB  = double.NaN;

    // Tangents pointing AWAY from the corner along each curve.
    var rawT1 = c1AtStart ?  c1.TangentAtStart : -c1.TangentAtEnd;
    var rawT2 = c2AtStart ?  c2.TangentAtStart : -c2.TangentAtEnd;

    if (rawT1.IsTiny() || rawT2.IsTiny()) return false;
    rawT1.Unitize(); rawT2.Unitize();

    // Project tangents onto the active CPlane (drop out-of-plane component).
    double t1x = rawT1 * cplane.XAxis, t1y = rawT1 * cplane.YAxis;
    double t2x = rawT2 * cplane.XAxis, t2y = rawT2 * cplane.YAxis;

    double len1 = Math.Sqrt(t1x * t1x + t1y * t1y);
    double len2 = Math.Sqrt(t2x * t2x + t2y * t2y);
    if (len1 < 1e-12 || len2 < 1e-12) return false;

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
    if (Math.Abs(det) < 1e-12) return false;

    double s1 = length * (t2x * py - t2y * px) / det;
    double s2 = length * (py  * t1x - px  * t1y) / det;

    if (s1 < 0 && s2 < 0) { s1 = -s1; s2 = -s2; }
    if (s1 <= 1e-12 || s2 <= 1e-12) return false;

    // Project approximate tangent-line points onto the actual curves.
    if (!c1.ClosestPoint(corner + s1 * rawT1, out tA)) return false;
    if (!c2.ClosestPoint(corner + s2 * rawT2, out tB)) return false;

    ptA = c1.PointAt(tA);
    ptB = c2.PointAt(tB);

    // Cut must lie strictly inside the curve (not at the corner endpoint).
    const double tol = 1e-10;
    if ( c1AtStart && tA <= c1.Domain.Min + tol) return false;
    if (!c1AtStart && tA >= c1.Domain.Max - tol) return false;
    if ( c2AtStart && tB <= c2.Domain.Min + tol) return false;
    if (!c2AtStart && tB >= c2.Domain.Max - tol) return false;

    return true;
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
      conduit.CutOff1 = c1AtStart
        ? crv1.Trim(crv1.Domain.Min, tAorig)
        : crv1.Trim(tAorig, crv1.Domain.Max);

    conduit.CutOff2 = null;
    if (crv2.ClosestPoint(ptB, out var tBorig))
      conduit.CutOff2 = c2AtStart
        ? crv2.Trim(crv2.Domain.Min, tBorig)
        : crv2.Trim(tBorig, crv2.Domain.Max);

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

    conduit.ChamferLine = new Line(ptA, ptB);
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
        get.AcceptNumber(true, false);
        get.AddOption("Length", _length.ToString("0.##", CultureInfo.InvariantCulture));
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
          if (v > 0) { _length = v; pointActive = false; SaveOptions(); }
        }
        else if (res == GetResult.Option)
        {
          _trim = trimOpt.CurrentValue;
          if (_trim) _join = joinOpt.CurrentValue;
          SaveOptions();

          if (get.Option()?.EnglishName == "Length")
          {
            conduit.Enabled = false;
            doc.Views.Redraw();
            HandleLengthSubprompt();
            conduit.Enabled = true;
            pointActive = false;
          }
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
    var chamferLineId = doc.Objects.AddLine(ptA, ptB);

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
        var chamferCrv = new LineCurve(ptA, ptB);
        var joined = Curve.JoinCurves(new Curve[] { trimmedC1, chamferCrv, trimmedC2 }, tol);
        if (joined != null && joined.Length == 1)
        {
          // Replace the chamfer line and both trimmed curves with the single joined result.
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
