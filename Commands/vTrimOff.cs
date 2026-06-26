using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vTrimOff : Command
{
  public override string EnglishName => "vTrimOff";

  private sealed class TargetCurve
  {
    public Guid ObjectId { get; init; }
    public Curve Curve { get; init; } = null!;
    public ObjectAttributes Attr { get; init; } = null!;
  }

  private sealed class BoundarySelection
  {
    public Curve Curve { get; init; } = null!;
    public HashSet<Guid> SourceIds { get; init; } = new();
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var tol = doc.ModelAbsoluteTolerance;

    var go = new GetObject();
    go.SetCommandPrompt("Select boundary box, or boundary box plus curves to trim");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.AlreadySelectedObjectSelect = true;
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(false);

    var getResult = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    if (getResult != GetResult.Object || go.ObjectCount == 0)
      return Result.Cancel;

    var selected = new List<TargetCurve>();

    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      if (r.Curve() is not { } crv)
        continue;

      selected.Add(new TargetCurve
      {
        ObjectId = r.ObjectId,
        Curve = crv.DuplicateCurve(),
        Attr = r.Object()?.Attributes?.Duplicate() ?? new ObjectAttributes()
      });
    }

    if (selected.Count == 0)
      return Result.Cancel;

    var vp = doc.Views.ActiveView?.ActiveViewport;
    var plane = Plane.WorldXY;
    if (vp != null && vp.GetCameraFrame(out var camFrame))
      plane = new Plane(Point3d.Origin, camFrame.XAxis, camFrame.YAxis);

    var boundarySelection = TryPickBoundary(selected, plane, tol);
    if (boundarySelection == null)
    {
      RhinoApp.WriteLine("vTrimOff: select a closed boundary box.");
      return Result.Nothing;
    }

    var boundarySourceIds = boundarySelection.SourceIds;
    var boundary = new List<Curve> { boundarySelection.Curve.DuplicateCurve() };

    var scanDocument = selected.All(t => boundarySourceIds.Contains(t.ObjectId));
    var targets = scanDocument
      ? CollectDocumentTargets(doc, boundarySourceIds)
      : selected.Where(t => !boundarySourceIds.Contains(t.ObjectId)).ToList();

    if (targets.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no target curves found.");
      return Result.Nothing;
    }

    var keepPairs = new List<(Curve Curve, ObjectAttributes Attr)>();
    var processedIds = new HashSet<Guid>();
    var trimmedCurves = 0;
    var removedSegments = 0;

    foreach (var target in targets)
    {
      var crv = target.Curve.DuplicateCurve();
      var pieces = SplitCurveByBoundary(crv, boundary, tol);

      // Boundary-only mode scans the document, but only modifies curves that cross
      // the boundary. Fully-inside and fully-outside document curves stay untouched.
      if (scanDocument && pieces.Count == 0)
        continue;

      if (pieces.Count == 0)
      {
        if (PointIsInsideOrOn(crv.PointAtNormalizedLength(0.5), boundary, plane, tol))
          keepPairs.Add((crv, target.Attr.Duplicate()));
        else
          removedSegments++;

        processedIds.Add(target.ObjectId);
        continue;
      }

      var keptForCurve = 0;

      foreach (var piece in pieces)
      {
        if (piece.GetLength() < tol)
          continue;

        // After splitting, midpoint classification is deliberate:
        // a circle corner arc that is inside the box is kept, and the outside arc is removed.
        if (PointIsInsideOrOn(piece.PointAtNormalizedLength(0.5), boundary, plane, tol))
        {
          keepPairs.Add((piece, target.Attr.Duplicate()));
          keptForCurve++;
        }
        else
        {
          removedSegments++;
        }
      }

      processedIds.Add(target.ObjectId);
      trimmedCurves++;

      if (keptForCurve == 0)
        removedSegments++;
    }

    if (processedIds.Count == 0)
    {
      RhinoApp.WriteLine("vTrimOff: no selected/document curves cross the boundary.");
      return Result.Nothing;
    }

    foreach (var id in processedIds)
      doc.Objects.Delete(id, true);

    foreach (var (crv, attr) in keepPairs)
      doc.Objects.AddCurve(crv, attr);

    RhinoApp.WriteLine($"vTrimOff: trimmed {trimmedCurves} curve{(trimmedCurves == 1 ? "" : "s")}; removed {removedSegments} outside segment{(removedSegments == 1 ? "" : "s")}.");
    doc.Views.Redraw();
    return Result.Success;
  }

  private static List<TargetCurve> CollectDocumentTargets(RhinoDoc doc, HashSet<Guid> boundaryObjIds)
  {
    var targets = new List<TargetCurve>();

    foreach (var obj in doc.Objects)
    {
      if (boundaryObjIds.Contains(obj.Id))
        continue;

      if (obj.IsDeleted || obj.IsReference)
        continue;

      if (obj.Geometry is not Curve crv)
        continue;

      targets.Add(new TargetCurve
      {
        ObjectId = obj.Id,
        Curve = crv.DuplicateCurve(),
        Attr = obj.Attributes.Duplicate()
      });
    }

    return targets;
  }

  private static BoundarySelection? TryPickBoundary(List<TargetCurve> selected, Plane plane, double tol)
  {
    var closedIndex = PickBoundaryCurve(selected, plane, tol);
    if (closedIndex >= 0)
    {
      return new BoundarySelection
      {
        Curve = selected[closedIndex].Curve.DuplicateCurve(),
        SourceIds = new HashSet<Guid> { selected[closedIndex].ObjectId }
      };
    }

    var (closed, _) = BuildClosedPerimeterLikeVPart(
      selected.Select(s => s.Curve.DuplicateCurve()).ToList(),
      plane,
      tol);

    if (closed == null)
      return null;

    return new BoundarySelection
    {
      Curve = closed,
      SourceIds = selected.Select(s => s.ObjectId).ToHashSet()
    };
  }

  private static (Curve? Closed, List<LineCurve> Bridges) BuildClosedPerimeterLikeVPart(
    List<Curve> curves,
    Plane plane,
    double tol)
  {
    var bridges = new List<LineCurve>();

    if (curves.Count == 1 && curves[0].IsClosed)
      return (curves[0].DuplicateCurve(), bridges);

    var regions = Curve.CreateBooleanRegions(curves.ToArray(), plane, combineRegions: true, tol);
    if (regions != null)
    {
      for (var r = 0; r < regions.RegionCount; r++)
      {
        var rc = regions.RegionCurves(r);
        if (rc == null)
          continue;

        foreach (var c in rc)
          if (c?.IsClosed == true)
            return (c, bridges);
      }
    }

    var pieces = curves.Select(c => c.DuplicateCurve()).ToList();

    var openEnds = new List<(int CrvIdx, bool IsStart, Point3d Pt)>();
    for (var i = 0; i < pieces.Count; i++)
    {
      if (pieces[i].IsClosed)
        continue;

      openEnds.Add((i, true, pieces[i].PointAtStart));
      openEnds.Add((i, false, pieces[i].PointAtEnd));
    }

    var used = new HashSet<int>();
    for (var i = 0; i < openEnds.Count; i++)
    {
      if (used.Contains(i))
        continue;

      var bestDist = tol * 200.0;
      var bestJ = -1;

      for (var j = i + 1; j < openEnds.Count; j++)
      {
        if (used.Contains(j))
          continue;

        if (openEnds[j].CrvIdx == openEnds[i].CrvIdx)
          continue;

        var d = openEnds[i].Pt.DistanceTo(openEnds[j].Pt);
        if (d > tol && d < bestDist)
        {
          bestDist = d;
          bestJ = j;
        }
      }

      if (bestJ < 0)
        continue;

      var bridge = new LineCurve(openEnds[i].Pt, openEnds[bestJ].Pt);
      bridges.Add(bridge);
      pieces.Add(bridge);
      used.Add(i);
      used.Add(bestJ);
    }

    var joined = Curve.JoinCurves(pieces.ToArray(), tol * 10.0);
    if (joined != null && joined.Length == 1 && joined[0].IsClosed)
      return (joined[0], bridges);

    var final = Curve.JoinCurves(pieces.ToArray(), tol * 100.0);
    if (final != null && final.Length == 1 && final[0].IsClosed)
      return (final[0], bridges);

    return (null, bridges);
  }

  private static int PickBoundaryCurve(List<TargetCurve> selected, Plane plane, double tol)
  {
    var closed = Enumerable.Range(0, selected.Count)
                           .Where(i => selected[i].Curve.IsClosed)
                           .ToList();

    if (closed.Count == 0)
      return -1;

    if (closed.Count == 1)
      return closed[0];

    var bestIndex = -1;
    var bestScore = double.NegativeInfinity;

    foreach (var idx in closed)
    {
      var c = selected[idx].Curve;
      var boxScore = BoxLikeScore(c, plane, tol);
      var containsScore = 0;

      for (var oi = 0; oi < selected.Count; oi++)
      {
        if (oi == idx)
          continue;

        foreach (var pt in SampleCurve(selected[oi].Curve, 9))
        {
          var r = c.Contains(pt, plane, tol);
          if (r == PointContainment.Inside || r == PointContainment.Coincident)
            containsScore++;
        }
      }

      var area = Math.Abs(AreaMassProperties.Compute(c)?.Area ?? 0.0);
      var score = boxScore * 10000.0 + containsScore * 10.0 + area * 0.000001;

      if (score > bestScore)
      {
        bestScore = score;
        bestIndex = idx;
      }
    }

    return bestIndex;
  }

  private static BoundarySelection? TryBuildBoundaryFromBooleanRegions(List<TargetCurve> selected, Plane plane, double tol)
  {
    BoundarySelection? best = null;
    var bestScore = double.NegativeInfinity;

    void Consider(List<TargetCurve> candidateSet)
    {
      var candidate = TryBuildBoundaryFromSet(selected, candidateSet, plane, tol);
      if (candidate == null)
        return;

      var score = ScoreBoundaryCandidate(candidate.Curve, selected, plane, tol);
      if (score <= bestScore)
        return;

      bestScore = score;
      best = candidate;
    }

    Consider(selected);

    // If targets overlap the boundary box, building regions from all selected
    // curves can fail or create the wrong regions. Try smaller selected subsets
    // so the actual box curves can be found, like vPart finds the perimeter.
    if (selected.Count > 1 && selected.Count <= 12)
    {
      var maxSubsetSize = Math.Min(8, selected.Count);
      for (var size = 2; size <= maxSubsetSize; size++)
      {
        foreach (var subset in EnumerateSubsets(selected, size))
          Consider(subset);
      }
    }

    return best ?? TryBuildBoundaryFromBoxEdges(selected, plane, tol);
  }

  private static BoundarySelection? TryBuildBoundaryFromSet(
    List<TargetCurve> allSelected,
    List<TargetCurve> candidateSet,
    Plane plane,
    double tol)
  {
    var curves = candidateSet.Select(s => s.Curve.DuplicateCurve()).ToArray();
    var regions = Curve.CreateBooleanRegions(curves, plane, combineRegions: true, tol);
    if (regions == null)
      return null;

    Curve? best = null;
    var bestScore = double.NegativeInfinity;

    for (var r = 0; r < regions.RegionCount; r++)
    {
      var regionCurves = regions.RegionCurves(r);
      if (regionCurves == null)
        continue;

      foreach (var c in regionCurves)
      {
        if (c?.IsClosed != true)
          continue;

        var score = ScoreBoundaryCandidate(c, allSelected, plane, tol);
        if (score <= bestScore)
          continue;

        bestScore = score;
        best = c.DuplicateCurve();
      }
    }

    if (best == null)
      return null;

    var sourceIds = candidateSet.Count == allSelected.Count
      ? SourceIdsForBoundary(allSelected, best, tol)
      : candidateSet.Select(s => s.ObjectId).ToHashSet();

    if (sourceIds.Count == 0)
      return null;

    return new BoundarySelection { Curve = best, SourceIds = sourceIds };
  }

  private static double ScoreBoundaryCandidate(Curve candidate, List<TargetCurve> selected, Plane plane, double tol)
  {
    var boxScore = BoxLikeScore(candidate, plane, tol);
    var containsScore = 0;

    foreach (var target in selected)
    {
      foreach (var pt in SampleCurve(target.Curve, 9))
      {
        var pc = candidate.Contains(pt, plane, tol);
        if (pc == PointContainment.Inside || pc == PointContainment.Coincident)
          containsScore++;
      }
    }

    var area = Math.Abs(AreaMassProperties.Compute(candidate)?.Area ?? 0.0);
    return boxScore * 10000.0 + containsScore * 10.0 + area * 0.000001;
  }

  private static IEnumerable<List<TargetCurve>> EnumerateSubsets(List<TargetCurve> items, int size)
  {
    return EnumerateSubsets(items, size, 0, new List<TargetCurve>(size));
  }

  private static IEnumerable<List<TargetCurve>> EnumerateSubsets(
    List<TargetCurve> items,
    int size,
    int start,
    List<TargetCurve> current)
  {
    if (current.Count == size)
    {
      yield return current.ToList();
      yield break;
    }

    var remainingNeeded = size - current.Count;
    for (var i = start; i <= items.Count - remainingNeeded; i++)
    {
      current.Add(items[i]);

      foreach (var subset in EnumerateSubsets(items, size, i + 1, current))
        yield return subset;

      current.RemoveAt(current.Count - 1);
    }
  }


  private static BoundarySelection? TryBuildBoundaryFromBoxEdges(List<TargetCurve> selected, Plane plane, double tol)
  {
    var lineTol = Math.Max(tol * 20.0, RhinoMath.ZeroTolerance);

    var uValues = new List<double>();
    var vValues = new List<double>();

    foreach (var target in selected)
    {
      foreach (var pt in SampleCurve(target.Curve, 33))
      {
        if (!plane.ClosestParameter(pt, out var u, out var v))
          continue;

        uValues.Add(u);
        vValues.Add(v);
      }
    }

    var uCandidates = ClusterCandidateCoordinates(uValues, lineTol);
    var vCandidates = ClusterCandidateCoordinates(vValues, lineTol);

    if (uCandidates.Count < 2 || vCandidates.Count < 2)
      return null;

    Curve? bestCurve = null;
    HashSet<Guid>? bestSourceIds = null;
    var bestScore = double.NegativeInfinity;

    for (var ui0 = 0; ui0 < uCandidates.Count - 1; ui0++)
    {
      for (var ui1 = ui0 + 1; ui1 < uCandidates.Count; ui1++)
      {
        var u0 = uCandidates[ui0];
        var u1 = uCandidates[ui1];

        for (var vi0 = 0; vi0 < vCandidates.Count - 1; vi0++)
        {
          for (var vi1 = vi0 + 1; vi1 < vCandidates.Count; vi1++)
          {
            var v0 = vCandidates[vi0];
            var v1 = vCandidates[vi1];

            var score = ScoreRectangleBoundary(selected, plane, tol, lineTol, u0, u1, v0, v1, out var sourceIds);
            if (score <= bestScore || sourceIds.Count == 0)
              continue;

            bestScore = score;
            bestSourceIds = sourceIds;
            bestCurve = RectangleCurve(plane, u0, u1, v0, v1);
          }
        }
      }
    }

    if (bestCurve == null || bestSourceIds == null || bestSourceIds.Count == 0)
      return null;

    return new BoundarySelection { Curve = bestCurve, SourceIds = bestSourceIds };
  }

  private static List<double> ClusterCandidateCoordinates(IEnumerable<double> values, double tol)
  {
    var sorted = values.OrderBy(v => v).ToList();
    var clusters = new List<(double Value, int Count)>();

    var i = 0;
    while (i < sorted.Count)
    {
      var sum = sorted[i];
      var count = 1;
      var first = sorted[i];
      i++;

      while (i < sorted.Count && Math.Abs(sorted[i] - first) <= tol)
      {
        sum += sorted[i];
        count++;
        i++;
      }

      if (count >= 3)
        clusters.Add((sum / count, count));
    }

    return clusters
      .OrderByDescending(c => c.Count)
      .Take(12)
      .Select(c => c.Value)
      .OrderBy(v => v)
      .ToList();
  }

  private static double ScoreRectangleBoundary(
    List<TargetCurve> selected,
    Plane plane,
    double tol,
    double lineTol,
    double u0,
    double u1,
    double v0,
    double v1,
    out HashSet<Guid> sourceIds)
  {
    sourceIds = new HashSet<Guid>();

    var width = u1 - u0;
    var height = v1 - v0;
    if (width <= lineTol * 2.0 || height <= lineTol * 2.0)
      return double.NegativeInfinity;

    var edgeValues = new[]
    {
      new List<double>(),
      new List<double>(),
      new List<double>(),
      new List<double>()
    };

    var edgeSourceIds = new[]
    {
      new HashSet<Guid>(),
      new HashSet<Guid>(),
      new HashSet<Guid>(),
      new HashSet<Guid>()
    };

    foreach (var target in selected)
    {
      var perCurveEdgeValues = new[]
      {
        new List<double>(),
        new List<double>(),
        new List<double>(),
        new List<double>()
      };

      foreach (var pt in SampleCurve(target.Curve, 33))
      {
        if (!plane.ClosestParameter(pt, out var u, out var v))
          continue;

        if (v >= v0 - lineTol && v <= v1 + lineTol)
        {
          if (Math.Abs(u - u0) <= lineTol) perCurveEdgeValues[0].Add(v);
          if (Math.Abs(u - u1) <= lineTol) perCurveEdgeValues[1].Add(v);
        }

        if (u >= u0 - lineTol && u <= u1 + lineTol)
        {
          if (Math.Abs(v - v0) <= lineTol) perCurveEdgeValues[2].Add(u);
          if (Math.Abs(v - v1) <= lineTol) perCurveEdgeValues[3].Add(u);
        }
      }

      for (var edge = 0; edge < 4; edge++)
      {
        if (perCurveEdgeValues[edge].Count < 2)
          continue;

        edgeValues[edge].AddRange(perCurveEdgeValues[edge]);

        var span = perCurveEdgeValues[edge].Max() - perCurveEdgeValues[edge].Min();
        var edgeLength = edge < 2 ? height : width;

        if (span >= Math.Max(lineTol * 2.0, edgeLength * 0.10))
          edgeSourceIds[edge].Add(target.ObjectId);
      }
    }

    var coverage = new double[4];
    for (var edge = 0; edge < 4; edge++)
    {
      if (edgeValues[edge].Count < 2 || edgeSourceIds[edge].Count == 0)
        return double.NegativeInfinity;

      var span = edgeValues[edge].Max() - edgeValues[edge].Min();
      var edgeLength = edge < 2 ? height : width;
      coverage[edge] = span / edgeLength;

      if (coverage[edge] < 0.35)
        return double.NegativeInfinity;

      sourceIds.UnionWith(edgeSourceIds[edge]);
    }

    if (sourceIds.Count == 0)
      return double.NegativeInfinity;

    var minCoverage = coverage.Min();
    var totalCoverage = coverage.Sum();
    var area = width * height;

    return minCoverage * 10000.0 + totalCoverage * 100.0 + area * 0.000001;
  }

  private static Curve RectangleCurve(Plane plane, double u0, double u1, double v0, double v1)
  {
    var polyline = new Polyline(new[]
    {
      plane.PointAt(u0, v0),
      plane.PointAt(u1, v0),
      plane.PointAt(u1, v1),
      plane.PointAt(u0, v1),
      plane.PointAt(u0, v0)
    });

    return polyline.ToNurbsCurve();
  }

  private static HashSet<Guid> SourceIdsForBoundary(List<TargetCurve> selected, Curve boundary, double tol)
  {
    var ids = new HashSet<Guid>();

    foreach (var target in selected)
      if (CurveMostlyOnBoundary(target.Curve, boundary, tol))
        ids.Add(target.ObjectId);

    return ids;
  }

  private static bool CurveMostlyOnBoundary(Curve curve, Curve boundary, double tol)
  {
    var curveLength = curve.GetLength();
    var onTol = Math.Max(tol * 20.0, RhinoMath.ZeroTolerance);

    var overlapLength = 0.0;
    var events = Intersection.CurveCurve(curve, boundary, onTol, onTol);
    if (events != null)
    {
      foreach (var e in events)
      {
        if (!e.IsOverlap)
          continue;

        var overlap = curve.Trim(e.OverlapA.T0, e.OverlapA.T1);
        if (overlap != null)
          overlapLength += overlap.GetLength();
      }
    }

    if (curveLength > tol && overlapLength >= Math.Max(onTol, curveLength * 0.10))
      return true;

    var total = 0;
    var on = 0;

    foreach (var pt in SampleCurve(curve, 17))
    {
      total++;
      if (IsOnCurve(pt, boundary, onTol))
        on++;
    }

    return total > 0 && on >= Math.Max(2, (int)Math.Ceiling(total * 0.25));
  }

  private static bool IsOnCurve(Point3d pt, Curve curve, double tol)
  {
    if (!pt.IsValid || !curve.ClosestPoint(pt, out var t))
      return false;

    return pt.DistanceTo(curve.PointAt(t)) <= tol;
  }

  private static double BoxLikeScore(Curve curve, Plane plane, double tol)
  {
    var bbox = curve.GetBoundingBox(plane);
    if (!bbox.IsValid)
      return 0.0;

    var width = bbox.Max.X - bbox.Min.X;
    var height = bbox.Max.Y - bbox.Min.Y;
    if (width <= tol || height <= tol)
      return 0.0;

    var edgeTol = Math.Max(tol * 20.0, Math.Min(width, height) * 0.02);
    var onBoxEdge = 0;
    const int samples = 32;

    foreach (var ptWorld in SampleCurve(curve, samples))
    {
      if (!plane.ClosestParameter(ptWorld, out var u, out var v))
        continue;

      var onVertical = Math.Abs(u - bbox.Min.X) <= edgeTol || Math.Abs(u - bbox.Max.X) <= edgeTol;
      var onHorizontal = Math.Abs(v - bbox.Min.Y) <= edgeTol || Math.Abs(v - bbox.Max.Y) <= edgeTol;

      if (onVertical || onHorizontal)
        onBoxEdge++;
    }

    return (double)onBoxEdge / samples;
  }

  private static List<Curve> SplitCurveByBoundary(Curve crv, List<Curve> boundary, double tol)
  {
    if (crv.IsClosed)
      return SplitClosedCurveByBoundary(crv, boundary, tol);

    var splitParams = CollectSplitParams(crv, boundary, tol);
    if (splitParams.Count == 0)
      return new List<Curve>();

    var segments = crv.Split(splitParams);
    return segments?.Where(s => s.GetLength() >= tol).ToList() ?? new List<Curve>();
  }

  private static List<Curve> SplitClosedCurveByBoundary(Curve crv, List<Curve> boundary, double tol)
  {
    var hits = CollectRawIntersectionParams(crv, boundary, tol);
    if (hits.Count < 2)
      return new List<Curve>();

    var domain = crv.Domain;
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      return new List<Curve>();

    var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
    var parameters = hits
      .Select(t => NormalizeClosedParameter(t, domain))
      .Distinct(new ParameterEqualityComparer(paramTol))
      .OrderBy(t => t)
      .ToList();

    if (parameters.Count < 2)
      return new List<Curve>();

    var result = new List<Curve>();

    for (var i = 0; i < parameters.Count; i++)
    {
      var a = parameters[i];
      var b = i == parameters.Count - 1 ? parameters[0] + period : parameters[i + 1];

      if (b - a <= paramTol)
        continue;

      var piece = TrimClosedInterval(crv, a, b, domain, tol);
      if (piece != null && piece.GetLength() >= tol)
        result.Add(piece);
    }

    return result;
  }

  private static Curve? TrimClosedInterval(Curve crv, double a, double b, Interval domain, double tol)
  {
    var period = domain.T1 - domain.T0;

    while (a < domain.T0) a += period;
    while (a > domain.T1) a -= period;
    while (b < domain.T0) b += period;

    if (b <= domain.T1)
      return crv.Trim(a, b);

    var first = crv.Trim(a, domain.T1);
    var second = crv.Trim(domain.T0, b - period);

    if (first == null) return second;
    if (second == null) return first;

    var joined = Curve.JoinCurves(new[] { first, second }, tol);
    if (joined != null && joined.Length == 1)
      return joined[0];

    var pc = new PolyCurve();
    pc.Append(first);
    pc.Append(second);
    return pc;
  }

  private static SortedSet<double> CollectSplitParams(Curve crv, List<Curve> boundary, double tol)
  {
    var splitParams = new SortedSet<double>(new ParameterComparer(Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0));

    foreach (var t in CollectRawIntersectionParams(crv, boundary, tol))
      AddSplitParam(crv, splitParams, t, tol);

    return splitParams;
  }

  private static List<double> CollectRawIntersectionParams(Curve crv, List<Curve> boundary, double tol)
  {
    var result = new List<double>();

    foreach (var bc in boundary)
    {
      var events = Intersection.CurveCurve(crv, bc, tol, tol);
      if (events == null)
        continue;

      foreach (var e in events)
      {
        if (e.IsOverlap)
        {
          result.Add(e.OverlapA.T0);
          result.Add(e.OverlapA.T1);
        }
        else
        {
          result.Add(e.ParameterA);
        }
      }
    }

    return result;
  }

  private static void AddSplitParam(Curve crv, SortedSet<double> splitParams, double t, double tol)
  {
    var d = crv.Domain;
    var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;

    if (t <= d.T0 + paramTol || t >= d.T1 - paramTol)
      return;

    splitParams.Add(t);
  }

  private static double NormalizeClosedParameter(double t, Interval domain)
  {
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      return t;

    while (t < domain.T0) t += period;
    while (t > domain.T1) t -= period;
    return t;
  }

  private static IEnumerable<Point3d> SampleCurve(Curve curve, int count)
  {
    count = Math.Max(1, count);

    for (var i = 0; i < count; i++)
    {
      var s = (i + 0.5) / count;
      yield return curve.PointAtNormalizedLength(s);
    }
  }

  private static bool PointIsInsideOrOn(Point3d pt, List<Curve> closed, Plane plane, double tol)
  {
    foreach (var c in closed)
    {
      var r = c.Contains(pt, plane, tol);
      if (r == PointContainment.Inside || r == PointContainment.Coincident)
        return true;
    }
    return false;
  }

  private sealed class ParameterComparer : IComparer<double>
  {
    private readonly double _tol;

    public ParameterComparer(double tol)
    {
      _tol = Math.Max(tol, RhinoMath.ZeroTolerance);
    }

    public int Compare(double x, double y)
    {
      if (Math.Abs(x - y) <= _tol)
        return 0;
      return x < y ? -1 : 1;
    }
  }

  private sealed class ParameterEqualityComparer : IEqualityComparer<double>
  {
    private readonly double _tol;

    public ParameterEqualityComparer(double tol)
    {
      _tol = Math.Max(tol, RhinoMath.ZeroTolerance);
    }

    public bool Equals(double x, double y)
    {
      return Math.Abs(x - y) <= _tol;
    }

    public int GetHashCode(double obj)
    {
      return Math.Round(obj / _tol).GetHashCode();
    }
  }
}
