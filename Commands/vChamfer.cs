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

  private static double _length = 1.0;
  private static bool   _trim   = true;   // true = trim curves; false = add line only

  public override string EnglishName => "vChamfer";

  // â”€â”€ Option persistence â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

  private static void LoadOptions() =>
    vToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (vToolsOptionStore.TryGetDouble(section, LengthKey, out var l) && l > 0)
        _length = l;
      if (vToolsOptionStore.TryGetBool(section, TrimKey, out var t))
        _trim = t;
      return 0;
    });

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
    {
      section[LengthKey] = _length;
      section[TrimKey]   = _trim;
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
      go.AddOption("Length", _length.ToString("0.##", CultureInfo.InvariantCulture));
      var trimToggle = new OptionToggle(_trim, "No", "Yes");
      go.AddOptionToggle("Trim", ref trimToggle);

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
        _trim = trimToggle.CurrentValue;
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
    public Line? ChamferLine { get; set; }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (ChamferLine is { } line)
        e.Display.DrawLine(line, Color.Cyan, 2);
    }
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

    var conduit = new ChamferPreviewConduit { ChamferLine = new Line(ptA, ptB) };
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
        var trimOpt = new OptionToggle(_trim, "No", "Yes");
        get.AddOptionToggle("Trim", ref trimOpt);

        var res = get.Get();

        if (res == GetResult.Cancel)
          return Result.Cancel;

        if (res == GetResult.Nothing)
          break;

        if (res == GetResult.Option)
        {
          _trim = trimOpt.CurrentValue;

          if (get.Option()?.EnglishName == "Length")
          {
            conduit.Enabled = false;
            doc.Views.Redraw();
            HandleLengthSubprompt();
            conduit.Enabled = true;
          }

          if (ComputeChamfer(work1, c1AtStart, work2, c2AtStart, corner, _length, cplane,
                out ptA, out ptB, out tA, out tB))
            conduit.ChamferLine = new Line(ptA, ptB);
          else
            RhinoApp.WriteLine("vChamfer: length too large for this corner.");

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
    doc.Objects.AddLine(ptA, ptB);

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
    }

    SaveOptions();
    doc.Views.Redraw();
    return Result.Success;
  }
}
