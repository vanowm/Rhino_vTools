using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

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

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Source second point", ref copyMode, out var sourceXAxisPoint, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Target second point", ref copyMode, out var targetXAxisPoint, basePoint: targetOrigin, traceFrom: sourceXAxisPoint, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }
    previewSegments.Add(new OrientCommandCommon.PreviewSegment(sourceXAxisPoint, targetXAxisPoint));

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Source third point", ref copyMode, out var sourceYAxisPoint, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (!OrientCommandCommon.TryGetPointWithCopyOption(doc, "Target third point", ref copyMode, out var targetYAxisPoint, basePoint: targetOrigin, traceFrom: sourceYAxisPoint, previewSegments: previewSegments))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }
    previewSegments.Add(new OrientCommandCommon.PreviewSegment(sourceYAxisPoint, targetYAxisPoint));

    if (!OrientCommandCommon.TryBuildPlaneFromThreePoints(sourceOrigin, sourceXAxisPoint, sourceYAxisPoint, out var sourcePlane) ||
        !OrientCommandCommon.TryBuildPlaneFromThreePoints(targetOrigin, targetXAxisPoint, targetYAxisPoint, out var targetPlane))
    {
      OrientCommandCommon.SaveCopyOption(copyMode);
      RhinoApp.WriteLine("vOrient3pt: Could not build orientation plane from selected points.");
      return Result.Failure;
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
