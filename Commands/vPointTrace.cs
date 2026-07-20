using System;
using System.Collections.Generic;
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
    var highlightedIds = new HashSet<Guid>();
    int addedPointCount = 0;
    vTools.Log.Write("vPointTrace", $"start document={doc.RuntimeSerialNumber}");

    // ── Pick source curve ────────────────────────────────────────────────────
    var goSource = new GetObject();
    goSource.EnableTransparentCommands(true);
    goSource.SetCommandPrompt("Select source curve at starting end");
    goSource.GeometryFilter = ObjectType.Curve;
    goSource.SubObjectSelect = false;
    goSource.EnablePreSelect(false, true);

    if (goSource.Get() != GetResult.Object || goSource.CommandResult() != Result.Success)
      return Result.Cancel;

    var sourceRef = goSource.Object(0);
    var sourceCurve = sourceRef.Curve();
    if (sourceCurve == null)
      return Result.Failure;

    HighlightPickedObject(doc, sourceRef, highlightedIds);

    var sourceSelPt = sourceRef.SelectionPoint();
    sourceCurve = OrientFromSelectionPoint(sourceCurve, sourceSelPt);
    vTools.Log.Write("vPointTrace",
      $"source={sourceRef.ObjectId} length={sourceCurve.GetLength():0.###}");

    // ── Pick destination curve ───────────────────────────────────────────────
    var goDest = new GetObject();
    goDest.EnableTransparentCommands(true);
    goDest.SetCommandPrompt("Select destination curve at starting end");
    goDest.GeometryFilter = ObjectType.Curve;
    goDest.SubObjectSelect = false;
    goDest.EnablePreSelect(false, true);

    if (goDest.Get() != GetResult.Object || goDest.CommandResult() != Result.Success)
    {
      HighlightObjects(doc, highlightedIds, false);
      return Result.Cancel;
    }

    var destRef = goDest.Object(0);
    var destCurve = destRef.Curve();
    if (destCurve == null)
    {
      HighlightObjects(doc, highlightedIds, false);
      return Result.Failure;
    }

    HighlightPickedObject(doc, destRef, highlightedIds);

    // Inherit the destination curve's group so added points join it.
    var destGroupList = destRef.Object()?.Attributes.GetGroupList() ?? Array.Empty<int>();

    var destSelPt = destRef.SelectionPoint();
    destCurve = OrientFromSelectionPoint(destCurve, destSelPt);

    var destLength = destCurve.GetLength();
    vTools.Log.Write("vPointTrace",
      $"destination={destRef.ObjectId} length={destLength:0.###} " +
      $"groups=[{string.Join(",", destGroupList)}]");

    // ── Pick-and-place loop ──────────────────────────────────────────────────
    while (true)
    {
      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt("Pick point along source curve (Enter to finish)");
      gp.AcceptNothing(true);
      gp.Constrain(sourceCurve, allowPickingPointOffObject: false);

      gp.DynamicDraw += (_, e) =>
      {
        var pt = MapToDestination(sourceCurve, destCurve, destLength, e.CurrentPoint);
        if (!pt.HasValue)
          return;

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
        vTools.Log.Write("vPointTrace", $"map failed sourcePoint={FormatPoint(gp.Point())}");
        continue;
      }

      var pointId = AddTracePoint(doc, destPt.Value, destGroupList);
      if (pointId == Guid.Empty)
      {
        RhinoApp.WriteLine("vPointTrace: failed to add point to the document.");
        vTools.Log.Write("vPointTrace",
          $"add failed destinationPoint={FormatPoint(destPt.Value)} " +
          $"currentLayer={doc.Layers.CurrentLayerIndex}");
        continue;
      }

      addedPointCount++;
      vTools.Log.Write("vPointTrace",
        $"added point={pointId} location={FormatPoint(destPt.Value)} count={addedPointCount}");
      doc.Views.Redraw();
    }

    HighlightObjects(doc, highlightedIds, false);
    vTools.Log.Write("vPointTrace", $"end added={addedPointCount}");
    return Result.Success;
  }

  private static Guid AddTracePoint(RhinoDoc doc, Point3d point, IReadOnlyList<int> groupIndices)
  {
    try
    {
      if (groupIndices.Count == 0)
        return doc.Objects.AddPoint(point);

      var attrs = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };
      foreach (int groupIndex in groupIndices)
        attrs.AddToGroup(groupIndex);
      return doc.Objects.AddPoint(point, attrs);
    }
    catch (Exception ex)
    {
      vTools.Log.Write("vPointTrace", $"AddPoint exception: {ex}");
      return Guid.Empty;
    }
  }

  private static string FormatPoint(Point3d point) =>
    $"({point.X:0.###},{point.Y:0.###},{point.Z:0.###})";

  private static void HighlightPickedObject(RhinoDoc doc, ObjRef objRef, ISet<Guid> highlightedIds)
  {
    var obj = objRef.Object();
    if (obj == null)
      return;

    obj.Select(false);
    obj.Highlight(true);
    highlightedIds.Add(obj.Id);
    doc.Views.Redraw();
  }

  private static void HighlightObjects(RhinoDoc doc, IEnumerable<Guid> ids, bool state)
  {
    foreach (var id in ids)
      doc.Objects.FindId(id)?.Highlight(state);

    doc.Views.Redraw();
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
