using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
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

  private static double _desiredLength = 10.0;
  private static bool _extendWithLine;
  private static int _mode;

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
      var pick = PickCurveWithOptions(doc, history.Count > 0);
      if (pick.State == PickState.Cancel)
        return Result.Success;

      if (pick.State == PickState.Undo)
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
          continue;
        }

        RhinoApp.WriteLine("vLineLength: undo applied.");
        doc.Views.Redraw();
        continue;
      }

      if (pick.ObjectId == Guid.Empty)
        return Result.Cancel;

      var obj = doc.Objects.FindId(pick.ObjectId);
      if (obj?.Geometry is not Curve sourceCurve)
      {
        RhinoApp.WriteLine("vLineLength: selected curve no longer exists.");
        continue;
      }

      if (sourceCurve.IsClosed)
      {
        RhinoApp.WriteLine("vLineLength: closed curves are not supported.");
        continue;
      }

      var original = sourceCurve.DuplicateCurve();
      if (original == null)
      {
        RhinoApp.WriteLine("vLineLength: failed to capture curve for undo.");
        continue;
      }

      var currentLength = sourceCurve.GetLength();
      var targetLength = ComputeTargetLength(currentLength, _desiredLength, _mode);
      var movingEnd = ResolveMovingEnd(sourceCurve, pick.SelectionPoint, _mode);
      var extensionStyle = _extendWithLine ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;

      var resized = ResizeCurve(sourceCurve, targetLength, movingEnd, extensionStyle, doc.ModelAbsoluteTolerance);
      if (resized == null)
      {
        RhinoApp.WriteLine("vLineLength: failed to resize curve.");
        continue;
      }

      if (!doc.Objects.Replace(obj.Id, resized))
      {
        RhinoApp.WriteLine("vLineLength: failed to update curve.");
        continue;
      }

      history.Push(new UndoRecord(obj.Id, original));
      SavePersistedOptions();

      RhinoApp.WriteLine($"vLineLength: curve length set to {targetLength:g}.");
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
          mode = Math.Max(0, Math.Min(ModeNames.Length - 1, index));
        }

        return (desiredLength, extendWithLine, mode);
      });

    _desiredLength = Math.Max(values.desiredLength, RhinoMath.ZeroTolerance);
    _extendWithLine = values.extendWithLine;
    _mode = Math.Max(0, Math.Min(ModeNames.Length - 1, values.mode));
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[DesiredLengthKey] = _desiredLength;
        section[ExtendWithLineKey] = _extendWithLine;
        section[ModeKey] = _mode;
      });
  }

  private static PickResult PickCurveWithOptions(RhinoDoc doc, bool canUndo)
  {
    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select open curve to set length");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.AcceptNumber(true, false);
      go.AcceptString(true);
      go.AcceptNothing(true);

      var undoIndex = canUndo ? go.AddOption("Undo") : -1;
      var lengthOption = new OptionDouble(_desiredLength, RhinoMath.UnsetValue, RhinoMath.UnsetValue);
      var extendOption = new OptionToggle(_extendWithLine, "Smooth", "Line");
      go.AddOptionDouble("Length", ref lengthOption);
      go.AddOptionToggle("ExtendMode", ref extendOption);
      var modeOptionIndex = go.AddOptionList("Mode", ModeNames, _mode);

      var result = go.Get();
      if (go.CommandResult() != Result.Success)
        return new PickResult(PickState.Cancel, Guid.Empty, Point3d.Unset);

      if (result == GetResult.Number)
      {
        var number = go.Number();
        if (Math.Abs(number) <= RhinoMath.ZeroTolerance)
        {
          RhinoApp.WriteLine("vLineLength: length must be non-zero.");
          continue;
        }

        if (number < 0.0)
        {
          if (_mode == 0)
          {
            RhinoApp.WriteLine("vLineLength: negative length is not allowed in Total mode.");
            continue;
          }

          _mode = _mode == 2 ? 1 : 2;
          _desiredLength = Math.Abs(number);
        }
        else
        {
          _desiredLength = number;
        }

        _extendWithLine = extendOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.String)
      {
        var text = (go.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
        if (text is "total" or "t")
          _mode = 0;
        else if (text is "add" or "a" or "+")
          _mode = 1;
        else if (text is "subtract" or "sub" or "s" or "-")
          _mode = 2;
        else if (text is "add/subtract" or "addsubtract" or "as" or "sa")
          _mode = _mode == 1 ? 2 : 1;
        else
        {
          RhinoApp.WriteLine("vLineLength: unknown hidden mode keyword. Use total, add, subtract, add/subtract.");
          continue;
        }

        _extendWithLine = extendOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Option)
      {
        if (canUndo && go.OptionIndex() == undoIndex)
          return new PickResult(PickState.Undo, Guid.Empty, Point3d.Unset);

        _desiredLength = Math.Abs(lengthOption.CurrentValue);
        _extendWithLine = extendOption.CurrentValue;

        var option = go.Option();
        if (option != null && option.Index == modeOptionIndex)
          _mode = option.CurrentListOptionIndex;

        if (_desiredLength <= RhinoMath.ZeroTolerance)
        {
          RhinoApp.WriteLine("vLineLength: length must be non-zero.");
          continue;
        }

        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Nothing || result == GetResult.Cancel)
        return new PickResult(PickState.Cancel, Guid.Empty, Point3d.Unset);

      if (result != GetResult.Object)
        continue;

      var objRef = go.Object(0);
      if (objRef == null)
        continue;

      var curve = objRef.Curve();
      if (curve == null || curve.IsClosed)
      {
        RhinoApp.WriteLine("vLineLength: select an open curve.");
        continue;
      }

      _desiredLength = Math.Abs(lengthOption.CurrentValue);
      _extendWithLine = extendOption.CurrentValue;
      SavePersistedOptions();

      var selectionPoint = objRef.SelectionPoint();
      if (!selectionPoint.IsValid)
      {
        var domMid = curve.Domain.Mid;
        selectionPoint = curve.PointAt(domMid);
      }

      return new PickResult(PickState.Select, objRef.ObjectId, selectionPoint);
    }
  }

  private static double ComputeTargetLength(double currentLength, double desiredLength, int mode)
  {
    return mode switch
    {
      0 => desiredLength,
      1 => currentLength + desiredLength,
      _ => currentLength - desiredLength
    };
  }

  private static CurveEnd ResolveMovingEnd(Curve curve, Point3d pickPoint, int mode)
  {
    var startDist = pickPoint.DistanceTo(curve.PointAtStart);
    var endDist = pickPoint.DistanceTo(curve.PointAtEnd);
    var hoveredEnd = startDist <= endDist ? CurveEnd.Start : CurveEnd.End;

    // In Total mode the hovered end is anchor, so the opposite end moves.
    if (mode == 0)
      return hoveredEnd == CurveEnd.Start ? CurveEnd.End : CurveEnd.Start;

    return hoveredEnd;
  }

  private static Curve? ResizeCurve(Curve curve, double targetLength, CurveEnd movingEnd, CurveExtensionStyle extensionStyle, double tolerance)
  {
    var currentLength = curve.GetLength();
    if (targetLength <= RhinoMath.ZeroTolerance)
      return null;

    if (Math.Abs(currentLength - targetLength) <= tolerance)
      return curve.DuplicateCurve();

    if (targetLength < currentLength)
      return TrimToLength(curve, targetLength, movingEnd);

    var delta = targetLength - currentLength;
    return curve.Extend(movingEnd, delta, extensionStyle);
  }

  private static Curve? TrimToLength(Curve curve, double targetLength, CurveEnd movingEnd)
  {
    var domain = curve.Domain;

    if (movingEnd == CurveEnd.End)
    {
      if (!curve.LengthParameter(targetLength, out var trimParam))
        return null;
      return curve.Trim(new Interval(domain.T0, trimParam));
    }

    var keepLengthFromStart = curve.GetLength() - targetLength;
    if (!curve.LengthParameter(keepLengthFromStart, out var trimFromStart))
      return null;

    return curve.Trim(new Interval(trimFromStart, domain.T1));
  }

  private enum PickState
  {
    Select,
    Undo,
    Cancel
  }

  private readonly record struct PickResult(PickState State, Guid ObjectId, Point3d SelectionPoint);

  private readonly record struct UndoRecord(Guid ObjectId, Curve OriginalCurve);
}
