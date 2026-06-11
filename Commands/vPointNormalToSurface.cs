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
    go.SetCommandPrompt("Select target surface or polysurface");
    go.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter;

    // Important: whole polysurface, not picked sub-face.
    go.SubObjectSelect = false;

    go.EnablePreSelect(true, true);

    var result = go.Get();
    if (result != GetResult.Object || go.ObjectCount < 1)
      return false;

    var objRef = go.Object(0);
    if (objRef == null)
      return false;

    // Whole polysurface / Brep.
    var brep = objRef.Brep();
    if (brep != null)
    {
      targetBrep = brep.DuplicateBrep();
      return targetBrep != null;
    }

    // Single surface object.
    var surface = objRef.Surface();
    if (surface != null)
    {
      targetBrep = surface.ToBrep();
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

    if (brep == null || brep.Faces.Count == 0)
      return false;

    var cplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    if (!cplane.ClosestParameter(samplePoint, out var sampleU, out var sampleV))
      return false;

    var bestScreenD2 = double.MaxValue;
    var best3dD2 = double.MaxValue;
    Point3d bestPoint = Point3d.Unset;
    Vector3d bestNormal = Vector3d.Zero;

    foreach (var face in brep.Faces)
    {
      try
      {
        if (!face.ClosestPoint(samplePoint, out var u, out var v))
          continue;

        var relation = face.IsPointOnFace(u, v);
        if (relation == PointFaceRelation.Exterior)
          continue;

        var point = face.PointAt(u, v);
        if (!point.IsValid)
          continue;

        if (!cplane.ClosestParameter(point, out var pointU, out var pointV))
          continue;

        var du = pointU - sampleU;
        var dv = pointV - sampleV;
        var screenD2 = du * du + dv * dv;
        var d3 = point.DistanceToSquared(samplePoint);

        if (screenD2 > bestScreenD2)
          continue;

        if (Math.Abs(screenD2 - bestScreenD2) <= RhinoMath.SqrtEpsilon && d3 >= best3dD2)
          continue;

        var n = face.NormalAt(u, v);
        if (!n.IsValid || n.IsTiny())
          n = samplePoint - point;

        if (!n.IsValid || n.IsTiny())
          n = Vector3d.ZAxis;

        n.Unitize();

        bestScreenD2 = screenD2;
        best3dD2 = d3;
        bestPoint = point;
        bestNormal = n;
      }
      catch
      {
      }
    }

    if (!bestPoint.IsValid)
      return false;

    onSurface = bestPoint;
    normal = bestNormal;
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
