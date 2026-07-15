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

    var sourceOrigin = Point3d.Unset;
    var targetOrigin = Point3d.Unset;
    var sourceXAxisPoint = Point3d.Unset;
    var targetXAxisPoint = Point3d.Unset;
    var sourceYAxisPoint = Point3d.Unset;
    var targetYAxisPoint = Point3d.Unset;

    var hasSourceOrigin = false;
    var hasTargetOrigin = false;
    var hasSourceXAxisPoint = false;
    var hasTargetXAxisPoint = false;
    var hasSourceYAxisPoint = false;
    var hasTargetYAxisPoint = false;
    var targetXAxisWasPicked = false;
    var targetYAxisWasPicked = false;

    void RefreshPreview()
    {
      previewSegments.Clear();
      if (hasSourceOrigin && hasTargetOrigin)
        previewSegments.Add(new OrientCommon.PreviewSegment(sourceOrigin, targetOrigin));
      if (hasSourceXAxisPoint && hasTargetXAxisPoint && targetXAxisWasPicked)
        previewSegments.Add(new OrientCommon.PreviewSegment(sourceXAxisPoint, targetXAxisPoint));
      if (hasSourceYAxisPoint && hasTargetYAxisPoint && targetYAxisWasPicked)
        previewSegments.Add(new OrientCommon.PreviewSegment(sourceYAxisPoint, targetYAxisPoint));
    }

    Result Cancel()
    {
      OrientCommon.SaveCopyOption(copyMode);
      return Result.Cancel;
    }

    Result Failure(string message)
    {
      OrientCommon.SaveCopyOption(copyMode);
      RhinoApp.WriteLine(message);
      return Result.Failure;
    }

    Result Finish(Transform xform)
    {
      var transformedIds = OrientCommon.TransformObjects(doc, objectIds, xform, copyMode);
      if (copyMode)
        OrientCommon.RecreateGroupsForCopiedObjects(doc, objectIds, transformedIds);
      OrientCommon.SaveCopyOption(copyMode);
      doc.Views.Redraw();
      return Result.Success;
    }

    Result FinishTwoPoint()
    {
      if (!OrientCommon.TryBuildPlaneFromTwoPoints(doc, sourceOrigin, sourceXAxisPoint, out var sourcePlane) ||
          !OrientCommon.TryBuildPlaneFromTwoPoints(doc, targetOrigin, targetXAxisPoint, out var targetPlane))
        return Failure("vOrient3pt: Could not build orientation plane.");

      return Finish(Transform.PlaneToPlane(sourcePlane, targetPlane));
    }

    Result FinishThreePoint()
    {
      if (!OrientCommon.TryBuildPlaneFromThreePoints(sourceOrigin, sourceXAxisPoint, sourceYAxisPoint, out var sourcePlane) ||
          !OrientCommon.TryBuildPlaneFromThreePoints(targetOrigin, targetXAxisPoint, targetYAxisPoint, out var targetPlane))
        return Failure("vOrient3pt: Could not build orientation plane.");

      return Finish(Transform.PlaneToPlane(sourcePlane, targetPlane));
    }

    var step = 0;
    while (true)
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
          pickResult = OrientCommon.TryGetOptionalPointWithCopyOption(
            doc,
            "Source 2nd point. Press Enter for 1-point orient",
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
          if (pickResult == GetResult.Nothing)
            return Finish(Transform.Translation(targetOrigin - sourceOrigin));
          if (pickResult != GetResult.Point)
            return Cancel();
          hasSourceXAxisPoint = true;
          step = 3;
          break;

        case 3:
          pickResult = OrientCommon.TryGetOptionalPointWithCopyOption(
            doc,
            "Target 2nd point. Press Enter to use source point",
            ref copyMode,
            out targetXAxisPoint,
            basePoint: targetOrigin,
            traceFrom: sourceXAxisPoint,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            hasSourceXAxisPoint = false;
            sourceXAxisPoint = Point3d.Unset;
            step = 2;
            break;
          }
          if (pickResult == GetResult.Nothing)
          {
            targetXAxisPoint = sourceXAxisPoint;
            targetXAxisWasPicked = false;
          }
          else if (pickResult == GetResult.Point)
          {
            targetXAxisWasPicked = true;
          }
          else
          {
            return Cancel();
          }
          hasTargetXAxisPoint = true;
          step = 4;
          break;

        case 4:
          pickResult = OrientCommon.TryGetOptionalPointWithCopyOption(
            doc,
            "Source 3rd point. Press Enter for 2-point orient",
            ref copyMode,
            out sourceYAxisPoint,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            hasTargetXAxisPoint = false;
            targetXAxisWasPicked = false;
            targetXAxisPoint = Point3d.Unset;
            step = 3;
            break;
          }
          if (pickResult == GetResult.Nothing)
            return FinishTwoPoint();
          if (pickResult != GetResult.Point)
            return Cancel();
          hasSourceYAxisPoint = true;
          step = 5;
          break;

        default:
          pickResult = OrientCommon.TryGetOptionalPointWithCopyOption(
            doc,
            "Target 3rd point. Press Enter to use source point",
            ref copyMode,
            out targetYAxisPoint,
            basePoint: targetOrigin,
            traceFrom: sourceYAxisPoint,
            previewSegments: previewSegments,
            canUndo: true);
          if (pickResult == GetResult.Undo)
          {
            hasSourceYAxisPoint = false;
            sourceYAxisPoint = Point3d.Unset;
            step = 4;
            break;
          }
          if (pickResult == GetResult.Nothing)
          {
            targetYAxisPoint = sourceYAxisPoint;
            targetYAxisWasPicked = false;
          }
          else if (pickResult == GetResult.Point)
          {
            targetYAxisWasPicked = true;
          }
          else
          {
            return Cancel();
          }
          hasTargetYAxisPoint = true;
          return FinishThreePoint();
      }
    }
  }
}
