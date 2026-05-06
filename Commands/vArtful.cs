using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using SysEnv = System.Environment;

namespace vTools.Commands;

/// <summary>
/// Artful layer-cleanup helper. Ported from Artful.py.
///
/// Workflow:
///   1) Sync settings, annotation tables and layers from the Artful template .3dm.
///   2) Set current layer to "- Traced -" (create if missing).
///   3) Find root-level numeric layers ("0", "1", "2", … only).
///   4) Move all objects from those numeric layers into ". Proliner .".
///   5) Delete the numeric layers.
///   6) Sort layers to match template order.
///
/// Everything runs inside a single undo record.
/// </summary>
public sealed class vArtful : Command
{
    private const string TracedLayer  = "- Traced -";
    private const string ProlineLayer = ". Proliner .";

    private static readonly string TemplatePath = Path.Combine(
        SysEnv.GetFolderPath(SysEnv.SpecialFolder.ApplicationData),
        @"McNeel\Rhinoceros\8.0\Localization\en-US\Template Files\Artful.3dm");

    public override string EnglishName => "vArtful";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        uint undoRecord = doc.BeginUndoRecord("vArtful");
        try
        {
            // 1. Sync template
            if (!ImportTemplate(doc))
                return Result.Failure;

            // 2. Ensure target layers exist
            int prolineIdx = EnsureLayer(doc, ProlineLayer);
            int tracedIdx  = EnsureLayer(doc, TracedLayer);

            // 3. Set current layer
            doc.Layers.SetCurrentLayerIndex(tracedIdx, true);

            // 4. Collect numeric root layers
            var numericLayers = CollectNumericLayers(doc);
            RhinoApp.WriteLine($"vArtful: Found {numericLayers.Count} root numeric layer(s).");

            // 5. Move objects from numeric layers to Proliner
            int totalMoved = 0;
            foreach (var layer in numericLayers)
            {
                if (layer.IsLocked || !layer.IsVisible)
                {
                    layer.IsLocked  = false;
                    layer.IsVisible = true;
                    doc.Layers.Modify(layer, layer.Index, true);
                }
                int moved = MoveObjectsToLayer(doc, layer.Index, prolineIdx);
                totalMoved += moved;
                RhinoApp.WriteLine($"  Moved {moved} obj(s) from '{layer.Name}' → '{ProlineLayer}'.");
            }

            // 6. Delete numeric layers (deepest first)
            int deletedCount = 0;
            foreach (var layer in numericLayers.OrderByDescending(l => l.FullPath.Count(c => c == ':')))
            {
                PrepareLayerForDelete(doc, layer.Index, tracedIdx);
                if (doc.Layers.Delete(layer.Index, true))
                    deletedCount++;
            }

            // 7. Sort layers to match template order
            int sortedCount = SortLayersLikeTemplate(doc);

            doc.Views.Redraw();

            RhinoApp.WriteLine(
                $"vArtful complete: current layer '{TracedLayer}', " +
                $"moved {totalMoved} obj(s), " +
                $"deleted {deletedCount} numeric layer(s), " +
                $"sorted {sortedCount} layer(s).");

            return Result.Success;
        }
        finally
        {
            if (undoRecord != uint.MaxValue)
                doc.EndUndoRecord(undoRecord);
        }
    }

    // ── Template import ───────────────────────────────────────────────────────

    private static bool ImportTemplate(RhinoDoc doc)
    {
        if (!File.Exists(TemplatePath))
        {
            RhinoApp.WriteLine($"vArtful: Template file not found:\n  {TemplatePath}");
            return false;
        }

        // Prefer headless RhinoDoc for live API access (units, render settings, views).
        RhinoDoc? templateDoc = null;
        try { templateDoc = RhinoDoc.OpenHeadless(TemplatePath); }
        catch { templateDoc = null; }

        if (templateDoc != null)
        {
            try
            {
                SyncDocumentPropertiesFromDoc(doc, templateDoc);
                SyncTablesFromDoc(doc, templateDoc);
                SyncLayersFromDoc(doc, templateDoc);
            }
            finally
            {
                try { templateDoc.Dispose(); } catch { }
            }
            return true;
        }

        // Fallback: File3dm-based read.
        var file3dm = File3dm.Read(TemplatePath);
        if (file3dm == null)
        {
            RhinoApp.WriteLine($"vArtful: Could not read template file:\n  {TemplatePath}");
            return false;
        }

        SyncDocumentPropertiesFromFile3dm(doc, file3dm);
        SyncTablesFromFile3dm(doc, file3dm);
        SyncLayersFromFile3dm(doc, file3dm);
        return true;
    }

    // ── Document property sync ────────────────────────────────────────────────

    private static void SyncDocumentPropertiesFromDoc(RhinoDoc doc, RhinoDoc template)
    {
        try { doc.ModelUnitSystem        = template.ModelUnitSystem;        } catch { }
        try { doc.PageUnitSystem         = template.PageUnitSystem;         } catch { }
        try { doc.ModelAbsoluteTolerance = template.ModelAbsoluteTolerance; } catch { }
        try { doc.ModelRelativeTolerance = template.ModelRelativeTolerance; } catch { }
        try { doc.RenderSettings         = template.RenderSettings.Duplicate(); } catch { }
        SyncGridCPlaneFromDoc(doc, template);
    }

    private static void SyncGridCPlaneFromDoc(RhinoDoc doc, RhinoDoc template)
    {
        try
        {
            var byName = new Dictionary<string, Rhino.Display.RhinoView>(StringComparer.OrdinalIgnoreCase);
            foreach (var tv in template.Views)
            {
                var name = tv?.ActiveViewport?.Name;
                if (name != null) byName[name] = tv;
            }

            Rhino.Display.RhinoView? fallback = null;
            try { fallback = template.Views.ActiveView; } catch { }

            foreach (var view in doc.Views)
            {
                if (view?.ActiveViewport == null) continue;
                var src = byName.TryGetValue(view.ActiveViewport.Name ?? "", out var found) ? found : fallback;
                if (src?.ActiveViewport == null) continue;

                try
                {
                    var cplane = src.ActiveViewport.GetConstructionPlane();
                    view.ActiveViewport.SetConstructionPlane(cplane);
                }
                catch { }

                foreach (var prop in new[] { "ConstructionGridVisible", "ConstructionAxesVisible", "WorldAxesVisible" })
                {
                    try
                    {
                        var pi = typeof(Rhino.Display.RhinoViewport).GetProperty(prop);
                        if (pi != null && pi.CanRead && pi.CanWrite)
                            pi.SetValue(view.ActiveViewport, pi.GetValue(src.ActiveViewport));
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void SyncDocumentPropertiesFromFile3dm(RhinoDoc doc, File3dm file3dm)
    {
        var s = file3dm.Settings;
        if (s == null) return;
        try { doc.ModelUnitSystem        = s.ModelUnitSystem;        } catch { }
        try { doc.PageUnitSystem         = s.PageUnitSystem;         } catch { }
        try { doc.ModelAbsoluteTolerance = s.ModelAbsoluteTolerance; } catch { }
        try { doc.ModelRelativeTolerance = s.ModelRelativeTolerance; } catch { }
    }

    // ── Named table sync ──────────────────────────────────────────────────────

    private static void SyncTablesFromDoc(RhinoDoc doc, RhinoDoc template)
    {
        // DimStyles
        foreach (var ds in template.DimStyles)
        {
            if (ds == null || ds.IsDeleted || string.IsNullOrEmpty(ds.Name)) continue;
            try
            {
                var dup = ds.Duplicate();
                var existing = doc.DimStyles.FindName(ds.Name);
                if (existing != null)
                    doc.DimStyles.Modify(dup, existing.Index, true);
                else
                    doc.DimStyles.Add(dup, false);
            }
            catch { }
        }

        // Linetypes
        foreach (var lt in template.Linetypes)
        {
            if (lt == null || lt.IsDeleted || string.IsNullOrEmpty(lt.Name)) continue;
            try
            {
                int idx = doc.Linetypes.Find(lt.Name);
                if (idx >= 0)
                    doc.Linetypes.Modify(lt, idx, true);
                else
                    doc.Linetypes.Add(lt);
            }
            catch { }
        }

        // HatchPatterns
        foreach (var hp in template.HatchPatterns)
        {
            if (hp == null || hp.IsDeleted || string.IsNullOrEmpty(hp.Name)) continue;
            try
            {
                var existing = doc.HatchPatterns.FindName(hp.Name);
                if (existing != null)
                    doc.HatchPatterns.Modify(hp, existing.Index, true);
                else
                    doc.HatchPatterns.Add(hp);
            }
            catch { }
        }
    }

    private static void SyncTablesFromFile3dm(RhinoDoc doc, File3dm file3dm)
    {
        // DimStyles
        foreach (var ds in file3dm.AllDimStyles)
        {
            if (ds == null || ds.IsDeleted || string.IsNullOrEmpty(ds.Name)) continue;
            try
            {
                var dup = ds.Duplicate();
                var existing = doc.DimStyles.FindName(ds.Name);
                if (existing != null)
                    doc.DimStyles.Modify(dup, existing.Index, true);
                else
                    doc.DimStyles.Add(dup, false);
            }
            catch { }
        }

        // Linetypes
        foreach (var lt in file3dm.AllLinetypes)
        {
            if (lt == null || lt.IsDeleted || string.IsNullOrEmpty(lt.Name)) continue;
            try
            {
                int idx = doc.Linetypes.Find(lt.Name);
                if (idx >= 0)
                    doc.Linetypes.Modify(lt, idx, true);
                else
                    doc.Linetypes.Add(lt);
            }
            catch { }
        }

        // HatchPatterns
        foreach (var hp in file3dm.AllHatchPatterns)
        {
            if (hp == null || hp.IsDeleted || string.IsNullOrEmpty(hp.Name)) continue;
            try
            {
                var existing = doc.HatchPatterns.FindName(hp.Name);
                if (existing != null)
                    doc.HatchPatterns.Modify(hp, existing.Index, true);
                else
                    doc.HatchPatterns.Add(hp);
            }
            catch { }
        }
    }

    // ── Layer sync ────────────────────────────────────────────────────────────

    private static void SyncLayersFromDoc(RhinoDoc doc, RhinoDoc template)
    {
        var layers = template.Layers
            .Where(l => l != null && !l.IsDeleted && !string.IsNullOrEmpty(l.FullPath))
            .OrderBy(l => l.FullPath!.Count(c => c == ':'))
            .ToList();

        foreach (var tLayer in layers)
        {
            var fp = tLayer.FullPath!;
            int existing = doc.Layers.FindByFullPath(fp, -1);
            if (existing >= 0)
            {
                // update color only
                try
                {
                    var lyr = doc.Layers[existing];
                    lyr.Color = tLayer.Color;
                    doc.Layers.Modify(lyr, existing, true);
                }
                catch { }
                continue;
            }
            AddLayerFromTemplate(doc, fp, tLayer.Color, tLayer.IsVisible, tLayer.IsLocked);
        }
    }

    private static void SyncLayersFromFile3dm(RhinoDoc doc, File3dm file3dm)
    {
        var layers = file3dm.AllLayers
            .Where(l => l != null && !l.IsDeleted && !string.IsNullOrEmpty(l.FullPath))
            .OrderBy(l => l.FullPath!.Count(c => c == ':'))
            .ToList();

        foreach (var tLayer in layers)
        {
            var fp = tLayer.FullPath!;
            int existing = doc.Layers.FindByFullPath(fp, -1);
            if (existing >= 0)
            {
                try
                {
                    var lyr = doc.Layers[existing];
                    lyr.Color = tLayer.Color;
                    doc.Layers.Modify(lyr, existing, true);
                }
                catch { }
                continue;
            }
            AddLayerFromTemplate(doc, fp, tLayer.Color, tLayer.IsVisible, tLayer.IsLocked);
        }
    }

    private static void AddLayerFromTemplate(RhinoDoc doc, string fullPath, Color color, bool visible, bool locked)
    {
        var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
        var leaf  = parts[^1];
        int parentIdx = -1;

        if (parts.Length > 1)
        {
            var parentPath = string.Join("::", parts[..^1]);
            EnsureParentChain(doc, parentPath);
            parentIdx = doc.Layers.FindByFullPath(parentPath, -1);
        }

        var newLayer = new Layer
        {
            Name      = leaf,
            Color     = color,
            IsVisible = visible,
            IsLocked  = locked,
        };
        if (parentIdx >= 0)
            newLayer.ParentLayerId = doc.Layers[parentIdx].Id;
        doc.Layers.Add(newLayer);
    }

    private static void EnsureParentChain(RhinoDoc doc, string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;
        var parts = fullPath.Split(new[] { "::" }, StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++)
        {
            var path = string.Join("::", parts[..(i + 1)]);
            if (doc.Layers.FindByFullPath(path, -1) >= 0) continue;

            var parentPath = i > 0 ? string.Join("::", parts[..i]) : null;
            int parentIdx  = parentPath != null ? doc.Layers.FindByFullPath(parentPath, -1) : -1;
            var layer = new Layer { Name = parts[i] };
            if (parentIdx >= 0) layer.ParentLayerId = doc.Layers[parentIdx].Id;
            doc.Layers.Add(layer);
        }
    }

    // ── Layer utility ─────────────────────────────────────────────────────────

    private static int EnsureLayer(RhinoDoc doc, string name)
    {
        int idx = doc.Layers.FindByFullPath(name, -1);
        if (idx >= 0) return idx;
        return doc.Layers.Add(new Layer { Name = name });
    }

    private static List<Layer> CollectNumericLayers(RhinoDoc doc)
    {
        var result = new List<Layer>();
        foreach (var layer in doc.Layers)
        {
            if (layer == null || layer.IsDeleted) continue;
            if (layer.ParentLayerId != Guid.Empty) continue;
            if (layer.Name.Length > 0 && layer.Name.All(char.IsDigit))
                result.Add(layer);
        }
        return result;
    }

    private static int MoveObjectsToLayer(RhinoDoc doc, int srcIdx, int dstIdx)
    {
        int moved = 0;
        var srcLayer = doc.Layers[srcIdx];
        if (srcLayer == null) return 0;
        var objs = doc.Objects.FindByLayer(srcLayer);
        if (objs == null) return 0;
        foreach (var obj in objs)
        {
            if (obj == null) continue;
            var attr = obj.Attributes.Duplicate();
            attr.LayerIndex = dstIdx;
            if (doc.Objects.ModifyAttributes(obj, attr, true))
                moved++;
        }
        return moved;
    }

    private static void PrepareLayerForDelete(RhinoDoc doc, int layerIdx, int safeIdx)
    {
        var layer = doc.Layers[layerIdx];
        if (layer == null || layer.IsDeleted) return;

        if (doc.Layers.CurrentLayerIndex == layerIdx)
            doc.Layers.SetCurrentLayerIndex(safeIdx, true);

        if (layer.IsLocked || !layer.IsVisible)
        {
            layer.IsLocked  = false;
            layer.IsVisible = true;
            doc.Layers.Modify(layer, layerIdx, true);
        }
    }

    // ── Layer sorting ─────────────────────────────────────────────────────────

    private static int SortLayersLikeTemplate(RhinoDoc doc)
    {
        var templateOrder = ReadTemplateLayerOrder();
        if (templateOrder.Count == 0) return 0;

        // Current layers in display order
        var current = doc.Layers
            .Where(l => l != null && !l.IsDeleted)
            .OrderBy(l => l.SortIndex)
            .ToList();

        // Match each template path to a current layer
        var matched      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedOrder = new List<Layer>();

        foreach (var tplPath in templateOrder)
        {
            Layer? match = null;

            // exact full-path match
            match ??= current.FirstOrDefault(l =>
                string.Equals(l.FullPath, tplPath, StringComparison.OrdinalIgnoreCase));

            // leaf-name match (normalise typo "referrence" → "reference")
            if (match == null)
            {
                var tplLeaf = CanonicalLeaf(tplPath.Split(new[] { "::" }, StringSplitOptions.None)[^1]);
                match = current.FirstOrDefault(l =>
                    !string.IsNullOrEmpty(l.FullPath) &&
                    string.Equals(
                        CanonicalLeaf(l.FullPath!.Split(new[] { "::" }, StringSplitOptions.None)[^1]),
                        tplLeaf, StringComparison.OrdinalIgnoreCase));
            }

            if (match != null && !string.IsNullOrEmpty(match.FullPath) && matched.Add(match.FullPath!))
                matchedOrder.Add(match);
        }

        // Final order: unmatched layers (original order) then template-matched layers
        var finalIndices = current
            .Where(l => string.IsNullOrEmpty(l.FullPath) || !matched.Contains(l.FullPath!))
            .Concat(matchedOrder)
            .Select(l => l.Index)
            .ToList();

        var originalIndices = current.Select(l => l.Index).ToList();
        if (!finalIndices.SequenceEqual(originalIndices))
        {
            try { doc.Layers.Sort(finalIndices); }
            catch { /* Sort may not be available in all SDK versions; skip gracefully. */ }
            doc.Views.Redraw();
        }

        return finalIndices.Count;
    }

    private static List<string> ReadTemplateLayerOrder()
    {
        try
        {
            var file3dm = File3dm.Read(TemplatePath);
            if (file3dm == null) return new List<string>();

            var layers = file3dm.AllLayers
                .Where(l => l != null && !l.IsDeleted && !string.IsNullOrEmpty(l.FullPath))
                .ToList();

            bool hasExplicitSort = layers.Any(l => l.SortIndex >= 0);
            if (hasExplicitSort)
                layers = layers.OrderBy(l => l.SortIndex).ThenBy(l => l.FullPath).ToList();

            return layers.Select(l => l.FullPath!).ToList();
        }
        catch { return new List<string>(); }
    }

    private static string CanonicalLeaf(string leaf)
    {
        var l = leaf.Trim().ToLowerInvariant();
        return l == "referrence" ? "reference" : l;
    }
}
