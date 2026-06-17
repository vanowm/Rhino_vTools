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

    var boundaryIndex = PickBoundaryCurve(selected, plane, tol);
    if (boundaryIndex < 0)
    {
      RhinoApp.WriteLine("vTrimOff: select a closed boundary box.");
      return Result.Nothing;
    }

    var boundaryObjId = selected[boundaryIndex].ObjectId;
    var boundary = new List<Curve> { selected[boundaryIndex].Curve.DuplicateCurve() };

    var scanDocument = selected.Count == 1;
    var targets = scanDocument
      ? CollectDocumentTargets(doc, boundaryObjId)
      : selected.Where((_, i) => i != boundaryIndex).ToList();

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

  private static List<TargetCurve> CollectDocumentTargets(RhinoDoc doc, Guid boundaryObjId)
  {
    var targets = new List<TargetCurve>();

    foreach (var obj in doc.Objects)
    {
      if (obj.Id == boundaryObjId)
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
