using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native line-length editor command ported from LineLength.py.
/// </summary>
public sealed class vLineLength : Command
{
  private const string OptionsSectionName = "vLineLength";
  private const string DesiredLengthKey = "desiredLength";
  private const string ExtendWithLineKey = "extendWithLine";
  private const string ModeKey = "mode";

  private static readonly string[] ModeNames = { "Total", "Add", "Subtract" };

  private const int ModeTotal = 0;
  private const int ModeAdd = 1;
  private const int ModeSubtract = 2;

  private static double _desiredLength = 10.0;
  private static bool _extendWithLine;
  private static int _mode = ModeTotal;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLineLength";

  /// <summary>
  /// Resizes open curves with Total/Add/Subtract length modes.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var history = new Stack<UndoRecord>();

    while (true)
    {
      var pick = PickCurveWithPreview(doc, history.Count > 0);
      if (pick.State == PickerState.Cancel)
        return Result.Success;

      if (pick.State == PickerState.Undo)
      {
        if (history.Count == 0)
        {
          RhinoApp.WriteLine("vLineLength: nothing to undo.");
          continue;
        }

        var record = history.Pop();
        if (!doc.Objects.Replace(record.ObjectId, record.OriginalCurve))
        {
          RhinoApp.WriteLine("vLineLength: undo failed.");
        }
        else
        {
          RhinoApp.WriteLine("vLineLength: undo applied.");
          doc.Views.Redraw();
        }

        continue;
      }

      if (pick.ObjectId == Guid.Empty)
        return Result.Cancel;

      var rhObj = doc.Objects.FindId(pick.ObjectId);
      if (rhObj?.Geometry is not Curve curve)
      {
        RhinoApp.WriteLine("vLineLength: selected curve no longer exists.");
        continue;
      }

      if (curve.IsClosed)
      {
        RhinoApp.WriteLine("vLineLength: closed curves are not supported.");
        continue;
      }

      var original = curve.DuplicateCurve();
      if (original == null)
      {
        RhinoApp.WriteLine("vLineLength: failed to capture curve for undo.");
        continue;
      }

      var extensionStyle = _extendWithLine ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;
      var targetLength = ComputeTargetLength(curve.GetLength(), _desiredLength, _mode);

      var resized = ResizeCurve(curve, targetLength, pick.MovingEnd, extensionStyle, doc.ModelAbsoluteTolerance);
      if (resized == null)
      {
        RhinoApp.WriteLine("vLineLength: failed to resize curve.");
        continue;
      }

      if (!doc.Objects.Replace(pick.ObjectId, resized))
      {
        RhinoApp.WriteLine("vLineLength: failed to update curve.");
        continue;
      }

      history.Push(new UndoRecord(pick.ObjectId, original));

      SavePersistedOptions();
      RhinoApp.WriteLine($"vLineLength: curve length set to {targetLength:g}");
      doc.Views.Redraw();
    }
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var desiredLength = _desiredLength;
        var extendWithLine = _extendWithLine;
        var mode = _mode;

        if (vToolsOptionStore.TryGetDouble(section, DesiredLengthKey, out var persistedLength) && persistedLength > RhinoMath.ZeroTolerance)
          desiredLength = persistedLength;

        if (vToolsOptionStore.TryGetBool(section, ExtendWithLineKey, out var persistedExtend))
          extendWithLine = persistedExtend;

        if (vToolsOptionStore.TryGetDouble(section, ModeKey, out var persistedMode))
        {
          var index = (int)Math.Round(persistedMode, MidpointRounding.AwayFromZero);
          mode = ClampMode(index);
        }

        return (desiredLength, extendWithLine, mode);
      });

    _desiredLength = Math.Max(values.desiredLength, RhinoMath.ZeroTolerance);
    _extendWithLine = values.extendWithLine;
    _mode = ClampMode(values.mode);
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[DesiredLengthKey] = Math.Abs(_desiredLength);
        section[ExtendWithLineKey] = _extendWithLine;
        section[ModeKey] = _mode;
      });
  }

  private static PickResult PickCurveWithPreview(RhinoDoc doc, bool canUndo)
  {
    var gp = new GetPoint();
    gp.SetCommandPrompt("Hover snapped open curve to preview, click curve to confirm");
    gp.AcceptNumber(true, false);
    gp.AcceptString(true);

    var lengthOption = new OptionDouble(_desiredLength, RhinoMath.UnsetValue, RhinoMath.UnsetValue);
    var extendToggle = new OptionToggle(_extendWithLine, "Smooth", "Line");
    var modeIndex = _mode;

    var addPreviewColor = Color.FromArgb(255, 225, 95);
    var subtractPreviewColor = Color.FromArgb(255, 100, 50);
    var startColor = Color.FromArgb(85, 200, 95);
    var endColor = Color.FromArgb(255, 30, 30);

    var preview = new LineLengthPreviewConduit { Enabled = true };

    EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
    {
      preview.ClearPreview();

      if (!TryFindOpenCurveAtPoint(doc, e.CurrentPoint, out RhinoObject? _, out var curve) || curve == null)
        return;

      var movingEnd = ResolveMovingEndForMode(curve, e.CurrentPoint, modeIndex);
      var currentLength = curve.GetLength();
      var targetLength = ComputeTargetLength(currentLength, Math.Abs(lengthOption.CurrentValue), modeIndex);
      var style = extendToggle.CurrentValue ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;

      var previewCurve = ResizeCurve(curve, targetLength, movingEnd, style, doc.ModelAbsoluteTolerance);
      if (previewCurve == null)
        return;

      var curveItems = new List<CurvePreviewItem>();
      var lineItems = new List<LinePreviewItem>();
      var pointItems = new List<PointPreviewItem>();

      if (modeIndex != ModeTotal)
      {
        var originalMovingPoint = movingEnd == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
        var previewMovingPoint = movingEnd == CurveEnd.Start ? previewCurve.PointAtStart : previewCurve.PointAtEnd;
        var previewColor = modeIndex == ModeSubtract ? subtractPreviewColor : addPreviewColor;

        var changedSegment = BuildChangedSegment(curve, previewCurve, currentLength, targetLength, movingEnd, modeIndex);
        if (changedSegment != null)
          curveItems.Add(new CurvePreviewItem(changedSegment, previewColor, 3));
        else
          lineItems.Add(new LinePreviewItem(originalMovingPoint, previewMovingPoint, previewColor, 3));

        pointItems.Add(new PointPreviewItem(originalMovingPoint, PointStyle.Circle, 4, startColor));
        pointItems.Add(new PointPreviewItem(previewMovingPoint, PointStyle.Circle, 4, endColor));
        preview.SetPreview(curveItems, lineItems, pointItems);
        return;
      }

      var previewStart = previewCurve.PointAtStart;
      var previewEnd = previewCurve.PointAtEnd;
      var previewMoving = movingEnd == CurveEnd.Start ? previewStart : previewEnd;

      curveItems.Add(new CurvePreviewItem(previewCurve, addPreviewColor, 3));
      pointItems.Add(new PointPreviewItem(previewStart, PointStyle.Circle, 4, startColor));
      pointItems.Add(new PointPreviewItem(previewEnd, PointStyle.Circle, 4, startColor));
      pointItems.Add(new PointPreviewItem(previewMoving, PointStyle.Circle, 4, endColor));
      preview.SetPreview(curveItems, lineItems, pointItems);
    };

    gp.DynamicDraw += drawPreview;

    try
    {
      while (true)
      {
        gp.ClearCommandOptions();
        var undoOptionIndex = canUndo ? gp.AddOption("Undo") : -1;
        gp.AddOptionDouble("Length", ref lengthOption);
        gp.AddOptionToggle("ExtendMode", ref extendToggle);
        var modeOptionIndex = gp.AddOptionList("Mode", ModeNames, modeIndex);

        var result = gp.Get();
        if (gp.CommandResult() != Result.Success)
          return new PickResult(PickerState.Cancel, Guid.Empty, CurveEnd.None);

        if (result == GetResult.Number)
        {
          var numberValue = gp.Number();
          if (Math.Abs(numberValue) <= RhinoMath.ZeroTolerance)
          {
            RhinoApp.WriteLine("vLineLength: length must be non-zero.");
            continue;
          }

          if (numberValue < 0.0)
          {
            if (modeIndex == ModeTotal)
            {
              RhinoApp.WriteLine("vLineLength: negative length is not allowed in Total mode.");
              continue;
            }

            modeIndex = modeIndex == ModeSubtract ? ModeAdd : ModeSubtract;
            lengthOption.CurrentValue = Math.Abs(numberValue);
          }
          else
          {
            lengthOption.CurrentValue = numberValue;
          }

          _desiredLength = Math.Abs(lengthOption.CurrentValue);
          _extendWithLine = extendToggle.CurrentValue;
          _mode = ClampMode(modeIndex);
          SavePersistedOptions();
          continue;
        }

        if (result == GetResult.String)
        {
          var text = (gp.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
          if (text is "total" or "t")
            modeIndex = ModeTotal;
          else if (text is "add" or "a" or "+")
            modeIndex = ModeAdd;
          else if (text is "subtract" or "sub" or "s" or "-")
            modeIndex = ModeSubtract;
          else if (text is "add/subtract" or "addsubtract" or "as" or "sa")
            modeIndex = modeIndex == ModeAdd ? ModeSubtract : ModeAdd;
          else
          {
            RhinoApp.WriteLine("vLineLength: unknown hidden mode keyword. Use total, add, subtract, add/subtract.");
            continue;
          }

          _desiredLength = Math.Abs(lengthOption.CurrentValue);
          _extendWithLine = extendToggle.CurrentValue;
          _mode = ClampMode(modeIndex);
          SavePersistedOptions();
          continue;
        }

        if (result == GetResult.Option)
        {
          if (canUndo && gp.OptionIndex() == undoOptionIndex)
            return new PickResult(PickerState.Undo, Guid.Empty, CurveEnd.None);

          var option = gp.Option();
          if (option != null && option.Index == modeOptionIndex)
            modeIndex = ClampMode(option.CurrentListOptionIndex);

          if (modeIndex == ModeAdd && lengthOption.CurrentValue < 0.0)
          {
            modeIndex = ModeSubtract;
            lengthOption.CurrentValue = Math.Abs(lengthOption.CurrentValue);
          }

          if (Math.Abs(lengthOption.CurrentValue) <= RhinoMath.ZeroTolerance)
          {
            RhinoApp.WriteLine("vLineLength: length must be non-zero.");
            continue;
          }

          _desiredLength = Math.Abs(lengthOption.CurrentValue);
          _extendWithLine = extendToggle.CurrentValue;
          _mode = ClampMode(modeIndex);
          SavePersistedOptions();
          continue;
        }

        if (result != GetResult.Point)
          return new PickResult(PickerState.Cancel, Guid.Empty, CurveEnd.None);

        if (!TryFindOpenCurveAtPoint(doc, gp.Point(), out var rhObj, out var hitCurve) || rhObj == null || hitCurve == null)
        {
          RhinoApp.WriteLine("vLineLength: hover and click directly on an open curve.");
          continue;
        }

        var movingEnd = ResolveMovingEndForMode(hitCurve, gp.Point(), modeIndex);

        _desiredLength = Math.Abs(lengthOption.CurrentValue);
        _extendWithLine = extendToggle.CurrentValue;
        _mode = ClampMode(modeIndex);
        SavePersistedOptions();

        return new PickResult(PickerState.Select, rhObj.Id, movingEnd);
      }
    }
    finally
    {
      gp.DynamicDraw -= drawPreview;
      preview.ClearPreview();
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static bool TryFindOpenCurveAtPoint(RhinoDoc doc, Point3d worldPoint, out RhinoObject? bestObject, out Curve? bestCurve)
  {
    bestObject = null;
    bestCurve = null;

    if (!worldPoint.IsValid)
      return false;

    var bestDistance = double.MaxValue;
    var snapTolerance = doc.ModelAbsoluteTolerance * 2.0;

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.Curve,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      DeletedObjects = false
    };

    foreach (var rhObj in doc.Objects.GetObjectList(settings))
    {
      if (rhObj?.Geometry is not Curve curve || curve.IsClosed)
        continue;

      if (!curve.ClosestPoint(worldPoint, out var t))
        continue;

      var curvePoint = curve.PointAt(t);
      var distance = curvePoint.DistanceTo(worldPoint);
      if (distance > snapTolerance || distance >= bestDistance)
        continue;

      bestDistance = distance;
      bestObject = rhObj;
      bestCurve = curve;
    }

    return bestObject != null && bestCurve != null;
  }

  private static double ComputeTargetLength(double currentLength, double desiredLength, int mode)
  {
    return mode switch
    {
      ModeTotal => desiredLength,
      ModeAdd => currentLength + desiredLength,
      _ => currentLength - desiredLength
    };
  }

  private static Curve? BuildChangedSegment(Curve sourceCurve, Curve previewCurve, double currentLength, double targetLength, CurveEnd movingEnd, int mode)
  {
    if (mode == ModeSubtract)
    {
      var domain = sourceCurve.Domain;
      if (movingEnd == CurveEnd.Start)
      {
        var removedLength = currentLength - targetLength;
        if (!sourceCurve.LengthParameter(removedLength, out var splitParam))
          return null;

        return sourceCurve.Trim(new Interval(domain.T0, splitParam));
      }

      if (!sourceCurve.LengthParameter(targetLength, out var splitParamFromEnd))
        return null;

      return sourceCurve.Trim(new Interval(splitParamFromEnd, domain.T1));
    }

    var originalMovingPoint = movingEnd == CurveEnd.Start ? sourceCurve.PointAtStart : sourceCurve.PointAtEnd;
    if (!previewCurve.ClosestPoint(originalMovingPoint, out var split))
      return null;

    var previewDomain = previewCurve.Domain;
    return movingEnd == CurveEnd.Start
      ? previewCurve.Trim(new Interval(previewDomain.T0, split))
      : previewCurve.Trim(new Interval(split, previewDomain.T1));
  }

  private static CurveEnd ResolveMovingEndForMode(Curve curve, Point3d pickPoint, int mode)
  {
    var hoveredEnd = PickCurveEndFromPoint(curve, pickPoint);
    if (mode == ModeTotal)
      return hoveredEnd == CurveEnd.Start ? CurveEnd.End : CurveEnd.Start;

    return hoveredEnd;
  }

  private static CurveEnd PickCurveEndFromPoint(Curve curve, Point3d pickPoint)
  {
    var startDistance = pickPoint.DistanceTo(curve.PointAtStart);
    var endDistance = pickPoint.DistanceTo(curve.PointAtEnd);
    return startDistance <= endDistance ? CurveEnd.Start : CurveEnd.End;
  }

  private static Curve? TrimToLength(Curve curve, double desiredLength, CurveEnd movingEnd)
  {
    var domain = curve.Domain;

    if (movingEnd == CurveEnd.End)
    {
      if (!curve.LengthParameter(desiredLength, out var trimParam))
        return null;
      return curve.Trim(new Interval(domain.T0, trimParam));
    }

    var lengthToTrimFromStart = curve.GetLength() - desiredLength;
    if (!curve.LengthParameter(lengthToTrimFromStart, out var trimFromStart))
      return null;

    return curve.Trim(new Interval(trimFromStart, domain.T1));
  }

  private static Curve? ResizeCurve(Curve curve, double desiredLength, CurveEnd movingEnd, CurveExtensionStyle extensionStyle, double tolerance)
  {
    var currentLength = curve.GetLength();

    if (desiredLength <= RhinoMath.ZeroTolerance)
      return null;

    if (Math.Abs(currentLength - desiredLength) <= tolerance)
      return curve.DuplicateCurve();

    if (desiredLength < currentLength)
      return TrimToLength(curve, desiredLength, movingEnd);

    var delta = desiredLength - currentLength;
    return curve.Extend(movingEnd, delta, extensionStyle);
  }

  private static int ClampMode(int mode)
  {
    if (mode < ModeTotal)
      return ModeTotal;
    if (mode > ModeSubtract)
      return ModeSubtract;
    return mode;
  }

  private enum PickerState
  {
    Select,
    Undo,
    Cancel
  }

  private readonly record struct PickResult(PickerState State, Guid ObjectId, CurveEnd MovingEnd);

  private readonly record struct UndoRecord(Guid ObjectId, Curve OriginalCurve);

  private readonly record struct CurvePreviewItem(Curve Curve, Color Color, int Thickness);

  private readonly record struct LinePreviewItem(Point3d Start, Point3d End, Color Color, int Thickness);

  private readonly record struct PointPreviewItem(Point3d Point, PointStyle Style, int Size, Color Color);

  private sealed class LineLengthPreviewConduit : DisplayConduit
  {
    private List<CurvePreviewItem> _curveItems = new();
    private List<LinePreviewItem> _lineItems = new();
    private List<PointPreviewItem> _pointItems = new();

    public void ClearPreview()
    {
      _curveItems = new List<CurvePreviewItem>();
      _lineItems = new List<LinePreviewItem>();
      _pointItems = new List<PointPreviewItem>();
    }

    public void SetPreview(List<CurvePreviewItem>? curveItems, List<LinePreviewItem>? lineItems, List<PointPreviewItem>? pointItems)
    {
      _curveItems = curveItems ?? new List<CurvePreviewItem>();
      _lineItems = lineItems ?? new List<LinePreviewItem>();
      _pointItems = pointItems ?? new List<PointPreviewItem>();
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      e.Display.EnableDepthTesting(false);
      e.Display.EnableDepthWriting(false);

      try
      {
        foreach (var item in _curveItems)
          e.Display.DrawCurve(item.Curve, item.Color, item.Thickness);

        foreach (var item in _lineItems)
          e.Display.DrawLine(item.Start, item.End, item.Color, item.Thickness);

        foreach (var item in _pointItems)
          e.Display.DrawPoint(item.Point, item.Style, item.Size, item.Color);
      }
      finally
      {
        e.Display.EnableDepthWriting(true);
        e.Display.EnableDepthTesting(true);
      }
    }
  }
}
