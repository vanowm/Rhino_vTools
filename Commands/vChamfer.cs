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

  // ── Formatting helpers ───────────────────────────────────────────────────
  private static string P(Point3d p)  => $"({p.X:F4},{p.Y:F4},{p.Z:F4})";
  private static string P(Point3d? p) => p.HasValue ? P(p.Value) : "null";

  public override string EnglishName => "vChamfer";

  // ── Option persistence ─────────────────────────────────────────────────────

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

  // ── Curve picking with options ─────────────────────────────────────────────

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

  // ── Corner detection ───────────────────────────────────────────────────────

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
      Log.Write("vChamfer", $"FindCorner  ep1={P(ep1)}  ep2={P(ep2)}  corner={P(vc)}");
      return (bestC1s, bestC2s, vc);
    }

    // Parallel tangents — fall back to endpoint midpoint.
    var mid = (ep1 + ep2) * 0.5;
    Log.Write("vChamfer", $"FindCorner  parallel tangents fallback  ep1={P(ep1)}  ep2={P(ep2)}  mid={P(mid)}");
    return (bestC1s, bestC2s, mid);
  }

  // ── Extension ─────────────────────────────────────────────────────────────

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

  // ── Chamfer computation ────────────────────────────────────────────────────

  /// <summary>
  /// Computes chamfer endpoints on working curves (already extended to corner).
  /// Returns false when geometry is degenerate.
  /// </summary>
  // Shoot a ray perpendicular to `tangent` in the XY plane from `pt` and find the
  // nearest forward intersection with c2. Falls back to ClosestPoint when no hit.
  private static (double Gap, double TB, Point3d PtB) NormalRayHit(
    Point3d pt, Vector3d tangent, Curve c2)
  {
    var normal = Vector3d.CrossProduct(Vector3d.ZAxis, tangent);
    if (!normal.Unitize())
      return (double.NaN, double.NaN, Point3d.Unset);

    // Orient normal toward c2.
    if (!c2.ClosestPoint(pt, out double tGuess))
      return (double.NaN, double.NaN, Point3d.Unset);
    var ptGuess = c2.PointAt(tGuess);
    if ((ptGuess - pt) * normal < 0.0) normal = -normal;

    double span = Math.Max(pt.DistanceTo(ptGuess) * 4.0, c2.GetLength() + 1.0);
    var line   = new Line(pt - normal * 1e-3, pt + normal * span);
    var events = Intersection.CurveLine(c2, line, 1e-6, 1e-6);

    if (events != null && events.Count > 0)
    {
      double bestD = double.MaxValue;
      double bestTB = tGuess;
      Point3d bestPt = ptGuess;
      for (int i = 0; i < events.Count; i++)
      {
        if (!events[i].IsPoint) continue;
        var hitPt = events[i].PointA;
        if ((hitPt - pt) * normal < -1e-6) continue;  // behind
        double d = hitPt.DistanceTo(pt);
        if (d < bestD) { bestD = d; bestTB = events[i].ParameterA; bestPt = hitPt; }
      }
      return (bestD < double.MaxValue ? bestD : pt.DistanceTo(ptGuess), bestTB, bestPt);
    }

    return (pt.DistanceTo(ptGuess), tGuess, ptGuess);  // fallback
  }

  // length = desired perpendicular gap from c1 to c2.
  // Binary-searches c1 from the corner end; ptB via normal-ray → perpendicular to middle curve.
  private static bool ComputeChamfer(
    Curve c1, bool c1AtStart,
    Curve c2,
    double targetGap,
    out Point3d ptA, out Point3d ptB,
    out double  tA,  out double  tB)
  {
    ptA = ptB = Point3d.Unset;
    tA  = tB  = double.NaN;

    if (targetGap < 0.0) return false;

    if (targetGap <= RhinoMath.ZeroTolerance)
    {
      tA  = c1AtStart ? c1.Domain.Min : c1.Domain.Max;
      ptA = c1.PointAt(tA);
      if (!c2.ClosestPoint(ptA, out tB)) return false;
      ptB = c2.PointAt(tB);
      return ptA.IsValid && ptB.IsValid;
    }

    // Verify target gap is reachable at the far end of c1.
    double tFar = c1AtStart ? c1.Domain.Max : c1.Domain.Min;
    var ptFar   = c1.PointAt(tFar);
    var tanFar  = c1.TangentAt(tFar);
    var (gapAtFar, _, _) = NormalRayHit(ptFar, tanFar, c2);
    if (double.IsNaN(gapAtFar) || gapAtFar < targetGap)
    {
      Log.Write("vChamfer", $"ComputeChamfer  targetGap={targetGap:G4} > maxGap={gapAtFar:G4}");
      return false;
    }

    // Binary search on arc-length s from corner end: find s where NormalRayGap = targetGap.
    double len1 = c1.GetLength();
    double lo = 0.0, hi = len1;
    for (int i = 0; i < 48; i++)
    {
      double s   = 0.5 * (lo + hi);
      double seg = c1AtStart ? s : (len1 - s);
      if (!c1.LengthParameter(seg, out double tMid)) break;
      var ptMid  = c1.PointAt(tMid);
      var tanMid = c1.TangentAt(tMid);
      var (gap, _, _) = NormalRayHit(ptMid, tanMid, c2);
      if (double.IsNaN(gap)) { lo = s; continue; }
      if (gap < targetGap) lo = s; else hi = s;
      if (hi - lo < 1e-9) break;
    }

    double sA   = 0.5 * (lo + hi);
    double segA = c1AtStart ? sA : (len1 - sA);
    if (!c1.LengthParameter(segA, out tA)) return false;
    ptA = c1.PointAt(tA);
    if (!ptA.IsValid) return false;

    var tanA = c1.TangentAt(tA);
    var (finalGap, tBfinal, ptBfinal) = NormalRayHit(ptA, tanA, c2);
    if (double.IsNaN(tBfinal) || !ptBfinal.IsValid) return false;
    tB  = tBfinal;
    ptB = ptBfinal;

    Log.Write("vChamfer", $"ComputeChamfer  OK  gap={finalGap:G4}  ptA={P(ptA)}  ptB={P(ptB)}");
    return true;
  }


  // ── Preview conduit ────────────────────────────────────────────────────────

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
  // ── Command ────────────────────────────────────────────────────────────────

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

    var click1 = ref1.SelectionPoint();
    var click2 = ref2.SelectionPoint();
    Log.Write("vChamfer", $"RunCommand  click1={P(click1.IsValid ? (Point3d?)click1 : null)}  click2={P(click2.IsValid ? (Point3d?)click2 : null)}");
    var (c1AtStart, c2AtStart, corner) = FindCorner(crv1, crv2);
    Log.Write("vChamfer", $"RunCommand  corner={P(corner)}  c1AtStart={c1AtStart}  c2AtStart={c2AtStart}");

    // Extend working copies to the virtual corner so chamfering always works
    // even when the curves are too short (e.g. previously chamfered corner).
    Curve work1 = ExtendToCorner(crv1, c1AtStart, corner);
    Curve work2 = ExtendToCorner(crv2, c2AtStart, corner);

    // Initial length: use stored _length. Click positions are only used for corner detection above.
    double runLength = _length;
    Log.Write("vChamfer", $"RunCommand  runLength={runLength:G4}");

    if (!ComputeChamfer(work1, c1AtStart, work2, runLength,
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
        var lengthOpt = new OptionDouble(runLength, 0.0, double.MaxValue);
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

          // Project click onto work1; offset _length arc-length toward the corner.
          // The click is the outer reference — chamfer is placed _length back from it.
          if (!work1.ClosestPoint(pickedPt, out double tPick))
          {
            RhinoApp.WriteLine("vChamfer: cannot project point onto curve.");
            continue;
          }

          double len1        = work1.GetLength();
          double arcToClick  = c1AtStart
            ? work1.GetLength(new Interval(work1.Domain.Min, tPick))
            : work1.GetLength(new Interval(tPick, work1.Domain.Max));
          double arcToChamfer = arcToClick - _length;

          if (arcToChamfer < 0.0)
          {
            RhinoApp.WriteLine($"vChamfer: picked point is closer to corner than Length ({_length:0.##}).");
            continue;
          }

          double segA = c1AtStart ? arcToChamfer : (len1 - arcToChamfer);
          if (!work1.LengthParameter(segA, out double tANew))
          {
            RhinoApp.WriteLine("vChamfer: cannot compute offset position.");
            continue;
          }

          var ptANew  = work1.PointAt(tANew);
          var tanANew = work1.TangentAt(tANew);
          var (newGap, tBNew, ptBNew) = NormalRayHit(ptANew, tanANew, work2);

          if (double.IsNaN(newGap) || !ptBNew.IsValid)
          {
            RhinoApp.WriteLine("vChamfer: cannot find chamfer on second curve.");
            continue;
          }

          tA = tANew; ptA = ptANew;
          tB = tBNew; ptB = ptBNew;
          runLength = newGap;
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
          if (ComputeChamfer(work1, c1AtStart, work2, runLength,
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
            runLength = _length;
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
          {
            runLength = _length;
            pointActive = false;
          }

          SaveOptions();
        }

        if (res == GetResult.Number || res == GetResult.Option)
        {
          if (ComputeChamfer(work1, c1AtStart, work2, runLength,
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
