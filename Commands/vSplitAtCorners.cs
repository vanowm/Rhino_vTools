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
/// Native corner-splitting command ported from SplitAtCorners.py.
/// </summary>
public sealed class vSplitAtCorners : Command
{
  private const string OptionsSectionName = "vSplitAtCorners";
  private const string MinAngleKey = "minAngleDeg";
  private const string DeleteInputKey = "deleteInput";

  private const double MinAllowedAngleDeg = 0.1;
  private const double MaxAllowedAngleDeg = 179.9;

  private static double _minAngleDeg = 30.0;
  private static bool _deleteInput = true;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vSplitAtCorners";

  /// <summary>
  /// Splits curves at detected corner discontinuities above MinAngle.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    if (!TryGetCurvesAndOptions(doc, out var curveObjects))
      return Result.Cancel;

    SavePersistedOptions();

    var splitCurveCount = 0;
    var addedPieceCount = 0;
    var totalSplitPoints = 0;

    foreach (var curveObject in curveObjects)
    {
      if (curveObject?.Geometry is not Curve sourceCurve)
        continue;

      var workingCurve = sourceCurve.DuplicateCurve();
      if (workingCurve == null)
        continue;

      var splitParameters = CollectSplitParameters(workingCurve, _minAngleDeg);
      if (splitParameters.Count == 0)
        continue;

      var pieces = workingCurve.Split(splitParameters);
      if (pieces == null || pieces.Length <= 1)
        continue;

      var attrs = curveObject.Attributes.Duplicate();
      var addedForThisCurve = 0;
      foreach (var piece in pieces)
      {
        if (piece == null || !piece.IsValid)
          continue;

        var id = doc.Objects.AddCurve(piece, attrs);
        if (id != Guid.Empty)
          addedForThisCurve++;
      }

      if (addedForThisCurve <= 1)
        continue;

      if (_deleteInput)
        doc.Objects.Delete(curveObject, true);

      splitCurveCount++;
      addedPieceCount += addedForThisCurve;
      totalSplitPoints += splitParameters.Count;
    }

    if (splitCurveCount == 0)
    {
      RhinoApp.WriteLine("vSplitAtCorners: no corners met the current MinAngle threshold.");
      return Result.Nothing;
    }

    doc.Views.Redraw();
    RhinoApp.WriteLine($"vSplitAtCorners: split {splitCurveCount} curve(s) at {totalSplitPoints} corner(s), created {addedPieceCount} piece(s).");
    return Result.Success;
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var minAngle = _minAngleDeg;
        var deleteInput = _deleteInput;

        if (vToolsOptionStore.TryGetDouble(section, MinAngleKey, out var persistedAngle))
          minAngle = ClampAngle(persistedAngle);
        if (vToolsOptionStore.TryGetBool(section, DeleteInputKey, out var persistedDelete))
          deleteInput = persistedDelete;

        return (minAngle, deleteInput);
      });

    _minAngleDeg = ClampAngle(values.minAngle);
    _deleteInput = values.deleteInput;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[MinAngleKey] = _minAngleDeg;
        section[DeleteInputKey] = _deleteInput;
      });
  }

  private static bool TryGetCurvesAndOptions(RhinoDoc doc, out List<RhinoObject> curveObjects)
  {
    curveObjects = new List<RhinoObject>();

    var go = new GetObject();
    go.SetCommandPrompt("Select curves. Press Enter to split");
    go.GeometryFilter = ObjectType.Curve;
    go.AcceptNothing(true);
    go.SubObjectSelect = false;
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var minAngleOption = new OptionDouble(_minAngleDeg, MinAllowedAngleDeg, MaxAllowedAngleDeg);
    var deleteOption = new OptionToggle(_deleteInput, "No", "Yes");
    var preselectedWaitingForEnter = false;

    while (true)
    {
      go.ClearCommandOptions();
      go.AddOptionDouble("MinAngle", ref minAngleOption);
      go.AddOptionToggle("DeleteInput", ref deleteOption);

      var result = go.GetMultiple(1, 0);
      if (go.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _minAngleDeg = ClampAngle(minAngleOption.CurrentValue);
        _deleteInput = deleteOption.CurrentValue;
        continue;
      }

      if (result == GetResult.Object)
      {
        _minAngleDeg = ClampAngle(minAngleOption.CurrentValue);
        _deleteInput = deleteOption.CurrentValue;

        curveObjects = SelectedCurveObjects(doc);

        if (go.ObjectsWerePreselected && !preselectedWaitingForEnter)
        {
          preselectedWaitingForEnter = true;
          go.EnablePreSelect(false, true);
          continue;
        }

        if (curveObjects.Count > 0)
          return true;

        continue;
      }

      if (result == GetResult.Nothing)
      {
        _minAngleDeg = ClampAngle(minAngleOption.CurrentValue);
        _deleteInput = deleteOption.CurrentValue;
        curveObjects = SelectedCurveObjects(doc);
        return curveObjects.Count > 0;
      }

      return false;
    }
  }

  private static List<RhinoObject> SelectedCurveObjects(RhinoDoc doc)
  {
    var list = new List<RhinoObject>();
    var seen = new HashSet<Guid>();

    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj == null || obj.Geometry is not Curve)
        continue;
      if (!seen.Add(obj.Id))
        continue;
      list.Add(obj);
    }

    return list;
  }

  private static List<double> CollectSplitParameters(Curve curve, double minAngleDeg)
  {
    var candidates = CornerCandidateParameters(curve);
    var tolerance = ParameterTolerance(curve);
    var splitParameters = new List<double>();

    foreach (var parameter in candidates)
    {
      var angle = CornerAngleDegrees(curve, parameter);
      if (angle < minAngleDeg)
        continue;

      if (splitParameters.Count == 0 || Math.Abs(parameter - splitParameters[^1]) > tolerance)
        splitParameters.Add(parameter);
    }

    return splitParameters;
  }

  private static List<double> CornerCandidateParameters(Curve curve)
  {
    var parameters = CollectDiscontinuityParameters(curve);

    if (curve.IsClosed)
    {
      var seam = curve.Domain.T0;
      var tolerance = ParameterTolerance(curve);
      var seamExists = false;
      foreach (var value in parameters)
      {
        if (Math.Abs(value - seam) <= tolerance)
        {
          seamExists = true;
          break;
        }
      }

      if (!seamExists)
        parameters.Add(seam);
    }

    parameters.Sort();
    return parameters;
  }

  private static List<double> CollectDiscontinuityParameters(Curve curve)
  {
    var result = new List<double>();
    var domain = curve.Domain;
    var t0 = domain.T0;
    var t1 = domain.T1;
    if (t1 <= t0)
      return result;

    var tolerance = ParameterTolerance(curve);
    var searchStart = t0;

    while (true)
    {
      if (!curve.GetNextDiscontinuity(Continuity.G1_continuous, searchStart, t1, out var parameter))
        break;

      if (parameter <= t0 + tolerance || parameter >= t1 - tolerance)
      {
        searchStart = Math.Min(t1, parameter + tolerance);
        if (searchStart >= t1)
          break;
        continue;
      }

      if (result.Count == 0 || Math.Abs(parameter - result[^1]) > tolerance)
        result.Add(parameter);

      searchStart = Math.Min(t1, parameter + tolerance);
      if (searchStart >= t1)
        break;
    }

    return result;
  }

  private static double CornerAngleDegrees(Curve curve, double parameter)
  {
    var domain = curve.Domain;
    var span = domain.T1 - domain.T0;
    if (span <= RhinoMath.ZeroTolerance)
      return 0.0;

    var delta = Math.Max(span * 1.0e-6, RhinoMath.SqrtEpsilon);
    var cornerPoint = curve.PointAt(parameter);

    Point3d leftPoint;
    Point3d rightPoint;

    if (curve.IsClosed && (Math.Abs(parameter - domain.T0) <= delta || Math.Abs(parameter - domain.T1) <= delta))
    {
      var leftParameter = domain.T1 - delta;
      var rightParameter = domain.T0 + delta;
      leftPoint = curve.PointAt(leftParameter);
      rightPoint = curve.PointAt(rightParameter);
    }
    else
    {
      var leftParameter = Math.Max(domain.T0, parameter - delta);
      var rightParameter = Math.Min(domain.T1, parameter + delta);
      if (rightParameter <= leftParameter)
        return 0.0;

      leftPoint = curve.PointAt(leftParameter);
      rightPoint = curve.PointAt(rightParameter);
    }

    var incoming = cornerPoint - leftPoint;
    var outgoing = rightPoint - cornerPoint;

    if (!incoming.Unitize() || !outgoing.Unitize())
      return 0.0;

    var angleRadians = Vector3d.VectorAngle(incoming, outgoing);
    return RhinoMath.IsValidDouble(angleRadians) ? RhinoMath.ToDegrees(angleRadians) : 0.0;
  }

  private static double ParameterTolerance(Curve curve)
  {
    var domain = curve.Domain;
    return Math.Max((domain.T1 - domain.T0) * 1.0e-9, RhinoMath.ZeroTolerance);
  }

  private static double ClampAngle(double value)
  {
    return Math.Max(MinAllowedAngleDeg, Math.Min(MaxAllowedAngleDeg, value));
  }
}
