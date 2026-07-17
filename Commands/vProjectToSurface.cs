using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using DrawingColor = System.Drawing.Color;

namespace vTools.Commands;

/// <summary>
/// Projects selected curves and points to a selected surface or polysurface.
/// </summary>
public sealed class vProjectToSurface : Command
{
  private const string CommandName = "vProjectToSurface";

  public override string EnglishName => "vProjectToSurface";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

    var targets = PickTargetBreps(doc);
    if (targets == null)
      return Result.Cancel;

    if (targets.Breps.Count == 0)
    {
      RhinoApp.WriteLine($"{CommandName}: select at least one target surface or polysurface.");
      return Result.Nothing;
    }

    UnselectObjects(doc, targets.ObjectIds);

    var sources = PickSources(doc, targets.Breps, tolerance);
    if (sources == null)
      return Result.Cancel;

    if (sources.Count == 0)
    {
      RhinoApp.WriteLine($"{CommandName}: select at least one curve or point.");
      return Result.Nothing;
    }

    var outputIds = new List<Guid>();
    var skipped = 0;

    var undoRecord = doc.BeginUndoRecord(CommandName);
    try
    {
      foreach (var source in sources)
      {
        switch (source.Geometry)
        {
          case Curve curve:
            var curveIds = AddProjectedCurves(doc, curve, source.Attributes, targets.Breps, tolerance);
            if (curveIds.Count == 0)
              skipped++;
            outputIds.AddRange(curveIds);
            break;

          case Point point:
            var pointId = AddProjectedPoint(doc, point.Location, source.Attributes, targets.Breps, tolerance);
            if (pointId == Guid.Empty)
              skipped++;
            else
              outputIds.Add(pointId);
            break;
        }
      }
    }
    finally
    {
      if (undoRecord > 0)
        doc.EndUndoRecord(undoRecord);
    }

    if (outputIds.Count == 0)
    {
      RhinoApp.WriteLine($"{CommandName}: no selected geometry touched the target surface.");
      return Result.Nothing;
    }

    doc.Objects.UnselectAll();
    foreach (var id in outputIds)
      doc.Objects.FindId(id)?.Select(true);

    doc.Views.Redraw();

    RhinoApp.WriteLine(
      $"{CommandName}: projected {outputIds.Count} object{(outputIds.Count == 1 ? "" : "s")}" +
      (skipped > 0 ? $" | skipped {skipped}" : ""));

    return Result.Success;
  }

  private static List<SourceItem>? PickSources(RhinoDoc doc, IReadOnlyList<Brep> targetBreps, double tolerance)
  {
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curves and points to project. Press Enter to create");
    go.GeometryFilter = ObjectType.Curve | ObjectType.Point;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var preview = new ProjectionPreviewConduit(doc, targetBreps, tolerance)
    {
      Enabled = true
    };

    EventHandler<RhinoObjectSelectionEventArgs> onSelectionChanged = (_, _) =>
    {
      preview.Invalidate();
      doc.Views.Redraw();
    };

    RhinoDoc.SelectObjects += onSelectionChanged;
    RhinoDoc.DeselectObjects += onSelectionChanged;

    var preselectedWaitingForEnter = false;
    try
    {
      while (true)
      {
        var result = go.GetMultiple(1, 0);
        preview.Invalidate();
        doc.Views.Redraw();

        if (go.CommandResult() != Result.Success)
          return null;

        if (result == GetResult.Object)
        {
          if (go.ObjectsWerePreselected && !preselectedWaitingForEnter)
          {
            preselectedWaitingForEnter = true;
            go.EnablePreSelect(false, true);
            continue;
          }

          var selectedSources = SourceItemsFromDocumentSelection(doc);
          if (selectedSources.Count > 0)
            return selectedSources;

          return SourceItemsFromGetObject(go);
        }

        if (result == GetResult.Nothing)
          return SourceItemsFromDocumentSelection(doc);

        if (result == GetResult.Cancel)
          return null;
      }
    }
    finally
    {
      RhinoDoc.SelectObjects -= onSelectionChanged;
      RhinoDoc.DeselectObjects -= onSelectionChanged;
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static TargetSelection? PickTargetBreps(RhinoDoc doc)
  {
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select target surfaces or polysurfaces");
    go.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var result = go.GetMultiple(1, 0);
    if (result != GetResult.Object || go.ObjectCount < 1 || go.CommandResult() != Result.Success)
      return null;

    var breps = new List<Brep>();
    var objectIds = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef == null)
        continue;

      var rhObj = objRef.Object();
      if (rhObj != null)
        objectIds.Add(rhObj.Id);

      var brep = objRef.Brep();
      if (brep != null)
      {
        var duplicate = brep.DuplicateBrep();
        if (duplicate != null)
          breps.Add(duplicate);
        continue;
      }

      var surface = objRef.Surface();
      if (surface == null)
        continue;

      var surfaceBrep = surface.ToBrep();
      if (surfaceBrep != null)
        breps.Add(surfaceBrep);
    }

    return new TargetSelection(breps, objectIds);
  }

  private static void UnselectObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var changed = false;
    foreach (var id in objectIds)
      changed |= (doc.Objects.FindId(id)?.Select(false) ?? 0) > 0;

    if (changed)
      doc.Views.Redraw();
  }

  private static List<Guid> AddProjectedCurves(
    RhinoDoc doc,
    Curve source,
    ObjectAttributes attributes,
    IReadOnlyList<Brep> targetBreps,
    double tolerance)
  {
    var curvesToAdd = BuildProjectedCurves(source, targetBreps, tolerance);
    if (curvesToAdd.Count == 0)
      return new List<Guid>();

    var ids = new List<Guid>();
    foreach (var curve in curvesToAdd)
    {
      var outputAttributes = attributes.Duplicate();
      outputAttributes.LayerIndex = doc.Layers.CurrentLayerIndex;
      var id = doc.Objects.AddCurve(curve, outputAttributes);
      if (id != Guid.Empty)
        ids.Add(id);
    }

    return ids;
  }

  private static List<Curve> BuildProjectedCurves(Curve source, IReadOnlyList<Brep> targetBreps, double tolerance)
  {
    var projected = PullCurveToBreps(source, targetBreps, tolerance)
      .Where(curve => IsUsableCurve(curve, tolerance))
      .ToList();

    if (projected.Count == 0)
      return new List<Curve>();

    var joined = Curve.JoinCurves(projected, tolerance);
    return (joined != null && joined.Length > 0 ? joined : projected.ToArray())
      .Where(curve => IsUsableCurve(curve, tolerance))
      .ToList();
  }

  private static IEnumerable<Curve> PullCurveToBreps(Curve source, IReadOnlyList<Brep> targetBreps, double tolerance)
  {
    foreach (var targetBrep in targetBreps)
    {
      foreach (var curve in PullCurveToBrep(source, targetBrep, tolerance))
        yield return curve;
    }
  }

  private static IEnumerable<Curve> PullCurveToBrep(Curve source, Brep targetBrep, double tolerance)
  {
    foreach (var face in targetBrep.Faces)
    {
      Curve[]? pulled = null;
      try
      {
        pulled = source.PullToBrepFace(face, tolerance);
      }
      catch
      {
        try { pulled = Curve.PullToBrepFace(source, face, tolerance); }
        catch { pulled = null; }
      }

      if (pulled == null)
        continue;

      foreach (var curve in pulled)
      {
        if (curve != null)
          yield return curve;
      }
    }
  }

  private static Guid AddProjectedPoint(
    RhinoDoc doc,
    Point3d point,
    ObjectAttributes attributes,
    IReadOnlyList<Brep> targetBreps,
    double tolerance)
  {
    return TryClosestPointOnBreps(targetBreps, point, tolerance, out var projected)
      ? doc.Objects.AddPoint(projected, attributes.Duplicate())
      : Guid.Empty;
  }

  private static bool TryClosestPointOnBreps(
    IReadOnlyList<Brep> targetBreps,
    Point3d point,
    double tolerance,
    out Point3d projected)
  {
    projected = Point3d.Unset;
    if (targetBreps.Count == 0)
      return false;

    var bestDistanceSquared = double.MaxValue;
    foreach (var brep in targetBreps)
    {
      foreach (var face in brep.Faces)
      {
        try
        {
          if (!face.ClosestPoint(point, out var u, out var v))
            continue;

          var relation = face.IsPointOnFace(u, v);
          if (relation == PointFaceRelation.Exterior)
            continue;

          var candidate = face.PointAt(u, v);
          if (!candidate.IsValid)
            continue;

          var distanceSquared = point.DistanceToSquared(candidate);
          if (distanceSquared > bestDistanceSquared + tolerance * tolerance)
            continue;

          bestDistanceSquared = distanceSquared;
          projected = candidate;
        }
        catch
        {
        }
      }
    }

    return projected.IsValid;
  }

  private static bool IsUsableCurve(Curve? curve, double tolerance)
  {
    if (curve == null || !curve.IsValid)
      return false;

    try
    {
      return curve.GetLength() > tolerance;
    }
    catch
    {
      return true;
    }
  }

  private static List<SourceItem> SourceItemsFromDocumentSelection(RhinoDoc doc)
    => SourceItemsFromObjects(SelectedSourceObjects(doc));

  private static List<SourceItem> SourceItemsFromGetObject(GetObject go)
  {
    var objects = new List<RhinoObject>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var rhObj = go.Object(i)?.Object();
      if (rhObj != null && seen.Add(rhObj.Id))
        objects.Add(rhObj);
    }

    return SourceItemsFromObjects(objects);
  }

  private static List<SourceItem> SourceItemsFromObjects(IEnumerable<RhinoObject> objects)
  {
    var sources = new List<SourceItem>();
    var seen = new HashSet<Guid>();
    foreach (var rhObj in objects)
    {
      if (!seen.Add(rhObj.Id))
        continue;

      GeometryBase? duplicate = null;
      if (rhObj.Geometry is Curve curve)
        duplicate = curve.DuplicateCurve();
      else if (rhObj.Geometry is Point point)
        duplicate = new Point(point.Location);

      if (duplicate == null)
        continue;

      var attributes = rhObj.Attributes?.Duplicate() ?? new ObjectAttributes();
      attributes.RemoveFromAllGroups();
      sources.Add(new SourceItem(duplicate, attributes));
    }

    return sources;
  }

  private static List<RhinoObject> SelectedSourceObjects(RhinoDoc doc)
    => doc.Objects.GetSelectedObjects(false, false)
      .Where(obj => obj.Geometry is Curve or Point)
      .ToList();

  private sealed class ProjectionPreviewConduit : DisplayConduit
  {
    private static readonly DrawingColor PreviewCurveColor = DrawingColor.FromArgb(230, 255, 165, 0);
    private static readonly DrawingColor PreviewPointColor = DrawingColor.FromArgb(230, 255, 235, 120);

    private readonly RhinoDoc _doc;
    private readonly IReadOnlyList<Brep> _targetBreps;
    private readonly double _tolerance;
    private string? _selectionSignature;
    private List<Curve> _curves = new();
    private List<Point3d> _points = new();

    public ProjectionPreviewConduit(RhinoDoc doc, IReadOnlyList<Brep> targetBreps, double tolerance)
    {
      _doc = doc;
      _targetBreps = targetBreps;
      _tolerance = tolerance;
    }

    public void Invalidate()
    {
      _selectionSignature = null;
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      RefreshCacheIfNeeded();

      foreach (var curve in _curves)
        e.Display.DrawCurve(curve, PreviewCurveColor, 3);

      foreach (var point in _points)
        e.Display.DrawPoint(point, PointStyle.ActivePoint, 5, PreviewPointColor);
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
      RefreshCacheIfNeeded();

      foreach (var curve in _curves)
        e.IncludeBoundingBox(curve.GetBoundingBox(true));

      foreach (var point in _points)
        e.IncludeBoundingBox(new BoundingBox(point, point));
    }

    private void RefreshCacheIfNeeded()
    {
      var selectedObjects = SelectedSourceObjects(_doc);
      var signature = string.Join("|", selectedObjects.Select(obj => obj.Id.ToString("N")));
      if (_selectionSignature != null && string.Equals(signature, _selectionSignature, StringComparison.Ordinal))
        return;

      _selectionSignature = signature;
      _curves = new List<Curve>();
      _points = new List<Point3d>();

      foreach (var obj in selectedObjects)
      {
        switch (obj.Geometry)
        {
          case Curve curve:
            _curves.AddRange(BuildProjectedCurves(curve, _targetBreps, _tolerance));
            break;

          case Point point:
            if (TryClosestPointOnBreps(_targetBreps, point.Location, _tolerance, out var projected))
              _points.Add(projected);
            break;
        }
      }
    }
  }

  private sealed record TargetSelection(List<Brep> Breps, HashSet<Guid> ObjectIds);
  private sealed record SourceItem(GeometryBase Geometry, ObjectAttributes Attributes);
}
