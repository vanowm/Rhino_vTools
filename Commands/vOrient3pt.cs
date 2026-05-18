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
    var objectIds = OrientCommandCommon.SelectObjectsToOrient(doc);
    if (objectIds.Count == 0)
      return Result.Cancel;

    var copyMode = OrientCommandCommon.LoadCopyOption();
    var previewSegments = new List<OrientCommandCommon.PreviewSegment>();

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Source first point", ref copyMode, out var sourceOrigin, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Target first point", ref copyMode, out var targetOrigin, traceFrom: sourceOrigin, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }
    previewSegments.Add(new OrientCommandCommon.PreviewSegment(sourceOrigin, targetOrigin));

    // Source second point is optional — Enter falls back to 1-point orient (translation)
    var src2 = OrientCommandCommon.TryGetOptionalPointWithCopyOption(
      doc, "Source second point. Press Enter for 1-point orient",
      ref copyMode, out var sourceXAxisPoint, previewSegments: previewSegments);

    if (src2 == GetResult.Cancel)
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (src2 == GetResult.Nothing)
    {
      // 1-point fallback: translation only
      var xform1pt = Transform.Translation(targetOrigin - sourceOrigin);
      var transformedIds1pt = OrientCommandCommon.TransformObjects(doc, objectIds, xform1pt, copyMode);
      if (copyMode)
        OrientCommandCommon.RecreateGroupsForCopiedObjects(doc, objectIds, transformedIds1pt);
      OrientCommandCommon.SaveCopyOption(copyMode);
      doc.Views.Redraw();
      return Result.Success;
    }

    // Target second point is optional — Enter uses source point as target
    var tgt2 = OrientCommandCommon.TryGetOptionalPointWithCopyOption(
      doc, "Target second point. Press Enter to use source point",
      ref copyMode, out var targetXAxisPoint,
      basePoint: targetOrigin, traceFrom: sourceXAxisPoint, previewSegments: previewSegments);

    if (tgt2 == GetResult.Cancel)
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (tgt2 == GetResult.Nothing)
      targetXAxisPoint = sourceXAxisPoint;
    else
      previewSegments.Add(new OrientCommandCommon.PreviewSegment(sourceXAxisPoint, targetXAxisPoint));

    // Source third point is optional — Enter falls back to 2-point orient
    var src3 = OrientCommandCommon.TryGetOptionalPointWithCopyOption(
      doc, "Source third point. Press Enter for 2-point orient",
      ref copyMode, out var sourceYAxisPoint, previewSegments: previewSegments);

    if (src3 == GetResult.Cancel)
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    Plane sourcePlane, targetPlane;

    if (src3 == GetResult.Nothing)
    {
      // 2-point fallback
      if (!OrientCommandCommon.TryBuildPlaneFromTwoPoints(doc, sourceOrigin, sourceXAxisPoint, out sourcePlane) ||
          !OrientCommandCommon.TryBuildPlaneFromTwoPoints(doc, targetOrigin, targetXAxisPoint, out targetPlane))
      {
        OrientCommandCommon.SaveCopyOption(copyMode);
        RhinoApp.WriteLine("vOrient3pt: Could not build orientation plane.");
        return Result.Failure;
      }
    }
    else
    {
      // Target third point is optional — Enter uses source point as target
      var tgt3 = OrientCommandCommon.TryGetOptionalPointWithCopyOption(
        doc, "Target third point. Press Enter to use source point",
        ref copyMode, out var targetYAxisPoint,
        basePoint: targetOrigin, traceFrom: sourceYAxisPoint, previewSegments: previewSegments);

      if (tgt3 == GetResult.Cancel)
      {
        OrientCommandCommon.SaveCopyOption(copyMode);
        return Result.Cancel;
      }

      if (tgt3 == GetResult.Nothing)
        targetYAxisPoint = sourceYAxisPoint;
      else
        previewSegments.Add(new OrientCommandCommon.PreviewSegment(sourceYAxisPoint, targetYAxisPoint));

      if (!OrientCommandCommon.TryBuildPlaneFromThreePoints(sourceOrigin, sourceXAxisPoint, sourceYAxisPoint, out sourcePlane) ||
          !OrientCommandCommon.TryBuildPlaneFromThreePoints(targetOrigin, targetXAxisPoint, targetYAxisPoint, out targetPlane))
      {
        OrientCommandCommon.SaveCopyOption(copyMode);
        RhinoApp.WriteLine("vOrient3pt: Could not build orientation plane.");
        return Result.Failure;
      }
    }

    var xform = Transform.PlaneToPlane(sourcePlane, targetPlane);
    var transformedIds = OrientCommandCommon.TransformObjects(doc, objectIds, xform, copyMode);

    if (copyMode)
      OrientCommandCommon.RecreateGroupsForCopiedObjects(doc, objectIds, transformedIds);

    OrientCommandCommon.SaveCopyOption(copyMode);
    doc.Views.Redraw();
    return Result.Success;
  }
}
