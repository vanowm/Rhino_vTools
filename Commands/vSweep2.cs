using System;
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
/// Sweep2 with a persistent Layer option and ChainEdges at every rail selection step.
/// </summary>
public sealed class vSweep2 : Command
{
  // ── Constants ──────────────────────────────────────────────────────────────

  private const string SectionName        = "vSweep2";
  private const string LayerKey           = "layer";
  private const string ChainAutoChainKey  = "chainAutoChain";
  private const string ChainContinuityKey = "chainContinuity";
  private const string ChainDirectionKey  = "chainDirection";
  private const string ChainGapTolKey     = "chainGapTol";
  private const string ChainAngTolKey     = "chainAngTol";

  private static readonly string[] ContNames = ["Position", "Tangency", "Curvature"];
  private static readonly string[] DirNames  = ["Backward", "Forward", "Both"];

  private const int DirBackward = 0, DirForward = 1; // DirBoth = 2

  // ── Persisted state ────────────────────────────────────────────────────────

  private static string _layer           = string.Empty;
  private static bool   _chainAutoChain  = false;
  private static int    _chainContinuity = 1;    // 0=Position 1=Tangency 2=Curvature
  private static int    _chainDirection  = 2;    // 0=Backward 1=Forward 2=Both
  private static double _chainGapTol     = -1.0; // -1 = use doc tolerance
  private static double _chainAngTol     = 1.0;  // degrees

  public override string EnglishName => "vSweep2";

  // ── Persistence ────────────────────────────────────────────────────────────

  private static void LoadOptions()
  {
    var v = vToolsOptionStore.Read(SectionName, section =>
    {
      var layer     = _layer;
      var autoChain = _chainAutoChain;
      var contIdx   = _chainContinuity;
      var dirIdx    = _chainDirection;
      var gapTol    = _chainGapTol;
      var angTol    = _chainAngTol;

      if (vToolsOptionStore.TryGetString(section, LayerKey,           out var s))  layer     = s;
      if (vToolsOptionStore.TryGetBool  (section, ChainAutoChainKey,  out var ac)) autoChain = ac;
      if (vToolsOptionStore.TryGetDouble(section, ChainContinuityKey, out var ci)) contIdx   = Math.Clamp((int)Math.Round(ci, MidpointRounding.AwayFromZero), 0, ContNames.Length - 1);
      if (vToolsOptionStore.TryGetDouble(section, ChainDirectionKey,  out var di)) dirIdx    = Math.Clamp((int)Math.Round(di, MidpointRounding.AwayFromZero), 0, DirNames.Length - 1);
      if (vToolsOptionStore.TryGetDouble(section, ChainGapTolKey,     out var gt)) gapTol    = gt > 0.0 ? gt : -1.0;
      if (vToolsOptionStore.TryGetDouble(section, ChainAngTolKey,     out var at)) angTol    = at > 0.0 ? at :  1.0;

      return (layer, autoChain, contIdx, dirIdx, gapTol, angTol);
    });

    _layer           = v.layer;
    _chainAutoChain  = v.autoChain;
    _chainContinuity = v.contIdx;
    _chainDirection  = v.dirIdx;
    _chainGapTol     = v.gapTol;
    _chainAngTol     = v.angTol;
  }

  private static void SaveOptions() =>
    vToolsOptionStore.Update(SectionName, section =>
    {
      section[LayerKey]           = _layer;
      section[ChainAutoChainKey]  = _chainAutoChain;
      section[ChainContinuityKey] = (double)_chainContinuity;
      section[ChainDirectionKey]  = (double)_chainDirection;
      section[ChainGapTolKey]     = _chainGapTol;
      section[ChainAngTolKey]     = _chainAngTol;
    });

  // ── Layer helpers ──────────────────────────────────────────────────────────

  private static string LayerDisplay =>
    string.IsNullOrEmpty(_layer) ? "(current)" : _layer;

  private static void HandleLayerSubprompt(RhinoDoc doc, RunMode mode)
  {
    if (mode != RunMode.Scripted)
    {
      var found  = string.IsNullOrEmpty(_layer) ? -1
                   : doc.Layers.FindByFullPath(_layer, RhinoMath.UnsetIntIndex);
      int dlgIdx = found >= 0 ? found : doc.Layers.CurrentLayerIndex;
      bool dummy = false;
      if (Dialogs.ShowSelectLayerDialog(ref dlgIdx, "Select result layer", true, false, ref dummy))
      {
        var picked = doc.Layers[dlgIdx];
        if (picked != null) { _layer = picked.FullPath; SaveOptions(); }
      }
    }
    else
    {
      var gs = new GetString();
      gs.SetCommandPrompt("Layer name (* or . = <Current>)");
      gs.AcceptNothing(true);
      if (gs.Get() == GetResult.String)
      {
        var s = (gs.StringResult() ?? string.Empty).Trim();
        _layer = (s == "*" || s == "." || string.IsNullOrWhiteSpace(s)) ? string.Empty : s;
        SaveOptions();
      }
    }
  }

  private static int ResolveLayerIndex(RhinoDoc doc)
  {
    if (string.IsNullOrEmpty(_layer)) return doc.Layers.CurrentLayerIndex;
    var idx = doc.Layers.FindByFullPath(_layer, RhinoMath.UnsetIntIndex);
    return idx >= 0 ? idx : doc.Layers.Add(new Layer { Name = _layer });
  }

  // ── RunCommand ─────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    var rail1 = PickRailCurve(doc, "Select first rail",  mode);
    if (rail1 == null) return Result.Cancel;

    var rail2 = PickRailCurve(doc, "Select second rail", mode);
    if (rail2 == null) return Result.Cancel;

    var sections = new List<Curve>();

    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt(sections.Count == 0
        ? "Select cross-section curves (Enter when done)"
        : $"Select next cross-section ({sections.Count} selected, Enter when done)");
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AcceptNothing(true);
      go.AddOption("Layer", LayerDisplay);
      go.AddOption("Current");

      var r = go.Get();
      if (r == GetResult.Cancel || go.CommandResult() != Result.Success) return Result.Cancel;

      if (r == GetResult.Option)
      {
        var n = go.Option()?.EnglishName;
        if (n == "Layer")        HandleLayerSubprompt(doc, mode);
        else if (n == "Current") { _layer = string.Empty; SaveOptions(); }
        continue;
      }

      if (r == GetResult.Nothing) break;

      if (r == GetResult.Object)
      {
        var crv = go.Object(0).Curve();
        if (crv != null) sections.Add(crv);
      }
    }

    if (sections.Count == 0)
    {
      RhinoApp.WriteLine("vSweep2: no cross-section curves selected.");
      return Result.Cancel;
    }

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

    var attribs = new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc) };
    foreach (var brep in results)
      if (brep != null) doc.Objects.AddBrep(brep, attribs);

    doc.Views.Redraw();
    return Result.Success;
  }

  // ── Rail picker (single curve or ChainEdges) ───────────────────────────────

  private static Curve? PickRailCurve(RhinoDoc doc, string prompt, RunMode mode)
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
      go.AddOption("ChainEdges");

      var r = go.Get();
      if (r == GetResult.Cancel || go.CommandResult() != Result.Success) return null;

      if (r == GetResult.Option)
      {
        var n = go.Option()?.EnglishName;
        if (n == "Layer")        { HandleLayerSubprompt(doc, mode); continue; }
        if (n == "Current")      { _layer = string.Empty; SaveOptions(); continue; }
        if (n == "ChainEdges")   return PickChainedRail(doc, prompt, mode);
        continue;
      }

      if (r == GetResult.Object) return go.Object(0).Curve();
    }
  }

  // ── ChainEdges interaction ─────────────────────────────────────────────────

  private static Curve? PickChainedRail(RhinoDoc doc, string railLabel, RunMode mode)
  {
    var autoChain = _chainAutoChain;
    var contIdx   = _chainContinuity;
    var dirIdx    = _chainDirection;
    var gapTol    = _chainGapTol <= 0.0 ? doc.ModelAbsoluteTolerance : _chainGapTol;
    var angTolDeg = _chainAngTol;

    var segIds   = new List<Guid>();     // Rhino object IDs for deduplication
    var segments = new List<Curve>();    // curve geometry in chain order
    var tips     = new List<Point3d>(); // currently open (unconnected) endpoints

    // Undo history: (tip that was consumed, tip that was opened, openedWasNew)
    var undoStack = new Stack<(Point3d consumed, Point3d opened, bool openedWasNew)>();

    while (true)
    {
      var isFirst = segments.Count == 0;
      var go = new GetObject();
      go.SetCommandPrompt(isFirst
        ? $"Select segment for {railLabel}"
        : $"Select next segment for {railLabel}. Press Enter when done");
      go.GeometryFilter  = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      if (!isFirst) go.AcceptNothing(true);

      var autoChainToggle = new OptionToggle(autoChain, "No", "Yes");
      go.AddOptionToggle("AutoChain", ref autoChainToggle);
      var idxCont = go.AddOptionList("ChainContinuity", ContNames, contIdx);
      var idxDir  = go.AddOptionList("Direction",       DirNames,  dirIdx);
      var gapOpt  = new OptionDouble(gapTol,    true, 0.0);
      var angOpt  = new OptionDouble(angTolDeg, true, 0.0);
      go.AddOptionDouble("GapTolerance",   ref gapOpt);
      go.AddOptionDouble("AngleTolerance", ref angOpt);
      int idxUndo = !isFirst ? go.AddOption("Undo") : -1;
      int idxAll  = !isFirst ? go.AddOption("All")  : -1;

      var r = go.Get();

      // Read back mutable option values
      autoChain = autoChainToggle.CurrentValue;
      if (go.Option() != null)
      {
        if (go.Option()!.Index == idxCont) contIdx   = Math.Clamp(go.Option()!.CurrentListOptionIndex, 0, ContNames.Length - 1);
        if (go.Option()!.Index == idxDir)  dirIdx    = Math.Clamp(go.Option()!.CurrentListOptionIndex, 0, DirNames.Length  - 1);
      }
      gapTol    = gapOpt.CurrentValue;
      angTolDeg = angOpt.CurrentValue;

      _chainAutoChain  = autoChain;
      _chainContinuity = contIdx;
      _chainDirection  = dirIdx;
      _chainGapTol     = gapTol;
      _chainAngTol     = angTolDeg;
      SaveOptions();

      if (r == GetResult.Cancel) return null;
      if (r == GetResult.Nothing) break; // Enter = done

      if (r == GetResult.Option)
      {
        var idx = go.Option()?.Index;
        if (idx == idxUndo && segments.Count > 0)
        {
          segments.RemoveAt(segments.Count - 1);
          segIds.RemoveAt(segIds.Count - 1);
          if (undoStack.Count > 0)
          {
            var (consumed, opened, openedWasNew) = undoStack.Pop();
            if (openedWasNew)
              for (int j = tips.Count - 1; j >= 0; j--)
                if (tips[j].DistanceTo(opened) <= gapTol) { tips.RemoveAt(j); break; }
            tips.Add(consumed);
          }
        }
        else if (idx == idxAll)
        {
          ChainAutoExtend(doc, segIds, segments, tips, contIdx, gapTol, angTolDeg);
          break;
        }
        continue;
      }

      if (r == GetResult.Object)
      {
        var picked = go.Object(0);
        var crv    = picked.Curve();
        if (crv == null) continue;

        if (isFirst)
        {
          segIds.Add(picked.ObjectId);
          segments.Add(crv);
          tips.Clear();
          var s = crv.PointAtStart;
          var e = crv.PointAtEnd;
          var pp = picked.SelectionPoint();
          bool nearStart = pp.DistanceTo(s) <= pp.DistanceTo(e);
          if (dirIdx != DirForward)  tips.Add(nearStart ? s : e);
          if (dirIdx != DirBackward) tips.Add(nearStart ? e : s);
          // Collapse duplicate tips (seed is a closed curve)
          if (tips.Count == 2 && tips[0].DistanceTo(tips[1]) < gapTol) tips.RemoveAt(1);

          if (autoChain) { ChainAutoExtend(doc, segIds, segments, tips, contIdx, gapTol, angTolDeg); break; }
        }
        else
        {
          if (segIds.Contains(picked.ObjectId))
          { RhinoApp.WriteLine("vSweep2: curve is already in the chain."); continue; }

          if (ChainAddManual(picked.ObjectId, crv, segIds, segments, tips, undoStack, gapTol))
          {
            if (autoChain) { ChainAutoExtend(doc, segIds, segments, tips, contIdx, gapTol, angTolDeg); break; }
          }
          else
            RhinoApp.WriteLine("vSweep2: selected curve is not connected to the current chain.");
        }
      }
    }

    if (segments.Count == 0) return null;
    if (segments.Count == 1) return segments[0];

    var joined = Curve.JoinCurves(segments, gapTol);
    if (joined is { Length: > 0 }) return joined[0];
    var joined2 = Curve.JoinCurves(segments, doc.ModelAbsoluteTolerance * 10.0);
    return joined2 is { Length: > 0 } ? joined2[0] : null;
  }

  // ── Chain helpers ──────────────────────────────────────────────────────────

  /// <summary>Try to append one manually-picked curve to the chain.</summary>
  private static bool ChainAddManual(
    Guid objId, Curve c,
    List<Guid> segIds, List<Curve> segments, List<Point3d> tips,
    Stack<(Point3d, Point3d, bool)> undoStack, double gapTol)
  {
    for (int i = 0; i < tips.Count; i++)
    {
      var tip = tips[i];
      Point3d? newTip = null;
      if      (c.PointAtStart.DistanceTo(tip) <= gapTol) newTip = c.PointAtEnd;
      else if (c.PointAtEnd.DistanceTo(tip)   <= gapTol) newTip = c.PointAtStart;
      if (!newTip.HasValue) continue;

      segIds.Add(objId);
      segments.Add(c);
      tips.RemoveAt(i);

      bool alreadyPresent = false;
      foreach (var t in tips)
        if (t.DistanceTo(newTip.Value) <= gapTol) { alreadyPresent = true; break; }
      if (!alreadyPresent) tips.Add(newTip.Value);
      undoStack.Push((tip, newTip.Value, !alreadyPresent));
      return true;
    }
    return false;
  }

  /// <summary>Greedily extend the chain from all open tips using doc curves.</summary>
  private static void ChainAutoExtend(
    RhinoDoc doc, List<Guid> segIds, List<Curve> segments, List<Point3d> tips,
    int contIdx, double gapTol, double angTolDeg)
  {
    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true, LockedObjects = false, HiddenObjects = false,
      IncludeLights = false, IncludeGrips = false, IncludePhantoms = false,
    };

    var candidates = new List<(Guid id, Curve crv)>();
    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj.ObjectType != ObjectType.Curve) continue;
      if (segIds.Contains(obj.Id)) continue;
      if (obj.Geometry is Curve c) candidates.Add((obj.Id, c));
    }

    bool any = true;
    while (any && tips.Count > 0)
    {
      any = false;
      for (int ci = candidates.Count - 1; ci >= 0; ci--)
      {
        var (id, c) = candidates[ci];
        for (int ti = tips.Count - 1; ti >= 0; ti--)
        {
          var tip = tips[ti];
          Point3d? newTip = null;
          if      (c.PointAtStart.DistanceTo(tip) <= gapTol) newTip = c.PointAtEnd;
          else if (c.PointAtEnd.DistanceTo(tip)   <= gapTol) newTip = c.PointAtStart;
          if (!newTip.HasValue) continue;

          if (contIdx > 0 && !ChainMeetsContinuity(c, tip, segments, gapTol, angTolDeg)) continue;

          segIds.Add(id);
          segments.Add(c);
          candidates.RemoveAt(ci);
          tips.RemoveAt(ti);

          bool alreadyPresent = false;
          foreach (var t in tips)
            if (t.DistanceTo(newTip.Value) <= gapTol) { alreadyPresent = true; break; }
          if (!alreadyPresent) tips.Add(newTip.Value);
          any = true;
          break;
        }
      }
    }
  }

  /// <summary>Check that a candidate curve meets the tangency criterion at the connection point.</summary>
  private static bool ChainMeetsContinuity(
    Curve c, Point3d connectPt, List<Curve> segments, double gapTol, double angTolDeg)
  {
    Vector3d? existingTan = null;
    foreach (var s in segments)
    {
      if      (s.PointAtEnd.DistanceTo(connectPt)   <= gapTol) existingTan =  s.TangentAtEnd;
      else if (s.PointAtStart.DistanceTo(connectPt) <= gapTol) existingTan = -s.TangentAtStart;
    }
    if (!existingTan.HasValue) return true;

    var newTan = c.PointAtStart.DistanceTo(connectPt) <= gapTol
      ?  c.TangentAtStart
      : -c.TangentAtEnd;

    var dot    = Math.Clamp(Vector3d.Multiply(existingTan.Value, newTan), -1.0, 1.0);
    var angDeg = RhinoMath.ToDegrees(Math.Acos(dot));
    return angDeg <= angTolDeg;
  }
}
