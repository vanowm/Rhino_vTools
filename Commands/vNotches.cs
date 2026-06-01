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
  static bool[] _curveSides     = Array.Empty<bool>();

  public override string EnglishName => "vNotches";

  // ── Settings ─────────────────────────────────────────────────────────────

  static void LoadOptions(RhinoDoc doc)
  {
    vToolsOptionStore.Read<int>(Section, s =>
    {
      if (vToolsOptionStore.TryGetDouble(s, "notch_length",    out var v)) _notchLength   = v;
      if (vToolsOptionStore.TryGetDouble(s, "notch_offset",    out v))     _notchOffset   = v;
      if (vToolsOptionStore.TryGetDouble(s, "notch_width",     out v))     _notchWidth    = v;
      if (vToolsOptionStore.TryGetString(s, "notch_type",      out var t)) _notchType     = t;
      if (vToolsOptionStore.TryGetBool  (s, "percent",         out var b)) _percent       = b;
      if (vToolsOptionStore.TryGetBool  (s, "group",           out b))     _group         = b;
      if (vToolsOptionStore.TryGetBool  (s, "label",           out b))     _label         = b;
      if (vToolsOptionStore.TryGetString(s, "label_value",     out t))     _labelValue    = t;
      if (vToolsOptionStore.TryGetDouble(s, "label_size",      out v))     _labelSize     = v;
      if (vToolsOptionStore.TryGetBool  (s, "label_size_auto", out b))     _labelSizeAuto = b;
      if (vToolsOptionStore.TryGetDouble(s, "label_size_pct",  out var pctv))_labelSizePct  = (int)pctv;
      if (vToolsOptionStore.TryGetString(s, "notch_layer",     out t))     _notchLayer    = t;
      if (vToolsOptionStore.TryGetString(s, "label_layer",     out t))     _labelLayer    = t;
      if (vToolsOptionStore.TryGetDouble(s, "label_offset",    out v))     _labelOffset   = v;
      if (vToolsOptionStore.TryGetDouble(s, "label_offset_y",  out v))     _labelOffsetY  = v;
      if (vToolsOptionStore.TryGetBool  (s, "label_auto_adv",  out b))     _labelAutoAdv  = b;
      if (vToolsOptionStore.TryGetBool  (s, "label_side_flip", out b))     _labelSideFlip = b;
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
    vToolsOptionStore.Update(Section, sec =>
    {
      sec["notch_length"]    = s.NotchLengthOpt.CurrentValue;
      sec["notch_offset"]    = s.NotchOffsetOpt.CurrentValue;
      sec["notch_width"]     = s.NotchWidthOpt.CurrentValue;
      sec["notch_type"]      = s.NotchTypeValues[s.NotchTypeIndex];
      sec["percent"]         = s.PercentToggle.CurrentValue;
      sec["group"]           = s.GroupToggle.CurrentValue;
      sec["label"]           = s.LabelToggle.CurrentValue;
      sec["label_value"]     = s.LabelValueText;
      sec["label_size"]      = s.ManualLabelSize;
      sec["label_size_auto"] = s.LabelSizeAutoToggle.CurrentValue;
      sec["label_size_pct"]  = s.LabelSizePctValues[s.LabelSizePctIndex];
      sec["notch_layer"]     = s.NotchLayerName;
      sec["label_layer"]     = s.LabelLayerName;
      sec["label_offset"]    = s.LabelOffsetOpt.CurrentValue;
      sec["label_offset_y"]  = s.LabelOffsetYOpt.CurrentValue;
      sec["label_auto_adv"]  = s.LabelAutoAdv;
      sec["label_side_flip"] = s.LabelSideFlip;
      var arr = new System.Text.Json.Nodes.JsonArray();
      foreach (var b in s.CurveSides) arr.Add(b);
      sec["curve_sides"] = arr;
    });
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

    // Build initial curve sides — reuse stored per-curve values if count matches
    var initialSides = new bool[curves.Count];
    for (int i = 0; i < curves.Count; i++)
    {
      if (i < _curveSides.Length) initialSides[i] = _curveSides[i];
      else if (_curveSides.Length > 0) initialSides[i] = _curveSides[_curveSides.Length - 1];
      else initialSides[i] = false; // false = Left
    }

    var session = new NotchSession(doc, curves, curveIds, initialSides,
      _notchLength, _notchOffset, _notchWidth, _notchType,
      _percent, _group, _label, _labelValue,
      _labelSize, _labelSizeAuto, _labelSizePct,
      _notchLayer, _labelLayer, _labelOffset, _labelOffsetY,
      _labelAutoAdv, _labelSideFlip);

    RunLoop(doc, session);
    SaveOptions(session);

    // Deselect source curves
    foreach (var id in curveIds)
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
    go.SetCommandPrompt("Select one or more curves (near start)");
    go.GeometryFilter = ObjectType.Curve;
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

  // ── Main interactive loop ─────────────────────────────────────────────────

  static void RunLoop(RhinoDoc doc, NotchSession s)
  {
    var gp = new GetPoint();
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

    try
    {
      while (true)
      {
        if (s.PanelClosedExit) { FinalizeBlocks(doc, s); return; }

        RefreshCommandOptions(gp, s);
        var result = gp.Get();

        if (s.RefreshCommandLine) { s.RefreshCommandLine = false; continue; }
        if (s.PanelClosedExit)    { FinalizeBlocks(doc, s); return; }

        if (gp.CommandResult() != Result.Success) { FinalizeBlocks(doc, s); return; }

        if (result == GetResult.Nothing)
        {
          if (s.PanelNumericPending) { s.PanelNumericPending = false; continue; }
          if (s.IgnoreNextNothing)   { s.IgnoreNextNothing   = false; continue; }
          // Enter pressed on command line — done
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

        // Point picked — place notch(es)
        PlaceNotchAtPoint(doc, gp.Point(), s);
      }
    }
    finally
    {
      FinalizeBlocks(doc, s);
      if (panel != null)
      {
        s.SuppressPanelCloseExit = true;
        try { panel.Close(); } catch { }
      }
    }
  }

  // ── Command options ───────────────────────────────────────────────────────

  static void RefreshCommandOptions(GetPoint gp, NotchSession s)
  {
    gp.ClearCommandOptions();
    s.SideOptionIndex        = gp.AddOption("Side");
    s.ReverseOptionIndex     = gp.AddOption("Reverse");
    s.UndoOptionIndex        = gp.AddOption("Undo");
    s.TypeOptionIndex        = gp.AddOptionList("NotchType", s.NotchTypeValues, s.NotchTypeIndex);
    s.NotchLayerOptionIndex  = gp.AddOption("NotchLayer", s.NotchLayerName);
    gp.AddOptionDouble("NotchLength", ref s.NotchLengthOpt);
    if (s.NotchTypeValues[s.NotchTypeIndex] != "I")
      gp.AddOptionDouble("NotchWidth", ref s.NotchWidthOpt);
    gp.AddOptionDouble("NotchOffset", ref s.NotchOffsetOpt);
    gp.AddOptionToggle("LabelEnabled", ref s.LabelToggle);
    s.LabelValueOptionIndex  = gp.AddOption("Label", s.LabelValueText);
    s.LabelLayerOptionIndex  = -1;
    s.LabelSizeAutoIndex     = -1;
    s.LabelSizePctIndex2     = -1;
    if (s.LabelToggle.CurrentValue)
    {
      s.LabelLayerOptionIndex = gp.AddOption("LabelLayer", s.LabelLayerName);
      s.LabelSizeAutoIndex    = gp.AddOptionToggle("LabelSizeMode", ref s.LabelSizeAutoToggle);
      if (s.LabelSizeAutoToggle.CurrentValue)
        s.LabelSizePctIndex2  = gp.AddOptionList("LabelSizePct", s.LabelSizePctTexts, s.LabelSizePctIndex);
      else
        gp.AddOptionDouble("LabelSize", ref s.LabelSizeOpt);
      gp.AddOptionDouble("LabelOffsetX", ref s.LabelOffsetOpt);
      gp.AddOptionDouble("LabelOffsetY", ref s.LabelOffsetYOpt);
    }
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
  }

  // ── Place notch at clicked point ──────────────────────────────────────────

  static void PlaceNotchAtPoint(RhinoDoc doc, Point3d point, NotchSession s)
  {
    ClosestCurveHit(s, point, out int refIdx, out var refCurve, out double refT);
    if (refCurve == null) return;

    double lengthFromStart = LengthFromStart(refCurve, refT);
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
    bool canLabel    = s.LabelToggle.CurrentValue && labelText.Length > 0;
    string nextLabel = labelText;
    var placementLabels = new List<string>();
    if (canLabel)
    {
      foreach (var _ in s.Curves) placementLabels.Add(labelText);
      if (s.LabelAutoAdv)
        nextLabel = IncrementLabelValue(labelText);
    }

    List<double>? lengthsFromStart = null;
    double? percent = null;

    if (s.PercentToggle.CurrentValue)
    {
      double refLen = refCurve.GetLength();
      if (refLen <= 0.0) return;
      double pct = lengthFromStart / refLen;
      lengthsFromStart = s.Curves.Select(c => c.GetLength() * pct).ToList();
      percent = pct;
    }
    else
    {
      lengthsFromStart = Enumerable.Repeat(lengthFromStart, s.Curves.Count).ToList();
    }

    doc.UndoRecordingEnabled = true;
    var undoRec = doc.BeginUndoRecord("Notch");
    List<(Guid notch, Guid? label)>? newIds = null;
    try
    {
      newIds = AddNotchesPerCurve(doc, s, sides, activeGroupIndices,
        lengthsFromStart, notchLen, notchOff, notchTyp, notchWid,
        canLabel, placementLabels, resolvedLabelSize,
        effectiveNotchLayer, effectiveLabelLayer,
        s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue,
        s.LabelSideFlip, point, s.CurveEnabled);
    }
    finally
    {
      if (undoRec >= 0) doc.EndUndoRecord(undoRec);
    }

    if (newIds == null || !newIds.Any(n => n.notch != Guid.Empty)) return;

    // Record the placement for undo
    var record = new NotchRecord
    {
      Mode            = s.PercentToggle.CurrentValue ? "percent" : "distance",
      NotchLength     = notchLen,
      NotchOffset     = notchOff,
      NotchType       = notchTyp,
      NotchWidth      = notchWid,
      GroupEnabled    = s.GroupToggle.CurrentValue,
      LabelEnabled    = canLabel,
      LabelValues     = new List<string>(placementLabels),
      LabelSize       = resolvedLabelSize,
      NotchLayer      = s.NotchLayerName,
      LabelLayer      = s.LabelLayerName,
      LabelOffset     = s.LabelOffsetOpt.CurrentValue,
      LabelOffsetY    = s.LabelOffsetYOpt.CurrentValue,
      LengthsFromStart= new List<double>(lengthsFromStart),
      Percent         = percent,
    };
    s.NotchRecords.Add(record);

    // Track IDs per curve and for undo
    for (int i = 0; i < newIds.Count; i++)
    {
      if (i < s.NotchIdsByCurve.Count)
        s.NotchIdsByCurve[i].Add(newIds[i].notch);
      if (i < s.LabelIdsByCurve.Count)
        s.LabelIdsByCurve[i].Add(newIds[i].label);
    }
    s.PlacementIds.Add(new List<Guid>(newIds.Select(n => n.notch)));
    s.PlacementLabelIds.Add(new List<Guid?>(newIds.Select(n => n.label)));

    // Advance label
    if (canLabel && s.LabelAutoAdv)
    {
      s.LabelValueText = nextLabel;
      SyncPanelFromOptions(s);
    }

    // Update undo button state
    s.Panel?.UpdateUndoEnabled();

    doc.Views.Redraw();
  }

  // ── Undo ──────────────────────────────────────────────────────────────────

  static void UndoLastNotch(RhinoDoc doc, NotchSession s)
  {
    if (s.PlacementIds.Count == 0) return;
    var lastIds      = s.PlacementIds[^1];
    var lastLabelIds = s.PlacementLabelIds[^1];
    s.PlacementIds.RemoveAt(s.PlacementIds.Count - 1);
    s.PlacementLabelIds.RemoveAt(s.PlacementLabelIds.Count - 1);

    // Restore label value from the removed record
    if (s.NotchRecords.Count > 0)
    {
      var lastRec = s.NotchRecords[^1];
      if (lastRec.LabelEnabled && lastRec.LabelValues.Count > 0)
      {
        string restoredLabel = lastRec.LabelValues.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        if (!string.IsNullOrEmpty(restoredLabel))
          s.LabelValueText = restoredLabel;
      }
      s.NotchRecords.RemoveAt(s.NotchRecords.Count - 1);
    }

    foreach (var id in lastIds)
      if (id != Guid.Empty) doc.Objects.Delete(id, true);
    foreach (var id in lastLabelIds)
      if (id.HasValue && id.Value != Guid.Empty) doc.Objects.Delete(id.Value, true);

    // Remove from per-curve tracking
    for (int i = 0; i < s.NotchIdsByCurve.Count; i++)
      if (s.NotchIdsByCurve[i].Count > 0)
        s.NotchIdsByCurve[i].RemoveAt(s.NotchIdsByCurve[i].Count - 1);
    for (int i = 0; i < s.LabelIdsByCurve.Count; i++)
      if (s.LabelIdsByCurve[i].Count > 0)
        s.LabelIdsByCurve[i].RemoveAt(s.LabelIdsByCurve[i].Count - 1);

    SyncPanelFromOptions(s);
    doc.Views.Redraw();
  }

  // ── Finalize ──────────────────────────────────────────────────────────────

  static void FinalizeBlocks(RhinoDoc doc, NotchSession s)
  {
    if (s.Finalized) return;
    s.Finalized = true;
    // No block/iDef system — just redraw
    doc.Views.Redraw();
  }

  // ── Dynamic draw preview ──────────────────────────────────────────────────

  static void DrawPreview(RhinoDoc doc, NotchSession s, GetPointDrawEventArgs e)
  {
    var point = e.CurrentPoint;
    s.LastPreviewPoint = point;

    ClosestCurveHit(s, point, out int refIdx, out var refCurve, out double refT);
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
      var geom = NotchGeometry(s.Curves[i], lengths[i], nl, no, sides[i], nt, nw, point, null);
      if (geom == null) continue;
      if (geom is LineCurve lc)           e.Display.DrawLine(lc.Line, System.Drawing.Color.Cyan, 2);
      else if (geom is PolylineCurve plc)  e.Display.DrawPolyline(plc.ToPolyline(), System.Drawing.Color.Cyan, 2);

      if (canLabel)
      {
        GetCurveTangentAndDirection(s.Curves[i], lengths[i], sides[i], point, null,
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
        var kinkPt  = curve.PointAt(t);
        var ptBefore = curve.PointAt(tBefore);
        var ptAfter  = curve.PointAt(tAfter);
        var cursorVec  = new Vector3d(cursorPoint.Value.X - kinkPt.X, cursorPoint.Value.Y - kinkPt.Y, 0.0);
        var dirBefore  = new Vector3d(ptBefore.X - kinkPt.X, ptBefore.Y - kinkPt.Y, 0.0);
        var dirAfter   = new Vector3d(ptAfter.X  - kinkPt.X, ptAfter.Y  - kinkPt.Y, 0.0);
        double projB = Vector3d.Multiply(cursorVec, dirBefore);
        double projA = Vector3d.Multiply(cursorVec, dirAfter);
        return projA >= projB ? tanAfter : tanBefore;
      }
      catch { return defaultTangent; }
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
    Point3d? cursorPoint)
  {
    GetCurveTangentAndDirection(curve, lengthFromStart, side, cursorPoint, null,
      out var tangent, out var direction);

    var geom = NotchGeometry(curve, lengthFromStart, notchLength, notchOffset,
      side, notchType, notchWidth, cursorPoint, null);
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
    bool labelSideFlip, Point3d cursorPoint, bool[] curveEnabled)
  {
    var ids = new List<(Guid, Guid?)>();
    string firstSide = sides.Count > 0 ? sides[0] : "Left";

    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (curveEnabled != null && i < curveEnabled.Length && !curveEnabled[i])
      { ids.Add((Guid.Empty, null)); continue; }

      string labelCurveSide = ResolvedLabelCurveSide(sides[i], firstSide, i);
      if (labelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";

      string lv = (canLabel && i < labelValues.Count) ? labelValues[i] : "";
      int gi    = i < groupIndices.Length ? groupIndices[i] : -1;

      var (nid, lid) = AddNotch(doc, s.Curves[i], lengths[i],
        notchLen, notchOff, sides[i], gi,
        notchTyp, notchWid,
        canLabel, lv, labelSize,
        notchLayer, labelLayer,
        labelOffset, labelOffsetY,
        labelCurveSide, cursorPoint);

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

    if (!s.CurveEnabled[curveIndex])
    {
      s.NotchIdsByCurve[curveIndex].AddRange(Enumerable.Repeat(Guid.Empty, s.NotchRecords.Count));
      s.LabelIdsByCurve[curveIndex].AddRange(Enumerable.Repeat<Guid?>(null, s.NotchRecords.Count));
      // Rebuild placement IDs
      RebuildPlacementIds(s);
      return;
    }

    var newIds      = new List<Guid>();
    var newLabelIds = new List<Guid?>();
    string side     = s.CurveSides[curveIndex] ? "Left" : "Right"; // true = Left
    string firstSide= s.CurveSides[0] ? "Left" : "Right";
    int groupIdx    = s.SessionGroupIndices[curveIndex < s.SessionGroupIndices.Length ? curveIndex : 0];

    foreach (var rec in s.NotchRecords)
    {
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
        rec.LabelOffset, rec.LabelOffsetY, labelCurveSide, null);
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
    s.CurveSides[idx] = !s.CurveSides[idx];
    RebuildCurveNotches(doc, s, idx);
    SelectBothCurves(doc, s);
  }

  static void ReverseCurve(RhinoDoc doc, NotchSession s, int idx)
  {
    if (idx < 0 || idx >= s.Curves.Count) return;
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
    if (pick == Point3d.Unset) return curve;
    double sd = pick.DistanceTo(curve.PointAtStart);
    double ed = pick.DistanceTo(curve.PointAtEnd);
    if (ed < sd) { curve = curve.DuplicateCurve(); curve.Reverse(); }
    return curve;
  }

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
    public readonly int[] SessionGroupIndices;
    // Context group indices from source curves
    public readonly int[] CurveContextGroupIndices;

    // Loop control
    public bool PanelClosedExit;
    public bool RefreshCommandLine;
    public bool PanelNumericPending;
    public bool IgnoreNextNothing;
    public bool SuppressPanelCloseExit;
    public bool Finalized;

    // Command option indices (set each iteration)
    public int SideOptionIndex, ReverseOptionIndex, UndoOptionIndex;
    public int TypeOptionIndex, NotchLayerOptionIndex;
    public int LabelValueOptionIndex, LabelLayerOptionIndex;
    public int LabelSizeAutoIndex, LabelSizePctIndex2;

    public Point3d? LastPreviewPoint;
    public NotchPanel? Panel;

    public NotchSession(RhinoDoc doc, List<Curve> curves, List<Guid> curveIds, bool[] sides,
      double notchLength, double notchOffset, double notchWidth, string notchType,
      bool percent, bool group, bool label, string labelValue,
      double labelSize, bool labelSizeAuto, int labelSizePct,
      string notchLayer, string labelLayer, double labelOffset, double labelOffsetY,
      bool labelAutoAdv, bool labelSideFlip)
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

      NotchTypeIndex  = Array.IndexOf(NotchTypeValues, notchType?.ToUpper() ?? "I");
      if (NotchTypeIndex < 0) NotchTypeIndex = 0;

      LabelSizePctValues = Enumerable.Range(4, 17).Select(i => i * 5).ToArray(); // 20..100 step 5
      LabelSizePctTexts  = LabelSizePctValues.Select(v => $"{v}%").ToArray();
      LabelSizePctIndex  = Array.FindIndex(LabelSizePctValues, v => v == labelSizePct);
      if (LabelSizePctIndex < 0)
        LabelSizePctIndex = Array.FindIndex(LabelSizePctValues,
          v => v == LabelSizePctValues.OrderBy(x => Math.Abs(x - labelSizePct)).First());

      // Group indices for session — only when group=On
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
    public double?        Percent;
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
    readonly TextBox     _labelValueBox;
    readonly DropDown    _labelLayerDrop;
    readonly TextBox     _labelSizeBox;
    readonly CheckBox    _labelSizeAutoCheck;
    readonly DropDown    _labelSizePctDrop;
    readonly TextBox     _labelOffsetBox, _labelOffsetYBox;
    readonly Label       _fromStartLbl, _fromEndLbl, _fromPrevLbl;
    readonly Button      _undoBtn;
    readonly CheckBox[]  _sideChecks;
    readonly Button[]    _reverseButtons;
    readonly CheckBox[]  _enableChecks;

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
      { if (_suppress) return; s.NotchTypeIndex = _typeDropDown.SelectedIndex; Redraw(); };

      // Numeric fields
      _lengthBox = MakeTextBox($"{s.NotchLengthOpt.CurrentValue:G}");
      _offsetBox = MakeTextBox($"{s.NotchOffsetOpt.CurrentValue:G}");
      _widthBox  = MakeTextBox($"{s.NotchWidthOpt.CurrentValue:G}");

      AttachNumericLostFocus(_lengthBox, () =>
      { double v = ParseDouble(_lengthBox.Text, s.NotchLengthOpt.CurrentValue); s.NotchLengthOpt.CurrentValue = v; Redraw(); });
      AttachNumericLostFocus(_offsetBox, () =>
      { double v = ParseDouble(_offsetBox.Text, s.NotchOffsetOpt.CurrentValue); s.NotchOffsetOpt.CurrentValue = v; Redraw(); });
      AttachNumericLostFocus(_widthBox, () =>
      { double v = ParseDouble(_widthBox.Text, s.NotchWidthOpt.CurrentValue); s.NotchWidthOpt.CurrentValue = v; Redraw(); });

      // Notch layer dropdown
      _notchLayerDrop = new DropDown();
      PopulateLayerDropDown(_notchLayerDrop, doc, s.NotchLayerName, true);
      _notchLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress) return;
        s.NotchLayerName = GetDropDownLayerName(_notchLayerDrop, s.NotchLayerName);
        Redraw();
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
      { if (_suppress) return; s.PercentToggle.CurrentValue = _percentCheck.Checked == true; };
      _groupCheck   = new CheckBox { Text = "Group",   Checked = s.GroupToggle.CurrentValue };
      _groupCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.GroupToggle.CurrentValue = _groupCheck.Checked == true; };

      // Label
      _labelCheck    = new CheckBox { Text = "", Checked = s.LabelToggle.CurrentValue, ToolTip = "Show label" };
      _labelValueBox = MakeTextBox(s.LabelValueText);
      _autoAdvCheck  = new CheckBox { ToolTip = "Auto-advance label", Checked = s.LabelAutoAdv };
      _sideFlipCheck = new CheckBox { Text = "Flip side", Checked = s.LabelSideFlip };
      _labelCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelToggle.CurrentValue = _labelCheck.Checked == true; ApplyDynamic(); };
      _labelValueBox.LostFocus += (_, __) =>
      { s.LabelValueText = _labelValueBox.Text; };
      _autoAdvCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelAutoAdv = _autoAdvCheck.Checked == true; };
      _sideFlipCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelSideFlip = _sideFlipCheck.Checked == true; Redraw(); };

      _labelLayerDrop = new DropDown();
      PopulateLayerDropDown(_labelLayerDrop, doc, s.LabelLayerName, false);
      _labelLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress) return;
        s.LabelLayerName = GetDropDownLayerName(_labelLayerDrop, s.LabelLayerName);
        Redraw();
      };
      _labelLayerDrop.DropDownOpening += (_, __) =>
      {
        _suppress = true;
        try { PopulateLayerDropDown(_labelLayerDrop, doc, s.LabelLayerName, false); }
        finally { _suppress = false; }
      };

      _labelSizeBox = MakeTextBox($"{s.ManualLabelSize:G}");
      _labelSizeBox.Width = 50;
      AttachNumericLostFocus(_labelSizeBox, () =>
      { s.ManualLabelSize = Math.Max(0, ParseDouble(_labelSizeBox.Text, s.ManualLabelSize)); Redraw(); });

      _labelSizeAutoCheck = new CheckBox { Text = "Auto", Checked = s.LabelSizeAutoToggle.CurrentValue };
      _labelSizeAutoCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelSizeAutoToggle.CurrentValue = _labelSizeAutoCheck.Checked == true; ApplyDynamic(); Redraw(); };

      _labelSizePctDrop = new DropDown();
      _labelSizePctDrop.DataStore = s.LabelSizePctTexts;
      _labelSizePctDrop.SelectedIndex = Math.Max(0, s.LabelSizePctIndex);
      _labelSizePctDrop.SelectedIndexChanged += (_, __) =>
      { if (_suppress) return; s.LabelSizePctIndex = _labelSizePctDrop.SelectedIndex; Redraw(); };

      _labelOffsetBox  = MakeTextBox($"{s.LabelOffsetOpt.CurrentValue:G}");
      _labelOffsetYBox = MakeTextBox($"{s.LabelOffsetYOpt.CurrentValue:G}");
      AttachNumericLostFocus(_labelOffsetBox, () =>
      { s.LabelOffsetOpt.CurrentValue = ParseDouble(_labelOffsetBox.Text, s.LabelOffsetOpt.CurrentValue); Redraw(); });
      AttachNumericLostFocus(_labelOffsetYBox, () =>
      { s.LabelOffsetYOpt.CurrentValue = ParseDouble(_labelOffsetYBox.Text, s.LabelOffsetYOpt.CurrentValue); Redraw(); });

      // Distance labels
      _fromStartLbl = new Label { Text = "-" };
      _fromEndLbl   = new Label { Text = "-" };
      _fromPrevLbl  = new Label { Text = "-" };

      // Undo button
      _undoBtn = new Button { Text = "Undo", Height = 26 };
      _undoBtn.Click += (_, __) =>
      {
        UndoLastNotch(doc, s);
        UpdateUndoEnabled();
      };
      UpdateUndoEnabled();

      // Side/Reverse/Enable per curve
      _sideChecks     = new CheckBox[s.Curves.Count];
      _reverseButtons = new Button[s.Curves.Count];
      _enableChecks   = new CheckBox[s.Curves.Count];
      for (int i = 0; i < s.Curves.Count; i++)
      {
        int ci = i;
        _sideChecks[i] = new CheckBox { Text = $"Side {i + 1}", Checked = s.CurveSides[i] };
        _sideChecks[i].CheckedChanged += (_, __) =>
        {
          if (_suppress) return;
          s.CurveSides[ci] = _sideChecks[ci].Checked == true;
          RebuildCurveNotches(doc, s, ci);
          SelectBothCurves(doc, s);
          Redraw();
        };
        _reverseButtons[i] = new Button { Text = $"Reverse {i + 1}", Height = 26 };
        _reverseButtons[i].Click += (_, __) =>
        {
          ReverseCurve(doc, s, ci);
          _suppress = true;
          try { _sideChecks[ci].Checked = s.CurveSides[ci]; }
          finally { _suppress = false; }
          Redraw();
        };
        if (s.Curves.Count > 1)
        {
          _enableChecks[i] = new CheckBox { Checked = true, ToolTip = "Enable notch on this curve" };
          _enableChecks[i].CheckedChanged += (_, __) =>
          {
            if (_suppress) return;
            s.CurveEnabled[ci] = _enableChecks[ci].Checked == true;
            RebuildCurveNotches(doc, s, ci);
            Redraw();
          };
        }
      }

      // Layout
      Content = BuildLayout();
      MinimumSize = new Eto.Drawing.Size(280, 0);
      Shown += (_, __) => MinimumSize = new Eto.Drawing.Size(280, ClientSize.Height);
      ApplyDynamic();

      Closed += (_, __) =>
      {
        if (!s.SuppressPanelCloseExit)
        {
          s.PanelClosedExit = true;
          try { RhinoApp.RunScript("_Cancel", false); } catch { }
        }
      };
    }

    Control BuildLayout()
    {
      // ── Notch group ──────────────────────────────────────────────────────
      var notchTable = new TableLayout { Padding = new Eto.Drawing.Padding(6), Spacing = new Eto.Drawing.Size(6, 4) };
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Type"),   new TableCell(_typeDropDown,   true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer"),  new TableCell(_notchLayerDrop, true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Length"), new TableCell(_lengthBox,      true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Width"),  new TableCell(_widthBox,       true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset"), new TableCell(_offsetBox,      true) } });
      var notchGroup = new GroupBox { Text = "Notch", Content = notchTable };

      // ── Label group ──────────────────────────────────────────────────────
      var labelHeader = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      labelHeader.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelCheck,    false),
        new TableCell(_labelValueBox, false),
        new TableCell(_autoAdvCheck,  false),
        new TableCell(_sideFlipCheck, false),
        new TableCell(null,           true),   // filler — absorbs extra width
      } });

      var sizeRow = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      sizeRow.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelSizeBox,       false),
        new TableCell(_labelSizeAutoCheck, false),
        new TableCell(_labelSizePctDrop,   false),
        new TableCell(null,                true),   // filler
      } });

      // labelHeader spans full width above the 2-column sub-table
      var labelSubTable = new TableLayout { Spacing = new Eto.Drawing.Size(6, 4) };
      labelSubTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FLW("Layer"),    new TableCell(_labelLayerDrop, true) } });
      labelSubTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FLW("Size"),     new TableCell(sizeRow,         true) } });
      labelSubTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FLW("Offset X"), new TableCell(_labelOffsetBox, true) } });
      labelSubTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FLW("Offset Y"), new TableCell(_labelOffsetYBox,true) } });
      var labelContent = new StackLayout
      {
        Orientation = Orientation.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Spacing = 4,
        Padding = new Eto.Drawing.Padding(6),
      };
      labelContent.Items.Add(new StackLayoutItem(labelHeader,   false));
      labelContent.Items.Add(new StackLayoutItem(labelSubTable, false));
      var labelGroup = new GroupBox { Text = "Label", Content = labelContent };

      // ── Percent / Group ──────────────────────────────────────────────────
      var pgStack = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 10,
        VerticalContentAlignment = VerticalAlignment.Center };
      pgStack.Items.Add(new StackLayoutItem(_percentCheck, false));
      pgStack.Items.Add(new StackLayoutItem(_groupCheck,   false));

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
        curveStack.Items.Add(new StackLayoutItem(row));
      }

      // ── Distance info ────────────────────────────────────────────────────
      var distTable = new TableLayout { Spacing = new Eto.Drawing.Size(6, 2) };
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From start"),    new TableCell(_fromStartLbl, true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From end"),      new TableCell(_fromEndLbl,   true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From previous"), new TableCell(_fromPrevLbl,  true) } });

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
      root.Items.Add(new StackLayoutItem(pgStack,    false));
      root.Items.Add(new StackLayoutItem(curveStack, false));
      root.Items.Add(new StackLayoutItem(distTable,  false));
      root.Items.Add(new StackLayoutItem(_undoBtn,   false));

      return root;
    }

    static TableCell FL(string text) =>
      new TableCell(new Label { Text = text, VerticalAlignment = VerticalAlignment.Center });

    // Fixed-width label cell for the Label group (keeps label column stable on resize)
    static TableCell FLW(string text) =>
      new TableCell(new Label { Text = text, Width = 60, VerticalAlignment = VerticalAlignment.Center });

    void Redraw() => _s.Doc.Views.Redraw();

    void ApplyDynamic()
    {
      bool lbl = _s.LabelToggle.CurrentValue;
      _labelLayerDrop.Enabled     = lbl;
      _labelSizeBox.Enabled       = lbl && !_s.LabelSizeAutoToggle.CurrentValue;
      _labelSizeAutoCheck.Enabled = lbl;
      _labelSizePctDrop.Enabled   = lbl && _s.LabelSizeAutoToggle.CurrentValue;
      _labelOffsetBox.Enabled     = lbl;
      _labelOffsetYBox.Enabled    = lbl;
      _autoAdvCheck.Enabled       = lbl;
      _sideFlipCheck.Enabled      = lbl;
      _widthBox.Enabled           = _s.NotchTypeValues[_s.NotchTypeIndex] != "I";
    }

    static TextBox MakeTextBox(string text) =>
      new TextBox { Text = text, Width = 70, Height = 22 };

    static void AttachNumericLostFocus(TextBox box, Action handler)
    {
      box.LostFocus += (_, __) => handler();
      box.KeyDown   += (_, e) =>
      {
        bool isEnter = e.Key == Keys.Enter;
        if (isEnter) { handler(); e.Handled = true; }
      };
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
        _labelValueBox.Text            = _s.LabelValueText;
        _labelSizeBox.Text             = $"{_s.ManualLabelSize:G}";
        _labelSizeAutoCheck.Checked    = _s.LabelSizeAutoToggle.CurrentValue;
        if (_labelSizePctDrop.SelectedIndex != _s.LabelSizePctIndex)
          _labelSizePctDrop.SelectedIndex = Math.Max(0, _s.LabelSizePctIndex);
        _labelOffsetBox.Text           = $"{_s.LabelOffsetOpt.CurrentValue:G}";
        _labelOffsetYBox.Text          = $"{_s.LabelOffsetYOpt.CurrentValue:G}";
        _autoAdvCheck.Checked          = _s.LabelAutoAdv;
        _sideFlipCheck.Checked         = _s.LabelSideFlip;
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

    public void UpdateDistanceLabels(double? current, double? prevDelta, double? otherEnd)
    {
      _fromStartLbl.Text = current.HasValue  ? $"{current.Value:G}"  : "-";
      _fromEndLbl.Text   = otherEnd.HasValue ? $"{otherEnd.Value:G}" : "-";
      _fromPrevLbl.Text  = prevDelta.HasValue? $"{prevDelta.Value:G}": "-";
    }

    public void UpdateUndoEnabled() =>
      _undoBtn.Enabled = _s.PlacementIds.Count > 0;
  }
}
