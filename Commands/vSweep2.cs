using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Sweep2 with a persistent Layer option at every selection step.
/// Use * or . at the layer prompt to target &lt;Current&gt; layer.
/// </summary>
public sealed class vSweep2 : Command
{
  private const string SectionName = "vSweep2";
  private const string LayerKey = "layer";

  // Persisted between runs (statics are fine — command is a singleton).
  private static string _layer = string.Empty; // empty = use current layer

  public override string EnglishName => "vSweep2";

  // ── Option persistence ─────────────────────────────────────────────────────

  private static void LoadOptions()
  {
    _layer = vToolsOptionStore.Read(SectionName, section =>
    {
      vToolsOptionStore.TryGetString(section, LayerKey, out var layer);
      return layer;
    });
  }

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
    {
      section[LayerKey] = _layer;
    });

  // ── Layer helpers ──────────────────────────────────────────────────────────

  private static string LayerDisplay =>
    string.IsNullOrEmpty(_layer) ? "(current)" : _layer;

  private static void HandleLayerSubprompt(RhinoDoc doc, RunMode mode)
  {
    if (mode != RunMode.Scripted)
    {
      // GUI: built-in layer picker.
      int dlgIdx;
      if (string.IsNullOrEmpty(_layer))
      {
        dlgIdx = doc.Layers.CurrentLayerIndex;
      }
      else
      {
        var found = doc.Layers.FindByFullPath(_layer, RhinoMath.UnsetIntIndex);
        dlgIdx = found >= 0 ? found : doc.Layers.CurrentLayerIndex;
      }
      bool setCurrentState = false;
      if (Dialogs.ShowSelectLayerDialog(ref dlgIdx, "Select result layer", true, false, ref setCurrentState))
      {
        var picked = doc.Layers[dlgIdx];
        if (picked != null)
        {
          _layer = picked.FullPath;
          SaveOptions();
        }
      }
    }
    else
    {
      // Scripted (-): text sub-prompt; * or . = <Current>.
      var gs = new GetString();
      gs.SetCommandPrompt("Layer name (* or . = <Current>)");
      gs.AcceptNothing(true);
      if (gs.Get() == GetResult.String)
      {
        var s = (gs.StringResult() ?? string.Empty).Trim();
        _layer = (s == "*" || s == "." || string.IsNullOrWhiteSpace(s))
                 ? string.Empty
                 : s;
        SaveOptions();
      }
    }
  }

  private static int ResolveLayerIndex(RhinoDoc doc)
  {
    if (string.IsNullOrEmpty(_layer))
      return doc.Layers.CurrentLayerIndex;

    var idx = doc.Layers.FindByFullPath(_layer, RhinoMath.UnsetIntIndex);
    if (idx >= 0)
      return idx;

    // Layer not found — create it on the fly.
    var layer = new Layer { Name = _layer };
    return doc.Layers.Add(layer);
  }

  // ── Entry point ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    // ── Step 1: first rail ────────────────────────────────────────────────

    var rail1 = PickSingleCurve(doc, "Select first rail curve", mode);
    if (rail1 == null)
      return Result.Cancel;

    // ── Step 2: second rail ───────────────────────────────────────────────

    var rail2 = PickSingleCurve(doc, "Select second rail curve", mode);
    if (rail2 == null)
      return Result.Cancel;

    // ── Step 3: cross-sections (one at a time, Enter = done) ───────────────

    var sections = new List<Curve>();

    while (true)
    {
      var prompt = sections.Count == 0
        ? "Select cross-section curves (Enter when done)"
        : $"Select next cross-section ({sections.Count} selected, Enter when done)";

      var go = new GetObject();
      go.SetCommandPrompt(prompt);
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AcceptNothing(true);
      go.AddOption("Layer", LayerDisplay);
      go.AddOption("Current");

      var r = go.Get();

      if (r == GetResult.Cancel || go.CommandResult() != Result.Success)
        return Result.Cancel;

      if (r == GetResult.Option)
      {
        var optName = go.Option()?.EnglishName;
        if (optName == "Layer")
          HandleLayerSubprompt(doc, mode);
        else if (optName == "Current")
        { _layer = string.Empty; SaveOptions(); }
        continue;
      }

      if (r == GetResult.Nothing)
        break;

      if (r == GetResult.Object)
      {
        var crv = go.Object(0).Curve();
        if (crv != null)
          sections.Add(crv);
      }
    }

    if (sections.Count == 0)
    {
      RhinoApp.WriteLine("vSweep2: no cross-section curves selected.");
      return Result.Cancel;
    }

    // ── Step 4: perform sweep ─────────────────────────────────────────────

    var sweeper = new SweepTwoRail
    {
      SweepTolerance        = doc.ModelAbsoluteTolerance,
      AngleToleranceRadians = doc.ModelAngleToleranceRadians,
    };

    var results = sweeper.PerformSweep(rail1, rail2, sections);

    if (results == null || results.Length == 0)
    {
      RhinoApp.WriteLine("vSweep2: sweep failed — check that rails and cross-sections are compatible.");
      return Result.Failure;
    }

    // ── Step 5: add results on the resolved layer ─────────────────────────

    var layerIdx = ResolveLayerIndex(doc);
    var attribs  = new ObjectAttributes { LayerIndex = layerIdx };

    foreach (var brep in results)
    {
      if (brep != null)
        doc.Objects.AddBrep(brep, attribs);
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Rail picking helper ────────────────────────────────────────────────────

  private static Curve? PickSingleCurve(RhinoDoc doc, string prompt, RunMode mode)
  {
    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt(prompt);
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(true, true);
      go.AddOption("Layer", LayerDisplay);
      go.AddOption("Current");

      var r = go.Get();

      if (r == GetResult.Cancel || go.CommandResult() != Result.Success)
        return null;

      if (r == GetResult.Option)
      {
        var optName = go.Option()?.EnglishName;
        if (optName == "Layer")
          HandleLayerSubprompt(doc, mode);
        else if (optName == "Current")
        { _layer = string.Empty; SaveOptions(); }
        continue;
      }

      if (r == GetResult.Object)
        return go.Object(0).Curve();
    }
  }
}
