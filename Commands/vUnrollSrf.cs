using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

// Native RhinoCommon C# conversion of MultiUnroll_preselect_other_v26_compact_rewrite.py
// Drop this file into an existing Rhino plug-in project and change the namespace if needed.
// Command name: vUnrollSrf
// Modified 2026.06.29: nullable-warning cleanup and Rhino 8 TextEntity API update.
// Modified 2026.06.29: fixed layout advance so unrolled parts move along row X baseline instead of diagonal top-right drift.
// Modified 2026.06.29: restores the exact pre-command selection after finish/cancel, without changing the working object-add path.
// Modified 2026.06.29 19:27:58: synchronized label height/rotation frame with Python script; height uses raw surface-frame Y axis, not projected label-up point.

namespace vTools.Commands
{
  public class vUnrollSrf : Command
  {
    public override string EnglishName => "vUnrollSrf";

    private const string TextLayerName = "Reference";
    private const string TextObjectName = "MultiUnroll_NumberLabel";
    private const string LabelNumberKey = "MultiUnrollLabelNumber";
    private const string FlatGroupPrefix = "MultiUnroll_Flat";
    private const string OriginalGroupPrefix = "MultiUnroll_Original";

    private const string TextFont = "Arial";
    private const double TextHeightScale = 1.5;
    private const double TextLiftRatio = 0.001;
    private const double TextUpStepRatio = 2.5;
    private const int TextBoundarySamples = 7;
    private const bool TextMarkSixNine = true;

    private const double FollowingTolFactor = 100.0;
    private const double FollowingDiagFactor = 1.0e-4;
    private const int FollowingCurveSamples = 9;

    private enum LabelMode
    {
      Text = 0,
      Dots = 1,
      None = 2
    }

    private static readonly string[] LabelModeNames = { "Text", "Dots", "None" };

    // Session-sticky settings. If you want persistence across Rhino restarts, move these into your plug-in settings.
    private static LabelMode _labelMode = LabelMode.Text;
    private static bool _rotateFlatParts = true;
    private static bool _explode = false;
    private static bool _keepProperties = false;
    private static double _layoutSpacing = 1.0;
    private static double _xExtents = 0.0;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      var startIds = SelectedIds(doc);
      var surfaceIds = GetSurfaceIds(doc, startIds.Where(IsSurfaceLikeId).ToList());
      if (surfaceIds == null || surfaceIds.Count == 0)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var followingIds = GetFollowingIds(doc, startIds.Where(IsFollowingLikeId).ToList(), surfaceIds);
      if (followingIds == null)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var options = GetLayoutOptions(doc, "Start point for unrolls - press Enter for world 0");
      if (options == null)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      _labelMode = options.LabelMode;
      _rotateFlatParts = options.RotateFlatParts;
      _explode = options.Explode;
      _keepProperties = options.KeepProperties;
      _layoutSpacing = options.LayoutSpacing;
      _xExtents = options.XExtents;

      var sources = new List<SourceSurface>();
      foreach (var id in surfaceIds)
      {
        var rhObj = doc.Objects.FindId(id);
        if (rhObj == null)
          continue;
        var brep = BrepFromGeometry(rhObj.Geometry);
        if (brep == null)
          continue;
        sources.Add(new SourceSurface(id, rhObj.Geometry, brep));
      }

      if (sources.Count == 0)
      {
        RestoreSelection(doc, startIds);
        return Result.Nothing;
      }

      var addText = _labelMode == LabelMode.Text;
      var addDots = _labelMode == LabelMode.Dots;
      var addLabels = _labelMode != LabelMode.None;

      double? sharedHeight = addText ? SharedTextHeight(doc, sources.Select(s => s.Id)) : (double?)null;
      var followingItems = MakeFollowingItems(doc, followingIds);
      var assignment = followingItems.Count > 0 ? AssignFollowing(doc, followingItems, sources) : new AssignmentResult(sources.Count);

      double tol = doc.ModelAbsoluteTolerance;
      double xLimit = _xExtents;
      double xOrigin = options.StartPoint.X;
      double rowY = options.StartPoint.Y;
      double rowHeight = 0.0;
      var nextPoint = options.StartPoint;
      bool exceeded = false;
      int done = 0;
      int failed = 0;

      doc.Views.RedrawEnabled = false;
      try
      {
        for (int i = 0; i < sources.Count; i++)
        {
          var src = sources[i];
          var unroller = new Unroller(src.Brep) { ExplodeOutput = _explode };

          int number = done + 1;
          string display = LabelText(number);
          var frame = (addLabels || _rotateFlatParts)
            ? SurfaceLabelFrame(doc, src.Id, sharedHeight ?? HeightCandidate(doc, src.Id))
            : null;

          var surfaceItems = i < assignment.Buckets.Count ? assignment.Buckets[i] : new List<FollowingItem>();
          var curves = surfaceItems.Where(x => x.Kind == FollowingKind.Curve).Select(x => x.Geometry).OfType<Curve>().ToList();
          var points = surfaceItems.Where(x => x.Kind == FollowingKind.Point).Select(x => x.Geometry).OfType<Point>().ToList();
          var dots = surfaceItems.Where(x => x.Kind == FollowingKind.Dot).Select(x => x.Geometry).OfType<TextDot>().ToList();

          foreach (var curve in curves)
            unroller.AddFollowingGeometry(curve);
          foreach (var point in points)
            unroller.AddFollowingGeometry(point.Location);

          int labelPointIndex = -1;
          int labelUpIndex = -1;
          if (frame != null)
          {
            labelPointIndex = points.Count;
            unroller.AddFollowingGeometry(frame.Point);
            labelUpIndex = points.Count + 1;
            unroller.AddFollowingGeometry(frame.UpPoint);
          }

          foreach (var dot in dots)
            unroller.AddFollowingGeometry(dot);

          Curve[] unrolledCurves;
          Point3d[] unrolledPoints;
          TextDot[] unrolledDots;
          Brep[] unrolledBreps = unroller.PerformUnroll(out unrolledCurves, out unrolledPoints, out unrolledDots);
          if (unrolledBreps == null || unrolledBreps.Length == 0)
          {
            failed++;
            continue;
          }

          done++;

          var outputIds = new List<Guid>();
          var finalBreps = _explode ? unrolledBreps : (Brep.JoinBreps(unrolledBreps, tol) ?? unrolledBreps);
          foreach (var brep in finalBreps)
            AddValid(outputIds, doc.Objects.AddBrep(brep));

          if (unrolledCurves != null)
          {
            foreach (var curve in unrolledCurves)
              AddValid(outputIds, doc.Objects.AddCurve(curve));
          }

          Point3d? labelPoint = null;
          Point3d? labelUp = null;
          var hiddenPointIndexes = new HashSet<int>();
          if (unrolledPoints != null)
          {
            if (labelPointIndex >= 0 && labelPointIndex < unrolledPoints.Length)
            {
              labelPoint = unrolledPoints[labelPointIndex];
              hiddenPointIndexes.Add(labelPointIndex);
            }
            if (labelUpIndex >= 0 && labelUpIndex < unrolledPoints.Length)
            {
              labelUp = unrolledPoints[labelUpIndex];
              hiddenPointIndexes.Add(labelUpIndex);
            }
            for (int p = 0; p < unrolledPoints.Length; p++)
            {
              if (!hiddenPointIndexes.Contains(p))
                AddValid(outputIds, doc.Objects.AddPoint(unrolledPoints[p]));
            }
          }

          if (unrolledDots != null)
          {
            foreach (var dot in unrolledDots)
              AddValid(outputIds, doc.Objects.AddTextDot(dot));
          }

          Vector3d unrolledY = Vector3d.YAxis;
          if (labelPoint.HasValue && labelUp.HasValue)
            unrolledY = labelUp.Value - labelPoint.Value;
          else if (frame != null)
            unrolledY = frame.Y;

          var unrolledLabelIds = new List<Guid>();
          if (frame != null && labelPoint.HasValue)
          {
            if (addText)
            {
              var normal = NormalFromOutputBreps(finalBreps, labelPoint.Value);
              AddValid(unrolledLabelIds, AddFlatText(doc, display, labelPoint.Value, unrolledY, normal, frame.Height, src.Id, _keepProperties));
            }
            else if (addDots)
            {
              AddValid(unrolledLabelIds, AddDot(doc, display, labelPoint.Value, outputIds.FirstOrDefault(), _keepProperties));
            }
            outputIds.AddRange(unrolledLabelIds.Where(IsValidId));
          }

          if (_keepProperties)
          {
            TransferAttributes(doc, outputIds, src.Id);
            PutOnReferenceLayer(doc, unrolledLabelIds);
          }

          if (addLabels && frame != null)
          {
            Guid originalLabelId = Guid.Empty;
            if (addText)
              originalLabelId = AddFlatText(doc, display, frame.Point, frame.Y, frame.Normal, frame.Height, src.Id, _keepProperties);
            else if (addDots)
              originalLabelId = AddDot(doc, display, frame.Point, src.Id, _keepProperties);

            if (IsValidId(originalLabelId))
              GroupObjects(doc, new[] { src.Id, originalLabelId }, OriginalGroupPrefix, number);
          }

          GroupObjects(doc, outputIds, FlatGroupPrefix, number);

          if (_rotateFlatParts && frame != null && labelPoint.HasValue)
            RotateObjectsToTextUp(doc, outputIds, labelPoint.Value, unrolledY);

          var bbox = BoundingBoxOfObjects(doc, outputIds);
          if (bbox.HasValue && bbox.Value.IsValid)
          {
            var box = bbox.Value;
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;

            if (xLimit > 0)
            {
              if (width > xLimit)
              {
                xLimit = width;
                exceeded = true;
              }

              bool rowHasObjects = nextPoint.X > xOrigin + RhinoMath.ZeroTolerance;
              if (rowHasObjects && nextPoint.X + width > xOrigin + xLimit)
              {
                rowY += rowHeight + _layoutSpacing;
                rowHeight = 0.0;
                nextPoint = new Point3d(xOrigin, rowY, options.StartPoint.Z);
              }

              rowHeight = Math.Max(rowHeight, height);
            }

            var target = new Point3d(nextPoint.X, rowY, options.StartPoint.Z);
            var move = target - box.Min;
            TransformObjects(doc, outputIds, Transform.Translation(move));

            // Advance along the row baseline only. Do not use box.Max.Y here, or every part
            // starts from the previous part's top-right corner and the layout drifts diagonally.
            nextPoint = new Point3d(target.X + width + _layoutSpacing, rowY, options.StartPoint.Z);
          }
        }
      }
      finally
      {
        doc.Views.RedrawEnabled = true;
        RestoreSelection(doc, startIds);
        doc.Views.Redraw();
      }

      _xExtents = xLimit;

      if (exceeded)
      {
        RhinoApp.WriteLine("At least one unrolled object exceeded the X extents limit. Limit extended to {0:0.##} {1}.",
          xLimit, doc.ModelUnitSystem);
      }

      var msg = $"Successfully unrolled {done} objects";
      if (failed > 0)
        msg += $" | Unable to unroll {failed} objects";
      if (assignment.Skipped > 0)
        msg += $" | Skipped {assignment.Skipped} following object(s) not close to selected surfaces";
      if (sharedHeight.HasValue)
        msg += $" | Shared number text height {sharedHeight.Value:0.###}";
      msg += $" | Labels {_labelMode} | RotateFlatParts {(_rotateFlatParts ? "Yes" : "No")}";
      RhinoApp.WriteLine(msg);

      return done > 0 ? Result.Success : Result.Nothing;
    }

    private static List<Guid>? GetSurfaceIds(RhinoDoc doc, List<Guid> preselected)
    {
      preselected = Unique(preselected.Where(IsSurfaceLikeId));
      if (preselected.Count > 0)
        return preselected;

      var go = new GetObject();
      go.SetCommandPrompt("Select surface/polysurface objects to unroll");
      go.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
      go.SubObjectSelect = false;
      go.GroupSelect = true;
      go.EnablePreSelect(true, true);
      go.EnablePostSelect(true);
      var shared = AddSharedOptions(go);

      while (true)
      {
        var rc = go.GetMultiple(1, 0);
        if (rc == GetResult.Cancel || go.CommandResult() == Result.Cancel)
          return null;
        if (rc == GetResult.Option)
        {
          HandleSharedOption(go, shared);
          continue;
        }
        if (go.CommandResult() != Result.Success)
          return null;
        return Unique(Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId).Where(IsSurfaceLikeId));
      }
    }

    private static List<Guid>? GetFollowingIds(RhinoDoc doc, List<Guid> seedIds, List<Guid> surfaceIds)
    {
      seedIds = Unique(seedIds.Where(IsFollowingLikeId));
      var surfaceSet = new HashSet<Guid>(surfaceIds);

      SelectOnly(doc, seedIds);
      Highlight(doc, surfaceIds, true);

      var go = new GetObject();
      go.SetCommandPrompt("Select curves, points, or dots to unroll with highlighted surfaces. Press Enter when done");
      go.GeometryFilter = ObjectType.Curve | ObjectType.Point | ObjectType.TextDot;
      go.SubObjectSelect = false;
      go.GroupSelect = true;
      go.AcceptNothing(true);
      go.EnablePreSelect(true, true);
      go.EnablePostSelect(true);
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.AlreadySelectedObjectSelect = true;
      var shared = AddSharedOptions(go);

      try
      {
        while (true)
        {
          var rc = go.GetMultiple(0, 0);
          if (rc == GetResult.Cancel || go.CommandResult() == Result.Cancel)
            return null;
          if (rc == GetResult.Option)
          {
            HandleSharedOption(go, shared);
            continue;
          }
          if (go.ObjectsWerePreselected)
          {
            go.EnablePreSelect(false, true);
            continue;
          }
          break;
        }

        for (int i = 0; i < go.ObjectCount; i++)
        {
          var obj = go.Object(i).Object();
          obj?.Select(true);
        }

        return Unique(SelectedIds(doc).Where(id => !surfaceSet.Contains(id) && IsFollowingLikeId(id)));
      }
      finally
      {
        Highlight(doc, surfaceIds, false);
      }
    }

    private static LayoutOptions? GetLayoutOptions(RhinoDoc doc, string prompt)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt(prompt);
      var shared = AddSharedOptions(gp);
      var optExplode = new OptionToggle(_explode, "No", "Yes");
      var optProps = new OptionToggle(_keepProperties, "No", "Yes");
      var optSpacing = new OptionDouble(_layoutSpacing, true, 0.0);
      var optXLimit = new OptionDouble(_xExtents, true, 0.0);
      gp.AddOptionToggle("Explode", ref optExplode);
      gp.AddOptionToggle("KeepProperties", ref optProps);
      gp.AddOptionDouble("LayoutSpacing", ref optSpacing);
      gp.AddOptionDouble("XExtents", ref optXLimit);
      gp.AcceptNothing(true);

      var point = Point3d.Origin;
      while (true)
      {
        var rc = gp.Get();
        HandleSharedOption(gp, shared);
        if (gp.CommandResult() == Result.Cancel)
          return null;
        if (gp.CommandResult() == Result.Nothing)
          break;
        if (rc == GetResult.Point)
        {
          point = gp.Point();
          break;
        }
        if (rc == GetResult.Option)
          continue;
        break;
      }

      return new LayoutOptions
      {
        StartPoint = point,
        LabelMode = _labelMode,
        RotateFlatParts = _rotateFlatParts,
        Explode = optExplode.CurrentValue,
        KeepProperties = optProps.CurrentValue,
        LayoutSpacing = optSpacing.CurrentValue,
        XExtents = optXLimit.CurrentValue
      };
    }

    private class SharedOptions
    {
      public int LabelIndex = -1;
      public OptionToggle? RotateOption;
    }

    private static SharedOptions AddSharedOptions(GetBaseClass getter)
    {
      var state = new SharedOptions();
      state.LabelIndex = getter.AddOptionList("Labels", LabelModeNames, (int)_labelMode);
      state.RotateOption = new OptionToggle(_rotateFlatParts, "No", "Yes");
      getter.AddOptionToggle("RotateFlatParts", ref state.RotateOption);
      return state;
    }

    private static void HandleSharedOption(GetBaseClass getter, SharedOptions state)
    {
      if (state == null)
        return;

      if (state.RotateOption != null)
        _rotateFlatParts = state.RotateOption.CurrentValue;

      var option = getter.Option();
      if (option != null && option.Index == state.LabelIndex)
      {
        int idx = option.CurrentListOptionIndex;
        if (idx >= 0 && idx < LabelModeNames.Length)
          _labelMode = (LabelMode)idx;
      }
    }

    private static List<Guid> SelectedIds(RhinoDoc doc)
    {
      return doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
    }

    private static void SelectOnly(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      doc.Objects.UnselectAll();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Select(true);
      }
      doc.Views.Redraw();
    }

    private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      doc.Objects.UnselectAll();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Select(true);
      }
    }

    private static void Highlight(RhinoDoc doc, IEnumerable<Guid> ids, bool state)
    {
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Highlight(state);
      }
      doc.Views.Redraw();
    }

    private static bool IsSurfaceLikeId(Guid id)
    {
      var obj = RhinoDoc.ActiveDoc?.Objects.FindId(id);
      return obj != null && IsSurfaceLike(obj.ObjectType);
    }

    private static bool IsFollowingLikeId(Guid id)
    {
      var obj = RhinoDoc.ActiveDoc?.Objects.FindId(id);
      return obj != null && IsFollowingLike(obj.ObjectType);
    }

    private static bool IsSurfaceLike(ObjectType type)
    {
      return type == ObjectType.Surface || type == ObjectType.Brep || type == ObjectType.Extrusion;
    }

    private static bool IsFollowingLike(ObjectType type)
    {
      return type == ObjectType.Curve || type == ObjectType.Point || type == ObjectType.TextDot;
    }

    private static bool IsValidId(Guid id)
    {
      return id != Guid.Empty;
    }

    private static void AddValid(List<Guid> ids, Guid id)
    {
      if (IsValidId(id))
        ids.Add(id);
    }

    private static List<T> Unique<T>(IEnumerable<T> values)
    {
      var seen = new HashSet<T>();
      var output = new List<T>();
      if (values == null)
        return output;
      foreach (var value in values)
      {
        if (seen.Add(value))
          output.Add(value);
      }
      return output;
    }

    private static Brep? BrepFromGeometry(GeometryBase? geometry)
    {
      if (geometry is Brep brep)
        return brep;
      if (geometry is Extrusion extrusion)
        return extrusion.ToBrep();
      if (geometry is Surface surface)
        return surface.ToBrep();
      return null;
    }

    private static double VectorLength(Vector3d v)
    {
      return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    private static Vector3d? Unit(Vector3d v, double tol)
    {
      if (VectorLength(v) <= tol)
        return null;
      if (v.Unitize())
        return v;
      return null;
    }

    private static Vector3d? ProjectToPlane(Vector3d v, Vector3d normal, double tol)
    {
      var n = Unit(normal, tol);
      if (!n.HasValue)
        return Unit(v, tol);
      var projected = v - n.Value * (v * n.Value);
      return Unit(projected, tol);
    }

    private static Vector3d ClosestNormal(Brep brep, Point3d point, Vector3d fallback, double tol)
    {
      if (brep == null)
        return fallback;

      Vector3d? best = null;
      double bestDistance = double.MaxValue;
      foreach (var face in brep.Faces)
      {
        double u, v;
        if (!face.ClosestPoint(point, out u, out v))
          continue;
        var facePoint = face.PointAt(u, v);
        var distance = point.DistanceToSquared(facePoint);
        var normal = Unit(face.NormalAt(u, v), tol);
        if (normal.HasValue && distance < bestDistance)
        {
          best = normal.Value;
          bestDistance = distance;
        }
      }
      return best ?? fallback;
    }

    private static Vector3d NormalFromOutputBreps(IEnumerable<Brep> breps, Point3d point)
    {
      var tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? RhinoMath.ZeroTolerance;
      Vector3d bestNormal = Vector3d.ZAxis;
      double bestDistance = double.MaxValue;
      foreach (var brep in breps ?? Enumerable.Empty<Brep>())
      {
        var normal = ClosestNormal(brep, point, Vector3d.ZAxis, tol);
        double distance;
        try { distance = point.DistanceToSquared(brep.ClosestPoint(point)); }
        catch { distance = 0.0; }
        if (distance < bestDistance)
        {
          bestDistance = distance;
          bestNormal = normal;
        }
      }
      return bestNormal;
    }

    private static Point3d? LabelPoint(RhinoDoc doc, Guid objId)
    {
      var obj = doc.Objects.FindId(objId);
      if (obj == null)
        return null;
      var brep = BrepFromGeometry(obj.Geometry);
      Point3d point;
      if (brep != null)
      {
        var area = AreaMassProperties.Compute(brep);
        if (area != null)
          point = area.Centroid;
        else
          point = brep.GetBoundingBox(true).Center;

        try { return brep.ClosestPoint(point); }
        catch { return point; }
      }

      var bbox = obj.Geometry.GetBoundingBox(true);
      return bbox.IsValid ? bbox.Center : (Point3d?)null;
    }

    private static IEnumerable<Point3d> CurveSamples(Curve curve, int count)
    {
      var pts = new List<Point3d>();
      if (curve == null)
        return pts;
      try
      {
        var dom = curve.Domain;
        int n = Math.Max(count, 2);
        for (int i = 0; i < n; i++)
        {
          double t = dom.T0 + (dom.T1 - dom.T0) * i / (double)(n - 1);
          pts.Add(curve.PointAt(t));
        }
      }
      catch
      {
        pts.Add(curve.PointAtStart);
        pts.Add(curve.PointAtEnd);
      }
      return pts;
    }

    private static List<Point3d> BoundaryPoints(RhinoDoc doc, Guid objId)
    {
      var pts = new List<Point3d>();
      var obj = doc.Objects.FindId(objId);
      if (obj == null)
        return pts;

      var brep = BrepFromGeometry(obj.Geometry);
      if (brep != null)
      {
        pts.AddRange(brep.Vertices.Select(v => v.Location));
        foreach (var curve in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          pts.AddRange(CurveSamples(curve, TextBoundarySamples));
      }

      if (pts.Count == 0)
      {
        var bbox = obj.Geometry.GetBoundingBox(true);
        if (bbox.IsValid)
          pts.AddRange(bbox.GetCorners());
      }
      return pts;
    }

    private static Tuple<Vector3d, Vector3d, Vector3d> FrameAxes(RhinoDoc doc, Guid objId, Point3d point)
    {
      var brep = BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      var tol = doc.ModelAbsoluteTolerance;
      var normal = brep != null
        ? ClosestNormal(brep, point, Vector3d.ZAxis, tol)
        : Vector3d.ZAxis;
      var y = ProjectToPlane(Vector3d.ZAxis, normal, tol)
              ?? ProjectToPlane(Vector3d.YAxis, normal, tol)
              ?? Vector3d.YAxis;
      var x = Unit(Vector3d.CrossProduct(y, normal), tol) ?? Vector3d.XAxis;
      y = Unit(y, tol) ?? Vector3d.YAxis;
      normal = Unit(normal, tol) ?? Vector3d.ZAxis;
      return Tuple.Create(x, y, normal);
    }

    private static LabelFrame? SurfaceLabelFrame(RhinoDoc doc, Guid objId, double height)
    {
      var point = LabelPoint(doc, objId);
      if (!point.HasValue)
        return null;

      var brep = BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      var tol = doc.ModelAbsoluteTolerance;
      var axes = FrameAxes(doc, objId, point.Value);
      var y = axes.Item2;
      var normal = axes.Item3;

      var step = Math.Max(height * TextUpStepRatio, tol * 20.0);
      var upGuess = point.Value + y * step;
      var upPoint = upGuess;
      if (brep != null)
      {
        try
        {
          var closest = brep.ClosestPoint(upGuess);
          if (closest != Point3d.Unset && closest.DistanceTo(point.Value) > tol * 2.0)
            upPoint = closest;
        }
        catch { }
      }

      var actualY = Unit(upPoint - point.Value, tol) ?? y;
      return new LabelFrame(point.Value, upPoint, actualY, normal, height);
    }

    private static double CenteredSpan(IEnumerable<double> values)
    {
      var list = values.ToList();
      if (list.Count == 0)
        return 0.0;
      double min = list.Min();
      double max = list.Max();
      if (min < 0.0 && max > 0.0)
        return 2.0 * Math.Min(Math.Abs(min), Math.Abs(max));
      return max - min;
    }

    private static double HeightCandidate(RhinoDoc doc, Guid objId)
    {
      var point = LabelPoint(doc, objId);
      var pts = BoundaryPoints(doc, objId);
      if (!point.HasValue || pts.Count == 0)
      {
        var bbox = doc.Objects.FindId(objId)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!bbox.IsValid)
          return 1.0;
        var spans = new[] { bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y, bbox.Max.Z - bbox.Min.Z }
          .Where(s => s > doc.ModelAbsoluteTolerance).ToList();
        return spans.Count > 0 ? spans.Min() * 0.04 : 1.0;
      }

      var axes = FrameAxes(doc, objId, point.Value);
      var ys = pts.Select(p => (p - point.Value) * axes.Item2);
      var span = CenteredSpan(ys);
      return span > doc.ModelAbsoluteTolerance ? span * 0.55 : 1.0;
    }

    private static double SharedTextHeight(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var values = Unique(ids).Select(id => HeightCandidate(doc, id)).Where(v => v > doc.ModelAbsoluteTolerance).ToList();
      var baseHeight = values.Count > 0 ? values.Min() : 1.0;
      return Math.Max(baseHeight * TextHeightScale, doc.ModelAbsoluteTolerance * 10.0);
    }

    private static Plane TextPlane(Point3d origin, Vector3d yDirection, Vector3d normal, double tol)
    {
      normal = Unit(normal, tol) ?? Vector3d.ZAxis;
      var y = ProjectToPlane(yDirection, normal, tol) ?? Vector3d.YAxis;
      var x = Unit(Vector3d.CrossProduct(y, normal), tol) ?? Vector3d.XAxis;
      return new Plane(origin, x, y);
    }

    private static Guid AddFlatText(RhinoDoc doc, string text, Point3d point, Vector3d yDirection, Vector3d normal, double height, Guid sourceId, bool transfer)
    {
      var n = Unit(normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
      var lift = Math.Max(height * TextLiftRatio, doc.ModelAbsoluteTolerance * 2.0);
      var plane = TextPlane(point + n * lift, yDirection, n, doc.ModelAbsoluteTolerance);
      var attrs = LabelAttributes(doc, sourceId, transfer, text);

      try
      {
        var te = new TextEntity
        {
          PlainText = text,
          Plane = plane,
          TextHeight = height,
          Justification = TextJustification.MiddleCenter,
          Font = Font.FromQuartetProperties(TextFont, false, false)
        };
        return doc.Objects.AddText(te, attrs);
      }
      catch
      {
        // Older RhinoCommon fallback.
        return doc.Objects.AddText(text, plane, height, TextFont, false, false, attrs);
      }
    }

    private static Guid AddDot(RhinoDoc doc, string text, Point3d point, Guid sourceId, bool transfer)
    {
      var attrs = LabelAttributes(doc, sourceId, transfer, text);
      return doc.Objects.AddTextDot(text, point, attrs);
    }

    private static ObjectAttributes LabelAttributes(RhinoDoc doc, Guid sourceId, bool transfer, string labelText)
    {
      ObjectAttributes? attrs = null;
      if (transfer && IsValidId(sourceId))
        attrs = doc.Objects.FindId(sourceId)?.Attributes.Duplicate();
      attrs ??= new ObjectAttributes();
      attrs.Name = TextObjectName;
      attrs.LayerIndex = ReferenceLayerIndex(doc);
      attrs.SetUserString(LabelNumberKey, BaseNumber(labelText));
      return attrs;
    }

    private static string BaseNumber(object? text)
    {
      var s = text?.ToString()?.Trim() ?? string.Empty;
      if (s.EndsWith(".", StringComparison.Ordinal))
        s = s.Substring(0, s.Length - 1).Trim();
      return s;
    }

    private static string LabelText(int number)
    {
      var text = number.ToString();
      if (TextMarkSixNine && text.Length > 0 && text.All(ch => ch == '6' || ch == '9'))
      {
        var chars = text.Reverse().Select(ch => ch == '6' ? '9' : '6').ToArray();
        var rotated = new string(chars);
        if (rotated != text)
          return text + ".";
      }
      return text;
    }

    private static int ReferenceLayerIndex(RhinoDoc doc)
    {
      var existing = doc.Layers.FindName(TextLayerName);
      if (existing != null)
        return existing.Index;
      var layer = new Layer { Name = TextLayerName };
      int index = doc.Layers.Add(layer);
      return index >= 0 ? index : doc.Layers.CurrentLayerIndex;
    }

    private static void PutOnReferenceLayer(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      int layer = ReferenceLayerIndex(doc);
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.LayerIndex = layer;
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static void TransferAttributes(RhinoDoc doc, IEnumerable<Guid> targetIds, Guid sourceId)
    {
      var source = doc.Objects.FindId(sourceId);
      if (source == null)
        return;
      foreach (var id in Unique(targetIds))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.LayerIndex = source.Attributes.LayerIndex;
        attrs.ObjectColor = source.Attributes.ObjectColor;
        attrs.ColorSource = source.Attributes.ColorSource;
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static void SetMatchNumber(RhinoDoc doc, IEnumerable<Guid> ids, object? number)
    {
      var value = BaseNumber(number);
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.SetUserString(LabelNumberKey, value);
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static string UniqueGroupName(RhinoDoc doc, string prefix, object? number)
    {
      string baseName = $"{prefix}_{BaseNumber(number)}";
      string name = baseName;
      int index = 2;
      while (doc.Groups.FindName(name) != null)
        name = $"{baseName}_{index++}";
      return name;
    }

    private static int GroupObjects(RhinoDoc doc, IEnumerable<Guid> ids, string prefix, object? number)
    {
      var list = Unique(ids).Where(IsValidId).ToList();
      if (list.Count == 0)
        return -1;
      SetMatchNumber(doc, list, number);
      int groupIndex = doc.Groups.Add(UniqueGroupName(doc, prefix, number));
      if (groupIndex >= 0)
      {
        foreach (var id in list)
          doc.Groups.AddToGroup(groupIndex, id);
      }
      return groupIndex;
    }

    private static List<FollowingItem> MakeFollowingItems(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var items = new List<FollowingItem>();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        if (obj.ObjectType == ObjectType.Curve && obj.Geometry is Curve curve)
          items.Add(new FollowingItem(id, FollowingKind.Curve, curve));
        else if (obj.ObjectType == ObjectType.Point && obj.Geometry is Point point)
          items.Add(new FollowingItem(id, FollowingKind.Point, point));
        else if (obj.ObjectType == ObjectType.TextDot && obj.Geometry is TextDot dot)
          items.Add(new FollowingItem(id, FollowingKind.Dot, dot));
      }
      return items;
    }

    private static AssignmentResult AssignFollowing(RhinoDoc doc, List<FollowingItem> items, List<SourceSurface> surfaces)
    {
      var result = new AssignmentResult(surfaces.Count);
      foreach (var item in items)
      {
        int bestIndex = -1;
        Tuple<double, double>? bestScore = null;
        double bestLimit = 0.0;

        for (int i = 0; i < surfaces.Count; i++)
        {
          Tuple<double, double>? score = item.Kind == FollowingKind.Curve
            ? (item.Geometry is Curve curve ? CurveScore(surfaces[i].Brep, curve) : null)
            : PointScore(surfaces[i].Brep, FollowingPoint(item));

          if (score == null)
            continue;
          if (bestScore == null || CompareScore(score, bestScore) < 0)
          {
            bestIndex = i;
            bestScore = score;
            bestLimit = AssignTolerance(doc, surfaces[i].Geometry, item.Geometry);
          }
        }

        if (bestIndex < 0 || bestScore == null || bestScore.Item1 > bestLimit)
        {
          result.Skipped++;
          continue;
        }

        var assigned = item;
        if (item.Kind == FollowingKind.Point || item.Kind == FollowingKind.Dot)
        {
          var p = FollowingPoint(item);
          if (p.HasValue)
          {
            try
            {
              var cp = surfaces[bestIndex].Brep.ClosestPoint(p.Value);
              assigned = item.Kind == FollowingKind.Point
                ? new FollowingItem(item.Id, item.Kind, new Point(cp))
                : new FollowingItem(item.Id, item.Kind, DuplicateDotAt((TextDot)item.Geometry, cp));
            }
            catch { }
          }
        }

        result.Buckets[bestIndex].Add(assigned);
      }
      return result;
    }

    private static int CompareScore(Tuple<double, double> a, Tuple<double, double> b)
    {
      int primary = a.Item1.CompareTo(b.Item1);
      return primary != 0 ? primary : a.Item2.CompareTo(b.Item2);
    }

    private static double AssignTolerance(RhinoDoc doc, GeometryBase surfaceGeometry, GeometryBase followingGeometry)
    {
      return Math.Max(doc.ModelAbsoluteTolerance * FollowingTolFactor,
        Math.Max(GeometryDiagonal(surfaceGeometry), GeometryDiagonal(followingGeometry)) * FollowingDiagFactor);
    }

    private static double GeometryDiagonal(GeometryBase geometry)
    {
      if (geometry == null)
        return 0.0;
      var bbox = geometry.GetBoundingBox(true);
      return bbox.IsValid ? bbox.Diagonal.Length : 0.0;
    }

    private static Point3d? FollowingPoint(FollowingItem item)
    {
      if (item.Geometry is Point point)
        return point.Location;
      if (item.Geometry is TextDot dot)
        return dot.Point;
      return null;
    }

    private static Tuple<double, double>? PointScore(Brep brep, Point3d? point)
    {
      if (brep == null || !point.HasValue)
        return null;
      try
      {
        double d = point.Value.DistanceTo(brep.ClosestPoint(point.Value));
        return Tuple.Create(d, d);
      }
      catch { return null; }
    }

    private static Tuple<double, double>? CurveScore(Brep brep, Curve? curve)
    {
      if (brep == null || curve == null)
        return null;
      var distances = new List<double>();
      foreach (var p in CurveSamples(curve, FollowingCurveSamples))
      {
        try { distances.Add(p.DistanceTo(brep.ClosestPoint(p))); }
        catch { return null; }
      }
      return distances.Count > 0 ? Tuple.Create(distances.Max(), distances.Average()) : null;
    }

    private static TextDot DuplicateDotAt(TextDot? dot, Point3d point)
    {
      return new TextDot(dot?.Text ?? string.Empty, point);
    }

    private static double? AngleToPageUp(Vector3d vector, double tol)
    {
      var v = new Vector3d(vector.X, vector.Y, 0.0);
      if (VectorLength(v) <= tol)
        return null;
      v.Unitize();
      var target = new Vector3d(0.0, 1.0, 0.0);
      return Math.Atan2(v.X * target.Y - v.Y * target.X, v.X * target.X + v.Y * target.Y);
    }

    private static void RotateObjectsToTextUp(RhinoDoc doc, IEnumerable<Guid> ids, Point3d center, Vector3d textUp)
    {
      var angle = AngleToPageUp(textUp, doc.ModelAbsoluteTolerance);
      if (!angle.HasValue || Math.Abs(angle.Value) <= 1.0e-9)
        return;
      TransformObjects(doc, ids, Transform.Rotation(angle.Value, Vector3d.ZAxis, center));
    }

    private static void TransformObjects(RhinoDoc doc, IEnumerable<Guid> ids, Transform transform)
    {
      foreach (var id in Unique(ids))
        doc.Objects.Transform(id, transform, true);
    }

    private static BoundingBox? BoundingBoxOfObjects(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var bbox = BoundingBox.Empty;
      bool hasBox = false;
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var b = obj.Geometry.GetBoundingBox(true);
        if (!b.IsValid)
          continue;
        if (!hasBox)
        {
          bbox = b;
          hasBox = true;
        }
        else
          bbox.Union(b);
      }
      return hasBox ? bbox : (BoundingBox?)null;
    }

    private class LayoutOptions
    {
      public Point3d StartPoint;
      public LabelMode LabelMode;
      public bool RotateFlatParts;
      public bool Explode;
      public bool KeepProperties;
      public double LayoutSpacing;
      public double XExtents;
    }

    private class SourceSurface
    {
      public Guid Id { get; }
      public GeometryBase Geometry { get; }
      public Brep Brep { get; }

      public SourceSurface(Guid id, GeometryBase geometry, Brep brep)
      {
        Id = id;
        Geometry = geometry;
        Brep = brep;
      }
    }

    private class LabelFrame
    {
      public Point3d Point { get; }
      public Point3d UpPoint { get; }
      public Vector3d Y { get; }
      public Vector3d Normal { get; }
      public double Height { get; }

      public LabelFrame(Point3d point, Point3d upPoint, Vector3d y, Vector3d normal, double height)
      {
        Point = point;
        UpPoint = upPoint;
        Y = y;
        Normal = normal;
        Height = height;
      }
    }

    private enum FollowingKind
    {
      Curve,
      Point,
      Dot
    }

    private class FollowingItem
    {
      public Guid Id { get; }
      public FollowingKind Kind { get; }
      public GeometryBase Geometry { get; }

      public FollowingItem(Guid id, FollowingKind kind, GeometryBase geometry)
      {
        Id = id;
        Kind = kind;
        Geometry = geometry;
      }
    }

    private class AssignmentResult
    {
      public List<List<FollowingItem>> Buckets { get; }
      public int Skipped { get; set; }

      public AssignmentResult(int count)
      {
        Buckets = new List<List<FollowingItem>>();
        for (int i = 0; i < count; i++)
          Buckets.Add(new List<FollowingItem>());
      }
    }
  }
}
