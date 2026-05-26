using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace vTools.Commands;

/// <summary>
/// Native three-point orient command that maps source and target construction planes.
/// </summary>
public sealed class vOrient3pt : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vOrient3pt";

  /// <summary>
  /// Executes the three-point orient workflow.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var objectIds = OrientCommon.SelectObjectsToOrient(doc);
    if (objectIds.Count == 0)
      return Result.Cancel;

    var copyMode = OrientCommon.LoadCopyOption();
    var previewSegments = new List<OrientCommon.PreviewSegment>();

    if (!OrientCommon.TryGetPointWithCopyOption(doc, "Source first point", ref copyMode, out var sourceOrigin, previewSegments: previewSegments))
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (!OrientCommon.TryGetPointWithCopyOption(doc, "Target first point", ref copyMode, out var targetOrigin, traceFrom: sourceOrigin, previewSegments: previewSegments))
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }
    previewSegments.Add(new OrientCommon.PreviewSegment(sourceOrigin, targetOrigin));

    // Source second point is optional — Enter falls back to 1-point orient (translation)
    var src2 = OrientCommon.TryGetOptionalPointWithCopyOption(
      doc, "Source second point. Press Enter for 1-point orient",
      ref copyMode, out var sourceXAxisPoint, previewSegments: previewSegments);

    if (src2 == GetResult.Cancel)
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (src2 == GetResult.Nothing)
    {
      // 1-point fallback: translation only
      var xform1pt = Transform.Translation(targetOrigin - sourceOrigin);
      var transformedIds1pt = OrientCommon.TransformObjects(doc, objectIds, xform1pt, copyMode);
      if (copyMode)
        OrientCommon.RecreateGroupsForCopiedObjects(doc, objectIds, transformedIds1pt);
      OrientCommon.SaveCopyOption(copyMode);
      doc.Views.Redraw();
      return Result.Success;
    }

    // Target second point is optional — Enter uses source point as target
    var tgt2 = OrientCommon.TryGetOptionalPointWithCopyOption(
      doc, "Target second point. Press Enter to use source point",
      ref copyMode, out var targetXAxisPoint,
      basePoint: targetOrigin, traceFrom: sourceXAxisPoint, previewSegments: previewSegments);

    if (tgt2 == GetResult.Cancel)
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (tgt2 == GetResult.Nothing)
      targetXAxisPoint = sourceXAxisPoint;
    else
      previewSegments.Add(new OrientCommon.PreviewSegment(sourceXAxisPoint, targetXAxisPoint));

    // Source third point is optional — Enter falls back to 2-point orient
    var src3 = OrientCommon.TryGetOptionalPointWithCopyOption(
      doc, "Source third point. Press Enter for 2-point orient",
      ref copyMode, out var sourceYAxisPoint, previewSegments: previewSegments);

    if (src3 == GetResult.Cancel)
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    Plane sourcePlane, targetPlane;

    if (src3 == GetResult.Nothing)
    {
      // 2-point fallback
      if (!OrientCommon.TryBuildPlaneFromTwoPoints(doc, sourceOrigin, sourceXAxisPoint, out sourcePlane) ||
          !OrientCommon.TryBuildPlaneFromTwoPoints(doc, targetOrigin, targetXAxisPoint, out targetPlane))
      {
        OrientCommon.SaveCopyOption(copyMode);
        RhinoApp.WriteLine("vOrient3pt: Could not build orientation plane.");
        return Result.Failure;
      }
    }
    else
    {
      // Target third point is optional — Enter uses source point as target
      var tgt3 = OrientCommon.TryGetOptionalPointWithCopyOption(
        doc, "Target third point. Press Enter to use source point",
        ref copyMode, out var targetYAxisPoint,
        basePoint: targetOrigin, traceFrom: sourceYAxisPoint, previewSegments: previewSegments);

      if (tgt3 == GetResult.Cancel)
      {
        OrientCommon.SaveCopyOption(copyMode);
        return Result.Cancel;
      }

      if (tgt3 == GetResult.Nothing)
        targetYAxisPoint = sourceYAxisPoint;
      else
        previewSegments.Add(new OrientCommon.PreviewSegment(sourceYAxisPoint, targetYAxisPoint));

      if (!OrientCommon.TryBuildPlaneFromThreePoints(sourceOrigin, sourceXAxisPoint, sourceYAxisPoint, out sourcePlane) ||
          !OrientCommon.TryBuildPlaneFromThreePoints(targetOrigin, targetXAxisPoint, targetYAxisPoint, out targetPlane))
      {
        OrientCommon.SaveCopyOption(copyMode);
        RhinoApp.WriteLine("vOrient3pt: Could not build orientation plane.");
        return Result.Failure;
      }
    }

    var xform = Transform.PlaneToPlane(sourcePlane, targetPlane);
    var transformedIds = OrientCommon.TransformObjects(doc, objectIds, xform, copyMode);

    if (copyMode)
      OrientCommon.RecreateGroupsForCopiedObjects(doc, objectIds, transformedIds);

    OrientCommon.SaveCopyOption(copyMode);
    doc.Views.Redraw();
    return Result.Success;
  }
}
