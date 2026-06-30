using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native middle-curve command ported from MiddleCurve.py.
/// Creates an interpolated curve equidistant between two selected input curves.
/// </summary>
public sealed class vMiddleCurve : Command
{
  private const string OptionsSectionName = "vMiddleCurve";
  private const string LinesKey = "lines";
  private const string LineModeKey = "lineMode";
  private const string LineIntervalKey = "lineInterval";
  private const string SeamAllowanceKey = "seamAllowance";
  private const string MinimumLengthKey = "minimumLength";
  private const string LegacyTangentLinesKey = "tangentLines";
  private const string LegacyTangentLineIntervalKey = "tangentLineInterval";
  private const int MinSampleCount = 12;
  private const int MaxSampleCount = 2000;
  private const double DefaultLineInterval = 0.5;
  private const double DefaultSeamAllowance = 0.5;
  private const double DefaultMinimumLength = 0.25;

  private enum ConnectorLineMode
  {
    Normal,
    EqualDistance
  }

  private static bool _addLines;
  private static ConnectorLineMode _lineMode = ConnectorLineMode.EqualDistance;
  private static double _lineInterval = DefaultLineInterval;
  private static double _seamAllowance = DefaultSeamAllowance;
  private static double _minimumLength = DefaultMinimumLength;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vMiddleCurve";

  /// <summary>
  /// Prompts for two curves and creates an interpolated middle curve between them.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions(doc);

    if (!PromptTwoCurves(doc, out var curveA, out var curveB))
      return Result.Cancel;
    SaveOptions();

    AlignCurvePair(curveA!, curveB!);

    var build = BuildMiddleCurveAuto(doc, curveA!, curveB!, _seamAllowance);
    if (build == null)
    {
      RhinoApp.WriteLine("vMiddleCurve: could not create middle curve from selected inputs.");
      return Result.Failure;
    }

    if (!PreviewMiddleCurve(doc, curveA!, curveB!, build, out var middleCurve, out var connectorLines))
      return Result.Cancel;

    var newId = doc.Objects.AddCurve(middleCurve);
    if (newId == Guid.Empty)
    {
      RhinoApp.WriteLine("vMiddleCurve: failed to add curve to document.");
      return Result.Failure;
    }

    foreach (var connectorLine in connectorLines)
      _ = doc.Objects.AddCurve(connectorLine);

    doc.Views.Redraw();
    return Result.Success;
  }

  private static void LoadOptions(RhinoDoc doc)
  {
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    var values = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var addLines = _addLines;
        var lineMode = _lineMode;
        var interval = _lineInterval;
        var seamAllowance = _seamAllowance;
        var minimumLength = _minimumLength;

        if (ToolsOptionStore.TryGetBool(section, LinesKey, out var persistedLines) ||
            ToolsOptionStore.TryGetBool(section, LegacyTangentLinesKey, out persistedLines))
          addLines = persistedLines;
        if (ToolsOptionStore.TryGetString(section, LineModeKey, out var persistedMode) &&
            Enum.TryParse(persistedMode, true, out ConnectorLineMode parsedMode))
          lineMode = parsedMode;
        if (ToolsOptionStore.TryGetDouble(section, LineIntervalKey, out var persistedInterval) ||
            ToolsOptionStore.TryGetDouble(section, LegacyTangentLineIntervalKey, out persistedInterval))
        {
          if (persistedInterval > tolerance)
            interval = persistedInterval;
        }
        if (ToolsOptionStore.TryGetDouble(section, SeamAllowanceKey, out var persistedSeamAllowance) &&
            persistedSeamAllowance >= 0.0)
          seamAllowance = persistedSeamAllowance;
        if (ToolsOptionStore.TryGetDouble(section, MinimumLengthKey, out var persistedMinimumLength) &&
            persistedMinimumLength >= 0.0)
          minimumLength = persistedMinimumLength;

        return (addLines, lineMode, interval, seamAllowance, minimumLength);
      });

    _addLines = values.addLines;
    _lineMode = values.lineMode;
    _lineInterval = Math.Max(values.interval, tolerance);
    _seamAllowance = Math.Max(values.seamAllowance, 0.0);
    _minimumLength = Math.Max(values.minimumLength, 0.0);
    SaveOptions();
  }

  private static void SaveOptions() =>
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[LinesKey] = _addLines;
        section[LineModeKey] = _lineMode.ToString();
        section[LineIntervalKey] = _lineInterval;
        section[SeamAllowanceKey] = _seamAllowance;
        section[MinimumLengthKey] = _minimumLength;
      });

  private static bool PromptTwoCurves(RhinoDoc doc, out Curve? curveA, out Curve? curveB)
  {
    curveA = null;
    curveB = null;
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

    var go = new GetObject();
    go.SetCommandPrompt("Select 2 curves");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.AcceptNothing(true);
    go.AcceptNumber(true, true);
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var linesToggle = new OptionToggle(_addLines, "No", "Yes");
    var modeToggle = new OptionToggle(_lineMode == ConnectorLineMode.EqualDistance, "Normal", "EqualDistance");
    var intervalOption = new OptionDouble(_lineInterval, tolerance, double.MaxValue);
    var seamAllowanceOption = new OptionDouble(_seamAllowance, 0.0, double.MaxValue);
    var minimumLengthOption = new OptionDouble(_minimumLength, 0.0, double.MaxValue);
    go.AddOptionToggle("Lines", ref linesToggle);
    go.AddOptionToggle("LineMode", ref modeToggle);
    go.AddOptionDouble("LineInterval", ref intervalOption);
    go.AddOptionDouble("SeamAllowance", ref seamAllowanceOption);
    go.AddOptionDouble("MinimumLength", ref minimumLengthOption);

    var preselectedWaitingForEnter = false;

    while (true)
    {
      var result = go.GetMultiple(2, 2);
      UpdateOptions(linesToggle, modeToggle, intervalOption, seamAllowanceOption, minimumLengthOption, tolerance);

      if (result == GetResult.Nothing)
      {
        // User pressed Enter with objects already selected — accept preselection.
        var selected = SelectedCurveCopiesFromDoc(doc);
        if (selected != null)
        {
          curveA = selected[0];
          curveB = selected[1];
          return true;
        }
        return false;
      }

      if (result == GetResult.Cancel)
      {
        SaveOptions();
        return false;
      }

      if (result == GetResult.Number)
      {
        _lineInterval = Math.Max(go.Number(), tolerance);
        SaveOptions();
        continue;
      }

      if (result == GetResult.Option)
      {
        SaveOptions();
        continue;
      }

      if (result != GetResult.Object)
        return false;

      if (go.CommandResult() != Result.Success)
        return false;

      // First hit with pre-selected objects: disable preselect and loop back so user
      // confirms with Enter (matching Rhino's standard preselect-then-confirm pattern).
      if (go.ObjectsWerePreselected && !preselectedWaitingForEnter)
      {
        preselectedWaitingForEnter = true;
        go.EnablePreSelect(false, true);
        continue;
      }

      // Prefer doc-selected copies (covers post-Enter after preselect path).
      var fromDoc = SelectedCurveCopiesFromDoc(doc);
      if (fromDoc != null)
      {
        curveA = fromDoc[0];
        curveB = fromDoc[1];
        return true;
      }

      if (go.ObjectCount != 2)
        return false;

      var ca = go.Object(0).Curve();
      var cb = go.Object(1).Curve();
      if (ca == null || cb == null)
        return false;

      curveA = ca.DuplicateCurve();
      curveB = cb.DuplicateCurve();
      return true;
    }
  }

  private static void UpdateOptions(
    OptionToggle linesToggle,
    OptionToggle modeToggle,
    OptionDouble intervalOption,
    OptionDouble seamAllowanceOption,
    OptionDouble minimumLengthOption,
    double tolerance)
  {
    _addLines = linesToggle.CurrentValue;
    _lineMode = modeToggle.CurrentValue ? ConnectorLineMode.EqualDistance : ConnectorLineMode.Normal;
    _lineInterval = Math.Max(intervalOption.CurrentValue, tolerance);
    _seamAllowance = Math.Max(seamAllowanceOption.CurrentValue, 0.0);
    _minimumLength = Math.Max(minimumLengthOption.CurrentValue, 0.0);
  }

  private static bool PreviewMiddleCurve(
    RhinoDoc doc,
    Curve sourceA,
    Curve sourceB,
    MiddleCurveBuild initialBuild,
    out Curve middleCurve,
    out List<Curve> connectorLines)
  {
    middleCurve = initialBuild.Curve;
    connectorLines = new List<Curve>();
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

    List<Curve> CurrentConnectorLines(MiddleCurveBuild build) =>
      _addLines
        ? BuildConnectorLines(doc, build.Curve, sourceA, sourceB, _lineInterval, _lineMode, build, _minimumLength)
        : new List<Curve>();

    while (true)
    {
      var previewBuild = BuildMiddleCurveAuto(doc, sourceA, sourceB, _seamAllowance);
      var previewLines = previewBuild != null ? CurrentConnectorLines(previewBuild) : new List<Curve>();
      var gp = new GetPoint();
      gp.SetCommandPrompt("Preview middle curve. Press Enter to create");
      gp.AcceptNothing(true);
      gp.AcceptNumber(true, true);

      var linesToggle = new OptionToggle(_addLines, "No", "Yes");
      var modeToggle = new OptionToggle(_lineMode == ConnectorLineMode.EqualDistance, "Normal", "EqualDistance");
      var intervalOption = new OptionDouble(_lineInterval, tolerance, double.MaxValue);
      var seamAllowanceOption = new OptionDouble(_seamAllowance, 0.0, double.MaxValue);
      var minimumLengthOption = new OptionDouble(_minimumLength, 0.0, double.MaxValue);

      gp.AddOptionToggle("Lines", ref linesToggle);
      gp.AddOptionToggle("LineMode", ref modeToggle);
      gp.AddOptionDouble("LineInterval", ref intervalOption);
      gp.AddOptionDouble("SeamAllowance", ref seamAllowanceOption);
      gp.AddOptionDouble("MinimumLength", ref minimumLengthOption);

      EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
      {
        if (previewBuild == null)
          return;

        e.Display.DrawCurve(previewBuild.Curve, Color.DeepSkyBlue, 2);
        foreach (var connectorLine in previewLines)
          e.Display.DrawCurve(connectorLine, Color.Gold, 1);
      };

      gp.DynamicDraw += drawPreview;
      var result = gp.Get();
      gp.DynamicDraw -= drawPreview;

      UpdateOptions(linesToggle, modeToggle, intervalOption, seamAllowanceOption, minimumLengthOption, tolerance);

      if (result == GetResult.Cancel || gp.CommandResult() != Result.Success)
      {
        SaveOptions();
        return false;
      }

      if (result == GetResult.Number)
      {
        _lineInterval = Math.Max(gp.Number(), tolerance);
        SaveOptions();
        continue;
      }

      if (result == GetResult.Option)
      {
        SaveOptions();
        continue;
      }

      if (result == GetResult.Nothing || result == GetResult.Point)
      {
        var finalBuild = BuildMiddleCurveAuto(doc, sourceA, sourceB, _seamAllowance);
        if (finalBuild == null)
        {
          RhinoApp.WriteLine("vMiddleCurve: could not create middle curve from selected inputs.");
          SaveOptions();
          return false;
        }

        middleCurve = finalBuild.Curve;
        connectorLines = CurrentConnectorLines(finalBuild);
        SaveOptions();
        return true;
      }

      SaveOptions();
      return false;
    }
  }

  private static Curve[]? SelectedCurveCopiesFromDoc(RhinoDoc doc)
  {
    var selected = new List<Curve>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      var curve = obj?.Geometry as Curve;
      if (curve == null)
        continue;
      selected.Add(curve.DuplicateCurve());
    }
    return selected.Count == 2 ? selected.ToArray() : null;
  }

  private static void AlignCurvePair(Curve curveA, Curve curveB)
  {
    // Align seam of closed curve B to closest point to curve A's start.
    if (curveA.IsClosed && curveB.IsClosed)
    {
      if (curveB.ClosestPoint(curveA.PointAtStart, out var seamT))
        _ = curveB.ChangeClosedCurveSeam(seamT);
    }

    // Reverse curve B if its endpoints are anti-parallel to curve A.
    var sameScore = curveA.PointAtStart.DistanceTo(curveB.PointAtStart)
                  + curveA.PointAtEnd.DistanceTo(curveB.PointAtEnd);
    var reversedScore = curveA.PointAtStart.DistanceTo(curveB.PointAtEnd)
                      + curveA.PointAtEnd.DistanceTo(curveB.PointAtStart);
    if (reversedScore < sameScore)
      _ = curveB.Reverse();
  }

  private static List<Point3d> BuildMidPoints(
    RhinoDoc doc,
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    Plane workPlane,
    int sampleCount,
    double startDistance,
    double endDistance,
    IReadOnlyList<(double Distance, Point3d Point)> lockedSamples)
  {
    var points = new List<Point3d>(sampleCount + 1);
    var tolerance = doc.ModelAbsoluteTolerance;
    var distances = BuildSampleDistances(baseLength, sampleCount, tolerance, startDistance, endDistance, lockedSamples);

    foreach (var distance in distances)
    {
      var isLocked = TryGetLockedSamplePoint(distance, lockedSamples, tolerance, out var lockedPoint);
      var hasPoint = isLocked ||
        TrySolveEqualOffsetPoint(baseCurve, baseLength, targetCurve, targetLength, workPlane, distance, tolerance, out lockedPoint);
      if (!hasPoint)
        continue;

      var midpoint = lockedPoint;

      if (points.Count > 0 && midpoint.DistanceTo(points[^1]) <= tolerance)
        continue;

      points.Add(midpoint);
    }

    return points;
  }

  private static bool TrySolveEqualOffsetPoint(
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    Plane workPlane,
    double distance,
    double tolerance,
    out Point3d point)
  {
    point = Point3d.Unset;

    if (!TryPointAndTangentAtDistance(baseCurve, baseLength, distance, out var basePoint, out var tangent))
      return false;

    var planeNormal = workPlane.ZAxis;
    if (!planeNormal.Unitize())
      planeNormal = Vector3d.ZAxis;

    tangent -= planeNormal * (tangent * planeNormal);
    if (tangent.IsTiny())
      tangent = baseCurve.TangentAtStart;
    if (tangent.IsTiny())
      return false;
    tangent.Unitize();

    var normal = Vector3d.CrossProduct(planeNormal, tangent);
    if (normal.IsTiny())
      return false;
    normal.Unitize();

    if (!targetCurve.ClosestPoint(basePoint, out var targetT))
      return false;

    var targetPoint = targetCurve.PointAt(targetT);
    var towardTarget = targetPoint - basePoint;
    if (towardTarget * normal < 0.0)
      normal = -normal;

    return TrySolveEqualOffsetPointAlongNormal(basePoint, normal, targetCurve, baseLength, targetLength, tolerance, out point) ||
           TrySolveEqualOffsetPointAlongNormal(basePoint, -normal, targetCurve, baseLength, targetLength, tolerance, out point);
  }

  private static bool TryPointAndTangentAtDistance(
    Curve curve,
    double curveLength,
    double distance,
    out Point3d point,
    out Vector3d tangent)
  {
    if (distance <= 0.0 || curveLength <= RhinoMath.ZeroTolerance)
    {
      point = curve.PointAtStart;
      tangent = curve.TangentAtStart;
      return point.IsValid && !tangent.IsTiny();
    }

    if (distance >= curveLength)
    {
      point = curve.PointAtEnd;
      tangent = curve.TangentAtEnd;
      return point.IsValid && !tangent.IsTiny();
    }

    var ok = curve.LengthParameter(distance, out var t);
    if (!ok)
      t = curve.Domain.ParameterAt(distance / curveLength);

    point = curve.PointAt(t);
    tangent = curve.TangentAt(t);
    return point.IsValid && !tangent.IsTiny();
  }

  private static bool TryPointAtDistance(
    Curve curve,
    double curveLength,
    double distance,
    out Point3d point)
  {
    if (curveLength <= RhinoMath.ZeroTolerance || distance <= 0.0)
    {
      point = curve.PointAtStart;
      return point.IsValid;
    }

    if (distance >= curveLength)
    {
      point = curve.PointAtEnd;
      return point.IsValid;
    }

    var ok = curve.LengthParameter(distance, out var t);
    if (!ok)
      t = curve.Domain.ParameterAt(distance / curveLength);

    point = curve.PointAt(t);
    return point.IsValid;
  }

  private static bool TrySolveEqualOffsetPointAlongNormal(
    Point3d basePoint,
    Vector3d normal,
    Curve targetCurve,
    double baseLength,
    double targetLength,
    double tolerance,
    out Point3d point)
  {
    point = Point3d.Unset;

    if (!normal.Unitize())
      return false;

    if (!targetCurve.ClosestPoint(basePoint, out var t0))
      return false;

    var initialDistance = basePoint.DistanceTo(targetCurve.PointAt(t0));
    if (initialDistance <= tolerance)
    {
      point = basePoint;
      return true;
    }

    double DistanceDifference(double radius)
    {
      var candidate = basePoint + normal * radius;
      if (!targetCurve.ClosestPoint(candidate, out var t))
        return double.PositiveInfinity;

      return candidate.DistanceTo(targetCurve.PointAt(t)) - radius;
    }

    var lo = 0.0;
    var hi = Math.Max(initialDistance, tolerance * 10.0);
    var fHi = DistanceDifference(hi);
    var maxRadius = Math.Max(Math.Max(baseLength, targetLength), initialDistance) * 20.0;

    while (fHi > 0.0 && hi < maxRadius)
    {
      hi *= 2.0;
      fHi = DistanceDifference(hi);
    }

    if (double.IsInfinity(fHi) || fHi > 0.0)
      return false;

    for (var i = 0; i < 64; i++)
    {
      var mid = 0.5 * (lo + hi);
      var fMid = DistanceDifference(mid);
      if (Math.Abs(fMid) <= Math.Max(tolerance * 0.1, 1.0e-6))
      {
        point = basePoint + normal * mid;
        return true;
      }

      if (fMid > 0.0)
        lo = mid;
      else
        hi = mid;
    }

    point = basePoint + normal * (0.5 * (lo + hi));
    return point.IsValid;
  }

  private static List<double> BuildSampleDistances(
    double workingLength,
    int sampleCount,
    double tolerance,
    double startDistance,
    double endDistance,
    IReadOnlyList<(double Distance, Point3d Point)> lockedSamples)
  {
    startDistance = Math.Max(0.0, Math.Min(workingLength, startDistance));
    endDistance = Math.Max(startDistance, Math.Min(workingLength, endDistance));

    var distances = new List<double>(sampleCount + 3);
    distances.Add(startDistance);
    distances.Add(endDistance);

    for (var i = 0; i <= sampleCount; i++)
    {
      var distance = workingLength * i / sampleCount;
      if (distance >= startDistance - tolerance && distance <= endDistance + tolerance)
        distances.Add(distance);
    }

    foreach (var sample in lockedSamples)
    {
      if (sample.Distance >= startDistance - tolerance && sample.Distance <= endDistance + tolerance)
        distances.Add(sample.Distance);
    }

    distances.Sort();

    var unique = new List<double>(distances.Count);
    foreach (var distance in distances)
    {
      var clamped = Math.Max(startDistance, Math.Min(endDistance, distance));
      if (unique.Count > 0 && Math.Abs(clamped - unique[^1]) <= tolerance)
        continue;
      unique.Add(clamped);
    }

    return unique;
  }

  private static List<(double Distance, Point3d Point)> FindIntersectionSamples(
    Curve baseCurve,
    Curve targetCurve,
    double workingLength,
    double tolerance)
  {
    var samples = new List<(double Distance, Point3d Point)>();
    var intersections = Intersection.CurveCurve(baseCurve, targetCurve, tolerance, tolerance);
    if (intersections == null)
      return samples;

    foreach (var intersection in intersections)
    {
      if (!intersection.IsPoint)
        continue;

      var t = intersection.ParameterA;
      if (t < baseCurve.Domain.T0 || t > baseCurve.Domain.T1)
        continue;

      var subDomain = new Interval(baseCurve.Domain.T0, t);
      var distance = baseCurve.GetLength(subDomain);
      if (distance >= -tolerance && distance <= workingLength + tolerance)
        AddLockedSample(samples, distance, intersection.PointA, tolerance);
    }

    AddEndpointExtensionSample(baseCurve, targetCurve, true, 0.0, tolerance, samples);
    AddEndpointExtensionSample(baseCurve, targetCurve, false, workingLength, tolerance, samples);

    return samples;
  }

  private static List<(double Distance, Point3d Point)> FindSeamAllowanceSamples(
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    Plane workPlane,
    double seamAllowance,
    double tolerance)
  {
    var samples = new List<(double Distance, Point3d Point)>();
    if (seamAllowance <= tolerance || baseLength <= tolerance || targetLength <= tolerance)
      return samples;

    var offsetBase = OffsetCurveTowardCurve(baseCurve, targetCurve, seamAllowance, workPlane, tolerance);
    var offsetTarget = OffsetCurveTowardCurve(targetCurve, baseCurve, seamAllowance, workPlane, tolerance);
    if (offsetBase == null || offsetTarget == null)
      return samples;

    AddOffsetIntersectionSamples(offsetBase, offsetTarget, baseCurve, baseLength, tolerance, samples);
    AddOffsetEndpointExtensionSamples(offsetBase, offsetTarget, baseCurve, baseLength, tolerance, samples);
    return samples;
  }

  private static Curve? OffsetCurveTowardCurve(
    Curve curve,
    Curve targetCurve,
    double distance,
    Plane plane,
    double tolerance)
  {
    Curve? best = null;
    var bestScore = double.MaxValue;

    foreach (var signedDistance in new[] { distance, -distance })
    {
      var offsets = curve.Offset(plane, signedDistance, tolerance, CurveOffsetCornerStyle.Sharp);
      if (offsets == null)
        continue;

      foreach (var offset in offsets)
      {
        if (offset == null)
          continue;

        var score = AverageDistanceToCurve(offset, targetCurve, tolerance);
        if (score >= bestScore)
          continue;

        bestScore = score;
        best = offset;
      }
    }

    return best;
  }

  private static double AverageDistanceToCurve(Curve sampleCurve, Curve targetCurve, double tolerance)
  {
    var sampleLength = sampleCurve.GetLength();
    if (sampleLength <= tolerance)
      return double.MaxValue;

    var total = 0.0;
    var count = 0;
    for (var i = 0; i <= 4; i++)
    {
      var distance = sampleLength * i / 4.0;
      if (!TryPointAtDistance(sampleCurve, sampleLength, distance, out var point))
        continue;
      if (!targetCurve.ClosestPoint(point, out var targetT))
        continue;

      total += point.DistanceTo(targetCurve.PointAt(targetT));
      count++;
    }

    return count == 0 ? double.MaxValue : total / count;
  }

  private static void AddOffsetIntersectionSamples(
    Curve offsetBase,
    Curve offsetTarget,
    Curve baseCurve,
    double baseLength,
    double tolerance,
    List<(double Distance, Point3d Point)> samples)
  {
    var intersections = Intersection.CurveCurve(offsetBase, offsetTarget, tolerance, tolerance);
    if (intersections == null)
      return;

    foreach (var intersection in intersections)
    {
      if (!intersection.IsPoint)
        continue;

      AddSeamSample(intersection.PointA, baseCurve, baseLength, tolerance, samples);
    }
  }

  private static void AddOffsetEndpointExtensionSamples(
    Curve offsetBase,
    Curve offsetTarget,
    Curve baseCurve,
    double baseLength,
    double tolerance,
    List<(double Distance, Point3d Point)> samples)
  {
    AddOffsetEndpointExtensionSample(offsetBase, offsetTarget, true, baseCurve, baseLength, tolerance, samples);
    AddOffsetEndpointExtensionSample(offsetBase, offsetTarget, false, baseCurve, baseLength, tolerance, samples);
  }

  private static void AddOffsetEndpointExtensionSample(
    Curve offsetBase,
    Curve offsetTarget,
    bool atStart,
    Curve baseCurve,
    double baseLength,
    double tolerance,
    List<(double Distance, Point3d Point)> samples)
  {
    if (offsetBase.IsClosed || offsetTarget.IsClosed)
      return;

    var basePoint = atStart ? offsetBase.PointAtStart : offsetBase.PointAtEnd;
    var targetPoint = atStart ? offsetTarget.PointAtStart : offsetTarget.PointAtEnd;
    var baseDir = atStart ? -offsetBase.TangentAtStart : offsetBase.TangentAtEnd;
    var targetDir = atStart ? -offsetTarget.TangentAtStart : offsetTarget.TangentAtEnd;

    if (baseDir.IsTiny() || targetDir.IsTiny())
      return;

    baseDir.Unitize();
    targetDir.Unitize();

    var span = Math.Max(Math.Max(offsetBase.GetLength(), offsetTarget.GetLength()), basePoint.DistanceTo(targetPoint));
    var extension = Math.Max(Math.Max(span * 2.0, tolerance * 100.0), 1.0);
    var baseLine = new Line(basePoint, basePoint + baseDir * extension);
    var targetLine = new Line(targetPoint, targetPoint + targetDir * extension);

    if (!Intersection.LineLine(baseLine, targetLine, out var baseParameter, out var targetParameter, tolerance, false))
      return;

    var parameterTolerance = Math.Max(tolerance / extension, 1.0e-9);
    if (baseParameter < -parameterTolerance || targetParameter < -parameterTolerance ||
        baseParameter > 1.0 + parameterTolerance || targetParameter > 1.0 + parameterTolerance)
      return;

    var baseHit = baseLine.PointAt(baseParameter);
    var targetHit = targetLine.PointAt(targetParameter);
    if (baseHit.DistanceTo(targetHit) > Math.Max(tolerance * 10.0, 1.0e-6))
      return;

    var point = new Point3d(
      0.5 * (baseHit.X + targetHit.X),
      0.5 * (baseHit.Y + targetHit.Y),
      0.5 * (baseHit.Z + targetHit.Z));
    AddSeamSample(point, baseCurve, baseLength, tolerance, samples);
  }

  private static void AddSeamSample(
    Point3d point,
    Curve baseCurve,
    double baseLength,
    double tolerance,
    List<(double Distance, Point3d Point)> samples)
  {
    if (!point.IsValid || !baseCurve.ClosestPoint(point, out var t))
      return;

    var distance = DistanceAtParameter(baseCurve, baseLength, t);
    if (distance < -tolerance || distance > baseLength + tolerance)
      return;

    AddLockedSample(samples, Math.Max(0.0, Math.Min(baseLength, distance)), point, tolerance);
  }

  private static void ResolveSeamRange(
    IReadOnlyList<(double Distance, Point3d Point)> seamSamples,
    double baseLength,
    double tolerance,
    bool startConnectorLinesAtEndWhenFullLength,
    out double startDistance,
    out double endDistance,
    out bool startConnectorLinesAtEnd)
  {
    startDistance = 0.0;
    endDistance = baseLength;
    startConnectorLinesAtEnd = startConnectorLinesAtEndWhenFullLength;
    if (seamSamples.Count == 0)
      return;

    var minDistance = baseLength;
    var maxDistance = 0.0;
    foreach (var sample in seamSamples)
    {
      var distance = Math.Max(0.0, Math.Min(baseLength, sample.Distance));
      minDistance = Math.Min(minDistance, distance);
      maxDistance = Math.Max(maxDistance, distance);
    }

    if (startConnectorLinesAtEnd)
      endDistance = maxDistance;
    else
      startDistance = minDistance;

    if (endDistance - startDistance <= tolerance)
    {
      startDistance = 0.0;
      endDistance = baseLength;
      startConnectorLinesAtEnd = startConnectorLinesAtEndWhenFullLength;
    }
  }

  private static void AddEndpointExtensionSample(
    Curve baseCurve,
    Curve targetCurve,
    bool atStart,
    double distance,
    double tolerance,
    List<(double Distance, Point3d Point)> samples)
  {
    if (baseCurve.IsClosed || targetCurve.IsClosed)
      return;

    var basePoint = atStart ? baseCurve.PointAtStart : baseCurve.PointAtEnd;
    var targetPoint = atStart ? targetCurve.PointAtStart : targetCurve.PointAtEnd;
    var baseDir = atStart ? -baseCurve.TangentAtStart : baseCurve.TangentAtEnd;
    var targetDir = atStart ? -targetCurve.TangentAtStart : targetCurve.TangentAtEnd;

    if (baseDir.IsTiny() || targetDir.IsTiny())
      return;

    baseDir.Unitize();
    targetDir.Unitize();

    var span = Math.Max(Math.Max(baseCurve.GetLength(), targetCurve.GetLength()), basePoint.DistanceTo(targetPoint));
    var extension = Math.Max(Math.Max(span * 2.0, tolerance * 100.0), 1.0);
    var baseLine = new Line(basePoint, basePoint + baseDir * extension);
    var targetLine = new Line(targetPoint, targetPoint + targetDir * extension);

    if (!Intersection.LineLine(baseLine, targetLine, out var baseParameter, out var targetParameter, tolerance, false))
      return;

    var parameterTolerance = Math.Max(tolerance / extension, 1.0e-9);
    if (baseParameter < -parameterTolerance || targetParameter < -parameterTolerance ||
        baseParameter > 1.0 + parameterTolerance || targetParameter > 1.0 + parameterTolerance)
      return;

    var baseHit = baseLine.PointAt(baseParameter);
    var targetHit = targetLine.PointAt(targetParameter);
    if (baseHit.DistanceTo(targetHit) > Math.Max(tolerance * 10.0, 1.0e-6))
      return;

    var point = new Point3d(
      0.5 * (baseHit.X + targetHit.X),
      0.5 * (baseHit.Y + targetHit.Y),
      0.5 * (baseHit.Z + targetHit.Z));
    AddLockedSample(samples, distance, point, tolerance);
  }

  private static void AddLockedSample(
    List<(double Distance, Point3d Point)> samples,
    double distance,
    Point3d point,
    double tolerance)
  {
    foreach (var sample in samples)
    {
      if (Math.Abs(distance - sample.Distance) <= tolerance &&
          point.DistanceTo(sample.Point) <= tolerance)
        return;
    }

    samples.Add((distance, point));
  }

  private static bool TryGetLockedSamplePoint(
    double distance,
    IReadOnlyList<(double Distance, Point3d Point)> samples,
    double tolerance,
    out Point3d point)
  {
    foreach (var sample in samples)
    {
      if (Math.Abs(distance - sample.Distance) > tolerance)
        continue;

      point = sample.Point;
      return true;
    }

    point = Point3d.Unset;
    return false;
  }

  private static Curve? CreateMiddleCurve(List<Point3d> midPoints)
  {
    if (midPoints.Count < 2)
      return null;

    if (midPoints.Count == 2)
      return new LineCurve(midPoints[0], midPoints[1]);

    var degree = midPoints.Count >= 4 ? 3 : 2;
    return Curve.CreateInterpolatedCurve(midPoints, degree);
  }

  private static int EstimateSampleCount(Curve curveA, Curve curveB, double workingLength)
  {
    var length = Math.Max(workingLength, 1.0);
    var spanHint = Math.Max(curveA.SpanCount, curveB.SpanCount) * 8;
    var lengthHint = (int)(length / 2.0);
    return Math.Max(MinSampleCount, Math.Min(MaxSampleCount, Math.Max(96, Math.Max(spanHint, lengthHint))));
  }

  private static Plane ResolveWorkPlane(RhinoDoc doc, Curve curveA, Curve curveB, double tolerance)
  {
    if (curveA.TryGetPlane(out var planeA, tolerance))
      return planeA;
    if (curveB.TryGetPlane(out var planeB, tolerance))
      return planeB;

    return doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
  }

  private static List<Curve> BuildConnectorLines(
    RhinoDoc doc,
    Curve middleCurve,
    Curve sourceA,
    Curve sourceB,
    double interval,
    ConnectorLineMode mode,
    MiddleCurveBuild build,
    double minimumLength)
  {
    var lines = mode == ConnectorLineMode.EqualDistance
      ? build.HasSeamTrim
        ? BuildSeamEqualDistanceLines(doc, middleCurve, sourceA, sourceB, interval, build)
        : BuildEqualDistanceLines(
          doc,
          middleCurve,
          sourceA,
          sourceB,
          interval,
          build.SourceAIsBase,
          build.MiddleStartBaseDistance,
          build.MiddleEndBaseDistance)
      : BuildNormalLines(doc, middleCurve, sourceA, sourceB, interval);
    return FilterConnectorLinesByLength(lines, minimumLength, doc.ModelAbsoluteTolerance, build.HasSeamTrim);
  }

  private static List<Curve> BuildSeamEqualDistanceLines(
    RhinoDoc doc,
    Curve middleCurve,
    Curve sourceA,
    Curve sourceB,
    double interval,
    MiddleCurveBuild build)
  {
    var result = new List<Curve>();
    var tolerance = doc.ModelAbsoluteTolerance;
    var middleLength = middleCurve.GetLength();
    var lengthA = sourceA.GetLength();
    var lengthB = sourceB.GetLength();
    var baseCurve = build.SourceAIsBase ? sourceA : sourceB;
    var targetCurve = build.SourceAIsBase ? sourceB : sourceA;
    var baseLength = build.SourceAIsBase ? lengthA : lengthB;
    var targetLength = build.SourceAIsBase ? lengthB : lengthA;
    if (middleLength <= tolerance || baseLength <= tolerance || targetLength <= tolerance)
      return result;

    if (!TryCreateMatchedDistanceFrame(baseCurve, baseLength, targetCurve, targetLength, tolerance, out var targetStartDistance, out var targetDirection))
    {
      targetStartDistance = 0.0;
      targetDirection = 1.0;
    }

    interval = Math.Max(interval, tolerance);
    var middleStartBaseDistance = Math.Max(0.0, Math.Min(baseLength, build.MiddleStartBaseDistance));
    var middleEndBaseDistance = Math.Max(0.0, Math.Min(baseLength, build.MiddleEndBaseDistance));
    if (Math.Abs(middleEndBaseDistance - middleStartBaseDistance) <= tolerance)
      return result;

    var minBaseDistance = Math.Min(middleStartBaseDistance, middleEndBaseDistance);
    var maxBaseDistance = Math.Max(middleStartBaseDistance, middleEndBaseDistance);
    var baseDirectionFromSeam = middleStartBaseDistance >= middleEndBaseDistance ? 1.0 : -1.0;
    var stationDistances = BuildMiddleLineDistancesFromEndWithoutStartRemainder(middleLength, interval, tolerance);
    double? previousBaseDistance = null;
    var generatedLines = new List<(Curve Line, double BaseDistance)>();

    for (var i = 1; i < stationDistances.Count; i++)
    {
      var middleDistance = stationDistances[i];
      if (!TryPointAtDistance(middleCurve, middleLength, middleDistance, out var stationPoint))
        continue;

      var fraction = Math.Max(0.0, Math.Min(1.0, middleDistance / middleLength));
      var guessDistance = middleStartBaseDistance + (middleEndBaseDistance - middleStartBaseDistance) * fraction;
      var searchMin = minBaseDistance;
      var searchMax = maxBaseDistance;
      if (previousBaseDistance.HasValue)
      {
        if (baseDirectionFromSeam > 0.0)
          searchMin = Math.Min(maxBaseDistance, previousBaseDistance.Value + tolerance);
        else
          searchMax = Math.Max(minBaseDistance, previousBaseDistance.Value - tolerance);
      }

      if (searchMax < searchMin - tolerance)
        break;

      if (TryBuildBestEqualDistanceLineAtStation(
            stationPoint,
            baseCurve,
            baseLength,
            targetCurve,
            targetLength,
            targetStartDistance,
            targetDirection,
            searchMin,
            searchMax,
            guessDistance,
            tolerance,
            build.SourceAIsBase,
            out var lineCurve,
            out var baseDistance))
      {
        generatedLines.Add((lineCurve, baseDistance));
        previousBaseDistance = baseDistance;
      }
    }

    if (generatedLines.Count >= 2 &&
        TryBuildFirstSeamEqualDistanceLine(
          middleCurve.PointAtEnd,
          baseCurve,
          baseLength,
          targetCurve,
          targetLength,
          targetStartDistance,
          targetDirection,
          middleEndBaseDistance,
          generatedLines[0].BaseDistance,
          generatedLines[1].BaseDistance,
          tolerance,
          build.SourceAIsBase,
          out var firstLine))
      result.Add(firstLine);

    foreach (var generatedLine in generatedLines)
      result.Add(generatedLine.Line);

    return result;
  }

  private static List<Curve> BuildEqualDistanceLines(
    RhinoDoc doc,
    Curve middleCurve,
    Curve sourceA,
    Curve sourceB,
    double interval,
    bool sourceAIsBase,
    double middleStartBaseDistance,
    double middleEndBaseDistance)
  {
    var result = new List<Curve>();
    var tolerance = doc.ModelAbsoluteTolerance;
    var middleLength = middleCurve.GetLength();
    var lengthA = sourceA.GetLength();
    var lengthB = sourceB.GetLength();
    var baseCurve = sourceAIsBase ? sourceA : sourceB;
    var targetCurve = sourceAIsBase ? sourceB : sourceA;
    var baseLength = sourceAIsBase ? lengthA : lengthB;
    var targetLength = sourceAIsBase ? lengthB : lengthA;
    interval = Math.Max(interval, tolerance);
    middleStartBaseDistance = Math.Max(0.0, Math.Min(baseLength, middleStartBaseDistance));
    middleEndBaseDistance = Math.Max(0.0, Math.Min(baseLength, middleEndBaseDistance));

    if (Math.Abs(middleEndBaseDistance - middleStartBaseDistance) <= tolerance || middleLength <= tolerance || targetLength <= tolerance)
      return result;

    if (!TryCreateMatchedDistanceFrame(baseCurve, baseLength, targetCurve, targetLength, tolerance, out var targetStartDistance, out var targetDirection))
    {
      targetStartDistance = 0.0;
      targetDirection = 1.0;
    }

    var minBaseDistance = Math.Min(middleStartBaseDistance, middleEndBaseDistance);
    var maxBaseDistance = Math.Max(middleStartBaseDistance, middleEndBaseDistance);
    var baseDirectionFromEnd = middleStartBaseDistance >= middleEndBaseDistance ? 1.0 : -1.0;
    double? previousBaseDistance = null;
    var distances = BuildMiddleLineDistancesFromEnd(middleLength, interval, tolerance);
    foreach (var middleDistance in distances)
    {
      var fraction = Math.Max(0.0, Math.Min(1.0, middleDistance / middleLength));
      var guessDistance = middleStartBaseDistance + (middleEndBaseDistance - middleStartBaseDistance) * fraction;
      if (!TryPointAtDistance(middleCurve, middleLength, middleDistance, out var stationPoint))
        continue;

      var searchMin = minBaseDistance;
      var searchMax = maxBaseDistance;
      if (previousBaseDistance.HasValue)
      {
        if (baseDirectionFromEnd > 0.0)
          searchMin = Math.Min(maxBaseDistance, previousBaseDistance.Value + tolerance);
        else
          searchMax = Math.Max(minBaseDistance, previousBaseDistance.Value - tolerance);
      }

      if (searchMax < searchMin - tolerance)
        break;

      if (TryBuildBestEqualDistanceLineAtStation(
            stationPoint,
            baseCurve,
            baseLength,
            targetCurve,
            targetLength,
            targetStartDistance,
            targetDirection,
            searchMin,
            searchMax,
            guessDistance,
            tolerance,
            sourceAIsBase,
            out var lineCurve,
            out var baseDistance))
      {
        result.Add(lineCurve);
        previousBaseDistance = baseDistance;
      }
    }

    return result;
  }

  private static bool TryBuildEqualDistanceLineAtBaseDistance(
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double targetStartDistance,
    double targetDirection,
    double distance,
    double tolerance,
    bool sourceAIsBase,
    out Curve lineCurve)
  {
    lineCurve = null!;
    var baseDistance = Math.Max(0.0, Math.Min(baseLength, distance));
    var targetDistance = targetStartDistance + targetDirection * baseDistance;
    if (targetDistance < -tolerance || targetDistance > targetLength + tolerance)
      return false;

    targetDistance = Math.Max(0.0, Math.Min(targetLength, targetDistance));
    if (!TryPointAtDistance(baseCurve, baseLength, baseDistance, out var basePoint))
      return false;
    if (!TryPointAtDistance(targetCurve, targetLength, targetDistance, out var targetPoint))
      return false;
    if (basePoint.DistanceTo(targetPoint) <= tolerance)
      return false;

    lineCurve = sourceAIsBase ? new LineCurve(basePoint, targetPoint) : new LineCurve(targetPoint, basePoint);
    return true;
  }

  private static bool TryBuildBestEqualDistanceLineAtStation(
    Point3d stationPoint,
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double targetStartDistance,
    double targetDirection,
    double searchMin,
    double searchMax,
    double guessDistance,
    double tolerance,
    bool sourceAIsBase,
    out Curve lineCurve,
    out double baseDistance)
  {
    lineCurve = null!;
    baseDistance = guessDistance;
    searchMin = Math.Max(0.0, Math.Min(baseLength, searchMin));
    searchMax = Math.Max(searchMin, Math.Min(baseLength, searchMax));
    guessDistance = Math.Max(searchMin, Math.Min(searchMax, guessDistance));

    if (searchMax - searchMin <= tolerance)
      return TryBuildEqualDistanceLineAtBaseDistance(
        baseCurve,
        baseLength,
        targetCurve,
        targetLength,
        targetStartDistance,
        targetDirection,
        guessDistance,
        tolerance,
        sourceAIsBase,
        out lineCurve);

    var found = false;
    var bestDistance = guessDistance;
    var bestScore = double.MaxValue;
    const int samples = 96;
    for (var i = 0; i <= samples; i++)
    {
      var candidate = searchMin + (searchMax - searchMin) * i / samples;
      if (!TryScoreEqualDistanceLineAtStation(
            stationPoint,
            baseCurve,
            baseLength,
            targetCurve,
            targetLength,
            targetStartDistance,
            targetDirection,
            candidate,
            guessDistance,
            tolerance,
            sourceAIsBase,
            out var score))
        continue;

      if (score >= bestScore)
        continue;

      found = true;
      bestScore = score;
      bestDistance = candidate;
    }

    if (!found)
      return false;

    var step = Math.Max((searchMax - searchMin) / samples, tolerance);
    var refineMin = Math.Max(searchMin, bestDistance - step);
    var refineMax = Math.Min(searchMax, bestDistance + step);
    for (var i = 0; i < 24; i++)
    {
      var left = refineMin + (refineMax - refineMin) / 3.0;
      var right = refineMax - (refineMax - refineMin) / 3.0;
      if (!TryScoreEqualDistanceLineAtStation(
            stationPoint,
            baseCurve,
            baseLength,
            targetCurve,
            targetLength,
            targetStartDistance,
            targetDirection,
            left,
            guessDistance,
            tolerance,
            sourceAIsBase,
            out var leftScore) ||
          !TryScoreEqualDistanceLineAtStation(
            stationPoint,
            baseCurve,
            baseLength,
            targetCurve,
            targetLength,
            targetStartDistance,
            targetDirection,
            right,
            guessDistance,
            tolerance,
            sourceAIsBase,
            out var rightScore))
        break;

      if (leftScore < rightScore)
        refineMax = right;
      else
        refineMin = left;
    }

    baseDistance = Math.Max(searchMin, Math.Min(searchMax, 0.5 * (refineMin + refineMax)));
    return TryBuildEqualDistanceLineAtBaseDistance(
      baseCurve,
      baseLength,
      targetCurve,
      targetLength,
      targetStartDistance,
      targetDirection,
      baseDistance,
      tolerance,
      sourceAIsBase,
      out lineCurve);
  }

  private static bool TryScoreEqualDistanceLineAtStation(
    Point3d stationPoint,
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double targetStartDistance,
    double targetDirection,
    double baseDistance,
    double guessDistance,
    double tolerance,
    bool sourceAIsBase,
    out double score)
  {
    score = double.MaxValue;
    if (!TryBuildEqualDistanceLineAtBaseDistance(
          baseCurve,
          baseLength,
          targetCurve,
          targetLength,
          targetStartDistance,
          targetDirection,
          baseDistance,
          tolerance,
          sourceAIsBase,
          out var lineCurve))
      return false;

    var line = (LineCurve)lineCurve;
    score = DistancePointToSegment(stationPoint, line.PointAtStart, line.PointAtEnd, tolerance)
            + Math.Abs(baseDistance - guessDistance) * 0.001;
    return true;
  }

  private static double DistancePointToSegment(Point3d point, Point3d segmentStart, Point3d segmentEnd, double tolerance)
  {
    var segment = segmentEnd - segmentStart;
    var lengthSquared = segment.SquareLength;
    if (lengthSquared <= tolerance * tolerance)
      return point.DistanceTo(segmentStart);

    var t = ((point - segmentStart) * segment) / lengthSquared;
    t = Math.Max(0.0, Math.Min(1.0, t));
    return point.DistanceTo(segmentStart + segment * t);
  }

  private static bool TryBuildFirstSeamEqualDistanceLine(
    Point3d seamPoint,
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double targetStartDistance,
    double targetDirection,
    double seamBaseDistance,
    double secondBaseDistance,
    double thirdBaseDistance,
    double tolerance,
    bool sourceAIsBase,
    out Curve lineCurve)
  {
    lineCurve = null!;
    var step = secondBaseDistance - thirdBaseDistance;
    var extrapolatedBaseDistance = secondBaseDistance + step;
    seamBaseDistance = Math.Max(0.0, Math.Min(baseLength, seamBaseDistance));
    extrapolatedBaseDistance = Math.Max(0.0, Math.Min(baseLength, extrapolatedBaseDistance));

    var searchMin = Math.Max(0.0, Math.Min(seamBaseDistance, extrapolatedBaseDistance));
    var searchMax = Math.Min(baseLength, Math.Max(seamBaseDistance, extrapolatedBaseDistance));
    if (step > tolerance)
      searchMin = Math.Max(searchMin, Math.Min(baseLength, secondBaseDistance + tolerance));
    else if (step < -tolerance)
      searchMax = Math.Min(searchMax, Math.Max(0.0, secondBaseDistance - tolerance));

    if (searchMax >= searchMin - tolerance &&
        TryBuildBestEqualDistanceLineAtStation(
          seamPoint,
          baseCurve,
          baseLength,
          targetCurve,
          targetLength,
          targetStartDistance,
          targetDirection,
          searchMin,
          searchMax,
          seamBaseDistance,
          tolerance,
          sourceAIsBase,
          out lineCurve,
          out _))
      return true;

    return TryBuildEqualDistanceLineAtBaseDistance(
      baseCurve,
      baseLength,
      targetCurve,
      targetLength,
      targetStartDistance,
      targetDirection,
      extrapolatedBaseDistance,
      tolerance,
      sourceAIsBase,
      out lineCurve);
  }

  private static bool TryCreateMatchedDistanceFrame(
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double tolerance,
    out double targetStartDistance,
    out double targetDirection)
  {
    targetStartDistance = 0.0;
    targetDirection = 1.0;

    var baseStart = baseCurve.PointAtStart;
    var baseEnd = baseCurve.PointAtEnd;
    if (!targetCurve.ClosestPoint(baseStart, out var targetStartParameter) ||
        !targetCurve.ClosestPoint(baseEnd, out var targetEndParameter))
      return false;

    var projectedStart = DistanceAtParameter(targetCurve, targetLength, targetStartParameter);
    var projectedEnd = DistanceAtParameter(targetCurve, targetLength, targetEndParameter);
    var projectedDirection = projectedEnd >= projectedStart ? 1.0 : -1.0;
    if (Math.Abs(projectedEnd - projectedStart) <= tolerance)
      projectedDirection = projectedStart <= targetLength - projectedStart ? 1.0 : -1.0;

    var found = false;
    var bestScore = double.MaxValue;
    var bestStartDistance = 0.0;
    var bestDirection = 1.0;

    void AddCandidate(double startDistance, double direction)
    {
      direction = direction >= 0.0 ? 1.0 : -1.0;
      var endDistance = startDistance + direction * baseLength;
      if (startDistance < -tolerance || startDistance > targetLength + tolerance ||
          endDistance < -tolerance || endDistance > targetLength + tolerance)
        return;

      var clampedStart = Math.Max(0.0, Math.Min(targetLength, startDistance));
      var clampedEnd = Math.Max(0.0, Math.Min(targetLength, endDistance));
      if (!TryPointAtDistance(targetCurve, targetLength, clampedStart, out var targetStart) ||
          !TryPointAtDistance(targetCurve, targetLength, clampedEnd, out var targetEnd))
        return;

      var midpointDistance = clampedStart + direction * baseLength * 0.5;
      if (!TryPointAtDistance(baseCurve, baseLength, baseLength * 0.5, out var baseMid) ||
          !TryPointAtDistance(targetCurve, targetLength, midpointDistance, out var targetMid))
        return;

      var score = targetStart.DistanceTo(baseStart)
                + targetEnd.DistanceTo(baseEnd)
                + 0.5 * targetMid.DistanceTo(baseMid);
      if (score >= bestScore)
        return;

      found = true;
      bestScore = score;
      bestStartDistance = clampedStart;
      bestDirection = direction;
    }

    AddCandidate(projectedStart, projectedDirection);
    AddCandidate(projectedEnd - projectedDirection * baseLength, projectedDirection);
    AddCandidate(projectedStart, 1.0);
    AddCandidate(projectedStart, -1.0);
    AddCandidate(projectedEnd - baseLength, 1.0);
    AddCandidate(projectedEnd + baseLength, -1.0);
    AddCandidate(0.0, 1.0);
    AddCandidate(Math.Max(0.0, targetLength - baseLength), 1.0);
    AddCandidate(targetLength, -1.0);
    AddCandidate(Math.Min(baseLength, targetLength), -1.0);

    if (!found)
      return false;

    targetStartDistance = bestStartDistance;
    targetDirection = bestDirection;
    return true;
  }

  private static bool IsBaseEndWider(
    Curve baseCurve,
    double baseLength,
    Curve targetCurve,
    double targetLength,
    double tolerance)
  {
    if (TryCreateMatchedDistanceFrame(baseCurve, baseLength, targetCurve, targetLength, tolerance, out var targetStartDistance, out var targetDirection))
    {
      var targetEndDistance = targetStartDistance + targetDirection * baseLength;
      if (targetStartDistance >= -tolerance && targetStartDistance <= targetLength + tolerance &&
          targetEndDistance >= -tolerance && targetEndDistance <= targetLength + tolerance &&
          TryPointAtDistance(baseCurve, baseLength, 0.0, out var baseStart) &&
          TryPointAtDistance(baseCurve, baseLength, baseLength, out var baseEnd) &&
          TryPointAtDistance(targetCurve, targetLength, Math.Max(0.0, Math.Min(targetLength, targetStartDistance)), out var targetStart) &&
          TryPointAtDistance(targetCurve, targetLength, Math.Max(0.0, Math.Min(targetLength, targetEndDistance)), out var targetEnd))
      {
        return baseEnd.DistanceTo(targetEnd) > baseStart.DistanceTo(targetStart) + tolerance;
      }
    }

    return baseCurve.PointAtEnd.DistanceTo(targetCurve.PointAtEnd) >
           baseCurve.PointAtStart.DistanceTo(targetCurve.PointAtStart) + tolerance;
  }

  private static double DistanceAtParameter(Curve curve, double curveLength, double parameter)
  {
    var domain = curve.Domain;
    var clampedParameter = Math.Max(domain.T0, Math.Min(domain.T1, parameter));
    if (clampedParameter <= domain.T0)
      return 0.0;
    if (clampedParameter >= domain.T1)
      return curveLength;

    return Math.Max(0.0, Math.Min(curveLength, curve.GetLength(new Interval(domain.T0, clampedParameter))));
  }

  private static List<Curve> BuildNormalLines(
    RhinoDoc doc,
    Curve middleCurve,
    Curve sourceA,
    Curve sourceB,
    double interval)
  {
    var result = new List<Curve>();
    var tolerance = doc.ModelAbsoluteTolerance;
    var middleLength = middleCurve.GetLength();
    interval = Math.Max(interval, tolerance);

    if (middleLength <= tolerance)
      return result;

    var workPlane = ResolveWorkPlane(doc, sourceA, sourceB, tolerance);
    var distances = BuildMiddleLineDistancesFromEnd(middleLength, interval, tolerance);

    foreach (var distance in distances)
    {
      if (!TryPointAndTangentAtDistance(middleCurve, middleLength, distance, out var center, out var tangent))
        continue;

      if (!TryMiddleNormal(workPlane, tangent, out var normal))
        continue;

      if (!TryBuildCenteredSourceLine(center, normal, sourceA, sourceB, middleLength, tolerance, out var lineCurve))
        continue;

      result.Add(lineCurve);
    }

    return result;
  }

  private static List<Curve> FilterConnectorLinesByLength(
    List<Curve> connectorLines,
    double minimumLength,
    double tolerance,
    bool preserveFirst)
  {
    minimumLength = Math.Max(0.0, minimumLength);
    if (minimumLength <= tolerance)
      return connectorLines;

    var result = new List<Curve>(connectorLines.Count);
    for (var i = 0; i < connectorLines.Count; i++)
    {
      var connectorLine = connectorLines[i];
      if (preserveFirst && i == 0)
      {
        result.Add(connectorLine);
        continue;
      }

      if (connectorLine.GetLength() + tolerance < minimumLength)
        continue;

      result.Add(connectorLine);
    }

    return result;
  }

  private static List<double> BuildMiddleLineDistancesFromEnd(double length, double interval, double tolerance)
  {
    var distances = new List<double>();
    interval = Math.Max(interval, tolerance);
    if (length <= tolerance)
      return distances;

    for (var distance = length; distance >= -tolerance; distance -= interval)
      distances.Add(Math.Max(0.0, distance));

    return distances;
  }

  private static List<double> BuildMiddleLineDistancesFromEndWithoutStartRemainder(double length, double interval, double tolerance)
  {
    var distances = new List<double>();
    interval = Math.Max(interval, tolerance);
    if (length <= tolerance)
      return distances;

    distances.Add(length);
    for (var distance = length - interval; distance >= interval - tolerance; distance -= interval)
      distances.Add(Math.Max(0.0, distance));

    return distances;
  }

  private static bool TryMiddleNormal(Plane workPlane, Vector3d tangent, out Vector3d normal)
  {
    normal = Vector3d.Unset;

    var planeNormal = workPlane.ZAxis;
    if (!planeNormal.Unitize())
      planeNormal = Vector3d.ZAxis;

    tangent -= planeNormal * (tangent * planeNormal);
    if (tangent.IsTiny())
      return false;
    tangent.Unitize();

    normal = Vector3d.CrossProduct(planeNormal, tangent);
    if (normal.IsTiny())
      return false;

    normal.Unitize();
    return true;
  }

  private static bool TryBuildCenteredSourceLine(
    Point3d center,
    Vector3d normal,
    Curve sourceA,
    Curve sourceB,
    double middleLength,
    double tolerance,
    out Curve lineCurve)
  {
    lineCurve = null!;

    if (!normal.Unitize())
      return false;

    var span = Math.Max(
      Math.Max(sourceA.GetLength(), sourceB.GetLength()) + middleLength,
      tolerance * 100.0);
    span = Math.Max(span, 1.0);

    var line = new Line(center - normal * span, center + normal * span);
    var hitsA = IntersectionsOnLine(sourceA, line, center, normal, tolerance);
    var hitsB = IntersectionsOnLine(sourceB, line, center, normal, tolerance);
    if (hitsA.Count == 0 || hitsB.Count == 0)
      return false;

    Point3d bestA = Point3d.Unset;
    Point3d bestB = Point3d.Unset;
    var bestScore = double.MaxValue;

    foreach (var hitA in hitsA)
    {
      foreach (var hitB in hitsB)
      {
        if (hitA.SignedDistance * hitB.SignedDistance >= 0.0)
          continue;

        var midpoint = new Point3d(
          0.5 * (hitA.Point.X + hitB.Point.X),
          0.5 * (hitA.Point.Y + hitB.Point.Y),
          0.5 * (hitA.Point.Z + hitB.Point.Z));
        var score = midpoint.DistanceTo(center);
        if (score >= bestScore)
          continue;

        bestScore = score;
        bestA = hitA.Point;
        bestB = hitB.Point;
      }
    }

    if (!bestA.IsValid || !bestB.IsValid || bestA.DistanceTo(bestB) <= tolerance)
      return false;

    lineCurve = new LineCurve(bestA, bestB);
    return true;
  }

  private static List<(Point3d Point, double SignedDistance)> IntersectionsOnLine(
    Curve curve,
    Line line,
    Point3d center,
    Vector3d normal,
    double tolerance)
  {
    var hits = new List<(Point3d Point, double SignedDistance)>();
    var events = Intersection.CurveLine(curve, line, tolerance, tolerance);
    if (events == null)
      return hits;

    for (var i = 0; i < events.Count; i++)
    {
      if (!events[i].IsPoint)
        continue;

      var point = events[i].PointA;
      if (!point.IsValid)
        continue;

      var signedDistance = (point - center) * normal;
      if (Math.Abs(signedDistance) <= tolerance)
        continue;

      if (hits.Exists(existing => existing.Point.DistanceTo(point) <= tolerance))
        continue;

      hits.Add((point, signedDistance));
    }

    return hits;
  }

  private static MiddleCurveBuild? BuildMiddleCurveAuto(RhinoDoc doc, Curve curveA, Curve curveB, double seamAllowance)
  {
    var lengthA = curveA.GetLength();
    var lengthB = curveB.GetLength();
    var tolerance = doc.ModelAbsoluteTolerance;
    var useAAsBase = lengthA <= lengthB;
    var baseCurve = useAAsBase ? curveA : curveB;
    var targetCurve = useAAsBase ? curveB : curveA;
    var baseLength = useAAsBase ? lengthA : lengthB;
    var targetLength = useAAsBase ? lengthB : lengthA;

    if (baseLength <= tolerance)
      return null;

    var workPlane = ResolveWorkPlane(doc, curveA, curveB, tolerance);
    var startConnectorLinesAtEndWhenFullLength = seamAllowance > tolerance &&
      IsBaseEndWider(baseCurve, baseLength, targetCurve, targetLength, tolerance);
    var seamSamples = FindSeamAllowanceSamples(baseCurve, baseLength, targetCurve, targetLength, workPlane, seamAllowance, tolerance);
    ResolveSeamRange(
      seamSamples,
      baseLength,
      tolerance,
      startConnectorLinesAtEndWhenFullLength,
      out var startDistance,
      out var endDistance,
      out var startConnectorLinesAtEnd);

    var lockedSamples = new List<(double Distance, Point3d Point)>();
    foreach (var seamSample in seamSamples)
      AddLockedSample(lockedSamples, seamSample.Distance, seamSample.Point, tolerance);
    foreach (var intersectionSample in FindIntersectionSamples(baseCurve, targetCurve, baseLength, tolerance))
      AddLockedSample(lockedSamples, intersectionSample.Distance, intersectionSample.Point, tolerance);

    var sampleCount = EstimateSampleCount(baseCurve, targetCurve, baseLength);
    var midPoints = BuildMidPoints(
      doc,
      baseCurve,
      baseLength,
      targetCurve,
      targetLength,
      workPlane,
      sampleCount,
      startDistance,
      endDistance,
      lockedSamples);
    var curve = CreateMiddleCurve(midPoints);
    if (curve == null)
      return null;

    var hasSeamTrim = startDistance > tolerance || endDistance < baseLength - tolerance;
    var seamAtStart = hasSeamTrim && startDistance > tolerance;
    var middleStartBaseDistance = startDistance;
    var middleEndBaseDistance = endDistance;
    if (seamAtStart)
    {
      _ = curve.Reverse();
      middleStartBaseDistance = endDistance;
      middleEndBaseDistance = startDistance;
    }

    return new MiddleCurveBuild(
      curve,
      useAAsBase,
      baseLength,
      targetLength,
      startDistance,
      endDistance,
      middleStartBaseDistance,
      middleEndBaseDistance,
      startConnectorLinesAtEnd,
      hasSeamTrim);
  }

  private sealed class MiddleCurveBuild
  {
    public MiddleCurveBuild(
      Curve curve,
      bool sourceAIsBase,
      double baseLength,
      double targetLength,
      double baseStartDistance,
      double baseEndDistance,
      double middleStartBaseDistance,
      double middleEndBaseDistance,
      bool startConnectorLinesAtEnd,
      bool hasSeamTrim)
    {
      Curve = curve;
      SourceAIsBase = sourceAIsBase;
      BaseLength = baseLength;
      TargetLength = targetLength;
      BaseStartDistance = baseStartDistance;
      BaseEndDistance = baseEndDistance;
      MiddleStartBaseDistance = middleStartBaseDistance;
      MiddleEndBaseDistance = middleEndBaseDistance;
      StartConnectorLinesAtEnd = startConnectorLinesAtEnd;
      HasSeamTrim = hasSeamTrim;
    }

    public Curve Curve { get; }
    public bool SourceAIsBase { get; }
    public double BaseLength { get; }
    public double TargetLength { get; }
    public double BaseStartDistance { get; }
    public double BaseEndDistance { get; }
    public double MiddleStartBaseDistance { get; }
    public double MiddleEndBaseDistance { get; }
    public bool StartConnectorLinesAtEnd { get; }
    public bool HasSeamTrim { get; }
  }
}
