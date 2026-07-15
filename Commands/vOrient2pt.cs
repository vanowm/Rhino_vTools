using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

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

    var sourceOrigin = Point3d.Unset;
    var targetOrigin = Point3d.Unset;
    var sourceXAxisPoint = Point3d.Unset;
    var targetXAxisPoint = Point3d.Unset;
    var hasSourceOrigin = false;
    var hasTargetOrigin = false;
    var step = 0;

    void RefreshPreview()
    {
      previewSegments.Clear();
      if (hasSourceOrigin && hasTargetOrigin)
        previewSegments.Add(new OrientCommon.PreviewSegment(sourceOrigin, targetOrigin));
    }

    Result Cancel()
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    while (step < 4)
    {
      RefreshPreview();
      GetResult pickResult;

      switch (step)
      {
        case 0:
          pickResult = OrientCommon.GetPointWithCopyOption(
            doc,
            "Source 1st point",
            ref copyMode,
            out sourceOrigin,
            previewSegments: previewSegments);
          if (pickResult != GetResult.Point)
            return Cancel();
          hasSourceOrigin = true;
          step = 1;
          break;

        case 1:
          pickResult = OrientCommon.GetPointWithCopyOption(
            doc,
            "Target 1st point",
            ref copyMode,
            out targetOrigin,
            traceFrom: sourceOrigin,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            hasSourceOrigin = false;
            sourceOrigin = Point3d.Unset;
            step = 0;
            break;
          }
          if (pickResult != GetResult.Point)
            return Cancel();
          hasTargetOrigin = true;
          step = 2;
          break;

        case 2:
          pickResult = OrientCommon.GetPointWithCopyOption(
            doc,
            "Source 2nd point",
            ref copyMode,
            out sourceXAxisPoint,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            hasTargetOrigin = false;
            targetOrigin = Point3d.Unset;
            step = 1;
            break;
          }
          if (pickResult != GetResult.Point)
            return Cancel();
          step = 3;
          break;

        default:
          pickResult = OrientCommon.GetPointWithCopyOption(
            doc,
            "Target 2nd point",
            ref copyMode,
            out targetXAxisPoint,
            basePoint: targetOrigin,
            traceFrom: sourceXAxisPoint,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            sourceXAxisPoint = Point3d.Unset;
            step = 2;
            break;
          }
          if (pickResult != GetResult.Point)
            return Cancel();
          step = 4;
          break;
      }
    }

    previewSegments.Clear();

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
