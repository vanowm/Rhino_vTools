using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Shared utilities for native orient commands.
/// </summary>
internal static class OrientCommandCommon
{
  private const string OrientOptionsSectionName = "vOrient";
  private const string CopyOptionKey = "copy";

  /// <summary>
  /// Preview line segment used while collecting orient points.
  /// </summary>
  internal sealed record PreviewSegment(Point3d Start, Point3d End);

  /// <summary>
  /// Loads persisted Copy option value for orient commands.
  /// </summary>
  internal static bool LoadCopyOption(bool fallback = false)
  {
    return vToolsOptionStore.Read(
      OrientOptionsSectionName,
      section => vToolsOptionStore.TryGetBool(section, CopyOptionKey, out var copyMode) ? copyMode : fallback);
  }

  /// <summary>
  /// Saves Copy option value for orient commands.
  /// </summary>
  internal static void SaveCopyOption(bool copyMode)
  {
    _ = vToolsOptionStore.Update(OrientOptionsSectionName, section => section[CopyOptionKey] = copyMode);
  }

  /// <summary>
  /// Prompts for objects that will be transformed by orient commands.
  /// </summary>
  internal static List<Guid> SelectObjectsToOrient(RhinoDoc doc)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select objects to orient");
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.DeselectAllBeforePostSelect = false;
    go.GroupSelect = true;
    go.SubObjectSelect = false;

    var result = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success || result != GetResult.Object)
      return new List<Guid>();

    var ids = new List<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var obj = go.Object(i);
      if (obj == null)
        continue;

      var id = obj.ObjectId;
      if (id != Guid.Empty && !ids.Contains(id))
        ids.Add(id);
    }

    return ids;
  }

  /// <summary>
  /// Gets one point while allowing Copy toggle edits and dynamic trace preview.
  /// </summary>
  internal static bool TryGetPointWithCopyOption(
    RhinoDoc doc,
    string prompt,
    ref bool copyMode,
    out Point3d point,
    Point3d? basePoint = null,
    Point3d? traceFrom = null,
    IReadOnlyList<PreviewSegment>? previewSegments = null)
  {
    return GetPointCore(doc, prompt, false, ref copyMode, out point, basePoint, traceFrom, previewSegments) == GetResult.Point;
  }

  /// <summary>
  /// Gets an optional point: Enter skips (GetResult.Nothing), Esc cancels (GetResult.Cancel).
  /// </summary>
  internal static GetResult TryGetOptionalPointWithCopyOption(
    RhinoDoc doc,
    string prompt,
    ref bool copyMode,
    out Point3d point,
    Point3d? basePoint = null,
    Point3d? traceFrom = null,
    IReadOnlyList<PreviewSegment>? previewSegments = null)
  {
    return GetPointCore(doc, prompt, true, ref copyMode, out point, basePoint, traceFrom, previewSegments);
  }

  private static GetResult GetPointCore(
    RhinoDoc doc,
    string prompt,
    bool acceptNothing,
    ref bool copyMode,
    out Point3d point,
    Point3d? basePoint,
    Point3d? traceFrom,
    IReadOnlyList<PreviewSegment>? previewSegments)
  {
    point = Point3d.Unset;

    var gp = new GetPoint();
    gp.SetCommandPrompt(prompt);
    if (acceptNothing)
      gp.AcceptNothing(true);
    if (basePoint.HasValue)
      gp.SetBasePoint(basePoint.Value, true);

    var copyToggle = new OptionToggle(copyMode, "No", "Yes");
    gp.AddOptionToggle("Copy", ref copyToggle);

    previewSegments ??= Array.Empty<PreviewSegment>();
    var faintColor = Color.LightGray;

    EventHandler<GetPointDrawEventArgs>? draw = (_, e) =>
    {
      foreach (var segment in previewSegments)
      {
        e.Display.DrawLine(segment.Start, segment.End, faintColor, 1);
        e.Display.DrawDottedLine(segment.Start, segment.End, faintColor);
      }

      if (traceFrom.HasValue)
      {
        e.Display.DrawLine(traceFrom.Value, e.CurrentPoint, faintColor, 1);
        e.Display.DrawDottedLine(traceFrom.Value, e.CurrentPoint, faintColor);
      }
    };

    gp.DynamicDraw += draw;
    try
    {
      while (true)
      {
        var result = gp.Get();
        copyMode = copyToggle.CurrentValue;

        if (result == GetResult.Point)
        {
          point = gp.Point();
          return GetResult.Point;
        }

        if (result == GetResult.Option)
          continue;

        return result;
      }
    }
    finally
    {
      gp.DynamicDraw -= draw;
    }
  }

  /// <summary>
  /// Builds a stable plane from two points using active CPlane Z as preferred up axis.
  /// </summary>
  internal static bool TryBuildPlaneFromTwoPoints(RhinoDoc doc, Point3d origin, Point3d xPoint, out Plane plane)
  {
    plane = Plane.Unset;

    var xAxis = xPoint - origin;
    if (!xAxis.Unitize())
      return false;

    var upAxis = doc.Views.ActiveView?.ActiveViewport?.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    if (Math.Abs(Vector3d.Multiply(xAxis, upAxis)) > 0.999)
      upAxis = Vector3d.YAxis;

    var yAxis = Vector3d.CrossProduct(upAxis, xAxis);
    if (!yAxis.Unitize())
      return false;

    plane = new Plane(origin, xAxis, yAxis);
    return plane.IsValid;
  }

  /// <summary>
  /// Builds a plane from three picked points.
  /// </summary>
  internal static bool TryBuildPlaneFromThreePoints(Point3d origin, Point3d xPoint, Point3d yPoint, out Plane plane)
  {
    plane = new Plane(origin, xPoint, yPoint);
    return plane.IsValid;
  }

  /// <summary>
  /// Applies transform to selected objects, in-place or as copies.
  /// </summary>
  internal static List<Guid> TransformObjects(RhinoDoc doc, IReadOnlyList<Guid> objectIds, Transform xform, bool copyMode)
  {
    var transformed = new List<Guid>();

    foreach (var id in objectIds)
    {
      if (id == Guid.Empty)
        continue;

      var newId = doc.Objects.Transform(id, xform, !copyMode);
      if (newId != Guid.Empty)
        transformed.Add(newId);
    }

    return transformed;
  }

  /// <summary>
  /// Recreates source groups as fresh groups for copied objects.
  /// </summary>
  internal static void RecreateGroupsForCopiedObjects(RhinoDoc doc, IReadOnlyList<Guid> sourceIds, IReadOnlyList<Guid> copiedIds)
  {
    var count = Math.Min(sourceIds.Count, copiedIds.Count);
    if (count <= 0)
      return;

    var sourceGroupToCopiedIds = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < count; i++)
    {
      var sourceId = sourceIds[i];
      var copiedId = copiedIds[i];
      if (sourceId == Guid.Empty || copiedId == Guid.Empty)
        continue;

      var sourceObj = doc.Objects.FindId(sourceId);
      var copiedObj = doc.Objects.FindId(copiedId);
      if (sourceObj == null || copiedObj == null)
        continue;

      var sourceGroupIndices = sourceObj.Attributes.GetGroupList();
      if (sourceGroupIndices == null || sourceGroupIndices.Length == 0)
        continue;

      var copiedAttr = copiedObj.Attributes.Duplicate();
      foreach (var groupIndex in sourceGroupIndices)
      {
        var sourceGroup = doc.Groups.FindIndex(groupIndex);
        var sourceGroupName = sourceGroup?.Name;
        if (string.IsNullOrWhiteSpace(sourceGroupName))
          continue;

        copiedAttr.RemoveFromGroup(groupIndex);

        if (!sourceGroupToCopiedIds.TryGetValue(sourceGroupName, out var ids))
        {
          ids = new List<Guid>();
          sourceGroupToCopiedIds[sourceGroupName] = ids;
        }

        ids.Add(copiedId);
      }

      doc.Objects.ModifyAttributes(copiedId, copiedAttr, false);
    }

    foreach (var pair in sourceGroupToCopiedIds)
    {
      var uniqueIds = pair.Value
        .Where(id => id != Guid.Empty && doc.Objects.FindId(id) != null)
        .Distinct()
        .ToList();

      if (uniqueIds.Count == 0)
        continue;

      var newGroupName = MakeUniqueGroupName(doc, pair.Key + "_copy");
      var newGroupIndex = doc.Groups.Add(newGroupName);
      if (newGroupIndex < 0)
        continue;

      foreach (var id in uniqueIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        var attr = obj.Attributes.Duplicate();
        attr.AddToGroup(newGroupIndex);
        doc.Objects.ModifyAttributes(id, attr, false);
      }
    }
  }

  /// <summary>
  /// Returns a unique group name based on the requested base name.
  /// </summary>
  internal static string MakeUniqueGroupName(RhinoDoc doc, string baseName)
  {
    var name = string.IsNullOrWhiteSpace(baseName) ? "Group" : baseName;
    var candidate = name;
    var suffix = 2;

    while (doc.Groups.FindName(candidate) != null)
    {
      candidate = name + "_" + suffix;
      suffix++;
    }

    return candidate;
  }
}
