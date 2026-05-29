using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Groups selected objects by closed-curve boundaries.
/// Boundaries are detected by joining all selected curves with
/// <see cref="Curve.JoinCurves"/>: any joined result that is closed and
/// planar becomes a boundary, so open or overlapping curves that together
/// enclose a region are handled correctly.
/// Every selected object whose representative point falls inside a boundary
/// is added to that boundary's group.
/// </summary>
public sealed class vGroup : Command
{
  /// <summary>Rhino command name.</summary>
  public override string EnglishName => "vGroup";

  /// <summary>
  /// Group indices created by vGroup in this Rhino session.
  /// On each run these are deleted before new groups are created, so
  /// re-running vGroup on the same objects replaces its own groups without
  /// touching any groups the user made manually.
  /// </summary>
  private static readonly HashSet<int> _ourGroupIndices = new();

  /// <summary>
  /// Joins all selected curves, uses any closed planar results as
  /// boundaries, and groups each selected object with the boundary that
  /// contains it.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select objects to group by closed curve boundary");
    go.GroupSelect = true;
    go.SubObjectSelect = false;
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    var tol = doc.ModelAbsoluteTolerance;

    // Collect all selected objects and extract curves for joining.
    var allIds      = new List<Guid>();
    var allCurves   = new List<Curve>();
    var allCurveIds = new List<Guid>(); // ID for each entry in allCurves

    foreach (var objRef in go.Objects())
    {
      var id  = objRef.ObjectId;
      allIds.Add(id);

      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is Curve c)
      {
        allCurves.Add(c);
        allCurveIds.Add(id);
      }
    }

    // Split all curves at their mutual intersections, trim dead-end segments,
    // then join the surviving core into closed boundaries.
    var boundaries    = new List<(Curve Curve, Plane Plane)>();
    var coreSegments  = new List<Curve>();
    var coreOriginIdx = new List<int>(); // index into allCurves for each core segment

    // Properly clean up groups created by a previous vGroup run.
    // doc.Groups.Delete() marks the group deleted in the table but does NOT
    // remove the index from object attributes, so objects would accumulate
    // memberships across runs and appear nested.  Strip attributes first.
    if (_ourGroupIndices.Count > 0)
    {
      foreach (RhinoObject obj in doc.Objects)
      {
        var groups = obj.Attributes.GetGroupList();
        if (groups == null || groups.Length == 0) continue;
        bool dirty = false;
        foreach (var gi in groups)
        {
          if (!_ourGroupIndices.Contains(gi)) continue;
          obj.Attributes.RemoveFromGroup(gi);
          dirty = true;
        }
        if (dirty) obj.CommitChanges();
      }
      foreach (var idx in _ourGroupIndices)
        doc.Groups.Delete(idx);
      _ourGroupIndices.Clear();
    }

    Log.Write(EnglishName, $"--- run start --- tol={tol:G4} curves={allCurves.Count} totalObjects={allIds.Count}");

    // Log every input curve.
    for (int i = 0; i < allCurves.Count; i++)
    {
      var c = allCurves[i];
      var s = c.PointAtStart;
      var e = c.PointAtEnd;
      Log.Write(EnglishName,
        $"  curve[{i}] {c.GetType().Name} IsClosed={c.IsClosed}" +
        $" TryGetPlane={c.TryGetPlane(out _, tol)}" +
        $" start=({s.X:F3},{s.Y:F3},{s.Z:F3})" +
        $" end=({e.X:F3},{e.Y:F3},{e.Z:F3})" +
        $" gap={s.DistanceTo(e):G4}");
    }

    if (allCurves.Count > 0)
    {
      // Pre-compute bounding boxes inflated by tolerance for fast pair rejection.
      var bboxes = new BoundingBox[allCurves.Count];
      for (int i = 0; i < allCurves.Count; i++)
      {
        bboxes[i] = allCurves[i].GetBoundingBox(false);
        bboxes[i].Inflate(tol);
      }

      // Collect split parameters per curve index.
      var splitParams = new Dictionary<int, List<double>>();
      for (int i = 0; i < allCurves.Count; i++)
      for (int j = i + 1; j < allCurves.Count; j++)
      {
        // Skip pairs whose bounding boxes don't overlap — they can't intersect.
        // (bboxes are already inflated by tol, so no extra tolerance needed here.)
        { var a = bboxes[i]; var b = bboxes[j];
          if (a.Max.X < b.Min.X || b.Max.X < a.Min.X ||
              a.Max.Y < b.Min.Y || b.Max.Y < a.Min.Y ||
              a.Max.Z < b.Min.Z || b.Max.Z < a.Min.Z) continue; }

        var events = Intersection.CurveCurve(allCurves[i], allCurves[j], tol, tol);
        int ptCount = 0, ovCount = 0;
        if (events != null)
        {
          foreach (var ev in events)
          {
            if (ev.IsPoint)
            {
              ptCount++;
              if (!splitParams.ContainsKey(i)) splitParams[i] = new List<double>();
              if (!splitParams.ContainsKey(j)) splitParams[j] = new List<double>();
              splitParams[i].Add(ev.ParameterA);
              splitParams[j].Add(ev.ParameterB);
            }
            else { ovCount++; }
          }
        }
        if (ptCount > 0 || ovCount > 0)
          Log.Write(EnglishName, $"  intersect[{i},{j}] pointEvents={ptCount} overlapEvents={ovCount}");
      }

      // Split each curve at its intersection parameters.
      // Track which original curve index each segment comes from.
      var segments     = new List<Curve>();
      var segOriginIdx = new List<int>();
      for (int i = 0; i < allCurves.Count; i++)
      {
        if (!splitParams.TryGetValue(i, out var parms) || parms.Count == 0)
        {
          segments.Add(allCurves[i]);
          segOriginIdx.Add(i);
          Log.Write(EnglishName, $"  split[{i}] no intersections → kept as-is");
          continue;
        }
        var splits = allCurves[i].Split(parms);
        if (splits != null && splits.Length > 0)
        {
          foreach (var s in splits) { segments.Add(s); segOriginIdx.Add(i); }
          Log.Write(EnglishName, $"  split[{i}] {parms.Count} params → {splits.Length} segments");
        }
        else
        {
          segments.Add(allCurves[i]);
          segOriginIdx.Add(i);
          Log.Write(EnglishName, $"  split[{i}] Split() returned null/empty → kept as-is");
        }
      }

      // Iteratively remove dead-end segments (degree-1 endpoint nodes).
      // Lines that extend past the enclosed region have dangling outer tails;
      // trimming round-by-round peels those off, leaving only segments that
      // participate in closed cycles (the real boundary polygon).
      var nodePos      = new List<Point3d>();
      var segStartNode = new int[segments.Count];
      var segEndNode   = new int[segments.Count];

      int GetOrAddNode(Point3d pt)
      {
        for (int n = 0; n < nodePos.Count; n++)
          if (pt.DistanceTo(nodePos[n]) <= tol) return n;
        nodePos.Add(pt);
        return nodePos.Count - 1;
      }

      for (int i = 0; i < segments.Count; i++)
      {
        segStartNode[i] = GetOrAddNode(segments[i].PointAtStart);
        segEndNode[i]   = GetOrAddNode(segments[i].PointAtEnd);
      }

      var removed    = new HashSet<int>();
      bool anyChange = true;
      int trimRounds = 0;
      while (anyChange)
      {
        anyChange = false;
        var degree = new Dictionary<int, int>();
        for (int i = 0; i < segments.Count; i++)
        {
          if (removed.Contains(i)) continue;
          degree.TryGetValue(segStartNode[i], out var ds);
          degree[segStartNode[i]] = ds + 1;
          degree.TryGetValue(segEndNode[i], out var de);
          degree[segEndNode[i]] = de + 1;
        }
        for (int i = 0; i < segments.Count; i++)
        {
          if (removed.Contains(i)) continue;
          if (segStartNode[i] == segEndNode[i]) continue; // self-loop = closed segment
          if (degree.GetValueOrDefault(segStartNode[i]) == 1 ||
              degree.GetValueOrDefault(segEndNode[i]) == 1)
          {
            removed.Add(i);
            anyChange = true;
          }
        }
        trimRounds++;
      }

      for (int i = 0; i < segments.Count; i++)
      {
        if (removed.Contains(i)) continue;
        coreSegments.Add(segments[i]);
        coreOriginIdx.Add(segOriginIdx[i]);
      }
      Log.Write(EnglishName,
        $"  dead-end trimming: {trimRounds} rounds, {removed.Count} removed," +
        $" {coreSegments.Count} core segments remain");

      if (coreSegments.Count == 0)
      {
        Log.Write(EnglishName, "  no core segments after trimming → no boundaries");
      }
      else
      {
      Log.Write(EnglishName, $"  joining {coreSegments.Count} core segments...");

      // Join all segments; log ALL results, keep closed planar ones as boundaries.
      // If a result is nearly closed (gap < 100× model tol), bridge the gap with
      // a tiny line so small endpoint mismatches don't silently drop a boundary.
      var joined = Curve.JoinCurves(coreSegments.ToArray(), tol);
      var closingGapTol = tol * 100;

      if (joined == null || joined.Length == 0)
      {
        Log.Write(EnglishName, "  JoinCurves returned null/empty");
      }
      else
      {
        for (int k = 0; k < joined.Length; k++)
        {
          var j = joined[k];
          if (j == null) { Log.Write(EnglishName, $"  joined[{k}] null"); continue; }
          var jS = j.PointAtStart;
          var jE = j.PointAtEnd;
          var jGap = jS.DistanceTo(jE);

          // Close a small endpoint gap by bridging with a line segment.
          if (!j.IsClosed && jGap > 0 && jGap < closingGapTol)
          {
            var bridge = new LineCurve(jE, jS);
            var reclosed = Curve.JoinCurves(new Curve[] { j, bridge }, tol);
            if (reclosed?.Length == 1 && reclosed[0] != null && reclosed[0].IsClosed)
            {
              Log.Write(EnglishName, $"  joined[{k}] gap={jGap:G4} < closingTol={closingGapTol:G4} → bridged and closed");
              j = reclosed[0];
              jS = j.PointAtStart;
              jE = j.PointAtEnd;
            }
          }

          var hasPln = j.TryGetPlane(out var pln, tol);
          Log.Write(EnglishName,
            $"  joined[{k}] {j.GetType().Name} IsClosed={j.IsClosed} TryGetPlane={hasPln}" +
            $" start=({jS.X:F3},{jS.Y:F3},{jS.Z:F3})" +
            $" end=({jE.X:F3},{jE.Y:F3},{jE.Z:F3})" +
            $" gap={jS.DistanceTo(jE):G4}");
          if (j.IsClosed && hasPln)
            boundaries.Add((j, pln));
        }
      }
      } // end if (coreSegments.Count > 0)
    }

    Log.Write(EnglishName, $"  boundaries found: {boundaries.Count}");

    if (boundaries.Count == 0)
    {
      Log.Write(EnglishName, "No closed planar boundary found — see details above.");
      RhinoApp.WriteLine("vGroup: no closed planar boundary found in selection.");
      return Result.Nothing;
    }

    // Compute member sets for every boundary first, then filter.
    // Each physical region often produces two closed loops from JoinCurves:
    // a tight inner perimeter (just the boundary curves) and a larger outer
    // envelope (everything inside).  We keep only the outermost loop per
    // region by dropping any boundary whose member set is a strict subset
    // of another boundary's member set.

    var boundaryMembers = new List<HashSet<Guid>>(boundaries.Count);
    foreach (var (bound, plane) in boundaries)
    {
      var members = new HashSet<Guid>();

      // Curves whose segment midpoints are Coincident with this boundary.
      for (int k = 0; k < coreSegments.Count; k++)
      {
        var midPt = coreSegments[k].PointAt(coreSegments[k].Domain.Mid);
        if (bound.Contains(midPt, plane, tol) == PointContainment.Coincident)
          members.Add(allCurveIds[coreOriginIdx[k]]);
      }

      // All remaining selected objects whose representative point is inside.
      foreach (var id in allIds)
      {
        if (members.Contains(id)) continue;
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        var pt = RepresentativePoint(obj);
        if (pt == null) continue;
        if (bound.Contains(pt.Value, plane, tol) is PointContainment.Inside
                                                   or PointContainment.Coincident)
          members.Add(id);
      }

      boundaryMembers.Add(members);
    }

    // Drop subset boundaries and create groups for the survivors.
    int groupCount = 0;
    for (int i = 0; i < boundaries.Count; i++)
    {
      var mi = boundaryMembers[i];
      if (mi.Count < 2) continue;

      // Skip if every member of mi also appears in some larger boundary mj.
      bool isSubset = false;
      for (int j = 0; j < boundaries.Count; j++)
      {
        if (j == i) continue;
        var mj = boundaryMembers[j];
        if (mj.Count <= mi.Count) continue; // can't be a strict superset
        if (mi.IsSubsetOf(mj)) { isSubset = true; break; }
      }
      if (isSubset) continue;

      Log.Write(EnglishName, $"  boundary[{i}] members={mi.Count} → group");

      var grpIdx = doc.Groups.Add();
      foreach (var id in mi)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        obj.Attributes.AddToGroup(grpIdx);
        obj.CommitChanges();
      }
      _ourGroupIndices.Add(grpIdx);
      groupCount++;
    }

    if (groupCount == 0)
      RhinoApp.WriteLine("vGroup: no enclosed objects found.");
    else
      RhinoApp.WriteLine($"vGroup: created {groupCount} group{(groupCount == 1 ? "" : "s")}.");

    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Returns a single representative 3-D point for an object used to test
  /// containment inside a closed planar curve:
  /// <list type="bullet">
  ///   <item>Point object → its location.</item>
  ///   <item>Curve → mid-domain point.</item>
  ///   <item>Everything else → bounding-box centre.</item>
  /// </list>
  /// Returns <see langword="null"/> when no valid point can be determined.
  /// </summary>
  private static Point3d? RepresentativePoint(RhinoObject obj)
  {
    var geo = obj.Geometry;
    if (geo == null)
      return null;

    if (geo is Point pt)
      return pt.Location;

    if (geo is Curve c)
      return c.PointAt(c.Domain.Mid);

    var bbox = geo.GetBoundingBox(accurate: true);
    return bbox.IsValid ? bbox.Center : null;
  }
}
