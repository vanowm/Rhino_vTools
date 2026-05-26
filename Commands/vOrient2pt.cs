using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Native two-point orient command that maps source and target axes using planes.
/// </summary>
public sealed class vOrient2pt : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vOrient2pt";

  /// <summary>
  /// Executes the two-point orient workflow.
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

    if (!OrientCommon.TryGetPointWithCopyOption(doc, "Source second point", ref copyMode, out var sourceXAxisPoint, previewSegments: previewSegments))
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    if (!OrientCommon.TryGetPointWithCopyOption(doc, "Target second point", ref copyMode, out var targetXAxisPoint, basePoint: targetOrigin, traceFrom: sourceXAxisPoint, previewSegments: previewSegments))
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }
    previewSegments.Add(new OrientCommon.PreviewSegment(sourceXAxisPoint, targetXAxisPoint));

    if (!OrientCommon.TryBuildPlaneFromTwoPoints(doc, sourceOrigin, sourceXAxisPoint, out var sourcePlane) ||
        !OrientCommon.TryBuildPlaneFromTwoPoints(doc, targetOrigin, targetXAxisPoint, out var targetPlane))
    {
      OrientCommon.SaveCopyOption(copyMode);
      RhinoApp.WriteLine("vOrient2pt: Could not build orientation plane from selected points.");
      return Result.Failure;
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
