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
using Rhino.UI;
using RhinoPoint2d = Rhino.Geometry.Point2d;

namespace vTools.Commands;

/// <summary>
/// Native trim/extend workflow with optional auto-cutter mode.
/// </summary>
public sealed class vTrim : Command
{
  private const string OptionsSectionName = "vTrim";
  private const string ExtendAsLineKey = "extendAsLine";
  private const string JoinAfterTrimKey = "joinAfterTrim";

  private static bool _extendAsLine = true;
  private static bool _joinAfterTrim = true;

  public override string EnglishName => "vTrim";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var history = new SessionHistory();

    var cutters = PickCutters(doc);
    if (cutters.State == PickerState.Cancel)
      return Result.Cancel;

    while (true)
    {
      var pick = PickTarget(doc, cutters.AutoMode, cutters.CutterIds, _extendAsLine, _joinAfterTrim);
      if (pick.State == PickerState.Cancel)
      {
        SavePersistedOptions();
        return Result.Cancel;
      }

      if (pick.State == PickerState.Done)
      {
        SavePersistedOptions();
        return Result.Success;
      }

      if (pick.State == PickerState.Undo)
      {
        if (!TryUndo(doc, history))
          RhinoApp.WriteLine("vTrim: nothing to undo.");

        doc.Views.Redraw();
        continue;
      }

      if (pick.State == PickerState.Redo)
      {
        if (!TryRedo(doc, history))
          RhinoApp.WriteLine("vTrim: nothing to redo.");

        doc.Views.Redraw();
        continue;
      }

      _extendAsLine = pick.ExtendAsLine;
      _joinAfterTrim = pick.JoinAfterTrim;
      SavePersistedOptions();

      if (pick.TargetObject == null || pick.TargetCurve == null || !pick.PickPoint.IsValid)
        continue;

      var changed = false;
      ActionRecord? record = null;
      if (pick.ExtendMode)
      {
        changed = ExtendCurveObject(
          doc,
          pick.TargetObject,
          pick.TargetCurve,
          pick.PickPoint,
          _extendAsLine,
          cutters.AutoMode,
          cutters.CutterIds,
          out record);
      }
      else
      {
        var cutterCurves = ResolveCuttersForTarget(doc, pick.TargetObject, pick.TargetCurve, pick.PickPoint, cutters.AutoMode, cutters.CutterIds);
        changed = TrimCurveObject(
          doc,
          pick.TargetObject,
          pick.TargetCurve,
          pick.PickPoint,
          cutterCurves,
          _joinAfterTrim,
          out record);
      }

      if (changed)
      {
        if (record != null)
          history.Push(record);

        doc.Objects.UnselectAll();
        doc.Views.Redraw();
      }
      else
      {
        doc.Objects.UnselectAll();
        doc.Views.Redraw();
        RhinoApp.WriteLine("vTrim: click did not produce a valid trim/extend result.");
      }
    }
  }

  private static void LoadPersistedOptions()
  {
    var loaded = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var extendAsLine = _extendAsLine;
        var joinAfterTrim = _joinAfterTrim;

        if (vToolsOptionStore.TryGetBool(section, ExtendAsLineKey, out var persistedExtendAsLine))
          extendAsLine = persistedExtendAsLine;

        if (vToolsOptionStore.TryGetBool(section, JoinAfterTrimKey, out var persistedJoinAfterTrim))
          joinAfterTrim = persistedJoinAfterTrim;

        return (extendAsLine, joinAfterTrim);
      });

    _extendAsLine = loaded.extendAsLine;
    _joinAfterTrim = loaded.joinAfterTrim;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[ExtendAsLineKey] = _extendAsLine;
        section[JoinAfterTrimKey] = _joinAfterTrim;
      });
  }

  private enum PickerState
  {
    Ok,
    Done,
    Undo,
    Redo,
    Cancel
  }

  private sealed record CutterPick(PickerState State, bool AutoMode, List<Guid> CutterIds);

  private sealed class TargetPick
  {
    public PickerState State { get; set; }
    public RhinoObject? TargetObject { get; set; }
    public Curve? TargetCurve { get; set; }
    public Point3d PickPoint { get; set; } = Point3d.Unset;
    public bool ExtendMode { get; set; }
    public bool ExtendAsLine { get; set; }
    public bool JoinAfterTrim { get; set; }
  }

  private sealed class CurveSnapshot
  {
    public CurveSnapshot(Guid objectId, Curve curve, ObjectAttributes attributes)
    {
      ObjectId = objectId;
      Curve = curve;
      Attributes = attributes;
    }

    public Guid ObjectId { get; }
    public Curve Curve { get; }
    public ObjectAttributes Attributes { get; }
  }

  private sealed class ActionRecord
  {
    public required CurveSnapshot BeforeTarget { get; init; }
    public required CurveSnapshot AfterTarget { get; init; }
    public List<CurveSnapshot> AddedCurves { get; } = new();
    public List<Guid> ActiveAddedIds { get; } = new();
  }

  private sealed class SessionHistory
  {
    private readonly Stack<ActionRecord> _undo = new();
    private readonly Stack<ActionRecord> _redo = new();

    public void Push(ActionRecord record)
    {
      _undo.Push(record);
      _redo.Clear();
    }

    public bool TryPopUndo(out ActionRecord? record)
    {
      if (_undo.Count == 0)
      {
        record = null;
        return false;
      }

      record = _undo.Pop();
      return true;
    }

    public bool TryPopRedo(out ActionRecord? record)
    {
      if (_redo.Count == 0)
      {
        record = null;
        return false;
      }

      record = _redo.Pop();
      return true;
    }

    public void PushUndo(ActionRecord record)
    {
      _undo.Push(record);
    }

    public void PushRedo(ActionRecord record)
    {
      _redo.Push(record);
    }
  }

  private static bool TryUndo(RhinoDoc doc, SessionHistory history)
  {
    if (!history.TryPopUndo(out var record) || record == null)
      return false;

    if (!ApplyRecordState(doc, record, redo: false))
    {
      history.PushUndo(record);
      return false;
    }

    history.PushRedo(record);
    return true;
  }

  private static bool TryRedo(RhinoDoc doc, SessionHistory history)
  {
    if (!history.TryPopRedo(out var record) || record == null)
      return false;

    if (!ApplyRecordState(doc, record, redo: true))
    {
      history.PushRedo(record);
      return false;
    }

    history.PushUndo(record);
    return true;
  }

  private static bool ApplyRecordState(RhinoDoc doc, ActionRecord record, bool redo)
  {
    if (redo)
    {
      if (!RestoreTargetSnapshot(doc, record.AfterTarget))
        return false;

      record.ActiveAddedIds.Clear();
      foreach (var add in record.AddedCurves)
      {
        var id = doc.Objects.AddCurve(add.Curve.DuplicateCurve(), add.Attributes.Duplicate());
        if (id != Guid.Empty)
          record.ActiveAddedIds.Add(id);
      }

      return true;
    }

    foreach (var id in record.ActiveAddedIds)
    {
      if (id != Guid.Empty)
        doc.Objects.Delete(id, true);
    }

    record.ActiveAddedIds.Clear();
    return RestoreTargetSnapshot(doc, record.BeforeTarget);
  }

  private static bool RestoreTargetSnapshot(RhinoDoc doc, CurveSnapshot snapshot)
  {
    if (!doc.Objects.Replace(snapshot.ObjectId, snapshot.Curve.DuplicateCurve()))
      return false;

    _ = doc.Objects.ModifyAttributes(snapshot.ObjectId, snapshot.Attributes.Duplicate(), true);
    return true;
  }

  private static bool TryCaptureCurveSnapshot(RhinoDoc doc, Guid objectId, out CurveSnapshot? snapshot)
  {
    snapshot = null;
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not Curve curve)
      return false;

    snapshot = new CurveSnapshot(objectId, curve.DuplicateCurve(), obj.Attributes.Duplicate());
    return true;
  }

  private static ActionRecord? BuildActionRecord(RhinoDoc doc, CurveSnapshot beforeTarget, Guid targetId, IReadOnlyList<Guid>? addedIds)
  {
    if (!TryCaptureCurveSnapshot(doc, targetId, out var afterTarget) || afterTarget == null)
      return null;

    var record = new ActionRecord
    {
      BeforeTarget = beforeTarget,
      AfterTarget = afterTarget
    };

    if (addedIds == null)
      return record;

    foreach (var id in addedIds)
    {
      if (id == Guid.Empty)
        continue;

      if (!TryCaptureCurveSnapshot(doc, id, out var addSnap) || addSnap == null)
        continue;

      record.AddedCurves.Add(addSnap);
      record.ActiveAddedIds.Add(id);
    }

    return record;
  }

  private static CutterPick PickCutters(RhinoDoc doc)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select cutting curves or press Enter for AutoClosest");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var result = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success)
      return new CutterPick(PickerState.Cancel, true, new List<Guid>());

    if (result == GetResult.Nothing)
      return new CutterPick(PickerState.Ok, true, new List<Guid>());

    var cutterIds = new List<Guid>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef == null)
        continue;

      var id = objRef.ObjectId;
      if (id == Guid.Empty || !seen.Add(id))
        continue;

      cutterIds.Add(id);
    }

    return new CutterPick(cutterIds.Count > 0 ? PickerState.Ok : PickerState.Cancel, false, cutterIds);
  }

  private static TargetPick PickTarget(
    RhinoDoc doc,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds,
    bool extendAsLine,
    bool joinAfterTrim)
  {
    var pick = new TargetPick
    {
      State = PickerState.Cancel,
      ExtendAsLine = extendAsLine,
      JoinAfterTrim = joinAfterTrim
    };

    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select curve to trim (hold Shift to extend); Enter when done");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AcceptNothing(true);
      go.AcceptString(true);
      go.DeselectAllBeforePostSelect = false;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(true);

      var extendToggle = new OptionToggle(extendAsLine, "Smooth", "Line");
      var joinToggle = new OptionToggle(joinAfterTrim, "No", "Yes");
      go.AddOptionToggle("Extend", ref extendToggle);
      go.AddOptionToggle("Join", ref joinToggle);

      var preview = new TrimPreviewConduit(doc, autoMode, cutterIds, extendAsLine, joinAfterTrim)
      {
        Enabled = true
      };

      var hover = new TrimHoverMouseCallback(doc, preview)
      {
        Enabled = true
      };

      var lastShiftState = ShiftPressed();
      preview.HoverExtendMode = lastShiftState;
      System.Windows.Forms.Timer? shiftTimer = null;

      void OnIdleShiftRefresh(object? _sender, EventArgs _args)
      {
        var currentShift = ShiftPressed();
        if (currentShift == lastShiftState)
          return;

        lastShiftState = currentShift;
        preview.HoverExtendMode = currentShift;
        doc.Views.Redraw();
      }

      RhinoApp.Idle += OnIdleShiftRefresh;

      try
      {
        shiftTimer = new System.Windows.Forms.Timer
        {
          Interval = 30
        };
        shiftTimer.Tick += OnIdleShiftRefresh;
        shiftTimer.Start();
      }
      catch
      {
        shiftTimer = null;
      }

      doc.Views.Redraw();

      GetResult result;
      try
      {
        result = go.Get();
      }
      finally
      {
        if (shiftTimer != null)
        {
          try
          {
            shiftTimer.Stop();
          }
          catch
          {
          }

          try
          {
            shiftTimer.Tick -= OnIdleShiftRefresh;
          }
          catch
          {
          }

          try
          {
            shiftTimer.Dispose();
          }
          catch
          {
          }
        }

        RhinoApp.Idle -= OnIdleShiftRefresh;
        hover.Enabled = false;
        preview.Enabled = false;
        doc.Views.Redraw();
      }

      if (go.CommandResult() != Result.Success)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      if (result == GetResult.Option)
      {
        extendAsLine = extendToggle.CurrentValue;
        joinAfterTrim = joinToggle.CurrentValue;
        pick.ExtendAsLine = extendAsLine;
        pick.JoinAfterTrim = joinAfterTrim;
        continue;
      }

      if (result == GetResult.String)
      {
        var text = (go.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
        while (text.StartsWith("_", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal))
          text = text[1..];

        if (text is "u" or "undo")
        {
          pick.State = PickerState.Undo;
          pick.ExtendAsLine = extendAsLine;
          pick.JoinAfterTrim = joinAfterTrim;
          return pick;
        }

        if (text is "r" or "redo")
        {
          pick.State = PickerState.Redo;
          pick.ExtendAsLine = extendAsLine;
          pick.JoinAfterTrim = joinAfterTrim;
          return pick;
        }

        RhinoApp.WriteLine("vTrim: unknown hidden keyword. Use 'u'/'undo' or 'r'/'redo'.");
        continue;
      }

      if (result == GetResult.Nothing)
      {
        pick.State = PickerState.Done;
        pick.ExtendAsLine = extendAsLine;
        pick.JoinAfterTrim = joinAfterTrim;
        return pick;
      }

      if (result != GetResult.Object || go.ObjectCount == 0)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var objRef = go.Object(0);
      if (objRef == null)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var targetObj = objRef.Object();
      var targetCurve = objRef.Curve();
      if (targetObj == null || targetCurve == null)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var pickPoint = objRef.SelectionPoint();
      if (!pickPoint.IsValid)
      {
        if (targetCurve.GetLength() > RhinoMath.ZeroTolerance && targetCurve.LengthParameter(0.5 * targetCurve.GetLength(), out var tMid))
          pickPoint = targetCurve.PointAt(tMid);
        else
          pickPoint = targetCurve.PointAtStart;
      }

      var pickedExtendMode = lastShiftState;
      var clickHover = hover.LastClick;

      if (clickHover.HasCapture)
        pickedExtendMode = clickHover.ExtendMode;

      // Use click-time hover object/point first so execution matches preview even with nearby-object disambiguation.
      if (clickHover.HasCapture && clickHover.ObjectId.HasValue)
      {
        var clickedObj = doc.Objects.FindId(clickHover.ObjectId.Value);
        if (clickedObj?.Geometry is Curve clickedCurve)
        {
          targetObj = clickedObj;
          targetCurve = clickedCurve;
          if (clickHover.Point.IsValid)
            pickPoint = clickHover.Point;
        }
      }
      else if (!clickHover.HasCapture)
      {
        var hoverPoint = preview.HoverPoint;
        if (preview.HoverObjectId.HasValue && preview.HoverObjectId.Value == targetObj.Id && hoverPoint.IsValid)
        {
          pickPoint = hoverPoint;
          pickedExtendMode = preview.HoverExtendMode;
        }
      }

      pick.State = PickerState.Ok;
      pick.TargetObject = targetObj;
      pick.TargetCurve = targetCurve;
      pick.PickPoint = pickPoint;
      pick.ExtendMode = pickedExtendMode;
      pick.ExtendAsLine = extendAsLine;
      pick.JoinAfterTrim = joinAfterTrim;
      return pick;
    }
  }

  private static bool ShiftPressed()
  {
    try
    {
      return (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift;
    }
    catch
    {
      return false;
    }
  }

  private readonly struct HoverClickCapture
  {
    public bool HasCapture { get; init; }
    public Guid? ObjectId { get; init; }
    public Point3d Point { get; init; }
    public bool ExtendMode { get; init; }
  }

  private sealed class TrimPreviewConduit : Rhino.Display.DisplayConduit
  {
    private readonly RhinoDoc _doc;
    private readonly bool _autoMode;
    private readonly IReadOnlyList<Guid> _cutterIds;

    public TrimPreviewConduit(RhinoDoc doc, bool autoMode, IReadOnlyList<Guid> cutterIds, bool extendAsLine, bool joinAfterTrim)
    {
      _doc = doc;
      _autoMode = autoMode;
      _cutterIds = cutterIds;
      ExtendAsLine = extendAsLine;
      JoinAfterTrim = joinAfterTrim;
    }

    public Guid? HoverObjectId { get; private set; }
    public RhinoObject? HoverObject { get; private set; }
    public Curve? HoverCurve { get; private set; }
    public Point3d HoverPoint { get; private set; } = Point3d.Unset;
    public bool HoverExtendMode { get; set; }
    public bool ExtendAsLine { get; set; }
    public bool JoinAfterTrim { get; set; }

    public void SetHover(RhinoObject? obj, Curve? curve, Point3d point, bool extendMode)
    {
      HoverObject = obj;
      HoverObjectId = obj?.Id;
      HoverCurve = curve;
      HoverPoint = point;
      HoverExtendMode = extendMode;
    }

    protected override void DrawOverlay(Rhino.Display.DrawEventArgs e)
    {
      if (HoverObject == null || HoverCurve == null || !HoverPoint.IsValid)
        return;

      var color = LayerColorForObject(_doc, HoverObject);
      e.Display.DrawCurve(HoverCurve, color, 2);

      if (HoverExtendMode)
      {
        var addPiece = BuildExtendPreviewPiece(_doc, HoverObject, HoverCurve, HoverPoint, ExtendAsLine, _autoMode, _cutterIds);
        if (addPiece != null)
          e.Display.DrawCurve(addPiece, Color.LimeGreen, 2);
      }
      else
      {
        var cutters = ResolveCuttersForTarget(_doc, HoverObject, HoverCurve, HoverPoint, _autoMode, _cutterIds);
        var removedPiece = BuildTrimPreviewPiece(_doc, HoverCurve, HoverPoint, cutters);
        if (removedPiece != null)
          e.Display.DrawCurve(removedPiece, Color.Red, 2);
      }
    }
  }

  private sealed class TrimHoverMouseCallback : MouseCallback
  {
    private readonly RhinoDoc _doc;
    private readonly TrimPreviewConduit _preview;
    private Guid? _lastHoverObjectId;
    private Point3d _lastHoverPoint = Point3d.Unset;

    public TrimHoverMouseCallback(RhinoDoc doc, TrimPreviewConduit preview)
    {
      _doc = doc;
      _preview = preview;
    }

    public HoverClickCapture LastClick { get; private set; }

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      var shiftDown = ShiftPressed();
      try
      {
        shiftDown = shiftDown || e.ShiftKeyDown;
      }
      catch
      {
      }

      LastClick = new HoverClickCapture
      {
        HasCapture = true,
        ObjectId = _lastHoverObjectId,
        Point = _lastHoverPoint,
        ExtendMode = shiftDown
      };
      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      var view = e.View;
      var viewport = view?.ActiveViewport;
      var vpClient = e.ViewportPoint;
      var vpPoint = new Point2d(vpClient.X, vpClient.Y);
      if (viewport == null)
      {
        base.OnMouseMove(e);
        return;
      }

      var pickTol = PickboxRadiusPixels();
      var tol2 = pickTol * pickTol;

      RhinoObject? bestObj = null;
      Curve? bestCurve = null;
      Point3d bestPoint = Point3d.Unset;
      double? bestD2 = null;

      foreach (var (obj, curve) in EnumerateDocCurves(_doc))
      {
        var sample = CurveBestScreenPick(curve, viewport, vpPoint);
        if (!sample.Point.IsValid || !sample.DistanceSquared.HasValue)
          continue;

        if (!bestD2.HasValue || sample.DistanceSquared.Value < bestD2.Value)
        {
          bestD2 = sample.DistanceSquared.Value;
          bestObj = obj;
          bestCurve = curve;
          bestPoint = sample.Point;
        }
      }

      var hoverObj = bestD2.HasValue && bestD2.Value <= tol2 ? bestObj : null;
      var hoverCurve = hoverObj != null ? bestCurve : null;
      var hoverPoint = hoverObj != null ? bestPoint : Point3d.Unset;

      _lastHoverObjectId = hoverObj?.Id;
      _lastHoverPoint = hoverPoint;

      _preview.SetHover(hoverObj, hoverCurve, hoverPoint, ShiftPressed());
      _doc.Views.Redraw();

      base.OnMouseMove(e);
    }

    private static double PickboxRadiusPixels()
    {
      try
      {
        return Math.Max(1.0, Rhino.ApplicationSettings.ModelAidSettings.MousePickboxRadius);
      }
      catch
      {
        return 8.0;
      }
    }
  }

  private readonly struct ScreenPickSample
  {
    public Point3d Point { get; init; }
    public double? DistanceSquared { get; init; }
  }

  private static ScreenPickSample CurveBestScreenPick(Curve curve, Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint)
  {
    var lineSample = LineBestScreenPick(curve, viewport, vpPoint);
    if (lineSample.Point.IsValid && lineSample.DistanceSquared.HasValue)
      return lineSample;

    if (!TryGetDomain(curve, out var d0, out var d1) || Math.Abs(d1 - d0) <= 1.0e-12)
      return default;

    var parameters = new List<double>();
    try
    {
      var div = curve.DivideByCount(96, true);
      if (div != null)
        parameters.AddRange(div.Select(v => (double)v));
    }
    catch
    {
    }

    if (parameters.Count == 0)
    {
      for (var i = 0; i <= 96; i++)
      {
        var t = d0 + ((d1 - d0) * (i / 96.0));
        parameters.Add(t);
      }
    }

    var bestT = d0;
    double? bestD2 = null;

    foreach (var t in parameters)
    {
      var pt = curve.PointAt(t);
      var d2 = PixelDistanceSquared(viewport, vpPoint, pt);
      if (!d2.HasValue)
        continue;

      if (!bestD2.HasValue || d2.Value < bestD2.Value)
      {
        bestD2 = d2.Value;
        bestT = t;
      }
    }

    if (!bestD2.HasValue)
      return default;

    var idx = parameters.FindIndex(v => Math.Abs(v - bestT) <= 1.0e-15);
    if (idx < 0)
      idx = 0;

    var left = parameters[Math.Max(0, idx - 1)];
    var right = parameters[Math.Min(parameters.Count - 1, idx + 1)];
    if (right <= left)
    {
      left = Math.Max(d0, bestT - ((d1 - d0) / 96.0));
      right = Math.Min(d1, bestT + ((d1 - d0) / 96.0));
    }

    for (var i = 0; i < 8; i++)
    {
      var t1 = left + ((right - left) / 3.0);
      var t2 = right - ((right - left) / 3.0);

      var p1 = curve.PointAt(t1);
      var p2 = curve.PointAt(t2);
      var d21 = PixelDistanceSquared(viewport, vpPoint, p1);
      var d22 = PixelDistanceSquared(viewport, vpPoint, p2);
      if (!d21.HasValue || !d22.HasValue)
        break;

      if (d21.Value <= d22.Value)
      {
        right = t2;
        if (d21.Value < bestD2.Value)
        {
          bestD2 = d21.Value;
          bestT = t1;
        }
      }
      else
      {
        left = t1;
        if (d22.Value < bestD2.Value)
        {
          bestD2 = d22.Value;
          bestT = t2;
        }
      }
    }

    return new ScreenPickSample
    {
      Point = curve.PointAt(bestT),
      DistanceSquared = bestD2
    };
  }

  private static ScreenPickSample LineBestScreenPick(Curve curve, Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint)
  {
    var (start, end) = LineEndpoints(curve);
    if (!start.HasValue || !end.HasValue)
      return default;

    RhinoPoint2d c0;
    RhinoPoint2d c1;
    try
    {
      c0 = viewport.WorldToClient(start.Value);
      c1 = viewport.WorldToClient(end.Value);
    }
    catch
    {
      return default;
    }

    var x0 = (double)c0.X;
    var y0 = (double)c0.Y;
    var x1 = (double)c1.X;
    var y1 = (double)c1.Y;
    var px = vpPoint.X;
    var py = vpPoint.Y;

    var vx = x1 - x0;
    var vy = y1 - y0;
    var denom = (vx * vx) + (vy * vy);
    var t = denom <= 1.0e-12
      ? 0.0
      : Math.Max(0.0, Math.Min(1.0, (((px - x0) * vx) + ((py - y0) * vy)) / denom));

    var cx = x0 + (vx * t);
    var cy = y0 + (vy * t);
    var dx = cx - px;
    var dy = cy - py;
    var d2 = (dx * dx) + (dy * dy);

    var bestPoint = start.Value + ((end.Value - start.Value) * t);
    return new ScreenPickSample
    {
      Point = bestPoint,
      DistanceSquared = d2
    };
  }

  private static (Point3d? Start, Point3d? End) LineEndpoints(Curve curve)
  {
    if (curve is LineCurve lineCurve)
    {
      var line = lineCurve.Line;
      return (line.From, line.To);
    }

    try
    {
      if (curve.IsLinear(RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1.0e-6))
        return (curve.PointAtStart, curve.PointAtEnd);
    }
    catch
    {
    }

    return (null, null);
  }

  private static double? PixelDistanceSquared(Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint, Point3d worldPoint)
  {
    try
    {
      var client = viewport.WorldToClient(worldPoint);
      var dx = client.X - vpPoint.X;
      var dy = client.Y - vpPoint.Y;
      return (dx * dx) + (dy * dy);
    }
    catch
    {
      return null;
    }
  }

  private static IEnumerable<(RhinoObject Obj, Curve Curve)> EnumerateDocCurves(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj == null || obj.ObjectType != ObjectType.Curve)
        continue;

      if (obj.Geometry is not Curve curve)
        continue;

      yield return (obj, curve);
    }
  }

  private static Color LayerColorForObject(RhinoDoc doc, RhinoObject obj)
  {
    try
    {
      var layerIndex = obj.Attributes.LayerIndex;
      if (layerIndex >= 0)
      {
        var layer = doc.Layers[layerIndex];
        if (layer != null)
          return layer.Color;
      }
    }
    catch
    {
    }

    return Color.White;
  }

  private static List<Curve> ResolveCuttersForTarget(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds)
  {
    if (autoMode)
      return FindAutoCutters(doc, targetObj.Id, targetCurve, pickPoint);

    var cutters = new List<Curve>();
    foreach (var id in cutterIds)
    {
      if (id == Guid.Empty || id == targetObj.Id)
        continue;

      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not Curve curve)
        continue;

      cutters.Add(curve);
    }

    return cutters;
  }

  private static List<double> IntersectionParams(RhinoDoc doc, Curve a, Curve b)
  {
    var values = new List<double>();
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(a, b, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
    if (events == null)
      return values;

    foreach (var ev in events)
    {
      if (ev.IsPoint)
      {
        values.Add(ev.ParameterA);
      }
      else if (ev.IsOverlap)
      {
        values.Add(ev.OverlapA.T0);
        values.Add(ev.OverlapA.T1);
      }
    }

    return values;
  }

  private static List<double> UniqueParams(IEnumerable<double> values, double tolerance)
  {
    var sorted = values.OrderBy(v => v).ToList();
    var unique = new List<double>();
    foreach (var v in sorted)
    {
      if (unique.Count == 0 || Math.Abs(v - unique[^1]) > tolerance)
        unique.Add(v);
    }

    return unique;
  }

  private static Plane? ActiveViewPlane(RhinoDoc doc)
  {
    try
    {
      var viewport = doc.Views.ActiveView?.ActiveViewport;
      if (viewport == null)
        return null;

      var x = viewport.CameraX;
      var y = viewport.CameraY;
      if (x.IsTiny() || y.IsTiny() || !x.Unitize() || !y.Unitize())
        return null;

      var plane = new Plane(viewport.CameraLocation, x, y);
      return plane.IsValid ? plane : null;
    }
    catch
    {
      return null;
    }
  }

  private static Curve? ProjectCurveToPlane(Curve curve, Plane? plane)
  {
    if (plane == null)
      return null;

    try
    {
      return Curve.ProjectToPlane(curve, plane.Value);
    }
    catch
    {
      return null;
    }
  }

  private static bool TryGetDomain(Curve curve, out double d0, out double d1)
  {
    d0 = 0.0;
    d1 = 0.0;

    try
    {
      var domain = curve.Domain;
      d0 = domain.T0;
      d1 = domain.T1;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static List<double> CollectInteriorCurveCurveParams(Curve curveA, Curve curveB, double d0, double d1, double endTol, RhinoDoc doc)
  {
    var result = new List<double>();
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(curveA, curveB, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
    if (events == null)
      return result;

    foreach (var ev in events)
    {
      if (ev.IsPoint)
      {
        var t = ev.ParameterA;
        if (t > d0 + endTol && t < d1 - endTol)
          result.Add(t);
      }
      else if (ev.IsOverlap)
      {
        var t0 = ev.OverlapA.T0;
        var t1 = ev.OverlapA.T1;
        if (t0 > d0 + endTol && t0 < d1 - endTol)
          result.Add(t0);
        if (t1 > d0 + endTol && t1 < d1 - endTol)
          result.Add(t1);
      }
    }

    return result;
  }

  private static double? NearestPickDistanceForParams(Curve targetCurve, Point3d pickPoint, IEnumerable<double> parameters)
  {
    double? best = null;
    foreach (var t in parameters)
    {
      try
      {
        var pt = targetCurve.PointAt(t);
        var d = pt.DistanceTo(pickPoint);
        if (!best.HasValue || d < best.Value)
          best = d;
      }
      catch
      {
      }
    }

    return best;
  }

  private static List<double> ParamsClosestSource(
    RhinoDoc doc,
    Curve targetCurve,
    Curve cutterCurve,
    Point3d pickPoint,
    double d0,
    double d1,
    double endTol,
    Plane? viewPlane)
  {
    var worldParams = CollectInteriorCurveCurveParams(targetCurve, cutterCurve, d0, d1, endTol, doc);

    var viewParams = new List<double>();
    var targetProj = ProjectCurveToPlane(targetCurve, viewPlane);
    var cutterProj = ProjectCurveToPlane(cutterCurve, viewPlane);
    if (targetProj != null && cutterProj != null && TryGetDomain(targetProj, out var p0, out var p1))
    {
      var projEndTol = Math.Max(1.0e-9, Math.Abs(p1 - p0) * 1.0e-9);
      var projRaw = CollectInteriorCurveCurveParams(targetProj, cutterProj, p0, p1, projEndTol, doc);

      foreach (var tp in projRaw)
      {
        double tOrig;
        if (Math.Abs(p1 - p0) <= 1.0e-12)
        {
          tOrig = d0;
        }
        else
        {
          var s = (tp - p0) / (p1 - p0);
          s = Math.Max(0.0, Math.Min(1.0, s));
          tOrig = d0 + ((d1 - d0) * s);
        }

        if (tOrig > d0 + endTol && tOrig < d1 - endTol)
          viewParams.Add(tOrig);
      }
    }

    if (worldParams.Count > 0 && viewParams.Count > 0)
    {
      var isLinear = false;
      try
      {
        isLinear = targetCurve.IsLinear(doc.ModelAbsoluteTolerance);
      }
      catch
      {
      }

      if (isLinear)
      {
        var merged = worldParams.Concat(viewParams);
        return UniqueParams(merged, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
      }

      var worldD = NearestPickDistanceForParams(targetCurve, pickPoint, worldParams);
      var viewD = NearestPickDistanceForParams(targetCurve, pickPoint, viewParams);
      if (!worldD.HasValue)
        return viewParams;
      if (!viewD.HasValue)
        return worldParams;
      return worldD.Value <= viewD.Value ? worldParams : viewParams;
    }

    if (worldParams.Count > 0)
      return worldParams;
    return viewParams;
  }

  private static List<double> CollectSplitParameters(RhinoDoc doc, Curve targetCurve, IReadOnlyList<Curve> cutterCurves, Point3d pickPoint)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<double>();

    var endTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    var viewPlane = ActiveViewPlane(doc);

    var parameters = new List<double>();
    foreach (var cutter in cutterCurves)
      parameters.AddRange(ParamsClosestSource(doc, targetCurve, cutter, pickPoint, d0, d1, endTol, viewPlane));

    if (parameters.Count == 0)
      return new List<double>();

    return UniqueParams(parameters, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
  }

  private static List<double> SanitizeSplitParameters(Curve targetCurve, IReadOnlyList<double> splitParameters)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<double>();

    var boundaryTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    var valid = splitParameters.Where(t => t > d0 + boundaryTol && t < d1 - boundaryTol);
    return UniqueParams(valid, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
  }

  private static List<double> CollectPreviewSplitParameters(RhinoDoc doc, Curve targetCurve, IReadOnlyList<Curve> cutterCurves, Point3d pickPoint)
  {
    var split = CollectSplitParameters(doc, targetCurve, cutterCurves, pickPoint);
    split = SanitizeSplitParameters(targetCurve, split);
    if (split.Count > 0)
      return split;

    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return split;

    var fallback = new List<double>();
    foreach (var cutter in cutterCurves)
    {
      if (cutter == null)
        continue;

      fallback.AddRange(IntersectionParams(doc, targetCurve, cutter));
    }

    var viewPlane = ActiveViewPlane(doc);
    var targetProj = ProjectCurveToPlane(targetCurve, viewPlane);
    if (targetProj != null && TryGetDomain(targetProj, out var p0, out var p1))
    {
      foreach (var cutter in cutterCurves)
      {
        if (cutter == null)
          continue;

        var cutterProj = ProjectCurveToPlane(cutter, viewPlane);
        if (cutterProj == null)
          continue;

        var projected = IntersectionParams(doc, targetProj, cutterProj);
        foreach (var tProj in projected)
          fallback.Add(MapProjectedToLineParameter(tProj, p0, p1, d0, d1));
      }
    }

    if (fallback.Count == 0)
      return split;

    var span = Math.Abs(d1 - d0);
    var edgeInset = Math.Max(1.0e-9, span * 1.0e-6);
    var inRange = new List<double>();
    foreach (var t in fallback)
    {
      var clamped = Math.Max(d0, Math.Min(d1, t));
      if (clamped <= d0)
        clamped = Math.Min(d1, d0 + edgeInset);
      else if (clamped >= d1)
        clamped = Math.Max(d0, d1 - edgeInset);

      if (clamped > d0 && clamped < d1)
        inRange.Add(clamped);
    }

    if (inRange.Count == 0)
      return split;

    return UniqueParams(inRange, Math.Max(1.0e-9, span * 1.0e-8));
  }

  private static int ClosestPieceIndex(IReadOnlyList<Curve> pieces, Point3d pickPoint)
  {
    var bestIndex = -1;
    var bestD2 = double.MaxValue;

    for (var i = 0; i < pieces.Count; i++)
    {
      var piece = pieces[i];
      if (piece == null)
        continue;

      if (!piece.ClosestPoint(pickPoint, out var t))
        continue;

      var cp = piece.PointAt(t);
      var d2 = cp.DistanceToSquared(pickPoint);
      if (d2 < bestD2)
      {
        bestD2 = d2;
        bestIndex = i;
      }
    }

    return bestIndex;
  }

  private static Curve? TrimOpenCurveFromEnd(Curve targetCurve, Point3d pickPoint, IReadOnlyList<double> splitParameters, double tolScale = 1.0)
  {
    if (targetCurve == null || targetCurve.IsClosed || splitParameters.Count == 0)
      return null;

    if (!targetCurve.ClosestPoint(pickPoint, out var tPick))
      return null;

    var domain = targetCurve.Domain;
    var d0 = domain.T0;
    var d1 = domain.T1;
    var tFirst = splitParameters.Min();
    var tLast = splitParameters.Max();
    var tTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-6) * Math.Max(1.0, tolScale);

    if (tPick <= tFirst + tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(tFirst, d1));
      }
      catch
      {
        return null;
      }
    }

    if (tPick >= tLast - tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(d0, tLast));
      }
      catch
      {
        return null;
      }
    }

    return null;
  }

  private static Curve? TrimOpenCurveFromEndRemovedPiece(Curve targetCurve, Point3d pickPoint, IReadOnlyList<double> splitParameters, double tolScale = 1.0)
  {
    if (targetCurve == null || targetCurve.IsClosed || splitParameters.Count == 0)
      return null;

    if (!targetCurve.ClosestPoint(pickPoint, out var tPick))
      return null;

    var domain = targetCurve.Domain;
    var d0 = domain.T0;
    var d1 = domain.T1;
    var tFirst = splitParameters.Min();
    var tLast = splitParameters.Max();
    var tTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-6) * Math.Max(1.0, tolScale);

    if (tPick <= tFirst + tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(d0, tFirst));
      }
      catch
      {
        return null;
      }
    }

    if (tPick >= tLast - tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(tLast, d1));
      }
      catch
      {
        return null;
      }
    }

    return null;
  }

  private static Curve? BuildTrimPreviewPiece(RhinoDoc doc, Curve targetCurve, Point3d pickPoint, IReadOnlyList<Curve> cutterCurves)
  {
    var split = CollectPreviewSplitParameters(doc, targetCurve, cutterCurves, pickPoint);
    if (split.Count == 0)
      return null;

    var removed = TrimOpenCurveFromEndRemovedPiece(targetCurve, pickPoint, split);
    if (removed == null)
      removed = TrimOpenCurveFromEndRemovedPiece(targetCurve, pickPoint, split, 250.0);

    if (removed != null)
      return removed;

    Curve[]? pieces;
    try
    {
      pieces = targetCurve.Split(split);
    }
    catch
    {
      pieces = null;
    }

    if (pieces == null || pieces.Length < 2)
      return null;

    var idx = ClosestPieceIndex(pieces, pickPoint);
    return idx >= 0 ? pieces[idx] : null;
  }

  private static bool TrimCurveObject(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    IReadOnlyList<Curve> cutterCurves,
    bool joinAfterTrim,
    out ActionRecord? actionRecord)
  {
    actionRecord = null;

    if (!TryCaptureCurveSnapshot(doc, targetObj.Id, out var beforeTarget) || beforeTarget == null)
    {
      RhinoApp.WriteLine("vTrim: failed to capture target state.");
      return false;
    }

    var split = CollectSplitParameters(doc, targetCurve, cutterCurves, pickPoint);
    split = SanitizeSplitParameters(targetCurve, split);
    if (split.Count == 0)
    {
      RhinoApp.WriteLine("vTrim: no valid trim intersections for this pick.");
      return false;
    }

    var direct = TrimOpenCurveFromEnd(targetCurve, pickPoint, split);
    if (direct == null)
      direct = TrimOpenCurveFromEnd(targetCurve, pickPoint, split, 250.0);
    if (direct != null)
    {
      try
      {
        if (direct.GetLength() <= doc.ModelAbsoluteTolerance)
        {
          RhinoApp.WriteLine("vTrim: trim result too short; skipped.");
          return false;
        }
      }
      catch
      {
      }

      if (!doc.Objects.Replace(targetObj.Id, direct))
      {
        RhinoApp.WriteLine("vTrim: failed to replace target curve.");
        return false;
      }

      actionRecord = BuildActionRecord(doc, beforeTarget, targetObj.Id, null);
      return true;
    }

    Curve[]? pieces;
    try
    {
      pieces = targetCurve.Split(split);
    }
    catch
    {
      pieces = null;
    }

    if (pieces == null || pieces.Length < 2)
    {
      RhinoApp.WriteLine("vTrim: failed to split target curve.");
      return false;
    }

    var removeIndex = ClosestPieceIndex(pieces, pickPoint);
    if (removeIndex < 0)
    {
      RhinoApp.WriteLine("vTrim: could not resolve clicked segment.");
      return false;
    }

    var keep = new List<Curve>();
    foreach (var (piece, index) in pieces.Select((p, i) => (p, i)))
    {
      if (index == removeIndex || piece == null)
        continue;

      try
      {
        if (piece.GetLength() <= doc.ModelAbsoluteTolerance)
          continue;
      }
      catch
      {
        continue;
      }

      keep.Add(piece);
    }

    if (keep.Count == 0)
    {
      RhinoApp.WriteLine("vTrim: trim would remove entire curve; skipped.");
      return false;
    }

    var output = new List<Curve>(keep);
    if (joinAfterTrim)
    {
      try
      {
        var joined = Curve.JoinCurves(keep, doc.ModelAbsoluteTolerance);
        if (joined != null && joined.Length > 0)
          output = joined.Where(c => c != null).ToList();
      }
      catch
      {
      }
    }

    if (output.Count == 0)
    {
      RhinoApp.WriteLine("vTrim: trim result invalid.");
      return false;
    }

    var attr = targetObj.Attributes.Duplicate();
    if (!doc.Objects.Replace(targetObj.Id, output[0]))
    {
      RhinoApp.WriteLine("vTrim: failed to replace target curve.");
      return false;
    }

    var addedIds = new List<Guid>();
    for (var i = 1; i < output.Count; i++)
    {
      var id = doc.Objects.AddCurve(output[i], attr);
      if (id != Guid.Empty)
        addedIds.Add(id);
    }

    actionRecord = BuildActionRecord(doc, beforeTarget, targetObj.Id, addedIds);

    return true;
  }

  private static List<Curve> FindAutoCutters(RhinoDoc doc, Guid targetId, Curve targetCurve, Point3d pickPoint)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<Curve>();

    var endTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    var viewPlane = ActiveViewPlane(doc);
    var ranked = new List<(double Distance, Curve Curve)>();

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;

      var paramsForCurve = ParamsClosestSource(doc, targetCurve, curve, pickPoint, d0, d1, endTol, viewPlane);
      if (paramsForCurve.Count == 0)
        continue;

      var nearest = NearestPickDistanceForParams(targetCurve, pickPoint, paramsForCurve);
      if (!nearest.HasValue)
        continue;

      ranked.Add((nearest.Value, curve));
    }

    return ranked.OrderBy(r => r.Distance).Select(r => r.Curve).ToList();
  }

  private static bool TryGetExtendAnchorAndDirection(Curve curve, Point3d pickPoint, out CurveEnd movingEnd, out Point3d anchor, out Vector3d direction)
  {
    var start = curve.PointAtStart;
    var end = curve.PointAtEnd;

    if (pickPoint.DistanceToSquared(start) <= pickPoint.DistanceToSquared(end))
    {
      movingEnd = CurveEnd.Start;
      anchor = start;
      direction = -curve.TangentAtStart;
    }
    else
    {
      movingEnd = CurveEnd.End;
      anchor = end;
      direction = curve.TangentAtEnd;
    }

    if (!direction.Unitize())
      return false;

    return true;
  }

  private static bool IsForwardHit(Point3d anchor, Vector3d direction, Point3d candidate, double minForward, double pathTolerance, out double forwardDistance)
  {
    forwardDistance = double.PositiveInfinity;
    var vector = candidate - anchor;
    var forward = Vector3d.Multiply(vector, direction);
    if (forward <= minForward)
      return false;

    var lateral = vector - (direction * forward);
    if (lateral.Length > pathTolerance)
      return false;

    forwardDistance = forward;
    return true;
  }

  private static bool TryNearestForwardHitFromOverlap(
    Point3d anchor,
    Vector3d direction,
    Point3d overlapPointA,
    Point3d overlapPointB,
    double minForward,
    double pathTolerance,
    out Point3d bestPoint,
    out double bestDistance)
  {
    bestPoint = Point3d.Unset;
    bestDistance = double.PositiveInfinity;

    var rawA = Vector3d.Multiply(overlapPointA - anchor, direction);
    var rawB = Vector3d.Multiply(overlapPointB - anchor, direction);
    var minRaw = Math.Min(rawA, rawB);
    var maxRaw = Math.Max(rawA, rawB);

    // If overlap starts at/behind the anchor and continues forward,
    // extending would create coincident overlap with the driver.
    if (minRaw <= minForward && maxRaw > minForward)
      return false;

    if (IsForwardHit(anchor, direction, overlapPointA, minForward, pathTolerance, out var dA))
    {
      bestPoint = overlapPointA;
      bestDistance = dA;
    }

    if (IsForwardHit(anchor, direction, overlapPointB, minForward, pathTolerance, out var dB) && dB < bestDistance)
    {
      bestPoint = overlapPointB;
      bestDistance = dB;
    }

    return bestPoint.IsValid && double.IsFinite(bestDistance);
  }

  private static double MapProjectedToLineParameter(double projectedT, double projectedStart, double projectedEnd, double lineStart, double lineEnd)
  {
    if (Math.Abs(projectedEnd - projectedStart) <= 1.0e-12)
      return lineStart;

    var s = (projectedT - projectedStart) / (projectedEnd - projectedStart);
    s = Math.Max(0.0, Math.Min(1.0, s));
    return lineStart + ((lineEnd - lineStart) * s);
  }

  private static bool TryClosestForwardHit(
    RhinoDoc doc,
    Point3d anchor,
    Vector3d direction,
    Guid targetId,
    IReadOnlyCollection<Guid>? candidateIds,
    out Point3d hitPoint,
    out double extensionDistance)
  {
    hitPoint = Point3d.Unset;
    extensionDistance = double.PositiveInfinity;

    var minForward = Math.Max(1.0e-9, doc.ModelAbsoluteTolerance * 1.0e-4);
    var pathTol = Math.Max(doc.ModelAbsoluteTolerance * 10.0, 1.0e-4);
    var rayLength = Math.Max(10000.0, 100000.0 * doc.ModelAbsoluteTolerance);

    var line = new Line(anchor, anchor + (direction * rayLength));
    var lineCurve = new LineCurve(line);

    var viewPlane = ActiveViewPlane(doc);
    var projectedLine = ProjectCurveToPlane(lineCurve, viewPlane);

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;

      if (candidateIds != null && !candidateIds.Contains(obj.Id))
        continue;

      var candidateBest = double.PositiveInfinity;
      var candidatePoint = Point3d.Unset;

      var worldEvents = Rhino.Geometry.Intersect.Intersection.CurveCurve(lineCurve, curve, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
      if (worldEvents != null)
      {
        foreach (var ev in worldEvents)
        {
          if (ev.IsPoint)
          {
            var point = ev.PointA;
            if (IsForwardHit(anchor, direction, point, minForward, pathTol, out var dist) && dist < candidateBest)
            {
              candidateBest = dist;
              candidatePoint = point;
            }
          }
          else if (ev.IsOverlap)
          {
            var p0 = lineCurve.PointAt(ev.OverlapA.T0);
            var p1 = lineCurve.PointAt(ev.OverlapA.T1);

            if (TryNearestForwardHitFromOverlap(anchor, direction, p0, p1, minForward, pathTol, out var overlapPoint, out var overlapDistance) && overlapDistance < candidateBest)
            {
              candidateBest = overlapDistance;
              candidatePoint = overlapPoint;
            }
          }
        }
      }

      if (projectedLine != null)
      {
        var projectedCurve = ProjectCurveToPlane(curve, viewPlane);
        if (projectedCurve != null)
        {
          var projEvents = Rhino.Geometry.Intersect.Intersection.CurveCurve(projectedLine, projectedCurve, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
          if (projEvents != null && TryGetDomain(projectedLine, out var p0, out var p1) && TryGetDomain(lineCurve, out var l0, out var l1))
          {
            foreach (var ev in projEvents)
            {
              if (ev.IsPoint)
              {
                var tLine = MapProjectedToLineParameter(ev.ParameterA, p0, p1, l0, l1);
                var point = lineCurve.PointAt(tLine);
                if (IsForwardHit(anchor, direction, point, minForward, pathTol, out var dist) && dist < candidateBest)
                {
                  candidateBest = dist;
                  candidatePoint = point;
                }
              }
              else if (ev.IsOverlap)
              {
                var tLine0 = MapProjectedToLineParameter(ev.OverlapA.T0, p0, p1, l0, l1);
                var tLine1 = MapProjectedToLineParameter(ev.OverlapA.T1, p0, p1, l0, l1);
                var overlapPoint0 = lineCurve.PointAt(tLine0);
                var overlapPoint1 = lineCurve.PointAt(tLine1);

                if (TryNearestForwardHitFromOverlap(anchor, direction, overlapPoint0, overlapPoint1, minForward, pathTol, out var overlapPoint, out var overlapDistance) && overlapDistance < candidateBest)
                {
                  candidateBest = overlapDistance;
                  candidatePoint = overlapPoint;
                }
              }
            }
          }
        }
      }

      // Endpoint candidates along extension path.
      foreach (var endpoint in new[] { curve.PointAtStart, curve.PointAtEnd })
      {
        if (IsForwardHit(anchor, direction, endpoint, minForward, pathTol, out var dist) && dist < candidateBest)
        {
          candidateBest = dist;
          candidatePoint = endpoint;
        }
      }

      if (candidatePoint.IsValid && candidateBest < extensionDistance)
      {
        extensionDistance = candidateBest;
        hitPoint = candidatePoint;
      }
    }

    return hitPoint.IsValid && double.IsFinite(extensionDistance);
  }

  private static List<Curve> ExtendDriverCurves(RhinoDoc doc, Guid targetId, IReadOnlyCollection<Guid>? candidateIds)
  {
    var curves = new List<Curve>();

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;
      if (candidateIds != null && !candidateIds.Contains(obj.Id))
        continue;

      curves.Add(curve);
    }

    return curves;
  }

  private static Curve? ExtractAddedExtensionPiece(RhinoDoc doc, Curve extendedCurve, Point3d anchor, CurveEnd movingEnd)
  {
    if (!extendedCurve.ClosestPoint(anchor, out var tAnchor))
      return null;

    try
    {
      Curve? addedPiece = movingEnd == CurveEnd.Start
        ? extendedCurve.Trim(new Interval(extendedCurve.Domain.T0, tAnchor))
        : extendedCurve.Trim(new Interval(tAnchor, extendedCurve.Domain.T1));

      if (addedPiece == null)
        return null;

      if (addedPiece.GetLength() <= doc.ModelAbsoluteTolerance)
        return null;

      return addedPiece;
    }
    catch
    {
      return null;
    }
  }

  private static bool CurveOverlapsAnyDriver(RhinoDoc doc, Curve candidate, IReadOnlyList<Curve> drivers)
  {
    if (drivers.Count == 0)
      return false;

    var overlapLenTol = Math.Max(doc.ModelAbsoluteTolerance * 2.0, 1.0e-8);
    foreach (var driver in drivers)
    {
      if (driver == null)
        continue;

      var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(candidate, driver, doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians);
      if (events == null)
        continue;

      foreach (var ev in events)
      {
        if (!ev.IsOverlap)
          continue;

        try
        {
          var overlapPiece = candidate.Trim(ev.OverlapA);
          if (overlapPiece != null && overlapPiece.GetLength() > overlapLenTol)
            return true;
        }
        catch
        {
          if (Math.Abs(ev.OverlapA.Length) > 1.0e-9)
            return true;
        }
      }
    }

    return false;
  }

  private static Curve? ValidateExtendedCandidate(
    RhinoDoc doc,
    Curve? extendedCandidate,
    Point3d anchor,
    CurveEnd movingEnd,
    IReadOnlyList<Curve> drivers)
  {
    if (extendedCandidate == null)
      return null;

    var addedPiece = ExtractAddedExtensionPiece(doc, extendedCandidate, anchor, movingEnd);
    if (addedPiece == null)
      return null;

    if (CurveOverlapsAnyDriver(doc, addedPiece, drivers))
      return null;

    return extendedCandidate;
  }

  private static Curve? TryBuildExtendedCurve(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    CurveEnd movingEnd,
    Point3d anchor,
    Vector3d direction,
    double extensionDistance,
    bool extendAsLine,
    IReadOnlyCollection<Guid>? candidateIds)
  {
    var style = extendAsLine ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;
    var drivers = ExtendDriverCurves(doc, targetObj.Id, candidateIds);

    if (drivers.Count > 0)
    {
      try
      {
        var byBoundary = targetCurve.Extend(movingEnd, style, drivers.ToArray());
        var validatedBoundary = ValidateExtendedCandidate(doc, byBoundary, anchor, movingEnd, drivers);
        if (validatedBoundary != null)
          return validatedBoundary;
      }
      catch
      {
      }
    }

    try
    {
      var byLength = targetCurve.Extend(movingEnd, extensionDistance, style);
      var validatedByLength = ValidateExtendedCandidate(doc, byLength, anchor, movingEnd, drivers);
      if (validatedByLength != null)
        return validatedByLength;
    }
    catch
    {
    }

    // Last fallback: line-extend to hit point if boundary extend failed.
    try
    {
      var extra = Math.Max(extensionDistance, doc.ModelAbsoluteTolerance * 2.0);
      var byLength = targetCurve.Extend(movingEnd, extra, CurveExtensionStyle.Line);
      var validatedFallback = ValidateExtendedCandidate(doc, byLength, anchor, movingEnd, drivers);
      if (validatedFallback != null)
        return validatedFallback;
    }
    catch
    {
    }

    return null;
  }

  private static Curve? BuildExtendPreviewPiece(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    bool extendAsLine,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds)
  {
    if (!TryGetExtendAnchorAndDirection(targetCurve, pickPoint, out var movingEnd, out var anchor, out var direction))
      return null;

    IReadOnlyCollection<Guid>? candidateIds = null;
    if (!autoMode)
      candidateIds = cutterIds.Where(id => id != Guid.Empty && id != targetObj.Id).ToHashSet();

    if (!TryClosestForwardHit(doc, anchor, direction, targetObj.Id, candidateIds, out _, out var extensionDistance))
      return null;

    var extended = TryBuildExtendedCurve(doc, targetObj, targetCurve, movingEnd, anchor, direction, extensionDistance, extendAsLine, candidateIds);
    if (extended == null)
      return null;

    return ExtractAddedExtensionPiece(doc, extended, anchor, movingEnd);
  }

  private static bool ExtendCurveObject(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    bool extendAsLine,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds,
    out ActionRecord? actionRecord)
  {
    actionRecord = null;

    if (!TryCaptureCurveSnapshot(doc, targetObj.Id, out var beforeTarget) || beforeTarget == null)
    {
      RhinoApp.WriteLine("vTrim: failed to capture target state.");
      return false;
    }

    if (!TryGetExtendAnchorAndDirection(targetCurve, pickPoint, out var movingEnd, out var anchor, out var direction))
    {
      RhinoApp.WriteLine("vTrim: failed to resolve extend direction.");
      return false;
    }

    IReadOnlyCollection<Guid>? candidateIds = null;
    if (!autoMode)
      candidateIds = cutterIds.Where(id => id != Guid.Empty && id != targetObj.Id).ToHashSet();

    if (!TryClosestForwardHit(doc, anchor, direction, targetObj.Id, candidateIds, out _, out var extensionDistance))
    {
      RhinoApp.WriteLine("vTrim: no extend intersection in this direction.");
      return false;
    }

    var extended = TryBuildExtendedCurve(doc, targetObj, targetCurve, movingEnd, anchor, direction, extensionDistance, extendAsLine, candidateIds);
    if (extended == null)
    {
      RhinoApp.WriteLine("vTrim: failed to build extended curve.");
      return false;
    }

    if (!doc.Objects.Replace(targetObj.Id, extended))
    {
      RhinoApp.WriteLine("vTrim: failed to replace target curve.");
      return false;
    }

    actionRecord = BuildActionRecord(doc, beforeTarget, targetObj.Id, null);
    return true;
  }
}
