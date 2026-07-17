using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Places notches (I, V, U shapes) on one or more curves with an interactive live panel.
/// Identical to the Notches.py script, without the auto-sync daemon.
/// </summary>
public sealed class vNotches : Rhino.Commands.Command
{
  // ── Constants ────────────────────────────────────────────────────────────

  const string Section            = "vNotches";
  const string SpecialLayerCurrent = "*[Current]*";
  const double LabelWidthMult     = 0.9;
  const double DefaultLabelOffIn  = 0.1; // inches

  // ── Persisted defaults ───────────────────────────────────────────────────

  static double _notchLength    = 0.18;
  static double _notchOffset    = 0.5;
  static double _notchWidth     = 0.18;
  static string _notchType      = "I";
  static bool   _percent        = false;
  static bool   _group          = false;
  static bool   _label          = false;
  static string _labelValue     = "A";
  static double _labelSize      = 0.3;
  static bool   _labelSizeAuto  = false;
  static int    _labelSizePct   = 75;
  static string _notchLayer     = SpecialLayerCurrent;
  static string _labelLayer     = "PLOT";
  static double _labelOffset    = double.NaN; // resolved to model units on first load
  static double _labelOffsetY   = 0.0;
  static bool   _labelAutoAdv   = true;
  static bool   _labelSideFlip  = false;
  static double _multipleStartOffset = 2.0;
  static double _multipleEndOffset   = 2.0;
  static int    _multipleNumber      = 2;
  static bool[] _curveSides     = Array.Empty<bool>();
  static NotchSession? _activeSession;
  static GetPoint? _activeGetter;

  public override string EnglishName => "vNotches";

  internal static Result RunLocalHistory(RhinoDoc doc, bool redo, string source)
  {
    var session = _activeSession;
    var getter = _activeGetter;
    if (session == null || session.Doc != doc || getter == null)
      return Result.Nothing;

    GetBaseClass.PostCustomMessage(new NotchHistoryRequest(redo, source));
    vTools.Log.Write("vNotches", $"{source} {(redo ? "redo" : "undo")} requested");
    return Result.Success;
  }

  static void ApplyLocalHistory(RhinoDoc doc, NotchSession session, bool redo, string source)
  {
    if (redo)
    {
      if (session.RedoBatches.Count == 0)
      {
        RhinoApp.WriteLine("vNotches: nothing to redo.");
        vTools.Log.Write("vNotches", $"{source} redo ignored: empty stack");
        return;
      }
      RedoLastNotch(doc, session);
    }
    else
    {
      if (session.NotchRecords.Count == 0)
      {
        RhinoApp.WriteLine("vNotches: nothing to undo.");
        vTools.Log.Write("vNotches", $"{source} undo ignored: empty stack");
        return;
      }
      UndoLastNotch(doc, session);
    }

    vTools.Log.Write("vNotches",
      $"{source} {(redo ? "redo" : "undo")} handled records={session.NotchRecords.Count} redo={session.RedoBatches.Count}");
  }

  // ── Settings ─────────────────────────────────────────────────────────────

  static void LoadOptions(RhinoDoc doc)
  {
    ToolsOptionStore.Read<int>(Section, s =>
    {
      if (ToolsOptionStore.TryGetDouble(s, "notch_length",    out var v)) _notchLength   = v;
      if (ToolsOptionStore.TryGetDouble(s, "notch_offset",    out v))     _notchOffset   = v;
      if (ToolsOptionStore.TryGetDouble(s, "notch_width",     out v))     _notchWidth    = v;
      if (ToolsOptionStore.TryGetString(s, "notch_type",      out var t)) _notchType     = t;
      if (ToolsOptionStore.TryGetBool  (s, "percent",         out var b)) _percent       = b;
      if (ToolsOptionStore.TryGetBool  (s, "group",           out b))     _group         = b;
      if (ToolsOptionStore.TryGetBool  (s, "label",           out b))     _label         = b;
      if (ToolsOptionStore.TryGetString(s, "label_value",     out t))     _labelValue    = t;
      if (ToolsOptionStore.TryGetDouble(s, "label_size",      out v))     _labelSize     = v;
      if (ToolsOptionStore.TryGetBool  (s, "label_size_auto", out b))     _labelSizeAuto = b;
      if (ToolsOptionStore.TryGetDouble(s, "label_size_pct",  out var pctv))_labelSizePct  = (int)pctv;
      if (ToolsOptionStore.TryGetString(s, "notch_layer",     out t))     _notchLayer    = t;
      if (ToolsOptionStore.TryGetString(s, "label_layer",     out t))     _labelLayer    = t;
      if (ToolsOptionStore.TryGetDouble(s, "label_offset",    out v))     _labelOffset   = v;
      if (ToolsOptionStore.TryGetDouble(s, "label_offset_y",  out v))     _labelOffsetY  = v;
      if (ToolsOptionStore.TryGetBool  (s, "label_auto_adv",  out b))     _labelAutoAdv  = b;
      if (ToolsOptionStore.TryGetBool  (s, "label_side_flip", out b))     _labelSideFlip = b;
      if (ToolsOptionStore.TryGetDouble(s, "multiple_start_offset", out v)) _multipleStartOffset = Math.Max(0.0, v);
      if (ToolsOptionStore.TryGetDouble(s, "multiple_end_offset",   out v)) _multipleEndOffset   = Math.Max(0.0, v);
      if (ToolsOptionStore.TryGetDouble(s, "multiple_number",       out v)) _multipleNumber      = Math.Clamp((int)Math.Round(v), 2, 10000);
      if (s?["curve_sides"] is System.Text.Json.Nodes.JsonArray arr)
      {
        var sides = new List<bool>();
        foreach (var el in arr)
          if (el is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<bool>(out var bv)) sides.Add(bv);
        _curveSides = sides.ToArray();
      }
      return 0;
    });
    if (double.IsNaN(_labelOffset))
      _labelOffset = ModelUnitsFromInches(doc, DefaultLabelOffIn);
  }

  static void SaveOptions(NotchSession s)
{
  UpdateStaticDefaultsFromSession(s);

  bool ok = ToolsOptionStore.Update(Section, sec =>
  {
    sec["notch_length"]    = _notchLength;
    sec["notch_offset"]    = _notchOffset;
    sec["notch_width"]     = _notchWidth;
    sec["notch_type"]      = _notchType;
    sec["percent"]         = _percent;
    sec["group"]           = _group;
    sec["label"]           = _label;
    sec["label_value"]     = _labelValue;
    sec["label_size"]      = _labelSize;
    sec["label_size_auto"] = _labelSizeAuto;
    sec["label_size_pct"]  = _labelSizePct;
    sec["notch_layer"]     = _notchLayer;
    sec["label_layer"]     = _labelLayer;
    sec["label_offset"]    = _labelOffset;
    sec["label_offset_y"]  = _labelOffsetY;
    sec["label_auto_adv"]  = _labelAutoAdv;
    sec["label_side_flip"] = _labelSideFlip;
    sec["multiple_start_offset"] = _multipleStartOffset;
    sec["multiple_end_offset"]   = _multipleEndOffset;
    sec["multiple_number"]       = _multipleNumber;

    var arr = new System.Text.Json.Nodes.JsonArray();
    foreach (var b in _curveSides) arr.Add(b);
    sec["curve_sides"] = arr;
  });

  if (!ok)
    RhinoApp.WriteLine($"vNotches: failed to save options: {ToolsOptionStore.LastError}");
}

static void UpdateStaticDefaultsFromSession(NotchSession s)
{
  _notchLength   = s.NotchLengthOpt.CurrentValue;
  _notchOffset   = s.NotchOffsetOpt.CurrentValue;
  _notchWidth    = s.NotchWidthOpt.CurrentValue;
  _notchType     = s.NotchTypeValues[s.NotchTypeIndex];

  _percent       = s.PercentToggle.CurrentValue;
  _group         = s.GroupToggle.CurrentValue;
  _label         = s.LabelToggle.CurrentValue;
  _labelValue    = s.LabelValueText;

  _labelSize     = s.ManualLabelSize;
  _labelSizeAuto = s.LabelSizeAutoToggle.CurrentValue;
  _labelSizePct  = s.LabelSizePctValues[s.LabelSizePctIndex];

  _notchLayer    = s.NotchLayerName;
  _labelLayer    = s.LabelLayerName;
  _labelOffset   = s.LabelOffsetOpt.CurrentValue;
  _labelOffsetY  = s.LabelOffsetYOpt.CurrentValue;

  _labelAutoAdv  = s.LabelAutoAdv;
  _labelSideFlip = s.LabelSideFlip;
  _multipleStartOffset = s.MultipleStartOffset;
  _multipleEndOffset   = s.MultipleEndOffset;
  _multipleNumber      = s.MultipleNumber;
  _curveSides    = s.CurveSides.ToArray();
}
  // ── Entry point ───────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions(doc);

    if (!TrySelectCurves(doc, out var curves, out var curveIds))
      return Result.Cancel;

    // Print curve lengths
    if (curves.Count == 1)
    {
      RhinoApp.WriteLine("Curve length: " + FormatFractionalInches(doc, curves[0].GetLength()));
    }
    else
    {
      var la = curves[0].GetLength();
      var lb = curves[1].GetLength();
      var diff = Math.Abs(la - lb);
      if (diff <= doc.ModelAbsoluteTolerance)
        RhinoApp.WriteLine("Curve length: " + FormatFractionalInches(doc, la));
      else
      {
        RhinoApp.WriteLine("Curve 1 length: " + FormatFractionalInches(doc, la));
        RhinoApp.WriteLine("Curve 2 length: " + FormatFractionalInches(doc, lb));
        RhinoApp.WriteLine("Length difference: " + FormatFractionalInches(doc, diff));
      }
    }

    // Build initial curve sides â€” reuse stored per-curve values if count matches
    var initialSides = new bool[curves.Count];
    for (int i = 0; i < curves.Count; i++)
    {
      if (i < _curveSides.Length) initialSides[i] = _curveSides[i];
      else if (_curveSides.Length > 0) initialSides[i] = _curveSides[_curveSides.Length - 1];
      else initialSides[i] = false; // false = Right
    }

    var session = new NotchSession(doc, curves, curveIds, initialSides,
      _notchLength, _notchOffset, _notchWidth, _notchType,
      _percent, _group, _label, _labelValue,
      _labelSize, _labelSizeAuto, _labelSizePct,
      _notchLayer, _labelLayer, _labelOffset, _labelOffsetY,
      _labelAutoAdv, _labelSideFlip,
      _multipleStartOffset, _multipleEndOffset, _multipleNumber);

    RunLoop(doc, session);
    SaveOptions(session);

    // Deselect source curves
    foreach (var id in session.CurveIds)
      doc.Objects.FindId(id)?.Select(false);
    doc.Views.Redraw();

    return Result.Success;
  }

  // ── Curve selection ───────────────────────────────────────────────────────

  static bool TrySelectCurves(RhinoDoc doc, out List<Curve> curves, out List<Guid> curveIds)
  {
    curves    = new List<Curve>();
    curveIds  = new List<Guid>();
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select one or more curves (near start)");
    go.GeometryFilter = ObjectType.Curve;
    go.GroupSelect = false;
    go.EnablePreSelect(true, true);
    var res = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success || res != GetResult.Object)
      return false;
    for (int i = 0; i < go.ObjectCount; i++)
    {
      var crv = go.Object(i).Curve();
      if (crv == null) return false;
      var pick = go.Object(i).SelectionPoint();
      crv = OrientCurveToPickPoint(crv, pick);
      curves.Add(crv);
      curveIds.Add(go.Object(i).ObjectId);
    }
    return curves.Count > 0;
  }

  static bool TryUpdateCurveSelection(RhinoDoc doc, NotchSession s)
  {
    var sideSequence = s.CurveSides.ToArray();
    vTools.Log.Write("vNotches", $"curve selection begin: {DescribeCurveSides(s)}");

    var generatedIds = s.NotchIdsByCurve
      .SelectMany(ids => ids)
      .Concat(s.NotchRecords.SelectMany(record => record.DetachedNotchIds))
      .Where(id => id != Guid.Empty)
      .ToHashSet();

    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Add or remove curves. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = false;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);
    go.EnablePostSelect(true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;
    go.AlreadySelectedObjectSelect = true;
    go.SetCustomGeometryFilter((obj, _, _) => !generatedIds.Contains(obj.Id));

    bool preselectedWaitingForEnter = false;
    while (true)
    {
      var result = go.GetMultiple(0, 0);
      if (result == GetResult.Cancel || go.CommandResult() != Result.Success)
      {
        SelectBothCurves(doc, s);
        return false;
      }

      if (result == GetResult.Object && go.ObjectsWerePreselected && !preselectedWaitingForEnter)
      {
        preselectedWaitingForEnter = true;
        go.EnablePreSelect(false, true);
        continue;
      }

      if (result is GetResult.Object or GetResult.Nothing)
        break;
    }

    var selectedPool = doc.Objects.GetSelectedObjects(false, false)
      .Where(obj => obj.Geometry is Curve && !generatedIds.Contains(obj.Id))
      .ToList();
    if (selectedPool.Count == 0)
    {
      RhinoApp.WriteLine("vNotches: keep at least one curve selected.");
      SelectBothCurves(doc, s);
      return false;
    }

    var selectionPoints = new Dictionary<Guid, Point3d>();
    var getObjectOrder = new List<Guid>();
    for (int i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef == null || objRef.ObjectId == Guid.Empty)
        continue;
      var point = objRef.SelectionPoint();
      if (point != Point3d.Unset)
        selectionPoints[objRef.ObjectId] = point;
      getObjectOrder.Add(objRef.ObjectId);
    }

    var selectedById = selectedPool.ToDictionary(obj => obj.Id);
    var getSelectionOrder = new List<Guid>();
    var getSelectionIds = new HashSet<Guid>();
    foreach (var id in getObjectOrder)
      if (selectedById.ContainsKey(id) && getSelectionIds.Add(id))
        getSelectionOrder.Add(id);

    bool sameCurveSet = selectedById.Count == s.CurveIds.Count &&
      selectedById.Keys.ToHashSet().SetEquals(s.CurveIds);
    bool explicitlyReselected = getSelectionOrder.Count > 0 &&
      getSelectionOrder.All(selectionPoints.ContainsKey);
    bool sequenceChanged = sameCurveSet &&
      explicitlyReselected &&
      getSelectionOrder.Count == s.CurveIds.Count &&
      !getSelectionOrder.SequenceEqual(s.CurveIds);
    bool clickedEndChanged = sameCurveSet && selectionPoints.Any(entry =>
    {
      int curveIndex = s.CurveIds.IndexOf(entry.Key);
      return curveIndex >= 0 && curveIndex < s.Curves.Count &&
        PickTargetsCurveEnd(s.Curves[curveIndex], entry.Value);
    });
    bool selectionDefinitionChanged = sequenceChanged || clickedEndChanged;

    var orientedCurvesById = new Dictionary<Guid, Curve>();
    for (int i = 0; i < s.CurveIds.Count && i < s.Curves.Count; i++)
      orientedCurvesById[s.CurveIds[i]] = s.Curves[i].DuplicateCurve();

    var orderedIds = new HashSet<Guid>();
    var selectedObjects = new List<RhinoObject>();

    // Retained curves keep their existing sequence. Newly selected curves follow
    // GetObject's click order instead of document object-table order.
    if (!sequenceChanged)
      foreach (var id in s.CurveIds)
        if (selectedById.TryGetValue(id, out var retained) && orderedIds.Add(id))
          selectedObjects.Add(retained);
    foreach (var id in getSelectionOrder)
      if (selectedById.TryGetValue(id, out var selected) && orderedIds.Add(id))
        selectedObjects.Add(selected);
    foreach (var selected in selectedPool)
      if (orderedIds.Add(selected.Id))
        selectedObjects.Add(selected);

    vTools.Log.Write("vNotches", "selection order: " + string.Join(", ",
      selectedObjects.Select((obj, i) => $"{i + 1}:{obj.Id.ToString("N")[..8]}")) +
      $" sameSet={sameCurveSet} reselected={explicitlyReselected} " +
      $"sequenceChanged={sequenceChanged} clickedEndChanged={clickedEndChanged}");

    var selectedIds = selectedObjects.Select(obj => obj.Id).ToHashSet();
    bool changed = false;
    var removedIndices = Enumerable.Range(0, s.CurveIds.Count)
      .Where(i => selectionDefinitionChanged || !selectedIds.Contains(s.CurveIds[i]))
      .ToList();
    for (int removed = removedIndices.Count - 1; removed >= 0; removed--)
    {
      RemoveSessionCurve(s, removedIndices[removed]);
      changed = true;
    }

    var retainedIds = s.CurveIds.ToHashSet();
    foreach (var rhObj in selectedObjects)
    {
      if (retainedIds.Contains(rhObj.Id) || rhObj.Geometry is not Curve sourceCurve)
        continue;

      Curve curve;
      if (selectionPoints.TryGetValue(rhObj.Id, out var pick))
      {
        curve = sourceCurve.DuplicateCurve();
        curve = OrientCurveToPickPoint(curve, pick, out bool reversed);
        vTools.Log.Write("vNotches",
          $"curve {s.Curves.Count + 1} picked {(reversed ? "end" : "start")}");
      }
      else if (orientedCurvesById.TryGetValue(rhObj.Id, out var orientedCurve))
      {
        curve = orientedCurve.DuplicateCurve();
        vTools.Log.Write("vNotches",
          $"curve {s.Curves.Count + 1} retained its current start");
      }
      else
      {
        curve = sourceCurve.DuplicateCurve();
        vTools.Log.Write("vNotches",
          $"curve {s.Curves.Count + 1} has no selection point; source direction retained");
      }
      AddSessionCurve(s, rhObj, curve);
      retainedIds.Add(rhObj.Id);
      changed = true;
    }

    var sidesBeforeRestore = s.CurveSides.ToArray();
    ApplyCurveSideSequence(s, sideSequence);
    for (int i = 0; i < s.CurveSides.Length; i++)
      if (i >= sidesBeforeRestore.Length || s.CurveSides[i] != sidesBeforeRestore[i])
        RebuildCurveNotches(doc, s, i);
    if (changed)
      s.RedoBatches.Clear();
    SaveOptions(s);
    vTools.Log.Write("vNotches", $"curve selection end: {DescribeCurveSides(s)}");
    SelectBothCurves(doc, s);
    s.PreviewValid = false;
    s.PreviewLengthsFromStart.Clear();
    return changed;
  }

  static void RemoveSessionCurve(NotchSession s, int curveIndex)
  {
    if (curveIndex < 0 || curveIndex >= s.Curves.Count)
      return;

    var notchIds = s.NotchIdsByCurve[curveIndex];
    var labelIds = s.LabelIdsByCurve[curveIndex];
    for (int recordIndex = 0; recordIndex < s.NotchRecords.Count; recordIndex++)
    {
      var record = s.NotchRecords[recordIndex];
      if (recordIndex < notchIds.Count && notchIds[recordIndex] != Guid.Empty)
        record.DetachedNotchIds.Add(notchIds[recordIndex]);
      Guid? labelId = recordIndex < labelIds.Count ? labelIds[recordIndex] : null;
      if (labelId.HasValue && labelId.Value != Guid.Empty)
        record.DetachedLabelIds.Add(labelId.Value);
    }

    s.NotchIdsByCurve.RemoveAt(curveIndex);
    s.LabelIdsByCurve.RemoveAt(curveIndex);
    foreach (var ids in s.PlacementIds)
      if (curveIndex < ids.Count) ids.RemoveAt(curveIndex);
    foreach (var ids in s.PlacementLabelIds)
      if (curveIndex < ids.Count) ids.RemoveAt(curveIndex);
    foreach (var record in s.NotchRecords)
    {
      if (curveIndex < record.LengthsFromStart.Count) record.LengthsFromStart.RemoveAt(curveIndex);
      if (curveIndex < record.CurveEnabled.Count) record.CurveEnabled.RemoveAt(curveIndex);
      if (curveIndex < record.LabelValues.Count) record.LabelValues.RemoveAt(curveIndex);
    }

    s.Curves.RemoveAt(curveIndex);
    s.CurveIds.RemoveAt(curveIndex);
    s.CurveSides = s.CurveSides.Where((_, i) => i != curveIndex).ToArray();
    s.CurveEnabled = s.CurveEnabled.Where((_, i) => i != curveIndex).ToArray();
    s.SessionGroupIndices = s.SessionGroupIndices.Where((_, i) => i != curveIndex).ToArray();
    s.CurveContextGroupIndices = s.CurveContextGroupIndices.Where((_, i) => i != curveIndex).ToArray();
  }

  static void AddSessionCurve(NotchSession s, RhinoObject rhObj, Curve curve)
  {
    int priorCurveCount = s.Curves.Count;
    bool initialSide = priorCurveCount > 0 && s.CurveSides[^1];
    var groups = rhObj.Attributes.GetGroupList();
    int contextGroup = groups != null && groups.Length > 0 ? groups[0] : -1;

    s.Curves.Add(curve);
    s.CurveIds.Add(rhObj.Id);
    s.CurveSides = s.CurveSides.Append(initialSide).ToArray();
    s.CurveEnabled = s.CurveEnabled.Append(true).ToArray();
    s.SessionGroupIndices = s.SessionGroupIndices.Append(-1).ToArray();
    s.CurveContextGroupIndices = s.CurveContextGroupIndices.Append(contextGroup).ToArray();

    int recordCount = s.NotchRecords.Count;
    s.NotchIdsByCurve.Add(Enumerable.Repeat(Guid.Empty, recordCount).ToList());
    s.LabelIdsByCurve.Add(Enumerable.Repeat<Guid?>(null, recordCount).ToList());
    foreach (var ids in s.PlacementIds) ids.Add(Guid.Empty);
    foreach (var ids in s.PlacementLabelIds) ids.Add(null);

    foreach (var record in s.NotchRecords)
    {
      while (record.LengthsFromStart.Count < priorCurveCount) record.LengthsFromStart.Add(0.0);
      while (record.CurveEnabled.Count < priorCurveCount) record.CurveEnabled.Add(false);
      while (record.LabelValues.Count < priorCurveCount) record.LabelValues.Add("");
      record.LengthsFromStart.Add(0.0);
      record.CurveEnabled.Add(false);
      record.LabelValues.Add("");
    }
  }

  static void ApplyCurveSideSequence(NotchSession s, IReadOnlyList<bool> sideSequence)
  {
    bool fallback = sideSequence.Count > 0 && sideSequence[^1];
    s.CurveSides = Enumerable.Range(0, s.Curves.Count)
      .Select(i => i < sideSequence.Count ? sideSequence[i] : fallback)
      .ToArray();
  }

  static string DescribeCurveSides(NotchSession s)
  {
    var values = new List<string>();
    for (int i = 0; i < s.CurveIds.Count && i < s.CurveSides.Length; i++)
      values.Add($"{i + 1}:{s.CurveIds[i].ToString("N")[..8]}={(s.CurveSides[i] ? "Left" : "Right")}");
    return values.Count > 0 ? string.Join(", ", values) : "none";
  }

  static void RebuildPanelForCurves(RhinoDoc doc, NotchSession s)
  {
    var oldPanel = s.Panel;
    Eto.Drawing.Point? location = oldPanel?.Location;
    if (oldPanel != null)
    {
      try { oldPanel.CommitPendingValues(); } catch { }
      s.SuppressPanelCloseExit = true;
      try { oldPanel.Close(); } catch { }
      finally { s.SuppressPanelCloseExit = false; }
    }

    var newPanel = new NotchPanel(doc, s);
    if (location.HasValue)
      newPanel.Location = location.Value;
    newPanel.Show();
    s.Panel = newPanel;
    SyncPanelFromOptions(s);
  }

  // ── Main interactive loop ─────────────────────────────────────────────────

  static void RunLoop(RhinoDoc doc, NotchSession s)
  {
    var gp = new GetPoint();
    gp.EnableTransparentCommands(true);
    gp.AcceptCustomMessage(true);
    gp.MouseMove += (_, e) =>
    {
      try
      {
        var vp = e.Viewport;
        if (vp == null)
          return;

        if (!vp.GetFrustumLine(e.WindowPoint.X, e.WindowPoint.Y, out var line))
          return;

        var cplane = vp.ConstructionPlane();
        var plane = new Plane(cplane.Origin, cplane.ZAxis);

        if (Rhino.Geometry.Intersect.Intersection.LinePlane(line, plane, out var t))
          s.LastCursorPoint = line.PointAt(t);
      }
      catch
      {
      }
    };
    gp.SetCommandPrompt(s.Curves.Count == 1
      ? "Select a point on curve (notch location)"
      : "Select a point on any selected curve (notch location)");

    // Live preview
    gp.DynamicDraw += (sender, e) => DrawPreview(doc, s, e);

    // Show panel
    var panel = new NotchPanel(doc, s);
    panel.Show();
    s.Panel = panel;
    SyncPanelFromOptions(s);
    UpdateDistanceLabels(s, null, null, null);

    EventHandler<CommandEventArgs> commandEnded = (_, e) =>
    {
      if (!string.Equals(e.CommandEnglishName, "Redo", StringComparison.OrdinalIgnoreCase))
        return;
      if (e.Document != null && e.Document != doc)
        return;
      s.TransparentRedoRequested = true;
      vTools.Log.Write("vNotches",
        $"Rhino Redo ended result={e.CommandResult} localRedo={s.RedoBatches.Count}");
    };
    Rhino.Commands.Command.EndCommand += commandEnded;
    _activeSession = s;
    _activeGetter = gp;
    NotchShortcutSession? shortcutSession = null;

    try
    {
      shortcutSession = new NotchShortcutSession();
      while (true)
      {
        if (s.PanelClosedExit) { FinalizeBlocks(doc, s); return; }

        gp.SetCommandPrompt(s.Curves.Count == 1
          ? "Select a point on curve (notch location)"
          : "Select a point on any selected curve (notch location)");
        RefreshCommandOptions(gp, s);
        var result = gp.Get();

        if (result == GetResult.CustomMessage && gp.CustomMessage() is NotchHistoryRequest historyRequest)
        {
          ApplyLocalHistory(doc, s, historyRequest.Redo, historyRequest.Source);
          continue;
        }

        if (s.TransparentRedoRequested)
        {
          s.TransparentRedoRequested = false;
          RedoLastNotch(doc, s);
          continue;
        }

        if (s.CurveSelectionRequested)
        {
          s.CurveSelectionRequested = false;
          if (TryUpdateCurveSelection(doc, s))
            RebuildPanelForCurves(doc, s);
          continue;
        }
        if (s.RefreshCommandLine) { s.RefreshCommandLine = false; continue; }
        if (s.PanelClosedExit)    { FinalizeBlocks(doc, s); return; }

        if (result == GetResult.Undo)
        {
          UndoLastNotch(doc, s);
          continue;
        }

        if (gp.CommandResult() != Result.Success) { FinalizeBlocks(doc, s); return; }

        if (result == GetResult.Nothing)
        {
          if (s.PanelNumericPending) { s.PanelNumericPending = false; continue; }
          if (s.IgnoreNextNothing)   { s.IgnoreNextNothing   = false; continue; }
          // Enter pressed on command line â€” done
          FinalizeBlocks(doc, s);
          return;
        }

        if (result == GetResult.Option)
        {
          HandleOption(doc, gp, s);
          SyncPanelFromOptions(s);
          continue;
        }

        if (result != GetResult.Point)
        {
          FinalizeBlocks(doc, s);
          return;
        }

        // Point picked â€” place notch(es)
        PlaceNotchFromPreview(doc, gp.Point(), s);
      }
    }
    finally
    {
      Rhino.Commands.Command.EndCommand -= commandEnded;
      shortcutSession?.Dispose();
      if (ReferenceEquals(_activeSession, s))
        _activeSession = null;
      if (ReferenceEquals(_activeGetter, gp))
        _activeGetter = null;
      FinalizeBlocks(doc, s);
      var currentPanel = s.Panel;
      if (currentPanel != null)
      {
        try { currentPanel.CommitPendingValues(); } catch { }
        s.SuppressPanelCloseExit = true;
        try { currentPanel.Close(); } catch { }
        s.Panel = null;
      }
    }
  }

  // ── Command options ───────────────────────────────────────────────────────

  static void RefreshCommandOptions(GetPoint gp, NotchSession s)
  {
    gp.ClearCommandOptions();
    s.SideOptionIndex        = gp.AddOption("Side");
    s.ReverseOptionIndex     = gp.AddOption("Reverse");
    s.UndoOptionIndex        = s.NotchRecords.Count > 0
      ? gp.AddOption("Undo", string.Empty, true)
      : -1;
    s.RedoOptionIndex        = s.RedoBatches.Count > 0
      ? gp.AddOption("Redo", string.Empty, true)
      : -1;
    s.TypeOptionIndex        = gp.AddOptionList("NotchType", s.NotchTypeValues, s.NotchTypeIndex);
    s.NotchLayerOptionIndex  = gp.AddOption("NotchLayer", s.NotchLayerName);
    gp.AddOptionDouble("NotchLength", ref s.NotchLengthOpt);
    gp.AddOptionDouble("NotchWidth", ref s.NotchWidthOpt);
    gp.AddOptionDouble("NotchOffset", ref s.NotchOffsetOpt);
    gp.AddOptionToggle("LabelEnabled", ref s.LabelToggle);
    s.LabelValueOptionIndex  = gp.AddOption("Label", s.LabelValueText);
    s.LabelLayerOptionIndex = gp.AddOption("LabelLayer", s.LabelLayerName);
    s.LabelSizeAutoIndex = gp.AddOptionToggle("LabelSizeMode", ref s.LabelSizeAutoToggle);
    s.LabelSizePctIndex2 = gp.AddOptionList("LabelSizePct", s.LabelSizePctTexts, s.LabelSizePctIndex);
    s.LabelSizeOpt.CurrentValue = s.ManualLabelSize;
    gp.AddOptionDouble("LabelSize", ref s.LabelSizeOpt);
    gp.AddOptionDouble("LabelOffsetX", ref s.LabelOffsetOpt);
    gp.AddOptionDouble("LabelOffsetY", ref s.LabelOffsetYOpt);
    gp.AddOptionToggle("NotchPercent", ref s.PercentToggle);
    gp.AddOptionToggle("NotchGroup", ref s.GroupToggle);
  }

  static void HandleOption(RhinoDoc doc, GetPoint gp, NotchSession s)
  {
    var opt = gp.Option();
    if (opt == null) return;
    int idx = opt.Index;

    // Compute which curve is nearest to last preview point for Side/Reverse
    Point3d? cursor = s.LastPreviewPoint;

    if (idx == s.SideOptionIndex)
    {
      int ci = cursor.HasValue ? ClosestCurveIndex(s, cursor.Value) : 0;
      ToggleCurveSide(doc, s, ci);
    }
    else if (idx == s.ReverseOptionIndex)
    {
      int ci = cursor.HasValue ? ClosestCurveIndex(s, cursor.Value) : 0;
      ReverseCurve(doc, s, ci);
    }
    else if (idx == s.UndoOptionIndex)
    {
      UndoLastNotch(doc, s);
    }
    else if (idx == s.RedoOptionIndex)
    {
      RedoLastNotch(doc, s);
    }
    else if (idx == s.TypeOptionIndex)
    {
      s.NotchTypeIndex = opt.CurrentListOptionIndex;
    }
    else if (idx == s.NotchLayerOptionIndex)
    {
      RhinoGet.GetString("Notch layer", false, ref s.NotchLayerName);
    }
    else if (idx == s.LabelValueOptionIndex)
    {
      RhinoGet.GetString("Label value", false, ref s.LabelValueText);
    }
    else if (idx == s.LabelLayerOptionIndex)
    {
      RhinoGet.GetString("Label layer", false, ref s.LabelLayerName);
    }
    else if (idx == s.LabelSizeAutoIndex)
    {
      // already toggled by RhinoCommon option machinery
    }
    else if (idx == s.LabelSizePctIndex2)
    {
      s.LabelSizePctIndex = opt.CurrentListOptionIndex;
    }

    s.ManualLabelSize = s.LabelSizeOpt.CurrentValue;
  }

  // ── Place notch at clicked point ──────────────────────────────────────────

  static void PlaceNotchAtPoint(RhinoDoc doc, Point3d point, NotchSession s)
  {
    ClosestCurveHit(s, point, out int refIdx, out var refCurve, out double refT);
    if (refCurve == null) return;

    s.PreviewRefCurveIndex = refIdx;

    double lengthFromStart = LengthFromStart(refCurve, refT);

    List<double> lengthsFromStart;

    if (s.PercentToggle.CurrentValue)
    {
      double refLen = refCurve.GetLength();
      if (refLen <= 0.0) return;

      double pct = lengthFromStart / refLen;
      lengthsFromStart = s.Curves.Select(c => c.GetLength() * pct).ToList();
    }
    else
    {
      lengthsFromStart = Enumerable.Repeat(lengthFromStart, s.Curves.Count).ToList();
    }

    PlaceNotchWithLengths(doc, s, lengthsFromStart, s.LastCursorPoint ?? point);
  }
  static void PlaceNotchFromPreview(RhinoDoc doc, Point3d clickedPoint, NotchSession s)
  {
    if (!s.PreviewValid || s.PreviewLengthsFromStart.Count != s.Curves.Count)
    {
      PlaceNotchAtPoint(doc, clickedPoint, s);
      return;
    }

    PlaceNotchWithLengths(doc, s, s.PreviewLengthsFromStart, s.PreviewCursorPoint);
  }

  static bool PlaceNotchWithLengths(RhinoDoc doc, NotchSession s,
    List<double> lengthsFromStart, Point3d? cursorPoint,
    bool allowLabel = true, bool manageUndo = true, bool advanceLabel = true,
    bool updateUi = true, bool usePercentMode = true, Guid? batchId = null)
  {
    var sides       = s.CurveSidesAsStrings();
    double notchLen = s.NotchLengthOpt.CurrentValue;
    double notchOff = s.NotchOffsetOpt.CurrentValue;
    string notchTyp = s.NotchTypeValues[s.NotchTypeIndex];
    double notchWid = s.NotchWidthOpt.CurrentValue;
    double resolvedLabelSize = EffectiveLabelSize(s);

    string effectiveNotchLayer = EffectiveLayerName(doc, s.NotchLayerName, s.NotchLayerName);
    string effectiveLabelLayer = EffectiveLayerName(doc, s.LabelLayerName, s.NotchLayerName);

    var activeGroupIndices = s.GroupToggle.CurrentValue
      ? s.SessionGroupIndices
      : s.CurveContextGroupIndices;

    string labelText = s.LabelValueText.Trim();
    bool canLabel    = allowLabel && s.LabelToggle.CurrentValue && labelText.Length > 0;
    string nextLabel = labelText;

    var placementLabels = new List<string>();
    if (canLabel)
    {
      foreach (var _ in s.Curves)
        placementLabels.Add(labelText);

      if (s.LabelAutoAdv)
        nextLabel = IncrementLabelValue(labelText);
    }

    double? percent = null;
    if (usePercentMode && s.PercentToggle.CurrentValue &&
        s.PreviewRefCurveIndex >= 0 && s.PreviewRefCurveIndex < s.Curves.Count)
    {
      var refCurve = s.Curves[s.PreviewRefCurveIndex];
      var refLen = refCurve.GetLength();
      if (refLen > 0.0 && s.PreviewRefCurveIndex < lengthsFromStart.Count)
        percent = lengthsFromStart[s.PreviewRefCurveIndex] / refLen;
    }

    uint undoRec = 0;
    bool undoStarted = false;
    if (manageUndo)
    {
      doc.UndoRecordingEnabled = true;
      undoRec = doc.BeginUndoRecord("Notch");
      undoStarted = true;
    }

    List<(Guid notch, Guid? label)>? newIds = null;
    try
    {
      newIds = AddNotchesPerCurve(doc, s, sides, activeGroupIndices,
        lengthsFromStart, notchLen, notchOff, notchTyp, notchWid,
        canLabel, placementLabels, resolvedLabelSize,
        effectiveNotchLayer, effectiveLabelLayer,
        s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue,
        s.LabelSideFlip, cursorPoint, s.CurveEnabled);
    }
    finally
    {
      if (undoStarted)
        doc.EndUndoRecord(undoRec);
    }

    if (newIds == null || !newIds.Any(n => n.notch != Guid.Empty))
      return false;

    s.RedoBatches.Clear();
    var record = new NotchRecord
    {
      BatchId          = batchId ?? Guid.NewGuid(),
      Mode             = usePercentMode && s.PercentToggle.CurrentValue ? "percent" : "distance",
      NotchLength      = notchLen,
      NotchOffset      = notchOff,
      NotchType        = notchTyp,
      NotchWidth       = notchWid,
      GroupEnabled     = s.GroupToggle.CurrentValue,
      LabelEnabled     = canLabel,
      LabelValues      = new List<string>(placementLabels),
      LabelSize        = resolvedLabelSize,
      NotchLayer       = s.NotchLayerName,
      LabelLayer       = s.LabelLayerName,
      LabelOffset      = s.LabelOffsetOpt.CurrentValue,
      LabelOffsetY     = s.LabelOffsetYOpt.CurrentValue,
      LengthsFromStart = new List<double>(lengthsFromStart),
      CurveEnabled     = s.CurveEnabled.ToList(),
      Percent          = percent,
    };

    s.NotchRecords.Add(record);

    for (int i = 0; i < newIds.Count; i++)
    {
      if (i < s.NotchIdsByCurve.Count)
        s.NotchIdsByCurve[i].Add(newIds[i].notch);

      if (i < s.LabelIdsByCurve.Count)
        s.LabelIdsByCurve[i].Add(newIds[i].label);
    }

    s.PlacementIds.Add(new List<Guid>(newIds.Select(n => n.notch)));
    s.PlacementLabelIds.Add(new List<Guid?>(newIds.Select(n => n.label)));

    if (canLabel && s.LabelAutoAdv && advanceLabel)
    {
      s.LabelValueText = nextLabel;
      SyncPanelFromOptions(s);
    }

    if (updateUi)
    {
      s.Panel?.UpdateUndoEnabled();
      doc.Views.Redraw();
    }

    return true;
  }

  static void PlaceMultipleNotches(RhinoDoc doc, NotchSession s)
  {
    int count = Math.Clamp(s.MultipleNumber, 2, 10000);
    double startOffset = Math.Max(0.0, s.MultipleStartOffset);
    double endOffset = Math.Max(0.0, s.MultipleEndOffset);
    var activeCurveIndices = Enumerable.Range(0, s.Curves.Count)
      .Where(i => i >= s.CurveEnabled.Length || s.CurveEnabled[i])
      .ToList();

    if (activeCurveIndices.Count == 0)
    {
      RhinoApp.WriteLine("vNotches: enable at least one curve before adding multiple notches.");
      return;
    }

    foreach (int curveIndex in activeCurveIndices)
    {
      double available = s.Curves[curveIndex].GetLength() - startOffset - endOffset;
      if (available <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine(
          $"vNotches: start and end offsets leave no usable distance on curve {curveIndex + 1}.");
        return;
      }
    }

    string originalLabel = s.LabelValueText;
    bool labelActive = s.LabelToggle.CurrentValue && originalLabel.Trim().Length > 0;
    bool firstPlacementAdded = false;
    int placementsAdded = 0;
    var batchId = Guid.NewGuid();

    doc.UndoRecordingEnabled = true;
    uint undoRec = doc.BeginUndoRecord("Multiple notches");
    try
    {
      for (int notchIndex = 0; notchIndex < count; notchIndex++)
      {
        double ratio = (double)notchIndex / (count - 1);
        var lengths = s.Curves
          .Select(curve => startOffset + (curve.GetLength() - startOffset - endOffset) * ratio)
          .ToList();

        bool added = PlaceNotchWithLengths(doc, s, lengths, null,
          allowLabel: notchIndex == 0,
          manageUndo: false,
          advanceLabel: false,
          updateUi: false,
          usePercentMode: false,
          batchId: batchId);
        if (!added)
          continue;

        placementsAdded++;
        if (notchIndex == 0)
          firstPlacementAdded = true;
      }
    }
    finally
    {
      doc.EndUndoRecord(undoRec);
    }

    if (placementsAdded == 0)
      return;

    if (firstPlacementAdded && labelActive && s.LabelAutoAdv)
      s.LabelValueText = IncrementLabelValue(originalLabel.Trim());

    SyncPanelFromOptions(s);
    s.Panel?.UpdateUndoEnabled();
    doc.Views.Redraw();
  }
  // ── Undo ──────────────────────────────────────────────────────────────────

  static void UndoLastNotch(RhinoDoc doc, NotchSession s)
  {
    if (s.NotchRecords.Count == 0) return;
    int removeCount = 1;
    if (s.NotchRecords[^1].BatchId != Guid.Empty)
    {
      Guid batchId = s.NotchRecords[^1].BatchId;
      removeCount = 0;
      for (int i = s.NotchRecords.Count - 1; i >= 0 && s.NotchRecords[i].BatchId == batchId; i--)
        removeCount++;
    }
    removeCount = Math.Min(removeCount, s.NotchRecords.Count);
    int firstRecordIndex = s.NotchRecords.Count - removeCount;
    var undoBatch = CaptureUndoBatch(doc, s, firstRecordIndex, removeCount);

    var removedRecords = s.NotchRecords
      .Skip(firstRecordIndex)
      .ToList();
    string restoredLabel = removedRecords
      .Where(rec => rec.LabelEnabled)
      .SelectMany(rec => rec.LabelValues)
      .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
    if (restoredLabel.Length > 0)
      s.LabelValueText = restoredLabel;

    for (int n = 0; n < removeCount; n++)
    {
      var lastIds = s.PlacementIds.Count > 0 ? s.PlacementIds[^1] : [];
      var lastLabelIds = s.PlacementLabelIds.Count > 0 ? s.PlacementLabelIds[^1] : [];
      if (s.PlacementIds.Count > 0)
        s.PlacementIds.RemoveAt(s.PlacementIds.Count - 1);
      if (s.PlacementLabelIds.Count > 0)
        s.PlacementLabelIds.RemoveAt(s.PlacementLabelIds.Count - 1);

      foreach (var id in lastIds)
        if (id != Guid.Empty) doc.Objects.Delete(id, true);
      foreach (var id in lastLabelIds)
        if (id.HasValue && id.Value != Guid.Empty) doc.Objects.Delete(id.Value, true);
    }

    foreach (var record in removedRecords)
    {
      foreach (var id in record.DetachedNotchIds)
        if (id != Guid.Empty) doc.Objects.Delete(id, true);
      foreach (var id in record.DetachedLabelIds)
        if (id != Guid.Empty) doc.Objects.Delete(id, true);
    }

    if (removeCount > 0 && s.NotchRecords.Count >= removeCount)
      s.NotchRecords.RemoveRange(s.NotchRecords.Count - removeCount, removeCount);

    foreach (var ids in s.NotchIdsByCurve)
      if (ids.Count >= removeCount) ids.RemoveRange(ids.Count - removeCount, removeCount);
    foreach (var ids in s.LabelIdsByCurve)
      if (ids.Count >= removeCount) ids.RemoveRange(ids.Count - removeCount, removeCount);

    s.RedoBatches.Push(undoBatch);
    vTools.Log.Write("vNotches",
      $"undo removed={removeCount} remaining={s.NotchRecords.Count} redo={s.RedoBatches.Count} curves={s.Curves.Count}");
    SyncPanelFromOptions(s);
    doc.Views.Redraw();
  }

  static void RedoLastNotch(RhinoDoc doc, NotchSession s)
  {
    if (s.RedoBatches.Count == 0) return;

    var batch = s.RedoBatches.Pop();
    int restoredObjects = 0;
    foreach (var placement in batch.Placements)
    {
      var notchIds = new List<Guid>();
      var labelIds = new List<Guid?>();
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        Guid notchId = curveIndex < placement.Notches.Count
          ? RestoreDocObject(doc, placement.Notches[curveIndex])
          : Guid.Empty;
        Guid labelId = curveIndex < placement.Labels.Count
          ? RestoreDocObject(doc, placement.Labels[curveIndex])
          : Guid.Empty;
        notchIds.Add(notchId);
        labelIds.Add(labelId == Guid.Empty ? null : labelId);
        if (notchId != Guid.Empty) restoredObjects++;
        if (labelId != Guid.Empty) restoredObjects++;
      }

      placement.Record.DetachedNotchIds.Clear();
      foreach (var snapshot in placement.DetachedNotches)
      {
        Guid id = RestoreDocObject(doc, snapshot);
        if (id == Guid.Empty) continue;
        placement.Record.DetachedNotchIds.Add(id);
        restoredObjects++;
      }

      placement.Record.DetachedLabelIds.Clear();
      foreach (var snapshot in placement.DetachedLabels)
      {
        Guid id = RestoreDocObject(doc, snapshot);
        if (id == Guid.Empty) continue;
        placement.Record.DetachedLabelIds.Add(id);
        restoredObjects++;
      }

      s.NotchRecords.Add(placement.Record);
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        s.NotchIdsByCurve[curveIndex].Add(notchIds[curveIndex]);
        s.LabelIdsByCurve[curveIndex].Add(labelIds[curveIndex]);
      }
      s.PlacementIds.Add(notchIds);
      s.PlacementLabelIds.Add(labelIds);
    }

    s.LabelValueText = batch.LabelValueAfterRedo;
    vTools.Log.Write("vNotches",
      $"redo restored={batch.Placements.Count} objects={restoredObjects} redo={s.RedoBatches.Count} curves={s.Curves.Count}");
    SyncPanelFromOptions(s);
    doc.Views.Redraw();
  }

  static NotchUndoBatch CaptureUndoBatch(
    RhinoDoc doc, NotchSession s, int firstRecordIndex, int recordCount)
  {
    var batch = new NotchUndoBatch(s.LabelValueText);
    for (int offset = 0; offset < recordCount; offset++)
    {
      int recordIndex = firstRecordIndex + offset;
      var record = s.NotchRecords[recordIndex];
      var placement = new NotchPlacementSnapshot(record);
      var notchIds = recordIndex < s.PlacementIds.Count
        ? s.PlacementIds[recordIndex]
        : [];
      var labelIds = recordIndex < s.PlacementLabelIds.Count
        ? s.PlacementLabelIds[recordIndex]
        : [];

      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        Guid notchId = curveIndex < notchIds.Count ? notchIds[curveIndex] : Guid.Empty;
        Guid? labelId = curveIndex < labelIds.Count ? labelIds[curveIndex] : null;
        placement.Notches.Add(CaptureDocObject(doc, notchId));
        placement.Labels.Add(CaptureDocObject(doc, labelId ?? Guid.Empty));
      }

      foreach (var id in record.DetachedNotchIds)
      {
        var snapshot = CaptureDocObject(doc, id);
        if (snapshot != null) placement.DetachedNotches.Add(snapshot);
      }
      foreach (var id in record.DetachedLabelIds)
      {
        var snapshot = CaptureDocObject(doc, id);
        if (snapshot != null) placement.DetachedLabels.Add(snapshot);
      }

      batch.Placements.Add(placement);
    }
    return batch;
  }

  static DocObjectSnapshot? CaptureDocObject(RhinoDoc doc, Guid objectId)
  {
    if (objectId == Guid.Empty) return null;
    var obj = doc.Objects.FindId(objectId);
    var geometry = obj?.Geometry?.Duplicate();
    if (obj == null || geometry == null) return null;
    return new DocObjectSnapshot(geometry, obj.Attributes.Duplicate());
  }

  static Guid RestoreDocObject(RhinoDoc doc, DocObjectSnapshot? snapshot)
  {
    if (snapshot == null) return Guid.Empty;
    var geometry = snapshot.Geometry.Duplicate();
    return geometry == null
      ? Guid.Empty
      : doc.Objects.Add(geometry, snapshot.Attributes.Duplicate());
  }

  // ── Finalize ──────────────────────────────────────────────────────────────

  static void FinalizeBlocks(RhinoDoc doc, NotchSession s)
  {
    if (s.Finalized) return;
    s.Finalized = true;
    // No block/iDef system â€” just redraw
    doc.Views.Redraw();
  }

  // ── Dynamic draw preview ──────────────────────────────────────────────────

  static void DrawPreview(RhinoDoc doc, NotchSession s, GetPointDrawEventArgs e)
  {
    var snapPoint = e.CurrentPoint;
    var cursorPoint = s.LastCursorPoint ?? snapPoint;

    s.LastPreviewPoint = snapPoint;
    s.PreviewValid = false;
    s.PreviewSnapPoint = snapPoint;
    s.PreviewCursorPoint = cursorPoint;

    ClosestCurveHit(s, snapPoint, out int refIdx, out var refCurve, out double refT);
    if (refCurve == null) { UpdateDistanceLabels(s, null, null, null); return; }

    double lfs  = LengthFromStart(refCurve, refT);
    double otherEnd = Math.Max(0.0, refCurve.GetLength() - lfs);
    double? prevDelta = null;
    if (s.NotchRecords.Count > 0)
    {
      var lastRec = s.NotchRecords[^1];
      double prevLen = LengthFromRecord(refCurve, lastRec, refIdx);
      prevDelta = Math.Abs(lfs - prevLen);
    }
    UpdateDistanceLabels(s, lfs, prevDelta, otherEnd);

    // Compute per-curve positions
    List<double> lengths;
    if (s.PercentToggle.CurrentValue)
    {
      double refLen = refCurve.GetLength();
      if (refLen <= 0.0) return;
      double pct = lfs / refLen;
      lengths = s.Curves.Select(c => c.GetLength() * pct).ToList();
    }
    else
    {
      lengths = Enumerable.Repeat(lfs, s.Curves.Count).ToList();
    }
    s.PreviewValid = true;
    s.PreviewRefCurveIndex = refIdx;
    s.PreviewLengthsFromStart = new List<double>(lengths);
    s.PreviewSnapPoint = snapPoint;
    s.PreviewCursorPoint = cursorPoint;
    var sides    = s.CurveSidesAsStrings();
    double nl    = s.NotchLengthOpt.CurrentValue;
    double no    = s.NotchOffsetOpt.CurrentValue;
    string nt    = s.NotchTypeValues[s.NotchTypeIndex];
    double nw    = s.NotchWidthOpt.CurrentValue;
    double lsize = EffectiveLabelSize(s);
    string ltext = s.LabelValueText.Trim();
    bool   canLabel = s.LabelToggle.CurrentValue && ltext.Length > 0 && lsize > doc.ModelAbsoluteTolerance;

    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (!s.CurveEnabled[i]) continue;
      Point3d? curveCursor = i == refIdx ? cursorPoint : null;
      var geom = NotchGeometry(s.Curves[i], lengths[i], nl, no, sides[i], nt, nw,
        curveCursor, null);
      if (geom == null) continue;
      if (geom is LineCurve lc)           e.Display.DrawLine(lc.Line, System.Drawing.Color.Cyan, 2);
      else if (geom is PolylineCurve plc)  e.Display.DrawPolyline(plc.ToPolyline(), System.Drawing.Color.Cyan, 2);

      if (canLabel)
      {
        GetCurveTangentAndDirection(s.Curves[i], lengths[i], sides[i], curveCursor, null,
          out var tangent, out var direction);
        if (!tangent.IsValid || !direction.IsValid) continue;

        string labelCurveSide = ResolvedLabelCurveSide(sides[i], sides.Count > 0 ? sides[0] : "Left", i);
        if (s.LabelSideFlip)
          labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";

        var (previewPlane, _, _) = ComputeLabelLayout(doc, s.Curves[i], lengths[i],
          direction, tangent, no, geom, ltext, lsize,
          s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue, labelCurveSide);
        if (!previewPlane.IsValid) continue;

        DrawLabelPreview(e.Display, previewPlane, ltext, lsize, System.Drawing.Color.Cyan);
      }
    }
  }

  static void DrawLabelPreview(DisplayPipeline display, Plane plane, string text, double size, System.Drawing.Color color)
  {
    try
    {
      var te = new TextEntity
      {
        Plane         = plane,
        PlainText     = text,
        TextHeight    = size,
        Justification = TextJustification.MiddleCenter,
        DimensionScale= 0.9,
      };
      try { te.DrawForward = false; } catch { }
      display.DrawAnnotation(te, color);
      return;
    }
    catch { }
    // Fallback: 3D text
    try
    {
      var t3d = new Rhino.Display.Text3d(text, plane, size);
      display.Draw3dText(t3d, color);
    }
    catch
    {
      display.DrawDot(plane.Origin, text);
    }
  }

  // ── Notch geometry ────────────────────────────────────────────────────────

  static GeometryBase? NotchGeometry(Curve curve, double lengthFromStart,
    double notchLength, double notchOffset, string side,
    string notchType, double notchWidth, Point3d? cursorPoint, Vector3d? tangentHint)
  {
    var (center, t) = PointAtCurveLength(curve, lengthFromStart);
    if (t == null) return null;

    var tangent = curve.TangentAt(t.Value);
    tangent.Z = 0.0;
    if (!tangent.Unitize())
    {
      if (!curve.PerpendicularFrameAt(t.Value, out var frame)) return null;
      tangent = frame.XAxis;
      tangent.Z = 0.0;
      if (!tangent.Unitize()) return null;
    }
    tangent = KinkAwareTangent(curve, t.Value, tangent, cursorPoint, tangentHint);

    var worldZ   = new Vector3d(0.0, 0.0, 1.0);
    var direction = Vector3d.CrossProduct(worldZ, tangent);
    if (!direction.Unitize()) return null;
    if (side == "Right") direction = -direction;

    notchType = (notchType ?? "I").ToUpperInvariant();

    if (notchType == "I")
    {
      Point3d start, end;
      if (notchOffset > 0.0)
      {
        start = center + direction * notchOffset;
        end   = center + direction * Math.Max(0.0, notchOffset - notchLength);
      }
      else
      {
        start = center;
        end   = center + direction * notchLength;
      }
      var lc = new LineCurve(start, end);
      return lc.IsValid ? lc : null;
    }

    double totalLength = curve.GetLength();
    double halfWidth   = Math.Max(0.0, notchWidth * 0.5);
    double centerLen   = Clamp(lengthFromStart, 0.0, totalLength);
    double leftRaw     = centerLen - halfWidth;
    double rightRaw    = centerLen + halfWidth;
    double leftLen     = Clamp(leftRaw, 0.0, totalLength);
    double rightLen    = Clamp(rightRaw, 0.0, totalLength);
    var (leftBase, _)  = PointAtCurveLength(curve, leftLen);
    var (rightBase, _) = PointAtCurveLength(curve, rightLen);
    if (leftRaw < 0.0)               leftBase  = leftBase  - tangent * Math.Abs(leftRaw);
    if (rightRaw > totalLength)      rightBase = rightBase + tangent * (rightRaw - totalLength);

    var tip = center + direction * notchLength;
    if (notchOffset > 0.0)
    {
      var shift = direction * notchOffset;
      leftBase  = leftBase  + shift;
      rightBase = rightBase + shift;
      tip       = center + direction * Math.Max(0.0, notchOffset - notchLength);
    }

    if (notchType == "V")
      return new PolylineCurve(new[] { leftBase, tip, rightBase });

    if (notchType == "U")
    {
      double halfFlat = Math.Max(0.0, notchWidth * 0.25);
      var leftTip  = tip - tangent * halfFlat;
      var rightTip = tip + tangent * halfFlat;
      return new PolylineCurve(new[] { leftBase, leftTip, rightTip, rightBase });
    }

    // Fallback
    return new LineCurve(center, center + direction * notchLength);
  }

  // ── Kink-aware tangent ────────────────────────────────────────────────────

  static Vector3d KinkAwareTangent(Curve curve, double t, Vector3d defaultTangent,
    Point3d? cursorPoint, Vector3d? tangentHint)
  {
    var domain = curve.Domain;
    double span = domain.Length;
    if (span <= 0.0) return defaultTangent;

    double eps     = span * 1e-4;
    double tBefore = Math.Max(domain.T0, t - eps);
    double tAfter  = Math.Min(domain.T1, t + eps);
    if (tBefore >= tAfter) return defaultTangent;

    var tanBefore = curve.TangentAt(tBefore);
    var tanAfter  = curve.TangentAt(tAfter);
    tanBefore.Z = 0.0; tanAfter.Z = 0.0;
    if (!tanBefore.Unitize() || !tanAfter.Unitize()) return defaultTangent;

    double dot = Vector3d.Multiply(tanBefore, tanAfter);
    if (dot >= Math.Cos(5.0 * Math.PI / 180.0)) return defaultTangent; // smooth

    if (cursorPoint.HasValue)
    {
      try
      {
        var kinkPt   = curve.PointAt(t);
        var ptBefore = curve.PointAt(tBefore);
        var ptAfter  = curve.PointAt(tAfter);

        var cursorDir = new Vector3d(
          cursorPoint.Value.X - kinkPt.X,
          cursorPoint.Value.Y - kinkPt.Y,
          0.0);

        var tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
        if (!cursorDir.IsValid || cursorDir.Length <= tol * 2.0)
        {
          var middle = tanBefore + tanAfter;
          middle.Z = 0.0;
          if (middle.IsValid && !middle.IsTiny() && middle.Unitize())
            return middle;

          return defaultTangent;
        }

        if (!cursorDir.Unitize())
          return defaultTangent;

        var dirBefore = new Vector3d(ptBefore.X - kinkPt.X, ptBefore.Y - kinkPt.Y, 0.0);
        var dirAfter  = new Vector3d(ptAfter.X  - kinkPt.X, ptAfter.Y  - kinkPt.Y, 0.0);

        if (!dirBefore.Unitize() || !dirAfter.Unitize())
          return defaultTangent;

        var dirMiddle = dirBefore + dirAfter;
        dirMiddle.Z = 0.0;

        if (!dirMiddle.IsValid || dirMiddle.IsTiny() || !dirMiddle.Unitize())
        {
          double beforeScoreFallback = Vector3d.Multiply(cursorDir, dirBefore);
          double afterScoreFallback  = Vector3d.Multiply(cursorDir, dirAfter);
          return afterScoreFallback >= beforeScoreFallback ? tanAfter : tanBefore;
        }

        var tanMiddle = tanBefore + tanAfter;
        tanMiddle.Z = 0.0;

        if (!tanMiddle.IsValid || tanMiddle.IsTiny() || !tanMiddle.Unitize())
          tanMiddle = defaultTangent;

        double beforeScore = Vector3d.Multiply(cursorDir, dirBefore);
        double middleScore = Vector3d.Multiply(cursorDir, dirMiddle);
        double afterScore  = Vector3d.Multiply(cursorDir, dirAfter);

        const double middleBias = 0.03;
        middleScore += middleBias;

        if (middleScore >= beforeScore && middleScore >= afterScore)
          return tanMiddle;

        return afterScore >= beforeScore ? tanAfter : tanBefore;
      }
      catch
      {
        return defaultTangent;
      }
    }
    if (tangentHint.HasValue)
    {
      var hint = new Vector3d(tangentHint.Value.X, tangentHint.Value.Y, 0.0);
      if (hint.Unitize())
        return Vector3d.Multiply(hint, tanAfter) >= Vector3d.Multiply(hint, tanBefore)
          ? tanAfter : tanBefore;
    }
    return defaultTangent;
  }

  // ── Tangent + direction ───────────────────────────────────────────────────

  static void GetCurveTangentAndDirection(Curve curve, double lengthFromStart, string side,
    Point3d? cursorPoint, Vector3d? tangentHint,
    out Vector3d tangent, out Vector3d direction)
  {
    tangent   = Vector3d.Unset;
    direction = Vector3d.Unset;
    var (_, t) = PointAtCurveLength(curve, lengthFromStart);
    if (t == null) return;

    tangent = curve.TangentAt(t.Value);
    tangent.Z = 0.0;
    if (!tangent.Unitize())
    {
      if (!curve.PerpendicularFrameAt(t.Value, out var frame)) return;
      tangent = frame.XAxis;
      tangent.Z = 0.0;
      if (!tangent.Unitize()) { tangent = Vector3d.Unset; return; }
    }
    tangent = KinkAwareTangent(curve, t.Value, tangent, cursorPoint, tangentHint);

    var worldZ = new Vector3d(0.0, 0.0, 1.0);
    direction  = Vector3d.CrossProduct(worldZ, tangent);
    if (!direction.Unitize()) { direction = Vector3d.Unset; return; }
    if (side == "Right") direction = -direction;
  }

  // ── Point at curve arc-length ─────────────────────────────────────────────

  static (Point3d pt, double? t) PointAtCurveLength(Curve curve, double lengthFromStart)
  {
    double total = curve.GetLength();
    double clamped = Clamp(lengthFromStart, 0.0, total);
    if (curve.LengthParameter(clamped, out double t))
      return (curve.PointAt(t), t);
    if (clamped <= 0.0) return (curve.PointAtStart, curve.Domain.T0);
    return (curve.PointAtEnd, curve.Domain.T1);
  }

  static double LengthFromStart(Curve curve, double t)
  {
    var domain = curve.Domain;
    var interval = new Interval(domain.T0, t);
    return curve.GetLength(interval);
  }

  // ── Label placement ───────────────────────────────────────────────────────

  static Plane BuildReadableTextPlane(RhinoDoc doc, Point3d anchor,
    Vector3d tangent, Vector3d direction, Point3d? curvePoint)
  {
    var xAxis = new Vector3d(tangent);
    if (!xAxis.Unitize()) return Plane.Unset;

    var view   = doc.Views.ActiveView;
    var upAxis = view != null
      ? view.ActiveViewport.ConstructionPlane().ZAxis
      : new Vector3d(0.0, 0.0, 1.0);
    if (!upAxis.Unitize()) upAxis = new Vector3d(0.0, 0.0, 1.0);

    var yAxis = Vector3d.CrossProduct(upAxis, xAxis);
    if (!yAxis.Unitize())
    {
      yAxis = new Vector3d(-direction.X, -direction.Y, -direction.Z);
      if (!yAxis.Unitize()) return Plane.Unset;
    }

    var plane = new Plane(anchor, xAxis, yAxis);
    if (!plane.IsValid) return Plane.Unset;

    var refX = view != null
      ? view.ActiveViewport.ConstructionPlane().XAxis
      : new Vector3d(1.0, 0.0, 0.0);
    if (!refX.Unitize()) refX = new Vector3d(1.0, 0.0, 0.0);

    if (Vector3d.Multiply(plane.XAxis, refX) < 0.0)
    {
      xAxis = -xAxis; yAxis = -yAxis;
      plane = new Plane(anchor, xAxis, yAxis);
      if (!plane.IsValid) return Plane.Unset;
    }

    if (curvePoint.HasValue)
    {
      var toCurve = new Vector3d(
        curvePoint.Value.X - anchor.X,
        curvePoint.Value.Y - anchor.Y,
        curvePoint.Value.Z - anchor.Z);
      if (Vector3d.Multiply(toCurve, plane.YAxis) < 0.0)
      {
        xAxis = -xAxis; yAxis = -yAxis;
        plane = new Plane(anchor, xAxis, yAxis);
        if (!plane.IsValid) return Plane.Unset;
      }
    }
    return plane;
  }

  static double EstimatedLabelHalfWidth(string text, double height)
  {
    if (height <= 0.0 || string.IsNullOrEmpty(text)) return 0.5 * height * 0.65;
    double units = 0.0;
    foreach (char ch in text)
    {
      if ("ilI1|".IndexOf(ch) >= 0)             units += 0.35;
      else if ("mwMW@#%&".IndexOf(ch) >= 0)     units += 0.95;
      else if (".,;:!`'\"".IndexOf(ch) >= 0)    units += 0.28;
      else if ("-_/\\".IndexOf(ch) >= 0)         units += 0.45;
      else if (ch == ' ')                        units += 0.32;
      else if (char.IsDigit(ch))                 units += 0.62;
      else                                       units += 0.68;
    }
    double w = Math.Max(height * 0.65, units * height);
    return 0.5 * w;
  }

  static double GeometryXInPlane(Plane plane, Point3d pt)
  {
    if (plane.ClosestParameter(pt, out double u, out _)) return u;
    return Vector3d.Multiply(new Vector3d(pt.X, pt.Y, pt.Z), plane.XAxis);
  }

  static (double min, double max) GeometryXRangeInPlane(GeometryBase geom, Plane plane)
  {
    var values = new List<double>();
    if (geom is LineCurve lc)
    {
      values.Add(GeometryXInPlane(plane, lc.Line.From));
      values.Add(GeometryXInPlane(plane, lc.Line.To));
    }
    else if (geom is PolylineCurve plc)
    {
      foreach (var pt in plc.ToPolyline())
        values.Add(GeometryXInPlane(plane, pt));
    }
    if (values.Count > 0) return (values.Min(), values.Max());
    var bbox = geom.GetBoundingBox(plane);
    return bbox.IsValid ? (bbox.Min.X, bbox.Max.X) : (0.0, 0.0);
  }

  static double MeasuredLabelHalfWidth(RhinoDoc doc, string text, double height, Plane plane)
  {
    if (height <= doc.ModelAbsoluteTolerance) return 0.0;
    // Measure in WorldXY for orientation-independent width
    var te = new TextEntity
    {
      Plane         = Plane.WorldXY,
      PlainText     = text,
      TextHeight    = height,
      Justification = TextJustification.MiddleCenter,
      DimensionScale= 0.9,
    };
    var bbox = te.GetBoundingBox(Plane.WorldXY);
    if (bbox.IsValid)
    {
      double w = Math.Max(0.0, bbox.Max.X - bbox.Min.X);
      if (w > doc.ModelAbsoluteTolerance)
        return 0.5 * w * LabelWidthMult;
    }
    return EstimatedLabelHalfWidth(text, height) * LabelWidthMult;
  }

  static double ChooseLabelTangentSide(Curve curve, double lengthFromStart,
    double preferredSign, double requiredOffset, double tol)
  {
    double total = curve.GetLength();
    if (total <= tol) return preferredSign;
    double d    = Clamp(lengthFromStart, 0.0, total);
    double posS = Math.Max(0.0, total - d);
    double negS = Math.Max(0.0, d);
    double prefS = preferredSign >= 0.0 ? posS : negS;
    if (prefS + tol >= requiredOffset) return preferredSign;
    double othS = preferredSign >= 0.0 ? negS : posS;
    if (othS + tol >= requiredOffset) return -preferredSign;
    return preferredSign;
  }

  static (Plane plane, bool sideFlipped, BoundingBox bbox) ComputeLabelLayout(
    RhinoDoc doc, Curve curve, double lengthFromStart,
    Vector3d direction, Vector3d tangent, double notchOffset,
    GeometryBase? notchGeom, string labelText, double labelSize,
    double labelOffset, double labelOffsetY, string curveSide)
  {
    double tol = doc.ModelAbsoluteTolerance;
    if (labelSize <= tol)
      return (Plane.Unset, false, BoundingBox.Empty);

    var (curvePoint, _) = PointAtCurveLength(curve, lengthFromStart);
    var anchor = curvePoint + direction * Math.Max(0.0, notchOffset) * 0.5;
    if (Math.Abs(labelOffsetY) > tol)
      anchor = anchor + direction * labelOffsetY;

    var plane = BuildReadableTextPlane(doc, anchor, tangent, direction, curvePoint);
    if (!plane.IsValid)
      return (Plane.Unset, false, BoundingBox.Empty);

    double labelHW  = MeasuredLabelHalfWidth(doc, labelText, labelSize, plane);
    var effectiveBbox = new BoundingBox(
      new Point3d(-labelHW, -0.5 * labelSize, 0.0),
      new Point3d( labelHW,  0.5 * labelSize, 0.0));

    if (notchGeom == null)
      return (plane, false, effectiveBbox);

    var (notchMin, notchMax) = GeometryXRangeInPlane(notchGeom, plane);
    double notchCenter  = 0.5 * (notchMin + notchMax);
    double labelCenter  = GeometryXInPlane(plane, plane.Origin);
    double requestedGap = labelOffset;
    double notchHW      = Math.Max(0.0, 0.5 * (notchMax - notchMin));

    double preferredCurveSign = curveSide == "Right" ? 1.0 : -1.0;
    double requiredOffset = notchHW + Math.Max(0.0, requestedGap) + 2.0 * labelHW;
    double sideSignCurve  = ChooseLabelTangentSide(curve, lengthFromStart, preferredCurveSign, requiredOffset, tol);
    bool sideFlipped      = sideSignCurve != preferredCurveSign;

    double tanPlaneDot    = Vector3d.Multiply(plane.XAxis, tangent);
    double curveToPlane   = tanPlaneDot < 0.0 ? -1.0 : 1.0;
    double sideSignPlane  = sideSignCurve * curveToPlane;

    double clearance = sideSignPlane * (labelCenter - notchCenter) - (notchHW + labelHW);
    if (clearance >= requestedGap - tol)
      return (plane, sideFlipped, effectiveBbox);

    double delta = sideSignPlane * (requestedGap - clearance);
    if (Math.Abs(delta) <= tol)
      return (plane, sideFlipped, effectiveBbox);

    var shifted = new Plane(plane);
    shifted.Origin = shifted.Origin + shifted.XAxis * delta;
    return (shifted, sideFlipped, effectiveBbox);
  }

  // ── Add notch to doc ──────────────────────────────────────────────────────

  static (Guid notch, Guid? label) AddNotch(RhinoDoc doc,
    Curve curve, double lengthFromStart,
    double notchLength, double notchOffset, string side, int groupIndex,
    string notchType, double notchWidth,
    bool labelEnabled, string labelText, double labelSize,
    string notchLayer, string labelLayer,
    double labelOffset, double labelOffsetY,
    string labelCurveSide,
    Point3d? cursorPoint, Vector3d? tangentHint)
  {
    GetCurveTangentAndDirection(curve, lengthFromStart, side, cursorPoint, tangentHint,
      out var tangent, out var direction);

    var geom = NotchGeometry(curve, lengthFromStart, notchLength, notchOffset,
      side, notchType, notchWidth, cursorPoint, tangentHint);
    if (geom == null) return (Guid.Empty, null);

    Guid notchId;
    if (geom is LineCurve lc)
    {
      var attrs = new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc, notchLayer) };
      if (groupIndex >= 0) attrs.AddToGroup(groupIndex);
      notchId = doc.Objects.AddLine(lc.Line, attrs);
    }
    else if (geom is PolylineCurve plc)
    {
      var attrs = new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc, notchLayer) };
      if (groupIndex >= 0) attrs.AddToGroup(groupIndex);
      notchId = doc.Objects.AddPolyline(plc.ToPolyline(), attrs);
    }
    else return (Guid.Empty, null);

    if (notchId == Guid.Empty) return (Guid.Empty, null);

    Guid? labelId = null;
    if (labelEnabled && tangent.IsValid && direction.IsValid)
    {
      string lt = (labelText ?? "").Trim();
      if (lt.Length > 0 && labelSize > doc.ModelAbsoluteTolerance)
      {
        var (labelPlane, _, _) = ComputeLabelLayout(doc, curve, lengthFromStart,
          direction, tangent, notchOffset, geom, lt, labelSize,
          labelOffset, labelOffsetY, labelCurveSide);
        if (labelPlane.IsValid)
        {
          var te = new TextEntity
          {
            Plane         = labelPlane,
            PlainText     = lt,
            TextHeight    = labelSize,
            Justification = TextJustification.MiddleCenter,
            DimensionScale= 0.9,
          };
          var la = new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc, labelLayer) };
          if (groupIndex >= 0) la.AddToGroup(groupIndex);
          var lid = doc.Objects.AddText(te, la);
          if (lid != Guid.Empty) labelId = lid;
        }
      }
    }

    return (notchId, labelId);
  }

  static List<(Guid notch, Guid? label)> AddNotchesPerCurve(
    RhinoDoc doc, NotchSession s, List<string> sides, int[] groupIndices,
    List<double> lengths, double notchLen, double notchOff,
    string notchTyp, double notchWid,
    bool canLabel, List<string> labelValues, double labelSize,
    string notchLayer, string labelLayer,
    double labelOffset, double labelOffsetY,
    bool labelSideFlip, Point3d? cursorPoint, bool[] curveEnabled)
  {
    var ids = new List<(Guid, Guid?)>();
    string firstSide = sides.Count > 0 ? sides[0] : "Left";
    int referenceIndex = cursorPoint.HasValue ? s.PreviewRefCurveIndex : -1;

    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (curveEnabled != null && i < curveEnabled.Length && !curveEnabled[i])
      { ids.Add((Guid.Empty, null)); continue; }

      string labelCurveSide = ResolvedLabelCurveSide(sides[i], firstSide, i);
      if (labelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";

      string lv = (canLabel && i < labelValues.Count) ? labelValues[i] : "";
      int gi    = i < groupIndices.Length ? groupIndices[i] : -1;
      Point3d? curveCursor = i == referenceIndex ? cursorPoint : null;

      var (nid, lid) = AddNotch(doc, s.Curves[i], lengths[i],
        notchLen, notchOff, sides[i], gi,
        notchTyp, notchWid,
        canLabel, lv, labelSize,
        notchLayer, labelLayer,
        labelOffset, labelOffsetY,
        labelCurveSide, curveCursor, null);

      if (nid != Guid.Empty)
      {
        GetCurveTangentAndDirection(s.Curves[i], lengths[i], sides[i],
          curveCursor, null, out var resolvedTangent, out var resolvedDirection);
        vTools.Log.Write("vNotches",
          $"placed curve={i + 1} side={sides[i]} ref={referenceIndex + 1} " +
          $"tangent=({resolvedTangent.X:0.###},{resolvedTangent.Y:0.###}) " +
          $"direction=({resolvedDirection.X:0.###},{resolvedDirection.Y:0.###})");
      }

      ids.Add((nid, lid));
    }
    return ids;
  }

  // ── Rebuild curve notches (after side/reverse change) ─────────────────────

  static void RebuildCurveNotches(RhinoDoc doc, NotchSession s, int curveIndex)
  {
    if (curveIndex < 0 || curveIndex >= s.Curves.Count) return;

    // Delete existing notch + label IDs for this curve
    while (s.NotchIdsByCurve[curveIndex].Count > 0)
    {
      var id = s.NotchIdsByCurve[curveIndex][^1];
      s.NotchIdsByCurve[curveIndex].RemoveAt(s.NotchIdsByCurve[curveIndex].Count - 1);
      if (id != Guid.Empty) doc.Objects.Delete(id, true);
    }
    while (s.LabelIdsByCurve[curveIndex].Count > 0)
    {
      var id = s.LabelIdsByCurve[curveIndex][^1];
      s.LabelIdsByCurve[curveIndex].RemoveAt(s.LabelIdsByCurve[curveIndex].Count - 1);
      if (id.HasValue && id.Value != Guid.Empty) doc.Objects.Delete(id.Value, true);
    }

    // if (!s.CurveEnabled[curveIndex])
    // {
    //   s.NotchIdsByCurve[curveIndex].AddRange(Enumerable.Repeat(Guid.Empty, s.NotchRecords.Count));
    //   s.LabelIdsByCurve[curveIndex].AddRange(Enumerable.Repeat<Guid?>(null, s.NotchRecords.Count));
    //   // Rebuild placement IDs
    //   RebuildPlacementIds(s);
    //   return;
    // }

    var newIds      = new List<Guid>();
    var newLabelIds = new List<Guid?>();
    string side     = s.CurveSides[curveIndex] ? "Left" : "Right"; // true = Left
    string firstSide= s.CurveSides[0] ? "Left" : "Right";
    int groupIdx    = s.SessionGroupIndices[curveIndex < s.SessionGroupIndices.Length ? curveIndex : 0];

    foreach (var rec in s.NotchRecords)
    {
      bool recordHadCurveEnabled =
      rec.CurveEnabled == null ||
      rec.CurveEnabled.Count == 0 ||
      (curveIndex < rec.CurveEnabled.Count && rec.CurveEnabled[curveIndex]);

      if (!recordHadCurveEnabled)
      {
        newIds.Add(Guid.Empty);
        newLabelIds.Add(null);
        continue;
      }
      double d = LengthFromRecord(s.Curves[curveIndex], rec, curveIndex);
      bool lbl = rec.LabelEnabled;
      string lv = (rec.LabelValues != null && curveIndex < rec.LabelValues.Count)
        ? rec.LabelValues[curveIndex] : "";
      string labelCurveSide = ResolvedLabelCurveSide(side, firstSide, curveIndex);
      if (s.LabelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";

      var (nid, lid) = AddNotch(doc, s.Curves[curveIndex], d,
        rec.NotchLength, rec.NotchOffset, side, groupIdx,
        rec.NotchType, rec.NotchWidth,
        lbl, lv, rec.LabelSize,
        EffectiveLayerName(doc, rec.NotchLayer, rec.NotchLayer),
        EffectiveLayerName(doc, rec.LabelLayer, rec.NotchLayer),
        rec.LabelOffset, rec.LabelOffsetY, labelCurveSide, null, null);
      newIds.Add(nid);
      newLabelIds.Add(lid);
    }
    s.NotchIdsByCurve[curveIndex].AddRange(newIds);
    s.LabelIdsByCurve[curveIndex].AddRange(newLabelIds);
    RebuildPlacementIds(s);
    doc.Views.Redraw();
  }

  static void RebuildPlacementIds(NotchSession s)
  {
    // Rebuild PlacementIds by transposing NotchIdsByCurve per record index
    s.PlacementIds.Clear();
    s.PlacementLabelIds.Clear();
    for (int r = 0; r < s.NotchRecords.Count; r++)
    {
      var ids   = new List<Guid>();
      var lids  = new List<Guid?>();
      for (int c = 0; c < s.Curves.Count; c++)
      {
        ids.Add(r < s.NotchIdsByCurve[c].Count ? s.NotchIdsByCurve[c][r] : Guid.Empty);
        lids.Add(r < s.LabelIdsByCurve[c].Count ? s.LabelIdsByCurve[c][r] : null);
      }
      s.PlacementIds.Add(ids);
      s.PlacementLabelIds.Add(lids);
    }
  }

  // ── Side / reverse ────────────────────────────────────────────────────────

  static void ToggleCurveSide(RhinoDoc doc, NotchSession s, int idx)
  {
    if (idx < 0 || idx >= s.CurveSides.Length) return;
    s.RedoBatches.Clear();
    s.CurveSides[idx] = !s.CurveSides[idx];
    RebuildCurveNotches(doc, s, idx);
    SelectBothCurves(doc, s);
    s.Panel?.UpdateUndoEnabled();
  }

  static void ReverseCurve(RhinoDoc doc, NotchSession s, int idx)
  {
    if (idx < 0 || idx >= s.Curves.Count) return;
    s.RedoBatches.Clear();
    double total = s.Curves[idx].GetLength();
    foreach (var rec in s.NotchRecords)
    {
      if (rec.LengthsFromStart == null || idx >= rec.LengthsFromStart.Count) continue;
      double old = rec.LengthsFromStart[idx];
      rec.LengthsFromStart[idx] = Clamp(total - old, 0.0, total);
    }
    s.Curves[idx].Reverse();
    s.CurveSides[idx] = !s.CurveSides[idx]; // side flips with reverse
    RebuildCurveNotches(doc, s, idx);
    SelectBothCurves(doc, s);
    s.Panel?.UpdateUndoEnabled();
  }

  static void SelectBothCurves(RhinoDoc doc, NotchSession s)
  {
    doc.Objects.UnselectAll();
    foreach (var id in s.CurveIds)
      doc.Objects.FindId(id)?.Select(true);
    doc.Views.Redraw();
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  static int ClosestCurveIndex(NotchSession s, Point3d point)
  {
    ClosestCurveHit(s, point, out int idx, out _, out _);
    return idx;
  }

  static void ClosestCurveHit(NotchSession s, Point3d point,
    out int closestIdx, out Curve? closestCurve, out double closestT)
  {
    closestIdx   = 0;
    closestCurve = s.Curves.Count > 0 ? s.Curves[0] : null;
    closestT     = 0.0;
    double closestDist = double.MaxValue;
    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (!s.Curves[i].ClosestPoint(point, out double t)) continue;
      double dist = s.Curves[i].PointAt(t).DistanceTo(point);
      if (dist < closestDist)
      {
        closestDist  = dist;
        closestIdx   = i;
        closestCurve = s.Curves[i];
        closestT     = t;
      }
    }
  }

  static double LengthFromRecord(Curve curve, NotchRecord rec, int curveIndex)
  {
    if (rec.LengthsFromStart != null && curveIndex < rec.LengthsFromStart.Count)
      return rec.LengthsFromStart[curveIndex];
    if (rec.Mode == "percent" && rec.Percent.HasValue)
      return curve.GetLength() * rec.Percent.Value;
    return rec.LengthsFromStart?.Count > 0 ? rec.LengthsFromStart[0] : 0.0;
  }

  static int ResolveLayerIndex(RhinoDoc doc, string layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName) || layerName == SpecialLayerCurrent)
      return doc.Layers.CurrentLayerIndex;
    int idx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
    if (idx >= 0) return idx;
    var layer = new Layer { Name = layerName };
    idx = doc.Layers.Add(layer);
    return idx >= 0 ? idx : doc.Layers.CurrentLayerIndex;
  }

  static string EffectiveLayerName(RhinoDoc doc, string layerChoice, string notchLayerChoice)
  {
    if (string.IsNullOrWhiteSpace(layerChoice) || layerChoice == SpecialLayerCurrent)
    {
      int ci = doc.Layers.CurrentLayerIndex;
      return ci >= 0 && ci < doc.Layers.Count ? doc.Layers[ci].FullPath : "";
    }
    return layerChoice;
  }

  static string ResolvedLabelCurveSide(string side, string firstSide, int index)
  {
    string cur   = side      == "Right" ? "Right" : "Left";
    string first = firstSide == "Right" ? "Right" : "Left";
    if (index > 0 && cur != first) return cur == "Right" ? "Left" : "Right";
    return cur;
  }

  static Curve OrientCurveToPickPoint(Curve curve, Point3d pick)
  {
    return OrientCurveToPickPoint(curve, pick, out _);
  }

  static Curve OrientCurveToPickPoint(Curve curve, Point3d pick, out bool reversed)
  {
    reversed = false;
    if (pick == Point3d.Unset) return curve;
    if (PickTargetsCurveEnd(curve, pick))
    {
      curve = curve.DuplicateCurve();
      curve.Reverse();
      reversed = true;
    }
    return curve;
  }

  static bool PickTargetsCurveEnd(Curve curve, Point3d pick) =>
    pick != Point3d.Unset &&
    pick.DistanceTo(curve.PointAtEnd) < pick.DistanceTo(curve.PointAtStart);

  static double Clamp(double v, double min, double max) =>
    v < min ? min : (v > max ? max : v);

  static double ModelUnitsFromInches(RhinoDoc doc, double inches) =>
    inches * RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);

  static double EffectiveLabelSize(NotchSession s)
  {
    if (s.LabelSizeAutoToggle.CurrentValue)
    {
      double pct = s.LabelSizePctValues[s.LabelSizePctIndex] * 0.01;
      return Math.Max(0.0, s.NotchOffsetOpt.CurrentValue * pct);
    }
    return Math.Max(0.0, s.ManualLabelSize);
  }

  static string FormatFractionalInches(RhinoDoc doc, double value)
  {
    double toIn = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Inches);
    double absin = Math.Abs(value * toIn);
    int whole    = (int)absin;
    double frac  = absin - whole;
    int num      = (int)Math.Round(frac * 64.0);
    if (num == 64) { whole++; num = 0; }
    string fracPart = num == 0 ? "" : $"{num}\u204464";
    string result   = (whole == 0 && fracPart.Length > 0)
      ? $"{fracPart}\u2033"
      : fracPart.Length > 0 ? $"{whole} {fracPart}\u2033" : $"{whole}\u2033";
    return (value < 0 ? "-" : "") + result;
  }

  static string FormatPanelNumber(double value) =>
    value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

  static string IncrementLabelValue(string text)
  {
    if (string.IsNullOrWhiteSpace(text)) return "A";
    var m = Regex.Match(text, @"([A-Za-z]+|\d+)$");
    if (!m.Success) return text + "1";
    string prefix = text[..m.Index];
    string suffix = m.Value;
    if (suffix.All(char.IsDigit))
    {
      int n = int.Parse(suffix) + 1;
      return prefix + n.ToString().PadLeft(suffix.Length, '0');
    }
    return prefix + IncrementAlpha(suffix.ToUpper());
  }

  static string IncrementAlpha(string s)
  {
    var chars = s.ToCharArray();
    for (int i = chars.Length - 1; i >= 0; i--)
    {
      if (chars[i] == 'Z') { chars[i] = 'A'; continue; }
      chars[i]++;
      return new string(chars);
    }
    return "A" + new string(chars);
  }

  // ── Panel sync ────────────────────────────────────────────────────────────

  static void SyncPanelFromOptions(NotchSession s)
  {
    s.Panel?.SyncFromSession();
  }

  static void UpdateDistanceLabels(NotchSession s, double? current, double? prevDelta, double? otherEnd)
  {
    s.Panel?.UpdateDistanceLabels(current, prevDelta, otherEnd);
  }

  // ── Session state ─────────────────────────────────────────────────────────

  sealed class NotchSession
  {
    public readonly RhinoDoc Doc;
    public readonly List<Curve> Curves;
    public readonly List<Guid>  CurveIds;
    public bool[]   CurveSides;  // true = Left
    public bool[]   CurveEnabled;

    public OptionDouble NotchLengthOpt;
    public OptionDouble NotchOffsetOpt;
    public OptionDouble NotchWidthOpt;
    public OptionDouble LabelSizeOpt;
    public OptionDouble LabelOffsetOpt;
    public OptionDouble LabelOffsetYOpt;
    public OptionToggle PercentToggle;
    public OptionToggle GroupToggle;
    public OptionToggle LabelToggle;
    public OptionToggle LabelSizeAutoToggle;

    public string LabelValueText;
    public double ManualLabelSize;
    public string NotchLayerName;
    public string LabelLayerName;
    public bool   LabelAutoAdv;
    public bool   LabelSideFlip;
    public double MultipleStartOffset;
    public double MultipleEndOffset;
    public int    MultipleNumber;
    public readonly string[] NotchTypeValues = ["I", "V", "U"];
    public int NotchTypeIndex;

    public readonly int[] LabelSizePctValues;
    public readonly string[] LabelSizePctTexts;
    public int LabelSizePctIndex;

    // Per-curve tracking
    public readonly List<List<Guid>>   NotchIdsByCurve;
    public readonly List<List<Guid?>>  LabelIdsByCurve;
    public readonly List<List<Guid>>   PlacementIds    = [];
    public readonly List<List<Guid?>>  PlacementLabelIds = [];
    public readonly List<NotchRecord>  NotchRecords    = [];

    // Group indices per curve for session grouping
    public int[] SessionGroupIndices;
    // Context group indices from source curves
    public int[] CurveContextGroupIndices;

    // Loop control
    public bool PanelClosedExit;
    public bool RefreshCommandLine;
    public bool PanelNumericPending;
    public bool IgnoreNextNothing;
    public bool CurveSelectionRequested;
    public bool TransparentRedoRequested;
    public bool SuppressPanelCloseExit;
    public bool Finalized;
    public bool NotchCollapsed;
    public bool LabelCollapsed;
    public bool MultipleCollapsed;

    // Command option indices (set each iteration)
    public int SideOptionIndex, ReverseOptionIndex, UndoOptionIndex, RedoOptionIndex;
    public int TypeOptionIndex, NotchLayerOptionIndex;
    public int LabelValueOptionIndex, LabelLayerOptionIndex;
    public int LabelSizeAutoIndex, LabelSizePctIndex2;

    public Point3d? LastPreviewPoint;
    public Point3d? LastCursorPoint;
    public NotchPanel? Panel;

    public bool PreviewValid;
    public Point3d PreviewSnapPoint;
    public Point3d PreviewCursorPoint;
    public int PreviewRefCurveIndex;
    public List<double> PreviewLengthsFromStart = [];
    public readonly Stack<NotchUndoBatch> RedoBatches = [];
    public NotchSession(RhinoDoc doc, List<Curve> curves, List<Guid> curveIds, bool[] sides,
      double notchLength, double notchOffset, double notchWidth, string notchType,
      bool percent, bool group, bool label, string labelValue,
      double labelSize, bool labelSizeAuto, int labelSizePct,
      string notchLayer, string labelLayer, double labelOffset, double labelOffsetY,
      bool labelAutoAdv, bool labelSideFlip,
      double multipleStartOffset, double multipleEndOffset, int multipleNumber)
    {
      Doc      = doc;
      Curves   = curves;
      CurveIds = curveIds;

      CurveSides   = sides;
      CurveEnabled = Enumerable.Repeat(true, curves.Count).ToArray();

      double tol = doc.ModelAbsoluteTolerance;
      NotchLengthOpt    = new OptionDouble(notchLength, tol, 1e9);
      NotchOffsetOpt    = new OptionDouble(notchOffset, 0.0, 1e9);
      NotchWidthOpt     = new OptionDouble(notchWidth,  tol, 1e9);
      LabelSizeOpt      = new OptionDouble(Math.Max(0.0, labelSize), 0.0, 1e9);
      LabelOffsetOpt    = new OptionDouble(labelOffset,  -1e9, 1e9);
      LabelOffsetYOpt   = new OptionDouble(labelOffsetY, -1e9, 1e9);
      PercentToggle     = new OptionToggle(percent,       "Off", "On");
      GroupToggle       = new OptionToggle(group,         "Off", "On");
      LabelToggle       = new OptionToggle(label,         "Off", "On");
      LabelSizeAutoToggle = new OptionToggle(labelSizeAuto, "Manual", "Auto");

      LabelValueText = labelValue ?? "A";
      ManualLabelSize = Math.Max(0.0, labelSize);
      NotchLayerName  = notchLayer ?? SpecialLayerCurrent;
      LabelLayerName  = labelLayer ?? "PLOT";
      LabelAutoAdv    = labelAutoAdv;
      LabelSideFlip   = labelSideFlip;
      MultipleStartOffset = Math.Max(0.0, multipleStartOffset);
      MultipleEndOffset   = Math.Max(0.0, multipleEndOffset);
      MultipleNumber      = Math.Clamp(multipleNumber, 2, 10000);

      NotchTypeIndex  = Array.IndexOf(NotchTypeValues, notchType?.ToUpper() ?? "I");
      if (NotchTypeIndex < 0) NotchTypeIndex = 0;

      LabelSizePctValues = Enumerable.Range(4, 17).Select(i => i * 5).ToArray(); // 20..100 step 5
      LabelSizePctTexts  = LabelSizePctValues.Select(v => $"{v}%").ToArray();
      LabelSizePctIndex  = Array.FindIndex(LabelSizePctValues, v => v == labelSizePct);
      if (LabelSizePctIndex < 0)
        LabelSizePctIndex = Array.FindIndex(LabelSizePctValues,
          v => v == LabelSizePctValues.OrderBy(x => Math.Abs(x - labelSizePct)).First());

      // Group indices for session â€” only when group=On
      SessionGroupIndices     = Enumerable.Repeat(-1, curves.Count).ToArray();
      CurveContextGroupIndices= new int[curves.Count];
      for (int i = 0; i < curves.Count; i++)
      {
        var rh = doc.Objects.FindId(curveIds[i]);
        var grps = rh?.Attributes.GetGroupList();
        CurveContextGroupIndices[i] = (grps != null && grps.Length > 0) ? grps[0] : -1;
      }

      NotchIdsByCurve = curves.Select(_ => new List<Guid>()).ToList();
      LabelIdsByCurve = curves.Select(_ => new List<Guid?>()).ToList();
    }

    public List<string> CurveSidesAsStrings() =>
      CurveSides.Select(b => b ? "Left" : "Right").ToList();
  }

  // ── Notch record ──────────────────────────────────────────────────────────

  sealed class NotchRecord
  {
    public Guid           BatchId;
    public string         Mode           = "distance";
    public double         NotchLength;
    public double         NotchOffset;
    public string         NotchType      = "I";
    public double         NotchWidth;
    public bool           GroupEnabled;
    public bool           LabelEnabled;
    public List<string>   LabelValues    = [];
    public double         LabelSize;
    public string         NotchLayer     = SpecialLayerCurrent;
    public string         LabelLayer     = "PLOT";
    public double         LabelOffset;
    public double         LabelOffsetY;
    public List<double>   LengthsFromStart = [];
    public List<bool>     CurveEnabled = [];
    public List<Guid>     DetachedNotchIds = [];
    public List<Guid>     DetachedLabelIds = [];
    public double?        Percent;
  }

  sealed class NotchUndoBatch
  {
    public NotchUndoBatch(string labelValueAfterRedo)
    {
      LabelValueAfterRedo = labelValueAfterRedo;
    }

    public string LabelValueAfterRedo { get; }
    public List<NotchPlacementSnapshot> Placements { get; } = [];
  }

  sealed class NotchPlacementSnapshot
  {
    public NotchPlacementSnapshot(NotchRecord record)
    {
      Record = record;
    }

    public NotchRecord Record { get; }
    public List<DocObjectSnapshot?> Notches { get; } = [];
    public List<DocObjectSnapshot?> Labels { get; } = [];
    public List<DocObjectSnapshot> DetachedNotches { get; } = [];
    public List<DocObjectSnapshot> DetachedLabels { get; } = [];
  }

  sealed class DocObjectSnapshot
  {
    public DocObjectSnapshot(GeometryBase geometry, ObjectAttributes attributes)
    {
      Geometry = geometry;
      Attributes = attributes;
    }

    public GeometryBase Geometry { get; }
    public ObjectAttributes Attributes { get; }
  }

  sealed class NotchHistoryRequest
  {
    public NotchHistoryRequest(bool redo, string source)
    {
      Redo = redo;
      Source = source;
    }

    public bool Redo { get; }
    public string Source { get; }
  }

  sealed class NotchShortcutSession : IDisposable
  {
    readonly string _undoMacro;
    readonly string _redoMacro;
    readonly string _alternateRedoMacro;
    bool _restoreNeeded;

    public NotchShortcutSession()
    {
      _undoMacro = Rhino.ApplicationSettings.ShortcutKeySettings.GetMacro(
        Rhino.ApplicationSettings.ShortcutKey.CtrlZ) ?? string.Empty;
      _redoMacro = Rhino.ApplicationSettings.ShortcutKeySettings.GetMacro(
        Rhino.ApplicationSettings.ShortcutKey.CtrlY) ?? string.Empty;
      _alternateRedoMacro = Rhino.ApplicationSettings.ShortcutKeySettings.GetMacro(
        Rhino.ApplicationSettings.ShortcutKey.ShiftCtrlZ) ?? string.Empty;

      try
      {
        _restoreNeeded = true;
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.CtrlZ, "'_vNotchesUndo");
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.CtrlY, "'_vNotchesRedo");
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.ShiftCtrlZ, "'_vNotchesRedo");
        vTools.Log.Write("vNotches", "installed temporary history shortcuts");
      }
      catch (Exception ex)
      {
        vTools.Log.Write("vNotches", $"failed to install history shortcuts: {ex.Message}");
        Dispose();
      }
    }

    public void Dispose()
    {
      if (!_restoreNeeded) return;
      _restoreNeeded = false;
      try
      {
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.CtrlZ, _undoMacro);
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.CtrlY, _redoMacro);
        Rhino.ApplicationSettings.ShortcutKeySettings.SetMacro(
          Rhino.ApplicationSettings.ShortcutKey.ShiftCtrlZ, _alternateRedoMacro);
        vTools.Log.Write("vNotches", "restored history shortcuts");
      }
      catch (Exception ex)
      {
        vTools.Log.Write("vNotches", $"failed to restore history shortcuts: {ex.Message}");
      }
    }
  }

  // ── Eto panel ─────────────────────────────────────────────────────────────

  sealed class NotchPanel : Eto.Forms.Form
  {
    readonly NotchSession _s;
    bool _suppress;

    // Controls
    readonly DropDown    _typeDropDown;
    readonly TextBox     _lengthBox, _offsetBox, _widthBox;
    readonly DropDown    _notchLayerDrop;
    readonly CheckBox    _percentCheck, _groupCheck;
    readonly CheckBox    _labelCheck, _autoAdvCheck, _sideFlipCheck;
    System.Windows.Controls.CheckBox? _labelHeaderCheck;
    readonly TextBox     _labelValueBox;
    readonly DropDown    _labelLayerDrop;
    readonly TextBox     _labelSizeBox;
    readonly CheckBox    _labelSizeAutoCheck;
    readonly DropDown    _labelSizePctDrop;
    readonly TextBox     _labelOffsetBox, _labelOffsetYBox;
    readonly NumericStepper _multipleStartOffsetStepper, _multipleEndOffsetStepper, _multipleNumberStepper;
    readonly Label       _multipleDistanceLabel;
    readonly Button      _multipleAddButton;
    readonly Label       _fromStartLbl, _fromEndLbl, _fromPrevLbl;
    readonly Button      _undoBtn, _redoBtn, _selectCurvesButton;
    readonly CheckBox[]  _sideChecks;
    readonly Button[]    _reverseButtons;
    readonly CheckBox[]  _enableChecks;
    readonly Label[]     _curveLengthLabels;

    public NotchPanel(RhinoDoc doc, NotchSession s)
    {
      _s = s;
      Title     = "Notches";
      Padding   = new Eto.Drawing.Padding(0);
      Resizable = true;
      Topmost   = true;
      ClientSize= new Eto.Drawing.Size(280, -1);

      // Type
      _typeDropDown = new DropDown();
      _typeDropDown.DataStore = s.NotchTypeValues;
      _typeDropDown.SelectedIndex = s.NotchTypeIndex;
      _typeDropDown.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress) return;
        s.NotchTypeIndex = Math.Max(0, _typeDropDown.SelectedIndex);
        ApplyDynamic();
        Redraw();
        Persist();
      };

      // Numeric fields
      _lengthBox = MakeTextBox($"{s.NotchLengthOpt.CurrentValue:G}");
      _offsetBox = MakeTextBox($"{s.NotchOffsetOpt.CurrentValue:G}");
      _widthBox  = MakeTextBox($"{s.NotchWidthOpt.CurrentValue:G}");

      AttachNumericLive(_lengthBox,
        () => s.NotchLengthOpt.CurrentValue,
        v => s.NotchLengthOpt.CurrentValue = v);
      AttachNumericLive(_offsetBox,
        () => s.NotchOffsetOpt.CurrentValue,
        v => s.NotchOffsetOpt.CurrentValue = v);
      AttachNumericLive(_widthBox,
        () => s.NotchWidthOpt.CurrentValue,
        v => s.NotchWidthOpt.CurrentValue = v);

      // Notch layer dropdown
      _notchLayerDrop = new DropDown();
      PopulateLayerDropDown(_notchLayerDrop, doc, s.NotchLayerName, true);
      _notchLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress) return;
        s.NotchLayerName = GetDropDownLayerName(_notchLayerDrop, s.NotchLayerName);
        Redraw();
        Persist();
      };
      _notchLayerDrop.DropDownOpening += (_, __) =>
      {
        _suppress = true;
        try { PopulateLayerDropDown(_notchLayerDrop, doc, s.NotchLayerName, true); }
        finally { _suppress = false; }
      };

      // Percent / Group
      _percentCheck = new CheckBox { Text = "Percent", Checked = s.PercentToggle.CurrentValue };
      _percentCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.PercentToggle.CurrentValue = _percentCheck.Checked == true; Redraw(); Persist(); };
      _groupCheck   = new CheckBox { Text = "Group",   Checked = s.GroupToggle.CurrentValue };
      _groupCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.GroupToggle.CurrentValue = _groupCheck.Checked == true; Redraw(); Persist(); };
      _selectCurvesButton = new Button { Text = "Select", Height = 26 };
      _selectCurvesButton.Click += (_, __) =>
      {
        CommitPendingValues();
        s.CurveSelectionRequested = true;
        RhinoApp.SetFocusToMainWindow(doc);
        if (!RhinoApp.RunScript("_Enter", false))
          s.CurveSelectionRequested = false;
      };

      // Label
      _labelCheck    = new CheckBox { Text = "", Checked = s.LabelToggle.CurrentValue };
      _labelValueBox = MakeTextBox(s.LabelValueText);
      _autoAdvCheck  = new CheckBox { ToolTip = "Auto-advance label", Checked = s.LabelAutoAdv };
      _sideFlipCheck = new CheckBox { Text = "Flip side", Checked = s.LabelSideFlip };
      _labelCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        bool enabled = _labelCheck.Checked == true;
        if (_labelHeaderCheck != null && _labelHeaderCheck.IsChecked != enabled)
          _labelHeaderCheck.IsChecked = enabled;
        s.LabelToggle.CurrentValue = enabled;
        ApplyDynamic();
        Redraw();
        Persist();
      };
      AttachTextLive(_labelValueBox, text => s.LabelValueText = text);
      _autoAdvCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelAutoAdv = _autoAdvCheck.Checked == true; Redraw(); Persist(); };
      _sideFlipCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelSideFlip = _sideFlipCheck.Checked == true; Redraw(); Persist(); };

      _labelLayerDrop = new DropDown();
      PopulateLayerDropDown(_labelLayerDrop, doc, s.LabelLayerName, false);
      _labelLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress) return;
        s.LabelLayerName = GetDropDownLayerName(_labelLayerDrop, s.LabelLayerName);
        Redraw();
        Persist();
      };
      _labelLayerDrop.DropDownOpening += (_, __) =>
      {
        _suppress = true;
        try { PopulateLayerDropDown(_labelLayerDrop, doc, s.LabelLayerName, false); }
        finally { _suppress = false; }
      };

      _labelSizeBox = MakeTextBox($"{s.ManualLabelSize:G}");
      _labelSizeBox.Width = 50;
      AttachNumericLive(_labelSizeBox,
        () => s.ManualLabelSize,
        v => s.ManualLabelSize = Math.Max(0, v));

      _labelSizeAutoCheck = new CheckBox { Text = "Auto", Checked = s.LabelSizeAutoToggle.CurrentValue };
      _labelSizeAutoCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelSizeAutoToggle.CurrentValue = _labelSizeAutoCheck.Checked == true; ApplyDynamic(); Redraw(); Persist(); };

      _labelSizePctDrop = new DropDown();
      _labelSizePctDrop.DataStore = s.LabelSizePctTexts;
      _labelSizePctDrop.SelectedIndex = Math.Max(0, s.LabelSizePctIndex);
      _labelSizePctDrop.SelectedIndexChanged += (_, __) =>
      { if (_suppress) return; s.LabelSizePctIndex = _labelSizePctDrop.SelectedIndex; Redraw(); Persist(); };

      _labelOffsetBox  = MakeTextBox($"{s.LabelOffsetOpt.CurrentValue:G}");
      _labelOffsetYBox = MakeTextBox($"{s.LabelOffsetYOpt.CurrentValue:G}");
      AttachNumericLive(_labelOffsetBox,
        () => s.LabelOffsetOpt.CurrentValue,
        v => s.LabelOffsetOpt.CurrentValue = v);
      AttachNumericLive(_labelOffsetYBox,
        () => s.LabelOffsetYOpt.CurrentValue,
        v => s.LabelOffsetYOpt.CurrentValue = v);

      // Multiple notches
      _multipleStartOffsetStepper = MakeOffsetStepper(s.MultipleStartOffset);
      _multipleEndOffsetStepper = MakeOffsetStepper(s.MultipleEndOffset);
      _multipleNumberStepper = new NumericStepper
      {
        Value = s.MultipleNumber,
        MinValue = 2,
        MaxValue = 10000,
        Increment = 1,
        DecimalPlaces = 0,
        Width = 90,
        Height = 22,
      };
      _multipleDistanceLabel = new Label { Text = "-", VerticalAlignment = VerticalAlignment.Center };
      _multipleAddButton = new Button { Text = "Add", Height = 26 };

      _multipleStartOffsetStepper.ValueChanged += (_, __) =>
      {
        if (_suppress) return;
        s.MultipleStartOffset = _multipleStartOffsetStepper.Value;
        UpdateMultipleState();
        Persist();
      };
      _multipleEndOffsetStepper.ValueChanged += (_, __) =>
      {
        if (_suppress) return;
        s.MultipleEndOffset = _multipleEndOffsetStepper.Value;
        UpdateMultipleState();
        Persist();
      };
      _multipleNumberStepper.ValueChanged += (_, __) =>
      {
        if (_suppress) return;
        s.MultipleNumber = Math.Clamp((int)Math.Round(_multipleNumberStepper.Value), 2, 10000);
        UpdateMultipleState();
        Persist();
      };
      _multipleAddButton.Click += (_, __) =>
      {
        CommitPendingValues();
        SyncFromSession();
        PlaceMultipleNotches(doc, s);
        Persist();
        UpdateMultipleState();
      };

      // Distance labels
      _fromStartLbl = new Label { Text = "-" };
      _fromEndLbl   = new Label { Text = "-" };
      _fromPrevLbl  = new Label { Text = "-" };

      // History buttons
      _undoBtn = new Button { Text = "Undo", Width = 54, Height = 24 };
      _undoBtn.Click += (_, __) =>
      {
        RunLocalHistory(doc, redo: false, source: "panel-undo");
      };
      _redoBtn = new Button { Text = "Redo", Width = 54, Height = 24 };
      _redoBtn.Click += (_, __) =>
      {
        RunLocalHistory(doc, redo: true, source: "panel-redo");
      };
      UpdateUndoEnabled();

      // Side/Reverse/Enable per curve
      _sideChecks     = new CheckBox[s.Curves.Count];
      _reverseButtons = new Button[s.Curves.Count];
      _enableChecks   = new CheckBox[s.Curves.Count];
      _curveLengthLabels = new Label[s.Curves.Count];
      for (int i = 0; i < s.Curves.Count; i++)
      {
        int ci = i;
        _sideChecks[i] = new CheckBox { Text = $"Side {i + 1}", Checked = s.CurveSides[i] };
        _sideChecks[i].CheckedChanged += (_, __) =>
        {
          if (_suppress) return;
          s.RedoBatches.Clear();
          s.CurveSides[ci] = _sideChecks[ci].Checked == true;
          RebuildCurveNotches(doc, s, ci);
          SelectBothCurves(doc, s);
          UpdateUndoEnabled();
          Redraw();
          Persist();
        };
        _reverseButtons[i] = new Button { Text = $"Reverse {i + 1}", Height = 26 };
        _reverseButtons[i].Click += (_, __) =>
        {
          ReverseCurve(doc, s, ci);
          _suppress = true;
          try { _sideChecks[ci].Checked = s.CurveSides[ci]; }
          finally { _suppress = false; }
          Redraw();
          Persist();
        };
        _curveLengthLabels[i] = new Label
        {
          Text = FormatPanelNumber(s.Curves[i].GetLength()),
          VerticalAlignment = VerticalAlignment.Center,
          TextAlignment = TextAlignment.Right,
        };
        if (s.Curves.Count > 1)
        {
          _enableChecks[i] = new CheckBox { Checked = true, ToolTip = "Enable notch on this curve" };
          _enableChecks[i].CheckedChanged += (_, __) =>
          {
            if (_suppress) return;
            s.CurveEnabled[ci] = _enableChecks[ci].Checked == true;
            // RebuildCurveNotches(doc, s, ci);
            UpdateMultipleState();
            Redraw();
            Persist();
          };
        }
      }

      // Layout
      Content = BuildLayout();
      MinimumSize = new Eto.Drawing.Size(280, 0);
      ApplyDynamic();

      KeyDown += (_, e) =>
      {
        if (!e.Control || InputEditorFocused()) return;
        bool redo = e.Key == Keys.Y || (e.Key == Keys.Z && e.Shift);
        bool undo = e.Key == Keys.Z && !e.Shift;
        if (!undo && !redo) return;
        RunLocalHistory(doc, redo, "panel");
        e.Handled = true;
      };

      Closed += (_, __) =>
      {
        if (!s.SuppressPanelCloseExit)
        {
          CommitPendingValues();
          SaveOptions(s);
          s.PanelClosedExit = true;
          try { RhinoApp.RunScript("_Cancel", false); } catch { }
        }
      };
    }

    bool InputEditorFocused() =>
      _lengthBox.HasFocus ||
      _offsetBox.HasFocus ||
      _widthBox.HasFocus ||
      _labelValueBox.HasFocus ||
      _labelSizeBox.HasFocus ||
      _labelOffsetBox.HasFocus ||
      _labelOffsetYBox.HasFocus ||
      _multipleStartOffsetStepper.HasFocus ||
      _multipleEndOffsetStepper.HasFocus ||
      _multipleNumberStepper.HasFocus;

    Control BuildLayout()
    {
      // ── Notch group ──────────────────────────────────────────────────────
      var notchTable = new TableLayout { Padding = new Eto.Drawing.Padding(6), Spacing = new Eto.Drawing.Size(6, 4) };
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Type"),   new TableCell(_typeDropDown,   true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer"),  new TableCell(_notchLayerDrop, true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Length"), new TableCell(_lengthBox,      true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Width"),  new TableCell(_widthBox,       true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset"), new TableCell(_offsetBox,      true) } });
      var notchGroup = new GroupBox { Text = "", Content = notchTable };
      InstallCollapsibleGroupHeader(notchGroup, notchTable, "Notch",
        () => _s.NotchCollapsed, value => _s.NotchCollapsed = value);

      // ── Label group ──────────────────────────────────────────────────────
      var labelHeader = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      labelHeader.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelValueBox, false),
        new TableCell(_autoAdvCheck,  false),
        new TableCell(_sideFlipCheck, false),
        new TableCell(null,           true),   // filler â€” absorbs extra width
      } });

      var sizeRow = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      sizeRow.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelSizeBox,       false),
        new TableCell(_labelSizeAutoCheck, false),
        new TableCell(_labelSizePctDrop,   false),
        new TableCell(null,                true),   // filler
      } });

      var labelTable = new TableLayout
      {
        Padding = new Eto.Drawing.Padding(6),
        Spacing = new Eto.Drawing.Size(6, 4),
      };
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL(""),         new TableCell(labelHeader,       true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer"),    new TableCell(_labelLayerDrop,   true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Size"),     new TableCell(sizeRow,           true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset X"), new TableCell(_labelOffsetBox,   true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset Y"), new TableCell(_labelOffsetYBox,  true) } });
      var labelGroup = new GroupBox { Text = "", Content = labelTable };
      InstallCollapsibleGroupHeader(labelGroup, labelTable, "Label",
        () => _s.LabelCollapsed, value => _s.LabelCollapsed = value,
        labelToggle: true);

      // ── Multiple group ───────────────────────────────────────────────────
      var multipleTable = new TableLayout { Padding = new Eto.Drawing.Padding(6), Spacing = new Eto.Drawing.Size(6, 4) };
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Start offset"), new TableCell(_multipleStartOffsetStepper, true) } });
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("End offset"),   new TableCell(_multipleEndOffsetStepper,   true) } });
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Number"),       new TableCell(_multipleNumberStepper,      true) } });
      var distanceStack = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        Items =
        {
          new StackLayoutItem(new Label { Text = "Distance" }, false),
          new StackLayoutItem(_multipleDistanceLabel, false),
        },
      };
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(distanceStack, false), new TableCell(_multipleAddButton, false) } });
      var multipleGroup = new GroupBox { Text = "", Content = multipleTable };
      InstallCollapsibleGroupHeader(multipleGroup, multipleTable, "Multiple",
        () => _s.MultipleCollapsed, value => _s.MultipleCollapsed = value);

      // ── Percent / Group ──────────────────────────────────────────────────
      var pgStack = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 10,
        VerticalContentAlignment = VerticalAlignment.Center };
      pgStack.Items.Add(new StackLayoutItem(_percentCheck, false));
      pgStack.Items.Add(new StackLayoutItem(_groupCheck,   false));
      pgStack.Items.Add(new StackLayoutItem(_selectCurvesButton, false));
      pgStack.Items.Add(new StackLayoutItem(null,          true));

      // ── Per-curve rows ───────────────────────────────────────────────────
      var curveStack = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
      for (int i = 0; i < _s.Curves.Count; i++)
      {
        var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6,
          VerticalContentAlignment = VerticalAlignment.Center };
        if (_s.Curves.Count > 1 && _enableChecks[i] != null)
          row.Items.Add(new StackLayoutItem(_enableChecks[i], false));
        row.Items.Add(new StackLayoutItem(_sideChecks[i],   false));
        row.Items.Add(new StackLayoutItem(_reverseButtons[i],false));
        row.Items.Add(new StackLayoutItem(null, true));
        row.Items.Add(new StackLayoutItem(_curveLengthLabels[i], false));
        curveStack.Items.Add(new StackLayoutItem(row));
      }

      // ── Distance info ────────────────────────────────────────────────────
      var distTable = new TableLayout { Spacing = new Eto.Drawing.Size(6, 2) };
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From start"),    new TableCell(_fromStartLbl, true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From end"),      new TableCell(_fromEndLbl,   true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From previous"), new TableCell(_fromPrevLbl,  true) } });
      var historyButtons = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = 2,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
          new StackLayoutItem(_undoBtn, false),
          new StackLayoutItem(_redoBtn, false),
        },
      };
      var infoRow = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
          new StackLayoutItem(distTable, true),
          new StackLayoutItem(historyButtons, false),
        },
      };

      // ── Root (vertical stack, no bottom spacer) ──────────────────────────
      var root = new StackLayout
      {
        Orientation = Orientation.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Spacing = 6,
        Padding = new Eto.Drawing.Padding(6),
      };
      root.Items.Add(new StackLayoutItem(notchGroup, false));
      root.Items.Add(new StackLayoutItem(labelGroup, false));
      root.Items.Add(new StackLayoutItem(multipleGroup, false));
      root.Items.Add(new StackLayoutItem(pgStack,    false));
      root.Items.Add(new StackLayoutItem(curveStack, false));
      root.Items.Add(new StackLayoutItem(infoRow,    false));

      return root;
    }

    static TableCell FL(string text) =>
      new TableCell(new Label { Text = text, VerticalAlignment = VerticalAlignment.Center });

    void Redraw() => _s.Doc.Views.Redraw();

    void ApplyDynamic()
    {
      UpdateMultipleState();
    }

    static TextBox MakeTextBox(string text) =>
      new TextBox { Text = text, Width = 70, Height = 22 };

    static NumericStepper MakeOffsetStepper(double value) =>
      new NumericStepper
      {
        Value = Math.Max(0.0, value),
        MinValue = 0.0,
        MaxValue = 1e9,
        Increment = 0.1,
        DecimalPlaces = 3,
        MaximumDecimalPlaces = 6,
        Width = 90,
        Height = 22,
      };

    void InstallCollapsibleGroupHeader(GroupBox group, Control content, string title,
      Func<bool> getCollapsed, Action<bool> setCollapsed, bool labelToggle = false)
    {
      System.Windows.Controls.StackPanel? headerPanel = null;
      System.Windows.Controls.Button? collapseButton = null;
      System.Windows.Controls.GroupBox? nativeGroup = null;

      static System.Windows.Shapes.Polyline DisclosureChevron(bool collapsed)
      {
        var points = new System.Windows.Media.PointCollection();
        if (collapsed)
        {
          points.Add(new System.Windows.Point(4, 2));
          points.Add(new System.Windows.Point(8, 6));
          points.Add(new System.Windows.Point(4, 10));
        }
        else
        {
          points.Add(new System.Windows.Point(2, 4));
          points.Add(new System.Windows.Point(6, 8));
          points.Add(new System.Windows.Point(10, 4));
        }

        return new System.Windows.Shapes.Polyline
        {
          Points = points,
          Stroke = System.Windows.SystemColors.ControlTextBrush,
          StrokeThickness = 1.5,
          StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
          StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
          Width = 12,
          Height = 12,
        };
      }

      void ApplyCollapsedState()
      {
        bool collapsed = getCollapsed();
        content.Visible = !collapsed;
        if (collapseButton != null)
        {
          collapseButton.Content = DisclosureChevron(collapsed);
          collapseButton.ToolTip = collapsed ? $"Restore {title}" : $"Collapse {title}";
        }
        nativeGroup?.InvalidateMeasure();
        if (Loaded)
          Application.Instance.AsyncInvoke(ResizePanelToContent);
      }

      void Install()
      {
        if (group.ControlObject is not System.Windows.Controls.GroupBox native)
          return;
        nativeGroup = native;

        if (headerPanel == null)
        {
          headerPanel = new System.Windows.Controls.StackPanel
          {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
          };

          collapseButton = new System.Windows.Controls.Button
          {
            Content = DisclosureChevron(getCollapsed()),
            Width = 18,
            Height = 18,
            Padding = new System.Windows.Thickness(0),
            Margin = new System.Windows.Thickness(0, 0, 3, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = false,
          };
          collapseButton.Click += (_, __) =>
          {
            setCollapsed(!getCollapsed());
            ApplyCollapsedState();
          };
          headerPanel.Children.Add(collapseButton);

          if (labelToggle)
          {
            var headerCheck = new System.Windows.Controls.CheckBox
            {
              Content = title,
              IsChecked = _s.LabelToggle.CurrentValue,
              VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            headerCheck.Checked += (_, __) => SetLabelEnabledFromHeader(true);
            headerCheck.Unchecked += (_, __) => SetLabelEnabledFromHeader(false);
            _labelHeaderCheck = headerCheck;
            headerPanel.Children.Add(headerCheck);
          }
          else
          {
            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
              Text = title,
              VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
          }
        }

        native.Header = headerPanel;
        ApplyCollapsedState();
      }

      content.Visible = !getCollapsed();
      Install();
      group.Load += (_, __) => Install();
    }

    void ResizePanelToContent()
    {
      if (Content == null)
        return;
      Content.UpdateLayout();
      var preferred = Content.GetPreferredSize();
      int height = Math.Max(1, (int)Math.Ceiling(preferred.Height));
      ClientSize = new Eto.Drawing.Size(Math.Max(280, ClientSize.Width), height);
    }

    void SetLabelEnabledFromHeader(bool enabled)
    {
      if (_suppress)
        return;
      if (_labelCheck.Checked != enabled)
        _labelCheck.Checked = enabled;
    }

    void AttachNumericLive(TextBox box, Func<double> current, Action<double> apply)
    {
      void ApplyPreview()
      {
        if (_suppress) return;

        if (double.TryParse(box.Text,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out double v))
        {
          apply(v);
          Redraw();
        }
      }

      void ApplyCommit()
      {
        if (_suppress) return;

        double v = ParseDouble(box.Text, current());
        apply(v);

        _suppress = true;
        try { box.Text = $"{v:G}"; }
        finally { _suppress = false; }

        Redraw();
        Persist();
      }

      box.TextChanged += (_, __) => ApplyPreview();
      box.LostFocus   += (_, __) => ApplyCommit();
      box.KeyDown     += (_, e) =>
      {
        if (e.Key == Keys.Enter)
        {
          ApplyCommit();
          e.Handled = true;
        }
      };
    }

    void AttachTextLive(TextBox box, Action<string> apply)
    {
      void ApplyPreview()
      {
        if (_suppress) return;
        apply(box.Text);
        Redraw();
      }

      void ApplyCommit()
      {
        if (_suppress) return;
        apply(box.Text);
        Redraw();
        Persist();
      }

      box.TextChanged += (_, __) => ApplyPreview();
      box.LostFocus   += (_, __) => ApplyCommit();
      box.KeyDown     += (_, e) =>
      {
        if (e.Key == Keys.Enter)
        {
          ApplyCommit();
          e.Handled = true;
        }
      };
    }

    void UpdateMultipleState()
    {
      double startOffset = _multipleStartOffsetStepper.Value;
      double endOffset = _multipleEndOffsetStepper.Value;
      int number = Math.Clamp((int)Math.Round(_multipleNumberStepper.Value), 2, 10000);
      bool valuesValid = true;

      var spacings = new List<double>();
      if (valuesValid)
      {
        for (int i = 0; i < _s.Curves.Count; i++)
        {
          if (i < _s.CurveEnabled.Length && !_s.CurveEnabled[i])
            continue;
          double available = _s.Curves[i].GetLength() - startOffset - endOffset;
          if (available <= _s.Doc.ModelAbsoluteTolerance)
          {
            valuesValid = false;
            break;
          }
          spacings.Add(available / (number - 1));
        }
      }

      if (spacings.Count == 0)
        valuesValid = false;

      if (!valuesValid)
      {
        _multipleDistanceLabel.Text = "-";
      }
      else
      {
        double min = spacings.Min();
        double max = spacings.Max();
        string minText = FormatPanelNumber(min);
        string maxText = FormatPanelNumber(max);
        _multipleDistanceLabel.Text = Math.Abs(max - min) <= _s.Doc.ModelAbsoluteTolerance
          ? minText
          : $"{minText} - {maxText}";
      }

    }

    static double ParseDouble(string text, double fallback)
    {
      if (double.TryParse(text, System.Globalization.NumberStyles.Any,
          System.Globalization.CultureInfo.InvariantCulture, out double v))
        return v;
      return fallback;
    }

    static void PopulateLayerDropDown(DropDown drop, RhinoDoc doc, string currentName, bool includeCurrentSpecial)
    {
      var items = new List<ListItem>();
      if (includeCurrentSpecial)
        items.Add(new ListItem { Text = SpecialLayerCurrent, Key = SpecialLayerCurrent });
      foreach (var layer in doc.Layers)
        if (!layer.IsDeleted)
          items.Add(new ListItem { Text = layer.FullPath, Key = layer.FullPath });
      drop.DataStore = items;
      // Restore selection
      int sel = items.FindIndex(i => i.Key == currentName);
      drop.SelectedIndex = sel >= 0 ? sel : 0;
    }

    static string GetDropDownLayerName(DropDown drop, string fallback)
    {
      if (drop.SelectedIndex < 0) return fallback;
      var items = drop.DataStore?.Cast<ListItem>().ToList();
      if (items == null || drop.SelectedIndex >= items.Count) return fallback;
      return items[drop.SelectedIndex].Key ?? fallback;
    }

    public void SyncFromSession()
    {
      _suppress = true;
      try
      {
        _typeDropDown.SelectedIndex    = _s.NotchTypeIndex;
        _lengthBox.Text                = $"{_s.NotchLengthOpt.CurrentValue:G}";
        _offsetBox.Text                = $"{_s.NotchOffsetOpt.CurrentValue:G}";
        _widthBox.Text                 = $"{_s.NotchWidthOpt.CurrentValue:G}";
        _percentCheck.Checked          = _s.PercentToggle.CurrentValue;
        _groupCheck.Checked            = _s.GroupToggle.CurrentValue;
        _labelCheck.Checked            = _s.LabelToggle.CurrentValue;
        if (_labelHeaderCheck != null)
          _labelHeaderCheck.IsChecked = _s.LabelToggle.CurrentValue;
        _labelValueBox.Text            = _s.LabelValueText;
        _labelSizeBox.Text             = $"{_s.ManualLabelSize:G}";
        _labelSizeAutoCheck.Checked    = _s.LabelSizeAutoToggle.CurrentValue;
        if (_labelSizePctDrop.SelectedIndex != _s.LabelSizePctIndex)
          _labelSizePctDrop.SelectedIndex = Math.Max(0, _s.LabelSizePctIndex);
        _labelOffsetBox.Text           = $"{_s.LabelOffsetOpt.CurrentValue:G}";
        _labelOffsetYBox.Text          = $"{_s.LabelOffsetYOpt.CurrentValue:G}";
        _autoAdvCheck.Checked          = _s.LabelAutoAdv;
        _sideFlipCheck.Checked         = _s.LabelSideFlip;
        _multipleStartOffsetStepper.Value = _s.MultipleStartOffset;
        _multipleEndOffsetStepper.Value   = _s.MultipleEndOffset;
        _multipleNumberStepper.Value      = _s.MultipleNumber;
        for (int i = 0; i < _sideChecks.Length; i++)
          if (i < _s.CurveSides.Length) _sideChecks[i].Checked = _s.CurveSides[i];
        if (_s.Curves.Count > 1)
          for (int i = 0; i < _enableChecks.Length; i++)
            if (_enableChecks[i] != null && i < _s.CurveEnabled.Length)
              _enableChecks[i].Checked = _s.CurveEnabled[i];
        ApplyDynamic();
        UpdateUndoEnabled();
      }
      finally { _suppress = false; }
    }

    public void CommitPendingValues()
    {
      if (_suppress) return;
      _s.NotchTypeIndex = Math.Max(0, _typeDropDown.SelectedIndex);
      _s.NotchLengthOpt.CurrentValue = ParseDouble(_lengthBox.Text, _s.NotchLengthOpt.CurrentValue);
      _s.NotchOffsetOpt.CurrentValue = ParseDouble(_offsetBox.Text, _s.NotchOffsetOpt.CurrentValue);
      _s.NotchWidthOpt.CurrentValue = ParseDouble(_widthBox.Text, _s.NotchWidthOpt.CurrentValue);
      _s.NotchLayerName = GetDropDownLayerName(_notchLayerDrop, _s.NotchLayerName);
      _s.PercentToggle.CurrentValue = _percentCheck.Checked == true;
      _s.GroupToggle.CurrentValue = _groupCheck.Checked == true;
      _s.LabelToggle.CurrentValue = _labelCheck.Checked == true;
      _s.LabelValueText = _labelValueBox.Text;
      _s.LabelAutoAdv = _autoAdvCheck.Checked == true;
      _s.LabelSideFlip = _sideFlipCheck.Checked == true;
      _s.LabelLayerName = GetDropDownLayerName(_labelLayerDrop, _s.LabelLayerName);
      _s.ManualLabelSize = Math.Max(0, ParseDouble(_labelSizeBox.Text, _s.ManualLabelSize));
      _s.LabelSizeAutoToggle.CurrentValue = _labelSizeAutoCheck.Checked == true;
      _s.LabelSizePctIndex = Math.Max(0, _labelSizePctDrop.SelectedIndex);
      _s.LabelOffsetOpt.CurrentValue = ParseDouble(_labelOffsetBox.Text, _s.LabelOffsetOpt.CurrentValue);
      _s.LabelOffsetYOpt.CurrentValue = ParseDouble(_labelOffsetYBox.Text, _s.LabelOffsetYOpt.CurrentValue);
      _s.MultipleStartOffset = _multipleStartOffsetStepper.Value;
      _s.MultipleEndOffset = _multipleEndOffsetStepper.Value;
      _s.MultipleNumber = Math.Clamp((int)Math.Round(_multipleNumberStepper.Value), 2, 10000);
    }

    void Persist()
    {
      CommitPendingValues();
      SaveOptions(_s);
    }

    public void UpdateDistanceLabels(double? current, double? prevDelta, double? otherEnd)
    {
      _fromStartLbl.Text = current.HasValue ? FormatPanelNumber(current.Value) : "-";
      _fromEndLbl.Text = otherEnd.HasValue ? FormatPanelNumber(otherEnd.Value) : "-";
      _fromPrevLbl.Text = prevDelta.HasValue ? FormatPanelNumber(prevDelta.Value) : "-";
    }

    public void UpdateUndoEnabled()
    {
      _undoBtn.Enabled = _s.NotchRecords.Count > 0;
      _redoBtn.Enabled = _s.RedoBatches.Count > 0;
    }
  }
}

[CommandStyle(Style.Hidden | Style.Transparent | Style.NotUndoable | Style.DoNotRepeat)]
public sealed class vNotchesUndo : Rhino.Commands.Command
{
  public override string EnglishName => "vNotchesUndo";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    vNotches.RunLocalHistory(doc, redo: false, source: "shortcut");
}

[CommandStyle(Style.Hidden | Style.Transparent | Style.NotUndoable | Style.DoNotRepeat)]
public sealed class vNotchesRedo : Rhino.Commands.Command
{
  public override string EnglishName => "vNotchesRedo";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    vNotches.RunLocalHistory(doc, redo: true, source: "shortcut");
}

