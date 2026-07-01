using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands
{
  /// <summary>
  /// vMatch — click near an edge mate dot on a flat unrolled part;
  /// the neighbour part is moved and rotated so its mating edge aligns
  /// with the selected edge at the specified gap distance.
  /// Auto sub-mode assembles a whole selection via BFS (with optional
  /// RandStart / RandNext randomisation).
  /// </summary>
  public sealed class vMatch : Command
  {
    // ── Constants shared with vUnrollSrf / MultiUnroll2.py ────────────────
    internal const string EdgeMateName        = "MultiUnroll_EdgeMate";
    internal const string EdgeMateIdKey       = "MultiUnrollEdgeMateId";
    internal const string EdgePartNumKey      = "MultiUnrollPartNumber";
    internal const string EdgeMatePartNumKey  = "MultiUnrollMatePartNumber";
    internal const string EdgeMateReversedKey = "MultiUnrollMateReversed";

    // ── Persistent settings ───────────────────────────────────────────────
    private const string SectionName   = "vMatch";
    private const string KeyDist       = "distance";
    private const string KeyRandStart  = "randStart";
    private const string KeyRandNext   = "randNext";

    private static double _distance   = 2.0;
    private static bool   _randStart  = false;
    private static bool   _randNext   = false;

    private static readonly Random _rng = new Random();

    public override string EnglishName => "vMatch";

    // ── Dot record ─────────────────────────────────────────────────────────
    private sealed class Dot
    {
      public Guid    Id       { get; }
      public Point3d Position { get; set; }
      public string  MateId   { get; }
      public string  PartNum  { get; }
      public Dot(Guid id, Point3d pos, string mateId, string partNum)
      { Id = id; Position = pos; MateId = mateId; PartNum = partNum; }
    }

    // ── Entry point ────────────────────────────────────────────────────────
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      LoadSettings();
      double tol     = doc.ModelAbsoluteTolerance;
      double pickTol = Math.Max(5.0, Math.Min(100.0, tol * 5000.0));

      var dots = ScanDots(doc);
      if (dots.Count == 0)
      {
        RhinoApp.WriteLine("vMatch: no edge mate dots found — run vUnrollSrf with EdgeDots=On first");
        return Result.Nothing;
      }

      while (true)
      {
        var gp = new GetPoint();
        gp.SetCommandPrompt($"Click near an edge mate dot  Distance={_distance:G}");
        int idxDist = gp.AddOption("Distance", $"{_distance:G}");
        int idxAuto = gp.AddOption("Auto");
        gp.AcceptNothing(false);

        var res = gp.Get();
        if (gp.CommandResult() == Result.Cancel) break;

        if (res == GetResult.Option)
        {
          var opt = gp.Option();
          if (opt != null && opt.Index == idxAuto)
          {
            dots = AutoAlign(doc, dots, _distance);
            continue;
          }
          // Distance — use GetString sub-prompt (memory rule: no AddOptionDouble)
          var gs = new GetString();
          gs.SetCommandPrompt($"Gap distance <{_distance:G}>");
          gs.SetDefaultString($"{_distance:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                              NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
              && v >= 0.0)
          {
            _distance = v;
            SaveSettings();
          }
          continue;
        }

        if (gp.CommandResult() != Result.Success) break;

        var pickPt = gp.Point();
        SaveSettings();

        var src = dots.OrderBy(d => d.Position.DistanceTo(pickPt)).FirstOrDefault();
        if (src == null || src.Position.DistanceTo(pickPt) > pickTol) continue;

        var mate = dots.FirstOrDefault(d => d.MateId == src.MateId && d.Id != src.Id);
        if (mate == null) continue;

        int srcGrp  = GrpOf(doc, src.Id);
        int mateGrp = GrpOf(doc, mate.Id);
        if (mateGrp < 0) continue;

        var srcObjs  = srcGrp >= 0 ? ObjsInGrp(doc, srcGrp) : new List<Guid>();
        var mateObjs = ObjsInGrp(doc, mateGrp);
        if (mateObjs.Count == 0) continue;

        var srcTang  = Tang2d(src.Position,  NakedEdges(doc, srcObjs));
        var mateTang = Tang2d(mate.Position, NakedEdges(doc, mateObjs));
        if (srcTang == null || mateTang == null) continue;

        var srcOut = Outward2d(doc, src.Position, srcTang.Value, srcObjs);
        var target = new Point3d(src.Position.X + srcOut.X * _distance,
                                 src.Position.Y + srcOut.Y * _distance, 0.0);

        var xf = PlaceXform(doc, srcTang.Value, srcOut, target,
                             mate.Position, mateTang.Value, mateObjs);
        if (!xf.HasValue) continue;

        doc.Views.RedrawEnabled = false;
        try   { foreach (var id in mateObjs) doc.Objects.Transform(id, xf.Value, true); }
        finally { doc.Views.RedrawEnabled = true; }

        doc.Views.Redraw();
        dots = ScanDots(doc);
      }

      return Result.Success;
    }

    // ── Auto sub-mode — inner loop with persistent multi-selection ─────────
    private static List<Dot> AutoAlign(RhinoDoc doc, List<Dot> allDots, double distance)
    {
      var brepsFilt = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion;

      while (true)
      {
        // Pass 1: snapshot whatever is currently selected (instant, no prompt)
        var goPre = new GetObject();
        goPre.GeometryFilter  = brepsFilt;
        goPre.SubObjectSelect = false;
        goPre.GroupSelect     = true;
        goPre.EnablePreSelect(true, true);
        goPre.EnablePostSelect(false);
        goPre.GetMultiple(0, 0);
        var snapIds = new HashSet<Guid>(
            Enumerable.Range(0, goPre.ObjectCount).Select(i => goPre.Object(i).ObjectId));

        // Pass 2: interactive — add / remove parts, toggle options
        var optRs  = new OptionToggle(_randStart, "Off", "On");
        var optRn  = new OptionToggle(_randNext,  "Off", "On");

        var go = new GetObject();
        go.SetCommandPrompt(
            $"Add/remove parts → Enter=run  RandStart={(_randStart ? "On" : "Off")} RandNext={(_randNext ? "On" : "Off")}");
        go.GeometryFilter            = brepsFilt;
        go.SubObjectSelect           = false;
        go.GroupSelect               = true;
        go.EnablePreSelect(false, false);
        go.EnablePostSelect(true);
        go.AcceptNothing(true);
        go.EnableClearObjectsOnEntry(false);
        go.EnableUnselectObjectsOnExit(false);
        go.DeselectAllBeforePostSelect = false;
        go.AlreadySelectedObjectSelect = true;

        int idxBack = go.AddOption("Back");
        go.AddOptionToggle("RandStart", ref optRs);
        go.AddOptionToggle("RandNext",  ref optRn);

        bool goBack = false;
        while (true)
        {
          var ires = go.GetMultiple(0, 0);
          _randStart = optRs.CurrentValue;
          _randNext  = optRn.CurrentValue;

          if (go.CommandResult() == Result.Cancel)
          {
            doc.Objects.UnselectAll();
            doc.Views.Redraw();
            SaveSettings();
            return ScanDots(doc);
          }
          if (ires == GetResult.Option)
          {
            var opt = go.Option();
            if (opt != null && opt.Index == idxBack) { goBack = true; break; }
            continue;
          }
          break;
        }

        SaveSettings();

        if (goBack)
        {
          doc.Objects.UnselectAll();
          doc.Views.Redraw();
          return ScanDots(doc);
        }

        // XOR: objects clicked in both passes = user toggled them off
        var interIds = new HashSet<Guid>(
            Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId));
        var finalIds = new HashSet<Guid>(snapIds);
        finalIds.SymmetricExceptWith(interIds);

        // Sync Rhino selection to the XOR result (deselect the toggled-off ones)
        foreach (var id in snapIds.Intersect(interIds))
          doc.Objects.FindId(id)?.Select(false);

        if (finalIds.Count == 0) continue;

        // Collect group indices in first-seen order
        var selGrpList = new List<int>();
        var seenGrps   = new HashSet<int>();
        foreach (var id in finalIds)
        {
          int g = GrpOf(doc, id);
          if (g >= 0 && seenGrps.Add(g)) selGrpList.Add(g);
        }
        if (selGrpList.Count == 0) continue;

        var selGrpSet = new HashSet<int>(selGrpList);
        int rootGrp   = _randStart
          ? selGrpList[_rng.Next(selGrpList.Count)]
          : selGrpList[0];

        // mate_id → dots lookup
        var mateMap = new Dictionary<string, List<Dot>>();
        foreach (var d in allDots)
        {
          if (!mateMap.TryGetValue(d.MateId, out var lst))
            mateMap[d.MateId] = lst = new List<Dot>();
          lst.Add(d);
        }

        var placed = new HashSet<int> { rootGrp };
        var queue  = new List<int>    { rootGrp };
        // Working cached dot positions updated after each move
        var dotPts = allDots.ToDictionary(d => d.Id, d => d.Position);

        doc.Views.RedrawEnabled = false;
        try
        {
          while (queue.Count > 0)
          {
            int qi      = _randNext && queue.Count > 1 ? _rng.Next(queue.Count) : 0;
            int currGrp = queue[qi];
            queue.RemoveAt(qi);

            var currDots = allDots.Where(d => GrpOf(doc, d.Id) == currGrp).ToList();
            foreach (var src in currDots)
            {
              if (!mateMap.TryGetValue(src.MateId, out var mList)) continue;
              var mateInfo = mList.FirstOrDefault(m => m.Id != src.Id);
              if (mateInfo == null) continue;

              int mateGrp = GrpOf(doc, mateInfo.Id);
              if (!selGrpSet.Contains(mateGrp) || placed.Contains(mateGrp)) continue;

              var srcDotPt  = dotPts[src.Id];
              var srcObjs   = ObjsInGrp(doc, currGrp);
              var mateObjs  = ObjsInGrp(doc, mateGrp);
              if (srcObjs.Count == 0 || mateObjs.Count == 0) continue;

              var srcTang  = Tang2d(srcDotPt,           NakedEdges(doc, srcObjs));
              var mateTang = Tang2d(dotPts[mateInfo.Id], NakedEdges(doc, mateObjs));
              if (srcTang == null || mateTang == null) continue;

              var srcOut = Outward2d(doc, srcDotPt, srcTang.Value, srcObjs);
              var target = new Point3d(srcDotPt.X + srcOut.X * distance,
                                       srcDotPt.Y + srcOut.Y * distance, 0.0);

              var xf = PlaceXform(doc, srcTang.Value, srcOut, target,
                                   dotPts[mateInfo.Id], mateTang.Value, mateObjs);
              if (!xf.HasValue) continue;

              foreach (var id in mateObjs)
                doc.Objects.Transform(id, xf.Value, true);

              // Update cached positions for moved group's dots
              foreach (var d in allDots.Where(d2 => GrpOf(doc, d2.Id) == mateGrp))
              {
                var pt = dotPts[d.Id];
                pt.Transform(xf.Value);
                dotPts[d.Id] = pt;
              }

              placed.Add(mateGrp);
              queue.Add(mateGrp);
            }
          }
        }
        finally { doc.Views.RedrawEnabled = true; }

        doc.Views.Redraw();
        allDots = ScanDots(doc);
      }
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    private static List<Dot> ScanDots(RhinoDoc doc)
    {
      var result = new List<Dot>();
      foreach (var obj in doc.Objects)
      {
        if (obj.ObjectType != ObjectType.TextDot) continue;
        if (obj.Attributes.Name != EdgeMateName) continue;
        string mateId  = obj.Attributes.GetUserString(EdgeMateIdKey)  ?? string.Empty;
        string partNum = obj.Attributes.GetUserString(EdgePartNumKey) ?? string.Empty;
        if (string.IsNullOrEmpty(mateId)) continue;
        if (obj.Geometry is TextDot td)
          result.Add(new Dot(obj.Id, td.Point, mateId, partNum));
      }
      return result;
    }

    private static int GrpOf(RhinoDoc doc, Guid id)
    {
      var grps = doc.Objects.FindId(id)?.Attributes.GetGroupList();
      return grps != null && grps.Length > 0 ? grps[0] : -1;
    }

    private static List<Guid> ObjsInGrp(RhinoDoc doc, int grpIdx)
    {
      var ids = new List<Guid>();
      foreach (var obj in doc.Objects)
      {
        var grps = obj.Attributes.GetGroupList();
        if (grps != null && Array.IndexOf(grps, grpIdx) >= 0)
          ids.Add(obj.Id);
      }
      return ids;
    }

    private static List<Curve> NakedEdges(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var curves = new List<Curve>();
      foreach (var id in ids)
      {
        var obj  = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;
        foreach (var c in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          if (c != null) curves.Add(c);
      }
      return curves;
    }

    private static Point3d? AreaCentroid2d(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      double area = 0, wx = 0, wy = 0;
      foreach (var id in ids)
      {
        var obj  = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;
        var amp = AreaMassProperties.Compute(brep);
        if (amp == null || amp.Area <= 1e-12) continue;
        area += amp.Area;
        wx   += amp.Centroid.X * amp.Area;
        wy   += amp.Centroid.Y * amp.Area;
      }
      if (area > 1e-12)
        return new Point3d(wx / area, wy / area, 0.0);
      // Fallback: bbox average
      var bbox = BoundingBox.Empty;
      bool hasBox = false;
      foreach (var id in ids)
      {
        var bb = doc.Objects.FindId(id)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!bb.IsValid) continue;
        bbox.Union(bb);
        hasBox = true;
      }
      return hasBox ? bbox.Center : (Point3d?)null;
    }

    private static Vector3d? Tang2d(Point3d pt, IEnumerable<Curve> edges)
    {
      Curve? best = null;
      double bestT = 0, bestD = double.MaxValue;
      foreach (var crv in edges)
      {
        if (!crv.ClosestPoint(pt, out double t)) continue;
        double d = pt.DistanceTo(crv.PointAt(t));
        if (d < bestD) { bestD = d; best = crv; bestT = t; }
      }
      if (best == null) return null;
      var tang = best.TangentAt(bestT);
      double mag = Math.Sqrt(tang.X * tang.X + tang.Y * tang.Y);
      return mag > 1e-12 ? new Vector3d(tang.X / mag, tang.Y / mag, 0.0) : (Vector3d?)null;
    }

    /// <summary>
    /// Returns the perpendicular to <paramref name="tang"/> that points AWAY
    /// from the source brep interior.  Uses brep face containment as primary
    /// test; falls back to centroid direction.
    /// </summary>
    private static Vector3d Outward2d(RhinoDoc doc, Point3d dotPt, Vector3d tang, IEnumerable<Guid> srcIds)
    {
      double tx = tang.X, ty = tang.Y;
      var pa = new Vector3d(-ty,  tx, 0.0); // 90° CCW
      var pb = new Vector3d( ty, -tx, 0.0); // 90° CW

      double tol = doc.ModelAbsoluteTolerance;
      double eps = Math.Max(tol * 20.0, 2.0);
      var testA  = new Point3d(dotPt.X + pa.X * eps, dotPt.Y + pa.Y * eps, 0.0);
      var testB  = new Point3d(dotPt.X + pb.X * eps, dotPt.Y + pb.Y * eps, 0.0);

      foreach (var id in srcIds)
      {
        var obj = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;

        bool aIn = false, bIn = false;
        foreach (var face in brep.Faces)
        {
          TestFacePoint(face, testA, tol, ref aIn);
          TestFacePoint(face, testB, tol, ref bIn);
        }
        if (aIn && !bIn) return pb;
        if (bIn && !aIn) return pa;
      }

      // Fallback: centroid direction
      var centroid = AreaCentroid2d(doc, srcIds);
      if (!centroid.HasValue) return pa;
      var diff = dotPt - centroid.Value;
      return (-ty * diff.X + tx * diff.Y) >= 0.0 ? pa : pb;
    }

    private static void TestFacePoint(BrepFace face, Point3d pt, double tol, ref bool inside)
    {
      if (inside) return;
      if (!face.ClosestPoint(pt, out double u, out double v)) return;
      if (face.PointAt(u, v).DistanceTo(pt) > tol * 50.0) return;
      try
      {
        if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
          inside = true;
      }
      catch { }
    }

    /// <summary>
    /// Builds the rigid transform that moves <paramref name="mateDot"/> to
    /// <paramref name="target"/> and aligns the mate edge tangent to
    /// src_tang (antiparallel first, then parallel).  Picks the rotation
    /// that places the most of the mate's bbox volume on the outward side.
    /// </summary>
    private static Transform? PlaceXform(
        RhinoDoc doc,
        Vector3d srcTang, Vector3d srcOut, Point3d target,
        Point3d mateDot, Vector3d mateTang, List<Guid> mateIds)
    {
      Transform? best = null;
      double bestScore = double.MinValue;

      foreach (var (fx, fy) in new[] { (-srcTang.X, -srcTang.Y), (srcTang.X, srcTang.Y) })
      {
        double mx = mateTang.X, my = mateTang.Y;
        double angle = Math.Atan2(mx * fy - my * fx, mx * fx + my * fy);
        var xf = Transform.Translation(target - mateDot)
               * Transform.Rotation(angle, Vector3d.ZAxis, mateDot);

        double score = 0.0;
        foreach (var id in mateIds)
        {
          var bb = doc.Objects.FindId(id)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
          if (!bb.IsValid) continue;
          foreach (var corner in bb.GetCorners())
          {
            var pt = corner;
            pt.Transform(xf);
            var d = pt - target;
            score += d.X * srcOut.X + d.Y * srcOut.Y;
          }
        }
        if (score > bestScore) { bestScore = score; best = xf; }
      }
      return best;
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private static void LoadSettings()
    {
      ToolsOptionStore.Read<int>(SectionName, s =>
      {
        if (ToolsOptionStore.TryGetDouble(s, KeyDist,      out var d))  _distance  = d;
        if (ToolsOptionStore.TryGetDouble(s, KeyRandStart, out var rs)) _randStart = rs > 0.5;
        if (ToolsOptionStore.TryGetDouble(s, KeyRandNext,  out var rn)) _randNext  = rn > 0.5;
        return 0;
      });
    }

    private static void SaveSettings()
    {
      ToolsOptionStore.Update(SectionName, s =>
      {
        s[KeyDist]      = _distance;
        s[KeyRandStart] = _randStart ? 1.0 : 0.0;
        s[KeyRandNext]  = _randNext  ? 1.0 : 0.0;
      });
    }
  }
}
