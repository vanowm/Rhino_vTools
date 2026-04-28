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
/// Native middle-curve command ported from MiddleCurve.py.
/// Creates an interpolated curve equidistant between two selected input curves.
/// </summary>
public sealed class vMiddleCurve : Command
{
  private const int MinSampleCount = 12;
  private const int MaxSampleCount = 2000;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vMiddleCurve";

  /// <summary>
  /// Prompts for two curves and creates an interpolated middle curve between them.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (!PromptTwoCurves(doc, out var curveA, out var curveB))
      return Result.Cancel;

    AlignCurvePair(curveA!, curveB!);

    var middleCurve = BuildMiddleCurveAuto(doc, curveA!, curveB!);
    if (middleCurve == null)
    {
      RhinoApp.WriteLine("vMiddleCurve: could not create middle curve from selected inputs.");
      return Result.Failure;
    }

    var newId = doc.Objects.AddCurve(middleCurve);
    if (newId == Guid.Empty)
    {
      RhinoApp.WriteLine("vMiddleCurve: failed to add curve to document.");
      return Result.Failure;
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  private static bool PromptTwoCurves(RhinoDoc doc, out Curve? curveA, out Curve? curveB)
  {
    curveA = null;
    curveB = null;

    var go = new GetObject();
    go.SetCommandPrompt("Select 2 curves");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var preselectedWaitingForEnter = false;

    while (true)
    {
      var result = go.GetMultiple(2, 2);

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
        return false;

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

  private static Point3d PointAtFraction(Curve curve, double fraction)
  {
    if (fraction <= 0.0)
      return curve.PointAtStart;
    if (fraction >= 1.0)
      return curve.PointAtEnd;

    return curve.NormalizedLengthParameter(fraction, out var t)
      ? curve.PointAt(t)
      : curve.PointAt(curve.Domain.ParameterAt(fraction));
  }

  private static Point3d MidpointAtFraction(Curve curveA, Curve curveB, double fraction)
  {
    var a = PointAtFraction(curveA, fraction);
    var b = PointAtFraction(curveB, fraction);
    return new Point3d(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), 0.5 * (a.Z + b.Z));
  }

  private static List<Point3d> BuildMidPoints(RhinoDoc doc, Curve curveA, Curve curveB, int sampleCount)
  {
    var points = new List<Point3d>(sampleCount + 1);
    var tolerance = doc.ModelAbsoluteTolerance;

    for (var i = 0; i <= sampleCount; i++)
    {
      var fraction = (double)i / sampleCount;
      var midpoint = MidpointAtFraction(curveA, curveB, fraction);

      if (points.Count > 0 && midpoint.DistanceTo(points[^1]) <= tolerance)
        continue;

      points.Add(midpoint);
    }

    return points;
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

  private static int EstimateInitialSampleCount(Curve curveA, Curve curveB)
  {
    var maxLength = Math.Max(Math.Max(curveA.GetLength(), curveB.GetLength()), 1.0);
    var spanHint = Math.Max(curveA.SpanCount, curveB.SpanCount) * 8;
    var lengthHint = (int)(maxLength / 10.0);
    return Math.Max(MinSampleCount, Math.Min(MaxSampleCount, Math.Max(32, Math.Max(spanHint, lengthHint))));
  }

  private static double MaxMiddleDeviation(Curve curveA, Curve curveB, Curve middleCurve, int sampleCount)
  {
    var maxError = 0.0;
    for (var i = 0; i < sampleCount; i++)
    {
      var fraction = (i + 0.5) / sampleCount;
      var midpoint = MidpointAtFraction(curveA, curveB, fraction);

      if (!middleCurve.ClosestPoint(midpoint, out var t))
        continue;

      var error = midpoint.DistanceTo(middleCurve.PointAt(t));
      if (error > maxError)
        maxError = error;
    }
    return maxError;
  }

  private static Curve? BuildMiddleCurveAuto(RhinoDoc doc, Curve curveA, Curve curveB)
  {
    var sampleCount = EstimateInitialSampleCount(curveA, curveB);
    var tolerance = doc.ModelAbsoluteTolerance;
    var sizeScale = Math.Max(Math.Max(curveA.GetLength(), curveB.GetLength()), 1.0);
    var targetError = Math.Max(tolerance * 3.0, sizeScale * 2.0e-5);

    while (true)
    {
      var midPoints = BuildMidPoints(doc, curveA, curveB, sampleCount);
      var middleCurve = CreateMiddleCurve(midPoints);
      if (middleCurve == null)
        return null;

      var error = MaxMiddleDeviation(curveA, curveB, middleCurve, sampleCount);
      if (error <= targetError || sampleCount >= MaxSampleCount)
        return middleCurve;

      sampleCount = Math.Min(MaxSampleCount, sampleCount * 2);
    }
  }
}
