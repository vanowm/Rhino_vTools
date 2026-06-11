using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Places points on a selected surface normal from picked points in space.
/// </summary>
public sealed class vPointNormalToSurface : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vPointNormalToSurface";

  /// <summary>
  /// Runs the PointNormalToSurface workflow.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return PointNormalToSurfaceWorkflow.Run(doc);
  }
}

/// <summary>
/// Shared PointNormalToSurface workflow implementation.
/// </summary>
internal static class PointNormalToSurfaceWorkflow
{
  private const string CommandName = "PointNormalToSurface";

  private sealed class PointRecord
  {
    public PointRecord(Guid id, Point3d point)
    {
      Id = id;
      Point = point;
    }

    public Guid Id { get; set; }
    public Point3d Point { get; }
  }

  internal static Result Run(RhinoDoc doc)
  {
    if (!TryPickSurfaceLike(doc, out var targetBrep) || targetBrep == null)
      return Result.Cancel;

    return PickPointsLoop(doc, targetBrep);
  }

  private static Result PickPointsLoop(RhinoDoc doc, Brep targetBrep)
  {
    var previewLineColor = Color.FromArgb(110, 180, 180, 180);
    var previewPointColor = Color.FromArgb(210, 225, 235, 245);

    var undoStack = new Stack<PointRecord>();
    var redoStack = new Stack<PointRecord>();

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick point in space (Enter to finish)");
      gp.AcceptNothing(true);
      gp.AcceptString(true);

      EventHandler<GetPointDrawEventArgs> drawPreview = (_, drawEvent) =>
      {
        if (!TryBrepPointAndNormal(targetBrep, drawEvent.CurrentPoint, out var onSurface, out var previewNormal))
          return;

        _ = previewNormal;
        drawEvent.Display.DrawLine(drawEvent.CurrentPoint, onSurface, previewLineColor, 1);
        drawEvent.Display.DrawPoint(onSurface, Rhino.Display.PointStyle.ActivePoint, 3, previewPointColor);
      };

      gp.DynamicDraw += drawPreview;
      GetResult result;
      try
      {
        result = gp.Get();
      }
      finally
      {
        gp.DynamicDraw -= drawPreview;
      }

      if (gp.CommandResult() != Result.Success)
        return gp.CommandResult();

      if (result == GetResult.Nothing)
        return Result.Success;

      if (result == GetResult.String)
      {
        var keyword = NormalizeHiddenKeyword(gp.StringResult());
        if (keyword is "u" or "undo")
        {
          if (undoStack.Count == 0)
          {
            RhinoApp.WriteLine($"{CommandName}: nothing to undo.");
            continue;
          }

          var record = undoStack.Pop();
          if (ApplyLocalUndo(doc, record))
          {
            redoStack.Push(record);
            doc.Views.Redraw();
          }
          else
          {
            RhinoApp.WriteLine($"{CommandName}: undo failed.");
          }

          continue;
        }

        if (keyword is "r" or "redo")
        {
          if (redoStack.Count == 0)
          {
            RhinoApp.WriteLine($"{CommandName}: nothing to redo.");
            continue;
          }

          var record = redoStack.Pop();
          if (ApplyLocalRedo(doc, record))
          {
            undoStack.Push(record);
            doc.Views.Redraw();
          }
          else
          {
            RhinoApp.WriteLine($"{CommandName}: redo failed.");
          }

          continue;
        }

        RhinoApp.WriteLine($"{CommandName}: hidden keywords are 'u'/'undo' and 'r'/'redo'.");
        continue;
      }

      if (result != GetResult.Point)
        return Result.Success;

      if (!TryBrepPointAndNormal(targetBrep, gp.Point(), out var onSurface, out var normal))
      {
        RhinoApp.WriteLine("Could not evaluate surface normal at picked location.");
        continue;
      }

      _ = normal;

      var pointId = doc.Objects.AddPoint(onSurface);
      if (pointId == Guid.Empty)
      {
        RhinoApp.WriteLine("Failed to add normal point.");
        continue;
      }

      undoStack.Push(new PointRecord(pointId, onSurface));
      redoStack.Clear();
      doc.Views.Redraw();
    }
  }

  private static bool TryPickSurfaceLike(RhinoDoc doc, out Brep? targetBrep)
  {
    targetBrep = null;

    var go = new GetObject();
    go.SetCommandPrompt("Select target surface or polysurface face");
    go.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter;
    go.SubObjectSelect = true;
    go.EnablePreSelect(true, true);

    var result = go.Get();
    if (result != GetResult.Object || go.ObjectCount < 1)
      return false;

    var objRef = go.Object(0);
    if (objRef == null)
      return false;

    var face = objRef.Face();
    if (face != null)
    {
      targetBrep = face.DuplicateFace(false);
      return targetBrep != null;
    }

    var brep = objRef.Brep();
    if (brep != null)
    {
      var pickPoint = objRef.SelectionPoint();
      var hasPickPoint = pickPoint.IsValid;
      var closestFace = FindClosestFaceOnBrep(brep, hasPickPoint ? pickPoint : Point3d.Unset, hasPickPoint);

      if (closestFace != null)
      {
        targetBrep = closestFace.DuplicateFace(false);
        return targetBrep != null;
      }

      targetBrep = brep.DuplicateBrep();
      return targetBrep != null;
    }

    var pickedSurface = objRef.Surface();
    if (pickedSurface != null)
    {
      targetBrep = pickedSurface.ToBrep();
      return targetBrep != null;
    }

    return false;
  }

  private static BrepFace? FindClosestFaceOnBrep(Brep brep, Point3d pickPoint, bool hasPickPoint)
  {
    if (brep == null || brep.Faces.Count < 1)
      return null;

    if (hasPickPoint)
    {
      BrepFace? bestFace = null;
      var bestD2 = double.MaxValue;

      foreach (var face in brep.Faces)
      {
        try
        {
          if (!face.ClosestPoint(pickPoint, out var u, out var v))
            continue;

          var facePoint = face.PointAt(u, v);
          var d2 = pickPoint.DistanceToSquared(facePoint);
          if (d2 >= bestD2)
            continue;

          bestD2 = d2;
          bestFace = face;
        }
        catch
        {
        }
      }

      if (bestFace != null)
        return bestFace;
    }

    try
    {
      return brep.Faces[0];
    }
    catch
    {
      return null;
    }
  }

  private static bool TryBrepPointAndNormal(Brep brep, Point3d samplePoint, out Point3d onSurface, out Vector3d normal)
  {
    onSurface = Point3d.Unset;
    normal = Vector3d.Zero;

    try
    {
      if (!brep.ClosestPoint(
          samplePoint,
          out onSurface,
          out var componentIndex,
          out var u,
          out var v,
          double.MaxValue,
          out normal))
      {
        return false;
      }

      if ((!normal.IsValid || normal.IsTiny()) &&
          componentIndex.ComponentIndexType == ComponentIndexType.BrepFace &&
          componentIndex.Index >= 0 &&
          componentIndex.Index < brep.Faces.Count)
      {
        normal = brep.Faces[componentIndex.Index].NormalAt(u, v);
      }
    }
    catch
    {
      return false;
    }

    if (!onSurface.IsValid)
      return false;

    if (!normal.IsValid || normal.IsTiny())
      normal = samplePoint - onSurface;

    if (!normal.IsValid || normal.IsTiny())
      normal = Vector3d.ZAxis;

    normal.Unitize();
    return true;
  }

  private static bool ApplyLocalUndo(RhinoDoc doc, PointRecord record)
  {
    if (record == null || record.Id == Guid.Empty)
      return false;

    try
    {
      return doc.Objects.Delete(record.Id, true);
    }
    catch
    {
      return false;
    }
  }

  private static bool ApplyLocalRedo(RhinoDoc doc, PointRecord record)
  {
    if (record == null)
      return false;

    try
    {
      var id = doc.Objects.AddPoint(record.Point);
      if (id == Guid.Empty)
        return false;

      record.Id = id;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static string NormalizeHiddenKeyword(string? text)
  {
    var value = (text ?? string.Empty).Trim().ToLowerInvariant();
    while (value.StartsWith("_", StringComparison.Ordinal) || value.StartsWith("-", StringComparison.Ordinal))
      value = value[1..];

    return value;
  }
}
