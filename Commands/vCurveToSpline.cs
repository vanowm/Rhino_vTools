using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native curve-to-spline conversion command ported from Curve2Spline.py.
/// </summary>
public sealed class vCurveToSpline : Command
{
  private const string OptionsSectionName = "vCurveToSpline";
  private const string JoinModeKey = "joinMode";
  private static readonly string[] JoinModes = { "None", "Connected", "All" };
  private static int _joinModeIndex = 2;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vCurveToSpline";

  /// <summary>
  /// Executes curve-to-spline conversion using interpolated control point chains.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var pickResult = TryGetSelectedCurvesAndJoinMode(doc, out var selectedCurves, out var joinMode);
    _joinModeIndex = Array.IndexOf(JoinModes, joinMode);
    if (_joinModeIndex < 0)
      _joinModeIndex = 2;
    SavePersistedOptions();

    if (pickResult != Result.Success)
      return pickResult;

    var tolerance = doc.ModelAbsoluteTolerance;
    var newCurveIds = new List<Guid>();

    foreach (var interpCurve in BuildInterpCurves(selectedCurves, joinMode, tolerance))
    {
      var id = doc.Objects.AddCurve(interpCurve);
      if (id != Guid.Empty)
        newCurveIds.Add(id);
    }

    if (newCurveIds.Count == 0)
    {
      RhinoApp.WriteLine("vCurveToSpline: Failed to create InterpCrv.");
      return Result.Failure;
    }

    // Keep focus on generated outputs.
    doc.Objects.UnselectAll();
    foreach (var id in newCurveIds)
      doc.Objects.Select(id);

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Loads persisted command options from the shared config.
  /// </summary>
  private static void LoadPersistedOptions()
  {
    var loadedIndex = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        if (vToolsOptionStore.TryGetString(section, JoinModeKey, out var joinMode))
        {
          var index = Array.FindIndex(JoinModes, mode => string.Equals(mode, joinMode, StringComparison.OrdinalIgnoreCase));
          if (index >= 0)
            return index;
        }

        if (vToolsOptionStore.TryGetDouble(section, JoinModeKey, out var numericIndex))
        {
          var index = (int)Math.Round(numericIndex, MidpointRounding.AwayFromZero);
          if (index >= 0 && index < JoinModes.Length)
            return index;
        }

        return _joinModeIndex;
      });

    _joinModeIndex = Math.Max(0, Math.Min(JoinModes.Length - 1, loadedIndex));
  }

  /// <summary>
  /// Saves current command options to the shared config.
  /// </summary>
  private static void SavePersistedOptions()
  {
    var modeName = JoinModes[Math.Max(0, Math.Min(_joinModeIndex, JoinModes.Length - 1))];
    _ = vToolsOptionStore.Update(OptionsSectionName, section => section[JoinModeKey] = modeName);
  }

  /// <summary>
  /// Gets selected curves plus join mode, with dynamic preview while editing options.
  /// </summary>
  private static Result TryGetSelectedCurvesAndJoinMode(RhinoDoc doc, out List<Curve> curves, out string joinMode)
  {
    curves = new List<Curve>();
    joinMode = JoinModes[Math.Max(0, Math.Min(_joinModeIndex, JoinModes.Length - 1))];

    var tolerance = doc.ModelAbsoluteTolerance;
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to convert to InterpCrv");
    go.GeometryFilter = ObjectType.Curve;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var preview = new InterpPreviewConduit(doc, joinMode, tolerance)
    {
      Enabled = true
    };

    var preselectedWaitingForEnter = false;
    doc.Views.Redraw();

    try
    {
      while (true)
      {
        go.ClearCommandOptions();
        var joinOptionIndex = go.AddOptionList("Join", JoinModes, _joinModeIndex);

        var getResult = go.GetMultiple(1, 0);
        if (go.CommandResult() != Result.Success)
          return go.CommandResult();

        if (getResult == GetResult.Option)
        {
          var option = go.Option();
          if (option != null && option.Index == joinOptionIndex)
          {
            _joinModeIndex = option.CurrentListOptionIndex;
            joinMode = JoinModes[Math.Max(0, Math.Min(_joinModeIndex, JoinModes.Length - 1))];
            preview.SetJoinMode(joinMode);
            doc.Views.Redraw();
          }
          continue;
        }

        if (getResult == GetResult.Object)
        {
          curves = CurvesFromDocumentSelection(doc);
          preview.SetJoinMode(joinMode);
          doc.Views.Redraw();

          if (go.ObjectsWerePreselected && !preselectedWaitingForEnter)
          {
            preselectedWaitingForEnter = true;
            go.EnablePreSelect(false, true);
            continue;
          }

          if (curves.Count > 0)
            return Result.Success;

          continue;
        }

        if (getResult == GetResult.Nothing)
        {
          curves = CurvesFromDocumentSelection(doc);
          return curves.Count > 0 ? Result.Success : Result.Cancel;
        }

        if (getResult == GetResult.Cancel)
          return Result.Cancel;

        return Result.Cancel;
      }
    }
    finally
    {
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  /// <summary>
  /// Returns selected curve objects from the document selection set.
  /// </summary>
  private static List<RhinoObject> SelectedCurveObjects(RhinoDoc doc)
  {
    var selectedCurveObjects = new List<RhinoObject>();
    var seenIds = new HashSet<Guid>();

    foreach (var rhinoObject in doc.Objects.GetSelectedObjects(false, false))
    {
      if (rhinoObject == null)
        continue;
      if (!seenIds.Add(rhinoObject.Id))
        continue;
      if (rhinoObject.Geometry is not Curve)
        continue;

      selectedCurveObjects.Add(rhinoObject);
    }

    return selectedCurveObjects;
  }

  /// <summary>
  /// Duplicates selected document curves for safe command-side processing.
  /// </summary>
  private static List<Curve> CurvesFromDocumentSelection(RhinoDoc doc)
  {
    return SelectedCurveObjects(doc)
      .Select(obj => (obj.Geometry as Curve)?.DuplicateCurve())
      .Where(curve => curve != null)
      .Cast<Curve>()
      .ToList();
  }

  /// <summary>
  /// Builds interpolated curves according to selected join mode.
  /// </summary>
  private static List<Curve> BuildInterpCurves(IReadOnlyList<Curve> curves, string joinMode, double tolerance)
  {
    var interpCurves = new List<Curve>();

    foreach (var group in CurveGroupsForMode(curves, joinMode, tolerance))
    {
      if (group.Count == 0)
        continue;

      var orderedChain = OrderCurvesAsChain(group, tolerance);
      var interpPoints = BuildInterpPoints(group, orderedChain, tolerance);
      var interpCurve = CreateInterpCurve(interpPoints);
      if (interpCurve != null)
        interpCurves.Add(interpCurve);
    }

    return interpCurves;
  }

  /// <summary>
  /// Splits selected curves into per-output groups based on join mode.
  /// </summary>
  private static List<List<Curve>> CurveGroupsForMode(IReadOnlyList<Curve> curves, string joinMode, double tolerance)
  {
    if (curves.Count == 0)
      return new List<List<Curve>>();

    if (string.Equals(joinMode, "None", StringComparison.OrdinalIgnoreCase))
      return curves.Select(c => new List<Curve> { c }).ToList();

    if (string.Equals(joinMode, "Connected", StringComparison.OrdinalIgnoreCase))
      return GroupTouchingCurves(curves, tolerance);

    return new List<List<Curve>> { curves.ToList() };
  }

  /// <summary>
  /// Returns true when either endpoint pair is within tolerance.
  /// </summary>
  private static bool CurvesTouch(Curve curveA, Curve curveB, double tolerance)
  {
    var a0 = curveA.PointAtStart;
    var a1 = curveA.PointAtEnd;
    var b0 = curveB.PointAtStart;
    var b1 = curveB.PointAtEnd;

    return PointsMatch(a0, b0, tolerance) ||
           PointsMatch(a0, b1, tolerance) ||
           PointsMatch(a1, b0, tolerance) ||
           PointsMatch(a1, b1, tolerance);
  }

  /// <summary>
  /// Groups curves into connected components by endpoint touching.
  /// </summary>
  private static List<List<Curve>> GroupTouchingCurves(IReadOnlyList<Curve> curves, double tolerance)
  {
    var count = curves.Count;
    if (count <= 1)
      return new List<List<Curve>> { curves.ToList() };

    var adjacency = new List<int>[count];
    for (var i = 0; i < count; i++)
      adjacency[i] = new List<int>();

    for (var i = 0; i < count; i++)
    {
      for (var j = i + 1; j < count; j++)
      {
        if (!CurvesTouch(curves[i], curves[j], tolerance))
          continue;

        adjacency[i].Add(j);
        adjacency[j].Add(i);
      }
    }

    var visited = new bool[count];
    var groups = new List<List<Curve>>();

    for (var start = 0; start < count; start++)
    {
      if (visited[start])
        continue;

      var stack = new Stack<int>();
      stack.Push(start);
      visited[start] = true;

      var group = new List<Curve>();
      while (stack.Count > 0)
      {
        var index = stack.Pop();
        group.Add(curves[index]);

        foreach (var neighbor in adjacency[index])
        {
          if (visited[neighbor])
            continue;
          visited[neighbor] = true;
          stack.Push(neighbor);
        }
      }

      groups.Add(group);
    }

    return groups;
  }

  /// <summary>
  /// Orders one curve group into a nearest-neighbor chain with endpoint flips.
  /// </summary>
  private static List<(int CurveIndex, bool Reverse)> OrderCurvesAsChain(IReadOnlyList<Curve> curves, double tolerance)
  {
    var count = curves.Count;
    if (count == 1)
      return new List<(int, bool)> { (0, false) };

    (int CurveIndex, bool Reverse) bestStart = (0, false);
    var bestStartScore = double.NegativeInfinity;

    for (var curveIndex = 0; curveIndex < count; curveIndex++)
    {
      foreach (var reverse in new[] { false, true })
      {
        var (startPoint, _) = OrientedEndpoints(curves[curveIndex], reverse);
        double? nearest = null;

        for (var otherIndex = 0; otherIndex < count; otherIndex++)
        {
          if (otherIndex == curveIndex)
            continue;

          var otherStart = curves[otherIndex].PointAtStart;
          var otherEnd = curves[otherIndex].PointAtEnd;

          nearest = MinDistance(nearest, startPoint.DistanceTo(otherStart));
          nearest = MinDistance(nearest, startPoint.DistanceTo(otherEnd));
        }

        var score = nearest ?? 0.0;
        if (score > bestStartScore)
        {
          bestStartScore = score;
          bestStart = (curveIndex, reverse);
        }
      }
    }

    var ordered = new List<(int, bool)> { bestStart };
    var remaining = new HashSet<int>(Enumerable.Range(0, count));
    remaining.Remove(bestStart.CurveIndex);

    var (_, currentEnd) = OrientedEndpoints(curves[bestStart.CurveIndex], bestStart.Reverse);
    while (remaining.Count > 0)
    {
      (int CurveIndex, bool Reverse, Point3d EndPoint)? bestNext = null;
      double? bestDistance = null;

      foreach (var candidateIndex in remaining)
      {
        foreach (var reverse in new[] { false, true })
        {
          var (candidateStart, candidateEnd) = OrientedEndpoints(curves[candidateIndex], reverse);
          var distance = currentEnd.DistanceTo(candidateStart);

          if (bestDistance == null || distance < bestDistance.Value)
          {
            bestDistance = distance;
            bestNext = (candidateIndex, reverse, candidateEnd);
          }
        }
      }

      if (!bestNext.HasValue)
        break;

      ordered.Add((bestNext.Value.CurveIndex, bestNext.Value.Reverse));
      remaining.Remove(bestNext.Value.CurveIndex);
      currentEnd = bestNext.Value.EndPoint;
    }

    return ordered;
  }

  /// <summary>
  /// Builds ordered interpolation points from oriented source curve control points.
  /// </summary>
  private static List<Point3d> BuildInterpPoints(
    IReadOnlyList<Curve> curves,
    IReadOnlyList<(int CurveIndex, bool Reverse)> orderedChain,
    double tolerance)
  {
    var interpPoints = new List<Point3d>();

    foreach (var (curveIndex, reverseCurve) in orderedChain)
    {
      var curvePoints = ControlPointsForCurve(curves[curveIndex], reverseCurve);
      if (curvePoints.Count == 0)
        continue;

      if (interpPoints.Count > 0 && PointsMatch(interpPoints[^1], curvePoints[0], tolerance))
        interpPoints.AddRange(curvePoints.Skip(1));
      else
        interpPoints.AddRange(curvePoints);
    }

    if (interpPoints.Count > 2 && PointsMatch(interpPoints[0], interpPoints[^1], tolerance))
      interpPoints.RemoveAt(interpPoints.Count - 1);

    return interpPoints;
  }

  /// <summary>
  /// Creates one interpolated curve from a point chain.
  /// </summary>
  private static Curve? CreateInterpCurve(IReadOnlyList<Point3d> interpPoints)
  {
    if (interpPoints.Count < 2)
      return null;

    var degree = 3;
    if (interpPoints.Count <= degree)
      degree = Math.Max(1, interpPoints.Count - 1);

    return Curve.CreateInterpolatedCurve(interpPoints, degree, CurveKnotStyle.Chord);
  }

  /// <summary>
  /// Returns oriented start/end endpoints for one curve orientation.
  /// </summary>
  private static (Point3d Start, Point3d End) OrientedEndpoints(Curve curve, bool reverse)
  {
    return reverse ? (curve.PointAtEnd, curve.PointAtStart) : (curve.PointAtStart, curve.PointAtEnd);
  }

  /// <summary>
  /// Returns control-point locations from a curve, optionally reversed.
  /// </summary>
  private static List<Point3d> ControlPointsForCurve(Curve curve, bool reverse)
  {
    var nurbs = curve.ToNurbsCurve();
    if (nurbs == null)
      return new List<Point3d>();

    var points = new List<Point3d>(nurbs.Points.Count);
    for (var i = 0; i < nurbs.Points.Count; i++)
      points.Add(nurbs.Points[i].Location);

    if (reverse)
      points.Reverse();

    return points;
  }

  /// <summary>
  /// Numeric helper for point equality with model tolerance.
  /// </summary>
  private static bool PointsMatch(Point3d a, Point3d b, double tolerance)
  {
    return a.DistanceTo(b) <= tolerance;
  }

  /// <summary>
  /// Numeric helper for nullable minimum selection.
  /// </summary>
  private static double? MinDistance(double? current, double candidate)
  {
    return !current.HasValue || candidate < current.Value ? candidate : current;
  }

  /// <summary>
  /// Lightweight viewport conduit that previews interpolated output from current selection.
  /// </summary>
  private sealed class InterpPreviewConduit : DisplayConduit
  {
    private readonly RhinoDoc _doc;
    private readonly double _tolerance;
    private readonly Color _color = Color.OrangeRed;
    private string _joinMode;
    private string _selectionSignature = string.Empty;
    private List<Curve> _previewCurves = new();

    public InterpPreviewConduit(RhinoDoc doc, string joinMode, double tolerance)
    {
      _doc = doc;
      _joinMode = joinMode;
      _tolerance = tolerance;
    }

    /// <summary>
    /// Updates the active preview join mode.
    /// </summary>
    public void SetJoinMode(string joinMode)
    {
      _joinMode = joinMode;
    }

    /// <summary>
    /// Draws current cached preview curves.
    /// </summary>
    protected override void DrawForeground(DrawEventArgs e)
    {
      RefreshCacheIfNeeded();

      foreach (var curve in _previewCurves)
        e.Display.DrawCurve(curve, _color, 3);
    }

    /// <summary>
    /// Rebuilds preview only when selection signature or join mode changed.
    /// </summary>
    private void RefreshCacheIfNeeded()
    {
      var selectedCurveObjects = SelectedCurveObjects(_doc);
      var signature = BuildSignature(selectedCurveObjects, _joinMode);
      if (string.Equals(signature, _selectionSignature, StringComparison.Ordinal))
        return;

      _selectionSignature = signature;
      var selectedCurves = selectedCurveObjects
        .Select(obj => (obj.Geometry as Curve)?.DuplicateCurve())
        .Where(curve => curve != null)
        .Cast<Curve>()
        .ToList();

      _previewCurves = BuildInterpCurves(selectedCurves, _joinMode, _tolerance);
    }

    /// <summary>
    /// Builds a deterministic cache key from selected ids and current join mode.
    /// </summary>
    private static string BuildSignature(IEnumerable<RhinoObject> curveObjects, string joinMode)
    {
      var ids = string.Join("|", curveObjects.Select(obj => obj.Id.ToString("N")));
      return joinMode + "::" + ids;
    }
  }
}