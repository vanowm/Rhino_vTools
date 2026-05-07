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
/// Cuts a corner formed by two curves with a straight line perpendicular to the
/// angle bisector at a specified cut length. The virtual corner is the intersection
/// of the tangent extensions from each curve's nearest endpoint — works even when
/// the curves were previously chamfered and no longer share a common endpoint.
/// Both curves are trimmed to the cut endpoints and a new line is added.
/// Optionally adds a closed offcut polyline representing the removed corner region.
///
/// Workflow:
///   Pick curve 1 — near the corner to chamfer.
///   Pick curve 2 — near the same corner.
///   Length and Offcut options are available at every prompt.
///   Press Enter to apply.
/// </summary>
public sealed class vChamfer : Command
{
  private const string SectionName = "vChamfer";
  private const string LengthKey   = "length";
  private const string OffcutKey   = "offcut";

  private static double _length = 1.0;
  private static bool   _offcut;

  public override string EnglishName => "vChamfer";

  // ── Option persistence ─────────────────────────────────────────────────────

  private static void LoadOptions() =>
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetDouble(section, LengthKey, out var l) && l > 0)
        _length = l;
      if (vToolsOptionStore.TryGetBool(section, OffcutKey, out var o))
        _offcut = o;
      return 0;
    });

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
    {
      section[LengthKey] = _length;
      section[OffcutKey] = _offcut;
    });

  // ── Curve picking with options ─────────────────────────────────────────────

  /// <summary>
  /// Picks a curve; Length and Offcut options are available during the pick.
  /// </summary>
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
      go.AddOption("Length", _length.ToString("0.##", CultureInfo.InvariantCulture));
      var offcutToggle = new OptionToggle(_offcut, "No", "Yes");
      go.AddOptionToggle("Offcut", ref offcutToggle);

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

      if (res == GetResult.Option)
      {
        _offcut = offcutToggle.CurrentValue;
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
        _length = v;
    }
  }

  // ── Corner detection ───────────────────────────────────────────────────────

  /// <summary>
  /// Finds the closest endpoint pair and computes a virtual corner as the
  /// intersection of the tangent extensions — correct even when curves do not
  /// share an endpoint (e.g. previously chamfered).
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

    // Tangents pointing TOWARD the virtual corner (extension past endpoint).
    var t1 = bestC1s ? -c1.TangentAtStart : c1.TangentAtEnd;
    var t2 = bestC2s ? -c2.TangentAtStart : c2.TangentAtEnd;
    t1.Unitize(); t2.Unitize();

    var lineA = new Line(ep1, ep1 + t1 * 1e4);
    var lineB = new Line(ep2, ep2 + t2 * 1e4);

    if (Intersection.LineLine(lineA, lineB, out double a, out double b, 1e-6, false))
      return (bestC1s, bestC2s, (lineA.PointAt(a) + lineB.PointAt(b)) * 0.5);

    // Parallel tangents — fall back to endpoint midpoint.
    return (bestC1s, bestC2s, (ep1 + ep2) * 0.5);
  }

  // ── Chamfer computation ────────────────────────────────────────────────────

  /// <summary>
  /// Computes the chamfer endpoints on each curve for the given cut length.
  /// Returns false when the geometry is degenerate or the length is too large.
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
    if (blen < 1e-12)
    {
      // Anti-parallel tangents: perp to t1 in CPlane.
      bx = -t1y; by = t1x;
    }
    else
    {
      bx /= blen; by /= blen;
    }

    // Perpendicular to bisector in CPlane 2-D (90° CCW rotation).
    double px = -by, py = bx;

    // Solve: s2*t2 - s1*t1 = L*(px, py)
    //   [t2x  -t1x] [s2]   [L*px]
    //   [t2y  -t1y] [s1] = [L*py]
    double det = t1x * t2y - t1y * t2x;
    if (Math.Abs(det) < 1e-12) return false;

    double s1 = length * (t2x * py - t2y * px) / det;
    double s2 = length * (py  * t1x - px  * t1y) / det;

    // If both negative, the perp was pointing the wrong way — flip.
    if (s1 < 0 && s2 < 0) { s1 = -s1; s2 = -s2; }
    if (s1 <= 1e-12 || s2 <= 1e-12) return false;

    // Snap approximate tangent-line point to the actual curve.
    var approxA = corner + s1 * rawT1;
    var approxB = corner + s2 * rawT2;

    if (!c1.ClosestPoint(approxA, out tA)) return false;
    if (!c2.ClosestPoint(approxB, out tB)) return false;

    ptA = c1.PointAt(tA);
    ptB = c2.PointAt(tB);

    // Cut parameter must lie inside the curve, not at (or beyond) the corner.
    const double tol = 1e-10;
    if ( c1AtStart && tA <= c1.Domain.Min + tol) return false;
    if (!c1AtStart && tA >= c1.Domain.Max - tol) return false;
    if ( c2AtStart && tB <= c2.Domain.Min + tol) return false;
    if (!c2AtStart && tB >= c2.Domain.Max - tol) return false;

    return true;
  }

  // ── Preview conduit ────────────────────────────────────────────────────────

  private sealed class ChamferPreviewConduit : DisplayConduit
  {
    public Line?     ChamferLine  { get; set; }
    public Polyline? OffcutPoly   { get; set; }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (ChamferLine is { } line)
        e.Display.DrawLine(line, Color.Cyan, 2);
      if (OffcutPoly is { } poly)
        e.Display.DrawPolyline(poly, Color.FromArgb(255, 160, 0), 1);
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  private static void UpdateOffcutPreview(
    ChamferPreviewConduit conduit,
    Point3d ptA, Point3d ptB,
    Point3d c1CornerPt, Point3d c2CornerPt)
  {
    if (!_offcut)
    {
      conduit.OffcutPoly = null;
      return;
    }

    var pts = c1CornerPt.DistanceTo(c2CornerPt) < 1e-6
      ? new[] { ptA, c1CornerPt, ptB, ptA }
      : new[] { ptA, c1CornerPt, c2CornerPt, ptB, ptA };
    conduit.OffcutPoly = new Polyline(pts);
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

    var (c1AtStart, c2AtStart, corner) = FindCorner(crv1, crv2);
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    // Corner endpoints on original curves (for offcut polyline).
    var c1CornerPt = c1AtStart ? crv1.PointAtStart : crv1.PointAtEnd;
    var c2CornerPt = c2AtStart ? crv2.PointAtStart : crv2.PointAtEnd;

    if (!ComputeChamfer(crv1, c1AtStart, crv2, c2AtStart, corner, _length, cplane,
          out var ptA, out var ptB, out var tA, out var tB))
    {
      RhinoApp.WriteLine("vChamfer: cannot compute chamfer for these curves.");
      return Result.Failure;
    }

    // Interactive preview loop.
    var conduit = new ChamferPreviewConduit { ChamferLine = new Line(ptA, ptB) };
    UpdateOffcutPreview(conduit, ptA, ptB, c1CornerPt, c2CornerPt);
    conduit.Enabled = true;
    doc.Views.Redraw();

    try
    {
      while (true)
      {
        var get = new GetOption();
        get.SetCommandPrompt("Press Enter to apply chamfer");
        get.AcceptNothing(true);
        get.AddOption("Length", _length.ToString("0.##", CultureInfo.InvariantCulture));
        var offcutOpt = new OptionToggle(_offcut, "No", "Yes");
        get.AddOptionToggle("Offcut", ref offcutOpt);

        var res = get.Get();

        if (res == GetResult.Cancel)
          return Result.Cancel;

        if (res == GetResult.Nothing)
          break; // Enter → apply

        if (res == GetResult.Option)
        {
          var opt = get.Option();
          _offcut = offcutOpt.CurrentValue;

          if (opt?.EnglishName == "Length")
          {
            conduit.Enabled = false;
            doc.Views.Redraw();
            HandleLengthSubprompt();
            conduit.Enabled = true;
          }

          if (ComputeChamfer(crv1, c1AtStart, crv2, c2AtStart, corner, _length, cplane,
                out ptA, out ptB, out tA, out tB))
          {
            conduit.ChamferLine = new Line(ptA, ptB);
            UpdateOffcutPreview(conduit, ptA, ptB, c1CornerPt, c2CornerPt);
          }
          else
          {
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

    // Apply: trim both curves, add chamfer line.
    var trimmedC1 = c1AtStart
      ? crv1.Trim(tA, crv1.Domain.Max)
      : crv1.Trim(crv1.Domain.Min, tA);

    var trimmedC2 = c2AtStart
      ? crv2.Trim(tB, crv2.Domain.Max)
      : crv2.Trim(crv2.Domain.Min, tB);

    if (trimmedC1 == null || trimmedC2 == null)
    {
      RhinoApp.WriteLine("vChamfer: curve trim failed.");
      return Result.Failure;
    }

    doc.Objects.Replace(ref1.ObjectId, trimmedC1);
    doc.Objects.Replace(ref2.ObjectId, trimmedC2);
    doc.Objects.AddLine(ptA, ptB);

    // Offcut: closed polyline of the corner piece that was removed.
    if (_offcut)
    {
      var pts = c1CornerPt.DistanceTo(c2CornerPt) < doc.ModelAbsoluteTolerance
        ? new[] { ptA, c1CornerPt, ptB, ptA }
        : new[] { ptA, c1CornerPt, c2CornerPt, ptB, ptA };
      doc.Objects.AddPolyline(pts);
    }

    SaveOptions();
    doc.Views.Redraw();
    return Result.Success;
  }
}
