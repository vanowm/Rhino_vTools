using System;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Adds points along a destination curve by picking corresponding arc-length
/// positions on a source curve.
/// </summary>
public sealed class vPointTrace : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vPointTrace";

  /// <summary>
  /// Prompts for source and destination curves (click near the start end to
  /// orient them), then loops letting the user pick points along the source;
  /// each pick adds a point on the destination at the same arc-length distance.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // ── Pick source curve ────────────────────────────────────────────────────
    var goSource = new GetObject();
    goSource.SetCommandPrompt("Select source curve at starting end");
    goSource.GeometryFilter = ObjectType.Curve;
    goSource.SubObjectSelect = false;
    goSource.EnablePreSelect(false, true);

    if (goSource.Get() != GetResult.Object || goSource.CommandResult() != Result.Success)
      return Result.Cancel;

    var sourceCurve = goSource.Object(0).Curve();
    if (sourceCurve == null)
      return Result.Failure;

    var sourceSelPt = goSource.Object(0).SelectionPoint();
    sourceCurve = OrientFromSelectionPoint(sourceCurve, sourceSelPt);

    // ── Pick destination curve ───────────────────────────────────────────────
    var goDest = new GetObject();
    goDest.SetCommandPrompt("Select destination curve at starting end");
    goDest.GeometryFilter = ObjectType.Curve;
    goDest.SubObjectSelect = false;
    goDest.EnablePreSelect(false, true);

    if (goDest.Get() != GetResult.Object || goDest.CommandResult() != Result.Success)
      return Result.Cancel;

    var destCurve = goDest.Object(0).Curve();
    if (destCurve == null)
      return Result.Failure;

    var destSelPt = goDest.Object(0).SelectionPoint();
    destCurve = OrientFromSelectionPoint(destCurve, destSelPt);

    var destLength = destCurve.GetLength();

    // ── Pick-and-place loop ──────────────────────────────────────────────────
    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick point along source curve (Enter to finish)");
      gp.AcceptNothing(true);
      gp.Constrain(sourceCurve, allowPickingPointOffObject: false);

      Point3d? destPreviewPt = null;

      gp.DynamicDraw += (_, e) =>
      {
        var pt = MapToDestination(sourceCurve, destCurve, destLength, e.CurrentPoint);
        if (!pt.HasValue)
          return;

        destPreviewPt = pt;
        e.Display.DrawPoint(pt.Value, System.Drawing.Color.LimeGreen);
      };

      var gpResult = gp.Get();

      if (gpResult == GetResult.Nothing || gpResult == GetResult.Cancel)
        break;

      if (gpResult != GetResult.Point)
        break;

      var destPt = MapToDestination(sourceCurve, destCurve, destLength, gp.Point());
      if (!destPt.HasValue)
      {
        RhinoApp.WriteLine("vPointTrace: could not map source point to destination curve.");
        continue;
      }

      doc.Objects.AddPoint(destPt.Value);
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Returns a copy of the curve reversed if the selection point is closer to
  /// its end than its start, so the start end matches the user's click side.
  /// </summary>
  private static Curve OrientFromSelectionPoint(Curve curve, Point3d selectionPoint)
  {
    if (!selectionPoint.IsValid)
      return curve;

    var dStart = selectionPoint.DistanceToSquared(curve.PointAtStart);
    var dEnd   = selectionPoint.DistanceToSquared(curve.PointAtEnd);

    if (dEnd < dStart)
    {
      var reversed = curve.DuplicateCurve();
      reversed.Reverse();
      return reversed;
    }

    return curve;
  }

  /// <summary>
  /// Given a picked point on the source curve, computes the arc-length distance
  /// from the source start and returns the corresponding point on the destination
  /// at the same arc-length from its start.
  /// </summary>
  private static Point3d? MapToDestination(Curve source, Curve dest, double destLength, Point3d sourcePt)
  {
    if (!source.ClosestPoint(sourcePt, out var tSource))
      return null;

    double arcDist;
    try
    {
      arcDist = source.GetLength(new Interval(source.Domain.T0, tSource));
    }
    catch
    {
      return null;
    }

    // Clamp to destination length.
    arcDist = Math.Min(arcDist, destLength);

    if (!dest.LengthParameter(arcDist, out var tDest))
      return null;

    return dest.PointAt(tDest);
  }
}
