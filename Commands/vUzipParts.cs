using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using static vTools.Commands.UzipCommon;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Builds multi-part zipper-style geometry from a selected center curve and
/// preselected helper curves, driven by the shared <c>vTools.config.json</c> file.
/// </summary>
public class vUzipParts : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vUzipParts";

  private static string LayerCut       = DefaultLayerCutName;
  private static string LayerPlot      = DefaultLayerPlotName;
  private static string LayerReference = DefaultLayerReferenceName;

  /// <summary>
  /// Executes vUzipParts: reads config, gathers inputs, builds parts, and updates config.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var configPath = GetToolsConfigPath();

    var toolsConfig = LoadToolsConfig(configPath);
    var uZipConfig = EnsureUZipSection(toolsConfig);
    var layerRuntime = NormalizeLayerRuntime(uZipConfig.Layers);
    ApplyLayerRuntime(layerRuntime);
    var ctx = new UzipLayerContext(LayerCut, LayerPlot, LayerReference);
    EnsureCommandLayers(doc, layerRuntime);

    var defaultLabel = (uZipConfig.Label ?? DefaultLabel).Trim();
    var defaultTail = Math.Max(0.0, uZipConfig.Tail);
    uZipConfig.Label = defaultLabel;
    uZipConfig.Tail = defaultTail;
    uZipConfig.Layers = layerRuntime.ToConfig();
    SaveToolsConfig(configPath, toolsConfig);

    var preselected = doc.Objects.GetSelectedObjects(false, false)
      .Where(o => o != null)
      .Select(o => o.Id)
      .ToList();

    var selected = SelectCenterCurve(doc, defaultLabel, defaultTail);
    if (selected.CenterId == Guid.Empty)
    {
      uZipConfig.Label = (selected.Label ?? DefaultLabel).Trim();
      uZipConfig.Tail = Math.Max(0.0, selected.Tail);
      uZipConfig.Layers = layerRuntime.ToConfig();
      SaveToolsConfig(configPath, toolsConfig);
      return Result.Cancel;
    }

    var centerCurve = CurveFromId(doc, selected.CenterId);
    if (centerCurve == null)
      return Result.Failure;

    var centerLayer = ObjLayerName(doc, selected.CenterId);
    var centerItem = new CurveItem(centerCurve.DuplicateCurve(), centerLayer, selected.CenterId);

    var touching = CollectPreselected(doc, selected.CenterId, centerCurve, preselected, requireTouch: true);
    var allPreselected = CollectPreselected(doc, selected.CenterId, centerCurve, preselected, requireTouch: false);

    // Save originals so a tail change during placement can re-derive endCurves correctly.
    var originalTouching = touching;
    var originalAllPreselected = allPreselected;

    var plane = GetCurvePlane(centerCurve);
    var insideSign = SolveInsideSign(doc, centerCurve, plane);

    var currentLabel = selected.Label;
    var currentTail = selected.Tail;

    var (endCurves, endParentIds) = BuildEndCurves(doc, originalTouching, centerCurve, plane, currentTail);
    if (Math.Abs(currentTail) <= RhinoMath.ZeroTolerance && endParentIds.Count > 0)
    {
      allPreselected = originalAllPreselected.Where(i => !i.ObjectId.HasValue || !endParentIds.Contains(i.ObjectId.Value)).ToList();
      touching = originalTouching.Where(i => !i.ObjectId.HasValue || !endParentIds.Contains(i.ObjectId.Value)).ToList();
    }

    var touchingIds = touching.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).ToHashSet();
    var specs = ResolvePartSpecs(centerLayer, uZipConfig.Parts, layerRuntime, ctx);
    uZipConfig.Parts = specs;
    if (specs.Count == 0)
    {
      RhinoApp.WriteLine("vUzipParts: no part definitions found in config.");
      return Result.Failure;
    }
    var stamp = DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);

    var allPartObjectIds = new List<List<Guid>>();
    var createdGroups = new List<string>();

    var watch = System.Diagnostics.Stopwatch.StartNew();
    BuildAllParts(doc, stamp, currentLabel, centerItem, allPreselected, touchingIds, centerCurve, plane, insideSign, endCurves, specs, allPartObjectIds, createdGroups, ctx);
    watch.Stop();
    RhinoApp.WriteLine($"vUzipParts built in {watch.Elapsed.TotalSeconds:F3}s");

    while (true)
    {
      var anchorGroup = createdGroups.Count > 0 ? createdGroups[0] : null;
      var (needsRebuild, newLabel, newTail) = PlaceGroupsWithPickOrDelete(doc, createdGroups, anchorGroup, currentLabel, currentTail);
      if (!needsRebuild)
        break;

      // Delete previous parts and rebuild with updated label/tail.
      DeleteCreatedGroupsAndMembers(doc, createdGroups);
      allPartObjectIds.Clear();
      createdGroups.Clear();
      currentLabel = newLabel;
      currentTail = newTail;

      var (rebuildEndCurves, rebuildEndParentIds) = BuildEndCurves(doc, originalTouching, centerCurve, plane, currentTail);
      var rebuildAllPreselected = originalAllPreselected;
      var rebuildTouching = originalTouching;
      if (Math.Abs(currentTail) <= RhinoMath.ZeroTolerance && rebuildEndParentIds.Count > 0)
      {
        rebuildAllPreselected = originalAllPreselected.Where(i => !i.ObjectId.HasValue || !rebuildEndParentIds.Contains(i.ObjectId.Value)).ToList();
        rebuildTouching = originalTouching.Where(i => !i.ObjectId.HasValue || !rebuildEndParentIds.Contains(i.ObjectId.Value)).ToList();
      }
      var rebuildTouchingIds = rebuildTouching.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).ToHashSet();

      watch.Restart();
      BuildAllParts(doc, stamp, currentLabel, centerItem, rebuildAllPreselected, rebuildTouchingIds, centerCurve, plane, insideSign, rebuildEndCurves, specs, allPartObjectIds, createdGroups, ctx);
      watch.Stop();
      RhinoApp.WriteLine($"vUzipParts rebuilt in {watch.Elapsed.TotalSeconds:F3}s");
    }

    uZipConfig.Label = (currentLabel ?? DefaultLabel).Trim();
    uZipConfig.Tail = Math.Max(0.0, currentTail);
    uZipConfig.Layers = layerRuntime.ToConfig();
    uZipConfig.Parts = specs;

    SaveToolsConfig(configPath, toolsConfig);
    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Returns the plug-in deployment directory used for configuration storage.
  /// </summary>
  private static void ApplyLayerRuntime(LayerRuntime runtime)
  {
    LayerReference = runtime.ReferenceName;
    LayerPlot = runtime.PlotName;
    LayerCut = runtime.CutName;
  }

  private static (Guid CenterId, string Label, double Tail) SelectCenterCurve(RhinoDoc doc, string defaultLabel, double defaultTail)
  {
    var label = defaultLabel;
    var tail = Math.Max(0.0, defaultTail);

    while (true)
    {
      var tailOpt = new OptionDouble(tail, 0.0, 1e9);
      var go = new GetObject();
      go.SetCommandPrompt("Select center curve of U-Zip");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AlreadySelectedObjectSelect = true;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.AcceptString(true);
      go.AcceptNumber(true, false);

      var labelOptionIndex = go.AddOption("Label", label);
      go.AddOptionDouble("Tail", ref tailOpt);

      var result = go.Get();
      tail = Math.Max(0.0, tailOpt.CurrentValue);
      if (go.CommandResult() != Result.Success)
        return (Guid.Empty, label, tail);

      if (result == GetResult.Object)
      {
        var obj = go.Object(0);
        if (obj == null)
          continue;
        return (obj.ObjectId, label, tail);
      }

      if (result == GetResult.Option)
      {
        var opt = go.Option();
        if (opt != null && opt.Index == labelOptionIndex)
        {
          var rc = RhinoGet.GetString("Label", true, ref label);
          if (rc == Result.Cancel)
            return (Guid.Empty, label, tail);
          label = (label ?? DefaultLabel).Trim();
        }
        continue;
      }

      if (result == GetResult.Number)
      {
        tail = Math.Max(0.0, go.Number());
        continue;
      }

      if (result == GetResult.String)
      {
        var typed = go.StringResult()?.Trim();
        if (!string.IsNullOrWhiteSpace(typed))
          label = typed;
        continue;
      }

      var residualTyped = go.StringResult()?.Trim();
      if (!string.IsNullOrWhiteSpace(residualTyped))
      {
        label = residualTyped;
        continue;
      }

      return (Guid.Empty, label, tail);
    }
  }

  private sealed class PlacementPreviewConduit : Rhino.Display.DisplayConduit
  {
    private readonly List<(GeometryBase Geom, System.Drawing.Color Color)> _items;
    public Point3d BasePoint;
    public Point3d CurrentPoint;
    public bool DrawEnabled;  // true only during sub-prompt entry (Label/Tail); prevents ghost with DynamicDraw

    public PlacementPreviewConduit(
      IEnumerable<(GeometryBase Geom, System.Drawing.Color Color)> items,
      Point3d basePoint)
    {
      _items = new List<(GeometryBase, System.Drawing.Color)>(items);
      BasePoint = basePoint;
      CurrentPoint = basePoint;
    }

    protected override void PostDrawObjects(Rhino.Display.DrawEventArgs e)
    {
      if (!DrawEnabled)
        return;
      var move = CurrentPoint - BasePoint;
      var xform = Transform.Translation(move);
      foreach (var (geom, color) in _items)
      {
        var draw = geom.Duplicate();
        if (draw == null)
          continue;
        draw.Transform(xform);
        switch (draw)
        {
          case Curve c:       e.Display.DrawCurve(c, color, 1); break;
          case Brep b:        e.Display.DrawBrepWires(b, color, 1); break;
          case Mesh m:        e.Display.DrawMeshWires(m, color); break;
          case TextEntity te: e.Display.DrawAnnotation(te, color); break;
          case Point p:       e.Display.DrawPoint(p.Location, Rhino.Display.PointStyle.Simple, 2, color); break;
        }
      }
    }
  }

  private static (bool NeedsRebuild, string Label, double Tail) PlaceGroupsWithPickOrDelete(RhinoDoc doc, List<string> groupNames, string? anchorGroupName, string label, double tail)
  {
    var selectedIds = new HashSet<Guid>();
    foreach (var name in groupNames)
    {
      var group = doc.Groups.FindName(name);
      if (group == null)
        continue;

      foreach (var member in doc.Groups.GroupMembers(group.Index))
      {
        if (member != null && doc.Objects.FindId(member.Id) != null)
          selectedIds.Add(member.Id);
      }
    }

    if (selectedIds.Count == 0)
      return (false, label, tail);

    var bbox = BoundingBox.Empty;
    foreach (var id in selectedIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry == null)
        continue;
      var gb = obj.Geometry.GetBoundingBox(true);
      if (!gb.IsValid)
        continue;
      bbox = bbox.IsValid ? BoundingBox.Union(bbox, gb) : gb;
    }

    if (!bbox.IsValid)
      return (false, label, tail);

    var basePoint = PartLeftEndOuterAnchor(doc, anchorGroupName)
      ?? new Point3d(bbox.Min.X, bbox.Max.Y, 0.5 * (bbox.Min.Z + bbox.Max.Z));

    var previewItems = new List<(GeometryBase Geom, System.Drawing.Color Color)>();
    foreach (var id in selectedIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry == null)
        continue;

      var color = System.Drawing.Color.Cyan;
      var layer = doc.Layers[obj.Attributes.LayerIndex];
      if (layer != null)
        color = layer.Color;

      var dup = obj.Geometry.Duplicate();
      if (dup != null)
        previewItems.Add((dup, color));
    }

    foreach (var id in selectedIds)
      doc.Objects.Hide(id, true);

    var conduit = new PlacementPreviewConduit(previewItems, basePoint);
    conduit.Enabled = true;
    doc.Views.Redraw();

    var gp = new GetPoint();
    gp.SetCommandPrompt("Pick placement point for created parts (Esc to cancel and delete)");
    var labelOptionIndex = gp.AddOption("Label", label);
    var tailOptionIndex = gp.AddOption("Tail", tail.ToString("0.##"));
    // DynamicDraw: draw preview for smooth cursor tracking and keep conduit CurrentPoint in sync.
    EventHandler<GetPointDrawEventArgs> handler = (_, e) =>
    {
      conduit.CurrentPoint = e.CurrentPoint;
      var moveVec = e.CurrentPoint - basePoint;
      var xform = Transform.Translation(moveVec);
      foreach (var (geom, color) in previewItems)
      {
        var draw = geom.Duplicate();
        if (draw == null) continue;
        draw.Transform(xform);
        switch (draw)
        {
          case Curve c:       e.Display.DrawCurve(c, color, 1); break;
          case Brep b:        e.Display.DrawBrepWires(b, color, 1); break;
          case Mesh m:        e.Display.DrawMeshWires(m, color); break;
          case TextEntity te: e.Display.DrawAnnotation(te, color); break;
          case Point p:       e.Display.DrawPoint(p.Location, Rhino.Display.PointStyle.Simple, 2, color); break;
        }
      }
    };
    gp.DynamicDraw += handler;

    while (true)
    {
      var result = gp.Get();

      if (result == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt != null && opt.Index == labelOptionIndex)
        {
          // Bracket sub-prompt with conduit so preview holds while user types.
          conduit.DrawEnabled = true;
          doc.Views.Redraw();
          var newLabel = label;
          RhinoGet.GetString("Label", true, ref newLabel);
          conduit.DrawEnabled = false;
          doc.Views.Redraw();
          var trimmedLabel = (newLabel ?? DefaultLabel).Trim();
          if (trimmedLabel != label)
          {
            gp.DynamicDraw -= handler;
            conduit.Enabled = false;
            return (true, trimmedLabel, tail);
          }
          // Label unchanged — refresh options.
          gp.ClearCommandOptions();
          labelOptionIndex = gp.AddOption("Label", label);
          tailOptionIndex = gp.AddOption("Tail", tail.ToString("0.##"));
        }
        else if (opt != null && opt.Index == tailOptionIndex)
        {
          // Bracket sub-prompt with conduit so preview holds while user types.
          conduit.DrawEnabled = true;
          doc.Views.Redraw();
          var newTail = tail;
          RhinoGet.GetNumber("Tail length", true, ref newTail);
          conduit.DrawEnabled = false;
          doc.Views.Redraw();
          newTail = Math.Max(0.0, newTail);
          if (Math.Abs(newTail - tail) > RhinoMath.ZeroTolerance)
          {
            gp.DynamicDraw -= handler;
            conduit.Enabled = false;
            return (true, label, newTail);
          }
          // Tail unchanged — refresh options.
          gp.ClearCommandOptions();
          labelOptionIndex = gp.AddOption("Label", label);
          tailOptionIndex = gp.AddOption("Tail", tail.ToString("0.##"));
        }
        continue;
      }

      gp.DynamicDraw -= handler;
      conduit.Enabled = false;

      if (result != GetResult.Point)
      {
        DeleteCreatedGroupsAndMembers(doc, groupNames);
        doc.Views.Redraw();
        return (false, label, tail);
      }

      var target = gp.Point();
      var move = target - basePoint;
      if (move.Length > doc.ModelAbsoluteTolerance)
      {
        var xform = Transform.Translation(move);
        foreach (var id in selectedIds.ToList())
        {
          var newId = doc.Objects.Transform(id, xform, true);
          if (newId != Guid.Empty)
          {
            selectedIds.Remove(id);
            selectedIds.Add(newId);
          }
        }
      }

      foreach (var id in selectedIds)
        doc.Objects.Show(id, true);

      doc.Objects.UnselectAll();
      foreach (var id in selectedIds)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      return (false, label, tail);
    }
  }

  private static Point3d? PartLeftEndOuterAnchor(RhinoDoc doc, string? groupName)
  {
    if (string.IsNullOrWhiteSpace(groupName))
      return null;

    var group = doc.Groups.FindName(groupName);
    if (group == null)
      return null;

    var cutCurves = new List<Curve>();
    foreach (var member in doc.Groups.GroupMembers(group.Index))
    {
      if (member == null)
        continue;

      var obj = doc.Objects.FindId(member.Id);
      if (obj == null)
        continue;

      var layerName = doc.Layers[obj.Attributes.LayerIndex]?.FullPath;
      if (!string.Equals(layerName, LayerCut, StringComparison.OrdinalIgnoreCase))
        continue;

      if (obj.Geometry is Curve c)
        cutCurves.Add(c);
    }

    if (cutCurves.Count < 2)
      return null;

    var outer = cutCurves.OrderByDescending(c => c.GetLength()).FirstOrDefault();
    if (outer == null)
      return null;

    var p0 = outer.PointAtStart;
    var p1 = outer.PointAtEnd;
    return p0.X <= p1.X ? p0 : p1;
  }

}
