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
/// Supports curves and points; points are treated as single anchor endpoints.
/// </summary>
public sealed class vCurveToSpline : Command
{
  private const string OptionsSectionName = "vCurveToSpline";
  private const string JoinModeKey = "joinMode";
  private static readonly string[] JoinModes = { "None", "Connected", "All" };
  private static int _joinModeIndex = 2;

  /// <summary>Unified representation of a selected curve or point object.</summary>
  private readonly struct Segment
  {
    /// <summary>Control points: one for a point object, N for a curve.</summary>
    public IReadOnlyList<Point3d> Points { get; }
    public Segment(IReadOnlyList<Point3d> points) => Points = points;
    public Point3d Start => Points[0];
    public Point3d End   => Points[^1];
    /// <summary>Returns the control-point list, optionally reversed.</summary>
    public IReadOnlyList<Point3d> OrientedPoints(bool reverse)
    {
      if (!reverse) return Points;
      var pts = Points.ToList();
      pts.Reverse();
      return pts;
    }
  }

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
    Log.Write("vCurveToSpline", $"BEGIN joinMode={JoinModes[_joinModeIndex]}");

    var pickResult = TryGetSelectedSegmentsAndJoinMode(doc, out var selectedSegments, out var joinMode);
    _joinModeIndex = Array.IndexOf(JoinModes, joinMode);
    if (_joinModeIndex < 0)
      _joinModeIndex = 2;
    SavePersistedOptions();

    if (pickResult != Result.Success)
    {
      Log.Write("vCurveToSpline", $"END result={pickResult}");
      return pickResult;
    }

    Log.Write("vCurveToSpline", $"selected segments={selectedSegments.Count} joinMode={joinMode}");
    var tolerance = doc.ModelAbsoluteTolerance;
    var newCurveIds = new List<Guid>();

    foreach (var interpCurve in BuildInterpCurves(selectedSegments, joinMode, tolerance))
    {
      var id = doc.Objects.AddCurve(interpCurve);
      if (id != Guid.Empty)
        newCurveIds.Add(id);
    }

    if (newCurveIds.Count == 0)
    {
      RhinoApp.WriteLine("vCurveToSpline: Failed to create InterpCrv.");
      Log.Write("vCurveToSpline", "END result=Failure (no curves created)");
      return Result.Failure;
    }

    // Keep focus on generated outputs.
    doc.Objects.UnselectAll();
    foreach (var id in newCurveIds)
      doc.Objects.Select(id);

    doc.Views.Redraw();
    Log.Write("vCurveToSpline", $"END result=Success created={newCurveIds.Count}");
    return Result.Success;
  }

  /// <summary>
  /// Loads persisted command options from the shared config.
  /// </summary>
  private static void LoadPersistedOptions()
  {
    var loadedIndex = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        if (ToolsOptionStore.TryGetString(section, JoinModeKey, out var joinMode))
        {
          var index = Array.FindIndex(JoinModes, mode => string.Equals(mode, joinMode, StringComparison.OrdinalIgnoreCase));
          if (index >= 0)
            return index;
        }

        if (ToolsOptionStore.TryGetDouble(section, JoinModeKey, out var numericIndex))
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
    _ = ToolsOptionStore.Update(OptionsSectionName, section => section[JoinModeKey] = modeName);
  }

  /// <summary>
  /// Gets selected curves and/or points plus join mode, with dynamic preview while editing options.
  /// </summary>
  private static Result TryGetSelectedSegmentsAndJoinMode(RhinoDoc doc, out List<Segment> segments, out string joinMode)
  {
    segments = new List<Segment>();
    joinMode = JoinModes[Math.Max(0, Math.Min(_joinModeIndex, JoinModes.Length - 1))];

    var tolerance = doc.ModelAbsoluteTolerance;
    var go = new GetObject();
    go.SetCommandPrompt("Select curves and/or points to convert to InterpCrv");
    go.GeometryFilter = ObjectType.Curve | ObjectType.Point;
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
            Log.Write("vCurveToSpline", $"Join -> {joinMode}");
            preview.SetJoinMode(joinMode);
            doc.Views.Redraw();
            SavePersistedOptions();
          }
          continue;
        }

        if (getResult == GetResult.Object)
        {
          segments = SegmentsFromDocumentSelection(doc);
          Log.Write("vCurveToSpline", $"Object: segments={segments.Count} preselected={go.ObjectsWerePreselected}");
          preview.SetJoinMode(joinMode);
          doc.Views.Redraw();

          if (go.ObjectsWerePreselected && !preselectedWaitingForEnter)
          {
            preselectedWaitingForEnter = true;
            go.EnablePreSelect(false, true);
            continue;
          }

          if (segments.Count > 0)
            return Result.Success;

          continue;
        }

        if (getResult == GetResult.Nothing)
        {
          segments = SegmentsFromDocumentSelection(doc);
          Log.Write("vCurveToSpline", $"Nothing: segments={segments.Count}");
          return segments.Count > 0 ? Result.Success : Result.Cancel;
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
  /// Returns selected curve and point objects from the document selection set.
  /// </summary>
  private static List<RhinoObject> SelectedGeometryObjects(RhinoDoc doc)
  {
    var result = new List<RhinoObject>();
    var seenIds = new HashSet<Guid>();

    foreach (var rhinoObject in doc.Objects.GetSelectedObjects(false, false))
    {
      if (rhinoObject == null)
        continue;
      if (!seenIds.Add(rhinoObject.Id))
        continue;
      if (rhinoObject.Geometry is Curve || rhinoObject.Geometry is Rhino.Geometry.Point)
        result.Add(rhinoObject);
    }

    return result;
  }

  /// <summary>
  /// Builds a Segment list from an ordered sequence of RhinoObjects.
  /// </summary>
  private static List<Segment> SegmentsFromObjects(IEnumerable<RhinoObject> objects)
  {
    var segments = new List<Segment>();
    foreach (var obj in objects)
    {
      if (obj == null) continue;
      if (obj.Geometry is Curve curve)
      {
        var nurbs = curve.ToNurbsCurve();
        if (nurbs == null) continue;
        var pts = new List<Point3d>(nurbs.Points.Count);
        for (var i = 0; i < nurbs.Points.Count; i++)
          pts.Add(nurbs.Points[i].Location);
        if (pts.Count >= 1)
          segments.Add(new Segment(pts));
      }
      else if (obj.Geometry is Rhino.Geometry.Point point)
      {
        segments.Add(new Segment(new[] { point.Location }));
      }
    }
    return segments;
  }

  /// <summary>
  /// Builds a Segment list from the current document selection (unordered fallback).
  /// </summary>
  private static List<Segment> SegmentsFromDocumentSelection(RhinoDoc doc)
    => SegmentsFromObjects(SelectedGeometryObjects(doc));

  /// <summary>
  /// Builds interpolated curves according to selected join mode.
  /// </summary>
  private static List<Curve> BuildInterpCurves(IReadOnlyList<Segment> segments, string joinMode, double tolerance)
  {
    var interpCurves = new List<Curve>();

    foreach (var group in SegmentGroupsForMode(segments, joinMode, tolerance))
    {
      if (group.Count == 0)
        continue;

      var orderedChain = OrderSegmentsAsChain(group, tolerance);
      var interpPoints = BuildInterpPoints(group, orderedChain, tolerance);
      var interpCurve = CreateInterpCurve(interpPoints);
      if (interpCurve != null)
        interpCurves.Add(interpCurve);
    }

    return interpCurves;
  }

  /// <summary>
  /// Splits segments into per-output groups based on join mode.
  /// </summary>
  private static List<List<Segment>> SegmentGroupsForMode(IReadOnlyList<Segment> segments, string joinMode, double tolerance)
  {
    if (segments.Count == 0)
      return new List<List<Segment>>();

    if (string.Equals(joinMode, "None", StringComparison.OrdinalIgnoreCase))
      return segments.Select(s => new List<Segment> { s }).ToList();

    if (string.Equals(joinMode, "Connected", StringComparison.OrdinalIgnoreCase))
      return GroupTouchingSegments(segments, tolerance);

    return new List<List<Segment>> { segments.ToList() };
  }

  /// <summary>
  /// Returns true when either endpoint pair of two segments is within tolerance.
  /// </summary>
  private static bool SegmentsTouch(Segment a, Segment b, double tolerance)
  {
    return PointsMatch(a.Start, b.Start, tolerance) ||
           PointsMatch(a.Start, b.End,   tolerance) ||
           PointsMatch(a.End,   b.Start, tolerance) ||
           PointsMatch(a.End,   b.End,   tolerance);
  }

  /// <summary>
  /// Groups segments into connected components by endpoint touching.
  /// </summary>
  private static List<List<Segment>> GroupTouchingSegments(IReadOnlyList<Segment> segments, double tolerance)
  {
    var count = segments.Count;
    if (count <= 1)
      return new List<List<Segment>> { segments.ToList() };

    var adjacency = new List<int>[count];
    for (var i = 0; i < count; i++)
      adjacency[i] = new List<int>();

    for (var i = 0; i < count; i++)
    {
      for (var j = i + 1; j < count; j++)
      {
        if (!SegmentsTouch(segments[i], segments[j], tolerance))
          continue;

        adjacency[i].Add(j);
        adjacency[j].Add(i);
      }
    }

    var visited = new bool[count];
    var groups = new List<List<Segment>>();

    for (var start = 0; start < count; start++)
    {
      if (visited[start])
        continue;

      var stack = new Stack<int>();
      stack.Push(start);
      visited[start] = true;

      var group = new List<Segment>();
      while (stack.Count > 0)
      {
        var index = stack.Pop();
        group.Add(segments[index]);

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
  /// Orders one segment group into a path by: (1) greedy nearest-neighbour closed tour,
  /// (2) 2-opt improvement on the tour, (3) cut the longest edge to open the path.
  /// Cutting the longest edge always lands at the natural gap, giving the correct start/end.
  /// </summary>
  private static List<(int SegmentIndex, bool Reverse)> OrderSegmentsAsChain(IReadOnlyList<Segment> segments, double tolerance)
  {
    var count = segments.Count;
    if (count == 1)
      return new List<(int, bool)> { (0, false) };

    // Step 1: greedy nearest-neighbour closed tour (start from 0 — arbitrary for a tour).
    var tour = new List<(int SegmentIndex, bool Reverse)>(count);
    var remaining = new HashSet<int>(Enumerable.Range(0, count));
    tour.Add((0, false));
    remaining.Remove(0);

    while (remaining.Count > 0)
    {
      var (si, rev) = tour[tour.Count - 1];
      var currentExit = rev ? segments[si].Start : segments[si].End;

      (int Si, bool Rev, Point3d Exit)? best = null;
      double? bestDist = null;

      foreach (var ci in remaining)
      {
        foreach (var flip in new[] { false, true })
        {
          var entry = flip ? segments[ci].End   : segments[ci].Start;
          var exit  = flip ? segments[ci].Start : segments[ci].End;
          var d = currentExit.DistanceTo(entry);
          if (bestDist == null || d < bestDist.Value)
          { bestDist = d; best = (ci, flip, exit); }
        }
      }

      if (!best.HasValue) break;
      tour.Add((best.Value.Si, best.Value.Rev));
      remaining.Remove(best.Value.Si);
    }

    // Step 2: 2-opt improvement on the closed tour.
    Improve2OptTour(segments, tour);

    // Step 3: cut the longest edge in the tour — that gap is the intended open-path endpoint pair.
    var longestIdx = 0;
    var longestDist = 0.0;
    for (var i = 0; i < count; i++)
    {
      var next = (i + 1) % count;
      var exitPt  = tour[i].Reverse  ? segments[tour[i].SegmentIndex].Start  : segments[tour[i].SegmentIndex].End;
      var entryPt = tour[next].Reverse ? segments[tour[next].SegmentIndex].End : segments[tour[next].SegmentIndex].Start;
      var d = exitPt.DistanceTo(entryPt);
      if (d > longestDist) { longestDist = d; longestIdx = i; }
    }

    var path = new List<(int, bool)>(count);
    for (var i = 0; i < count; i++)
      path.Add(tour[(longestIdx + 1 + i) % count]);

    return path;
  }

  /// <summary>
  /// 2-opt improvement on a closed tour. Reverses sub-tour [i+1..j] when it shortens total length.
  /// </summary>
  private static void Improve2OptTour(IReadOnlyList<Segment> segments, List<(int SegmentIndex, bool Reverse)> tour)
  {
    var count = tour.Count;
    if (count < 4) return;

    Point3d Entry(int idx)
    {
      var (si, rev) = tour[idx];
      return rev ? segments[si].End : segments[si].Start;
    }

    Point3d Exit(int idx)
    {
      var (si, rev) = tour[idx];
      return rev ? segments[si].Start : segments[si].End;
    }

    var improved = true;
    while (improved)
    {
      improved = false;
      for (var i = 0; i < count - 1; i++)
      {
        for (var j = i + 2; j < count; j++)
        {
          if (i == 0 && j == count - 1) continue; // same edge, skip

          var ni = (i + 1) % count;
          var nj = (j + 1) % count;

          var before = Exit(i).DistanceTo(Entry(ni)) + Exit(j).DistanceTo(Entry(nj));
          var after  = Exit(i).DistanceTo(Exit(j))   + Entry(ni).DistanceTo(Entry(nj));

          if (after < before - 1e-10)
          {
            var lo = ni;
            var hi = j;
            while (lo < hi)
            {
              var tmp = (tour[lo].SegmentIndex, !tour[lo].Reverse);
              tour[lo] = (tour[hi].SegmentIndex, !tour[hi].Reverse);
              tour[hi] = tmp;
              lo++; hi--;
            }
            if (lo == hi)
              tour[lo] = (tour[lo].SegmentIndex, !tour[lo].Reverse);

            improved = true;
          }
        }
      }
    }
  }

  /// <summary>
  /// Builds ordered interpolation points from the oriented segment chain.
  /// </summary>
  private static List<Point3d> BuildInterpPoints(
    IReadOnlyList<Segment> segments,
    IReadOnlyList<(int SegmentIndex, bool Reverse)> orderedChain,
    double tolerance)
  {
    var interpPoints = new List<Point3d>();

    foreach (var (segIndex, reverse) in orderedChain)
    {
      var segPoints = segments[segIndex].OrientedPoints(reverse);
      if (segPoints.Count == 0)
        continue;

      if (interpPoints.Count > 0 && PointsMatch(interpPoints[^1], segPoints[0], tolerance))
        interpPoints.AddRange(segPoints.Skip(1));
      else
        interpPoints.AddRange(segPoints);
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

  private static bool PointsMatch(Point3d a, Point3d b, double tolerance)
    => a.DistanceTo(b) <= tolerance;

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
      _selectionSignature = string.Empty;
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
    /// Rebuilds preview only when selection or join mode changed.
    /// </summary>
    private void RefreshCacheIfNeeded()
    {
      var selectedObjects = SelectedGeometryObjects(_doc);
      var signature = BuildSignature(selectedObjects, _joinMode);
      if (string.Equals(signature, _selectionSignature, StringComparison.Ordinal))
        return;

      _selectionSignature = signature;
      _previewCurves = BuildInterpCurves(SegmentsFromDocumentSelection(_doc), _joinMode, _tolerance);
    }

    private static string BuildSignature(IEnumerable<RhinoObject> objects, string joinMode)
    {
      var ids = string.Join("|", objects.Select(obj => obj.Id.ToString("N")));
      return joinMode + "::" + ids;
    }
  }
}