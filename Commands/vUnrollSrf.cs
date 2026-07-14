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
// Modified 2026.06.30 13:04:31: label up helper now stays on the same Brep face using local UV tangent stepping, avoiding upside-down flat labels from ClosestPoint jumping to another face/edge.
// Modified 2026.06.30 17:08:45: hidden same-face orientation curves are unrolled for the label frame, reducing 180-degree flat-text direction ambiguity from standalone helper points.
// Modified 2026.06.30 17:17:28: unrolled flat text keeps the raw unrolled up direction but forces the text plane normal to World +Z, preventing mirrored text while preserving orientation-marker direction.
// Modified 2026.07.01: per-part label height with TextHeightScale multiplier; ResolveOrientationCurves with strict 15% length + shared-dist filter; edge mate dots (M## markers on shared edges) with EdgeDots option; flat label boundary fallback.

namespace vTools.Commands
{
  public class vUnrollSrf : Command
  {
    public override string EnglishName => "vUnrollSrf";

    private const string TextLayerName = "Reference";
    private const string TextObjectName = "MultiUnroll_NumberLabel";
    private const string FailureMarkerName = "MultiUnroll_FailedMarker";
    private const string LabelNumberKey = "MultiUnrollLabelNumber";
    private const string FailedUnrollKey = "MultiUnrollFailed";
    private const string FlatGroupPrefix = "MultiUnroll_Flat";
    private const string OriginalGroupPrefix = "MultiUnroll_Original";
    private const string FailureMarkerText = "X";

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
    private static bool   _edgeDots = true;
    private static bool   _splitFaces = false;

    // Edge-mate dot constants (match MultiUnroll2.py / vMatch.cs)
    private const string EdgeMateName        = vMatch.EdgeMateName;
    private const string EdgeMateIdKey       = vMatch.EdgeMateIdKey;
    private const string EdgePartNumKey      = vMatch.EdgePartNumKey;
    private const string EdgeMatePartNumKey  = vMatch.EdgeMatePartNumKey;
    private const string EdgeMateReversedKey = vMatch.EdgeMateReversedKey;
    private const string EdgeMatePrefix      = "M";
    private const int    EdgeMateDotSize     = 10;
    private const double EdgeMateTolFactor   = 25.0;
    private const double EdgeMateDiagFactor  = 1.0e-4;
    private const int    EdgeMateSamples     = 7;

    // ── Debug logging ─────────────────────────────────────────────────────
    private static string? _dbgPath;

    private static void DbgInit(RhinoDoc doc)
    {
      _dbgPath = null;
      try
      {
        var dir = new System.IO.DirectoryInfo(
          System.IO.Path.GetDirectoryName(typeof(vUnrollSrf).Assembly.Location) ?? ".");
        while (dir != null)
        {
          if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "vTools.csproj")))
          {
            var logsDir = System.IO.Path.Combine(dir.FullName, "logs");
            System.IO.Directory.CreateDirectory(logsDir);
            _dbgPath = System.IO.Path.Combine(logsDir, "vUnrollSrf_debug.log");
            break;
          }
          dir = dir.Parent;
        }
        if (_dbgPath == null) return;
        System.IO.File.WriteAllText(_dbgPath,
          $"vUnrollSrf debug log\ntime={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
          $"model_tol={doc.ModelAbsoluteTolerance:G}\ndoc={doc.Path}\n\n");
      }
      catch { _dbgPath = null; }
    }

    private static void Dbg(string msg)
    {
      if (_dbgPath == null) return;
      try { System.IO.File.AppendAllText(_dbgPath, $"{DateTime.Now:HH:mm:ss} {msg}\n"); }
      catch { }
    }

    private static string P(Point3d? p)  => p.HasValue  ? $"({p.Value.X:G6}, {p.Value.Y:G6}, {p.Value.Z:G6})" : "None";
    private static string P(Point3d p)   => $"({p.X:G6}, {p.Y:G6}, {p.Z:G6})";
    private static string V(Vector3d? v) => v.HasValue  ? $"({v.Value.X:G6}, {v.Value.Y:G6}, {v.Value.Z:G6})" : "None";
    private static string V(Vector3d v)  => $"({v.X:G6}, {v.Y:G6}, {v.Z:G6})";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      DbgInit(doc);
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
      _splitFaces = options.SplitFaces;

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

      // SplitFaces: explode each multi-face brep into single-face breps so the
      // unroller handles each face independently, eliminating overlap in output.
      if (_splitFaces)
      {
        var split = new List<SourceSurface>();
        foreach (var src in sources)
        {
          if (src.Brep.Faces.Count <= 1) { split.Add(src); continue; }
          foreach (var face in src.Brep.Faces)
          {
            var fb = face.DuplicateFace(false);
            if (fb != null) split.Add(new SourceSurface(src.Id, src.Geometry, fb));
          }
        }
        sources = split;
      }

      var addText = _labelMode == LabelMode.Text;
      var addDots = _labelMode == LabelMode.Dots;
      var addLabels = _labelMode != LabelMode.None;

      var followingItems = MakeFollowingItems(doc, followingIds);
      var assignment = followingItems.Count > 0 ? AssignFollowing(doc, followingItems, sources) : new AssignmentResult(sources.Count);

      double tol = doc.ModelAbsoluteTolerance;

      // Build edge-mate pairs before unrolling (once for all source surfaces)
      var edgePairs = _edgeDots
        ? BuildEdgeMates(sources, tol)
        : (List<List<EdgeMateRecord>>?)null;
      double xLimit = _xExtents;
      double xOrigin = options.StartPoint.X;
      double rowY = options.StartPoint.Y;
      double rowHeight = 0.0;
      var nextPoint = options.StartPoint;
      bool exceeded = false;
      int done = 0;
      int failed = 0;
      var failedSourceIds = new List<Guid>();

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
            ? SurfaceLabelFrame(doc, src.Id, ItemTextHeight(doc, src.Id, display, src.Brep), src.Brep)
            : null;

          if (frame != null)
            Dbg($"part={number} original_frame label_height={frame.Height:G6} point={P(frame.Point)} y={V(frame.Y)} x={V(frame.X)} step={frame.Step:G6} up_pt={P(frame.UpPoint)} right_pt={P(frame.RightPoint)}");
          else
            Dbg($"part={number} original_frame None");

          var surfaceItems = i < assignment.Buckets.Count ? assignment.Buckets[i] : new List<FollowingItem>();
          var curves = surfaceItems.Where(x => x.Kind == FollowingKind.Curve).Select(x => x.Geometry).OfType<Curve>().ToList();
          var points = surfaceItems.Where(x => x.Kind == FollowingKind.Point).Select(x => x.Geometry).OfType<Point>().ToList();
          var dots = surfaceItems.Where(x => x.Kind == FollowingKind.Dot).Select(x => x.Geometry).OfType<TextDot>().ToList();

          foreach (var curve in curves)
            unroller.AddFollowingGeometry(curve);

          int labelUpCurveIndex = -1;
          int labelRightCurveIndex = -1;
          if (frame != null)
          {
            // Curves keep start/end direction more reliably than independent helper points.
            // These two hidden same-face curves define the flattened label frame.
            labelUpCurveIndex = curves.Count;
            unroller.AddFollowingGeometry(new LineCurve(frame.Point, frame.UpPoint));
            labelRightCurveIndex = curves.Count + 1;
            unroller.AddFollowingGeometry(new LineCurve(frame.Point, frame.RightPoint));
          }

          // Edge-mate curves — added after orientation helpers, before user points
          int orientCrvCount = frame != null ? 2 : 0;
          var edgeMateInfos = (edgePairs != null && i < edgePairs.Count)
            ? AddEdgeMateCurves(unroller, edgePairs[i], curves.Count + orientCrvCount)
            : new List<EdgeMateInfo>();

          foreach (var point in points)
            unroller.AddFollowingGeometry(point.Location);

          int labelPointIndex = -1;
          int labelUpIndex = -1;
          if (frame != null)
          {
            // Point fallback only. The hidden curves above are preferred for orientation.
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
          Brep[] unrolledBreps;
          try
          {
            unrolledBreps = unroller.PerformUnroll(out unrolledCurves, out unrolledPoints, out unrolledDots);
          }
          catch (Exception ex)
          {
            Dbg($"part={number} unroll_failed error={ex.Message}");
            AddFailureMarker(doc, src, frame);
            AddValid(failedSourceIds, src.Id);
            failed++;
            continue;
          }
          if (unrolledBreps == null || unrolledBreps.Length == 0)
          {
            Dbg($"part={number} unroll_failed no_breps");
            AddFailureMarker(doc, src, frame);
            AddValid(failedSourceIds, src.Id);
            failed++;
            continue;
          }

          Dbg($"part={number} unroll_output breps={unrolledBreps.Length} curves={unrolledCurves?.Length ?? 0} points={unrolledPoints?.Length ?? 0} dots={unrolledDots?.Length ?? 0}");

          done++;

          var outputIds = new List<Guid>();
          var finalBreps = _explode ? unrolledBreps : (Brep.JoinBreps(unrolledBreps, tol) ?? unrolledBreps);
          foreach (var brep in finalBreps)
            AddValid(outputIds, doc.Objects.AddBrep(brep));

          Point3d? curveLabelPoint = null;
          Point3d? curveLabelUp = null;
          Point3d? curveLabelRight = null;
          var hiddenCurveIndexes = new HashSet<int>();
          Dictionary<(string, int, int), Point3d>? edgeFlatPoints = null;
          if (unrolledCurves != null)
          {
            // Use shared-endpoint + length filter to reliably identify orientation curves
            // regardless of Rhino output reordering.
            int orientUpIdx = -1, orientRightIdx = -1;
            Point3d orientCtr = Point3d.Unset, orientUp = Point3d.Unset, orientRt = Point3d.Unset;
            bool orientOk = frame != null && ResolveOrientationCurves(unrolledCurves, frame, tol,
                  out orientUpIdx, out orientRightIdx, out orientCtr, out orientUp, out orientRt);
            if (orientOk)
            {
              Dbg($"part={number} orientation_match up_idx={orientUpIdx} right_idx={orientRightIdx} center={P(orientCtr)} up_end={P(orientUp)} right_end={P(orientRt)}");
              curveLabelPoint = orientCtr;
              curveLabelUp    = orientUp;
              curveLabelRight = orientRt;
              hiddenCurveIndexes.Add(orientUpIdx);
              hiddenCurveIndexes.Add(orientRightIdx);
            }
            else
            {
              Dbg($"part={number} orientation_match FAILED curves={unrolledCurves.Length}");
            }

            // Match edge mate curves by length scan (Rhino does not guarantee output order).
            // Also hides the matched edge curve indices inside hiddenCurveIndexes.
            edgeFlatPoints = MatchEdgeCurveOutputs(unrolledCurves, edgeMateInfos, hiddenCurveIndexes, tol);

            // Cleanup pass: hide any remaining orientation helper curves not caught by
            // ResolveOrientationCurves (e.g. when only one was returned by Rhino).
            // Check against both chord lengths (up/right) AND arc-length step — mirrors Python.
            if (frame != null && orientCrvCount > 0)
            {
              double upLen  = frame.Point.DistanceTo(frame.UpPoint);
              double rtLen  = frame.Point.DistanceTo(frame.RightPoint);
              double stp    = frame.Step;
              for (int ci = 0; ci < unrolledCurves.Length; ci++)
              {
                if (hiddenCurveIndexes.Contains(ci) || unrolledCurves[ci] == null) continue;
                double cl = unrolledCurves[ci].GetLength();
                if ((upLen > tol && Math.Abs(cl - upLen) / upLen < 0.15) ||
                    (rtLen > tol && Math.Abs(cl - rtLen) / rtLen < 0.15) ||
                    (stp   > tol && Math.Abs(cl - stp)   / stp   < 0.15))
                  hiddenCurveIndexes.Add(ci);
              }
            }

            for (int ci = 0; ci < unrolledCurves.Length; ci++)
            {
              if (!hiddenCurveIndexes.Contains(ci))
                AddValid(outputIds, doc.Objects.AddCurve(unrolledCurves[ci]));
            }
          }

          // Use curveLabelPoint (from ResolveOrientationCurves) as preferred label anchor
          var labelPoint = curveLabelPoint;
          var labelUp    = curveLabelUp;
          var labelRight = curveLabelRight;
          var hiddenPointIndexes = new HashSet<int>();
          if (unrolledPoints != null)
          {
            if (labelPointIndex >= 0 && labelPointIndex < unrolledPoints.Length)
            {
              if (!labelPoint.HasValue)
                labelPoint = unrolledPoints[labelPointIndex];
              hiddenPointIndexes.Add(labelPointIndex);
            }
            if (labelUpIndex >= 0 && labelUpIndex < unrolledPoints.Length)
            {
              if (!labelUp.HasValue)
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
          Vector3d? unrolledX = null;
          // curveY: from orientation curves, projected onto Z-plane and normalized (matches Python)
          Vector3d? curveY = null;
          if (curveLabelPoint.HasValue && curveLabelUp.HasValue)
          {
            var raw = new Vector3d(
              curveLabelUp.Value.X - curveLabelPoint.Value.X,
              curveLabelUp.Value.Y - curveLabelPoint.Value.Y, 0.0);
            if (raw.Unitize()) curveY = raw;
          }
          // pointY: from unrolled frame points, projected onto Z-plane and normalized
          Vector3d? pointY = null;
          if (labelPoint.HasValue && labelUp.HasValue)
          {
            var raw = new Vector3d(
              labelUp.Value.X - labelPoint.Value.X,
              labelUp.Value.Y - labelPoint.Value.Y, 0.0);
            if (raw.Unitize()) pointY = raw;
          }
          else if (frame != null)
          {
            pointY = new Vector3d(frame.Y.X, frame.Y.Y, 0.0);
            if (pointY.Value.Length > RhinoMath.ZeroTolerance)
            { var tmp = pointY.Value; tmp.Unitize(); pointY = tmp; }
          }
          // use curveY if valid, else pointY; final result projected onto Z-plane
          Vector3d chosen = Vector3d.YAxis;
          if (curveY.HasValue && curveY.Value.Length > RhinoMath.ZeroTolerance)
            chosen = curveY.Value;
          else if (pointY.HasValue && pointY.Value.Length > RhinoMath.ZeroTolerance)
            chosen = pointY.Value;
          unrolledY = chosen;

          // Also capture fallback label pt/up for logging (from unrolled points, before labelPoint override)
          Point3d? fallbackLabelPt  = (labelPoint.HasValue && !curveLabelPoint.HasValue) ? labelPoint : null;
          Point3d? fallbackLabelUp  = (labelUp.HasValue   && !curveLabelUp.HasValue)    ? labelUp    : null;
          if (!fallbackLabelPt.HasValue && unrolledPoints != null &&
              labelPointIndex >= 0 && labelPointIndex < unrolledPoints.Length)
            fallbackLabelPt = unrolledPoints[labelPointIndex];
          if (!fallbackLabelUp.HasValue && unrolledPoints != null &&
              labelUpIndex >= 0 && labelUpIndex < unrolledPoints.Length)
            fallbackLabelUp = unrolledPoints[labelUpIndex];

          Dbg($"part={number} label_unrolled" +
              $" curve_pt={P(curveLabelPoint)} curve_up={P(curveLabelUp)} curve_right={P(curveLabelRight)}" +
              $" fallback_pt={P(fallbackLabelPt)} fallback_up={P(fallbackLabelUp)}" +
              $" curve_y={V(curveY)} point_y={V(pointY)} chosen_y={V(unrolledY)}" +
              $" label_pt={P(labelPoint)}");

          if (curveLabelPoint.HasValue && curveLabelRight.HasValue)
            unrolledX = curveLabelRight.Value - curveLabelPoint.Value;
          else if (labelPoint.HasValue && labelRight.HasValue)
            unrolledX = labelRight.Value - labelPoint.Value;

          var unrolledLabelIds = new List<Guid>();
          if (frame != null && labelPoint.HasValue)
          {
            if (addText)
            {
              // Keep the raw unrolled up direction as the orientation marker, but do not use
              // the raw frame normal for flat labels. If the unrolled helper frame lands with
              // a -Z normal, annotation text becomes mirrored in Top view. World +Z keeps the
              // text readable while preserving the same unrolled Y/up direction.
              AddValid(unrolledLabelIds, AddFlatText(doc, display, labelPoint.Value, unrolledY, Vector3d.ZAxis, frame.Height, src.Id, _keepProperties));
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

          // Place edge mate dots on flat output
          if (edgeFlatPoints != null && edgeFlatPoints.Count > 0)
          {
            int refLayerIdx = ReferenceLayerIndex(doc);
            foreach (var info in UniqueEdgeMateInfos(edgeMateInfos))
            {
              var key = (info.Record.MateId, info.Record.EdgeIndex, info.Record.MatePartIndex);
              if (!edgeFlatPoints.TryGetValue(key, out var flatPt)) continue;
              var dotId = AddEdgeMateDot(doc, info.Record, flatPt, number, refLayerIdx);
              if (IsValidId(dotId))
                outputIds.Add(dotId);
            }
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
        var problemIds = Unique(failedSourceIds.Concat(assignment.SkippedIds)).ToList();
        RestoreSelection(doc, problemIds.Count > 0 ? problemIds : startIds);
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
      msg += $" | Labels {_labelMode} | RotateFlatParts {(_rotateFlatParts ? "Yes" : "No")} | EdgeDots {(_edgeDots ? "On" : "Off")}";
      RhinoApp.WriteLine(msg);

      return done > 0 ? Result.Success : Result.Nothing;
    }

    private static List<Guid>? GetSurfaceIds(RhinoDoc doc, List<Guid> preselected)
    {
      preselected = Unique(preselected.Where(IsSurfaceLikeId));
      if (preselected.Count > 0)
        return preselected;

      var go = new GetObject();
      go.EnableTransparentCommands(true);
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
      go.EnableTransparentCommands(true);
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
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt(prompt);
      var shared = AddSharedOptions(gp);
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
        StartPoint     = point,
        LabelMode      = _labelMode,
        RotateFlatParts= _rotateFlatParts,
        Explode        = _explode,
        SplitFaces     = _splitFaces,
        KeepProperties = _keepProperties,
        LayoutSpacing  = _layoutSpacing,
        XExtents       = _xExtents
      };
    }

    private class SharedOptions
    {
      public int LabelIndex = -1;
      public OptionToggle? RotateOption;
      public OptionToggle? EdgeDotsOption;
      public OptionToggle? ExplodeOption;
      public OptionToggle? SplitFacesOption;
      public OptionToggle? PropsOption;
      public int SpacingIndex = -1;
      public int XExtentsIndex = -1;
    }

    private static SharedOptions AddSharedOptions(GetBaseClass getter)
    {
      var state = new SharedOptions();
      state.LabelIndex = getter.AddOptionList("Labels", LabelModeNames, (int)_labelMode);
      state.RotateOption = new OptionToggle(_rotateFlatParts, "No", "Yes");
      getter.AddOptionToggle("RotateFlatParts", ref state.RotateOption);
      state.EdgeDotsOption = new OptionToggle(_edgeDots, "Off", "On");
      getter.AddOptionToggle("EdgeDots", ref state.EdgeDotsOption);
      state.ExplodeOption = new OptionToggle(_explode, "No", "Yes");
      getter.AddOptionToggle("Explode", ref state.ExplodeOption);
      state.SplitFacesOption = new OptionToggle(_splitFaces, "No", "Yes");
      getter.AddOptionToggle("SplitFaces", ref state.SplitFacesOption);
      state.PropsOption = new OptionToggle(_keepProperties, "No", "Yes");
      getter.AddOptionToggle("KeepProperties", ref state.PropsOption);
      state.SpacingIndex  = getter.AddOption("Spacing",  $"{_layoutSpacing:G}");
      state.XExtentsIndex = getter.AddOption("XExtents", $"{_xExtents:G}");
      return state;
    }

    private static void HandleSharedOption(GetBaseClass getter, SharedOptions state)
    {
      if (state == null) return;

      if (state.RotateOption    != null) _rotateFlatParts = state.RotateOption.CurrentValue;
      if (state.EdgeDotsOption  != null) _edgeDots        = state.EdgeDotsOption.CurrentValue;
      if (state.ExplodeOption   != null) _explode         = state.ExplodeOption.CurrentValue;
      if (state.SplitFacesOption!= null) _splitFaces      = state.SplitFacesOption.CurrentValue;
      if (state.PropsOption     != null) _keepProperties  = state.PropsOption.CurrentValue;

      var option = getter.Option();
      if (option != null && option.Index == state.LabelIndex)
      {
        int idx = option.CurrentListOptionIndex;
        if (idx >= 0 && idx < LabelModeNames.Length)
          _labelMode = (LabelMode)idx;
      }
      if (option != null && option.Index == state.SpacingIndex)
      {
        var gs = new GetString();
        gs.SetCommandPrompt("Layout spacing");
        gs.SetDefaultString($"{_layoutSpacing:G}");
        gs.AcceptNothing(true);
        if (gs.Get() == GetResult.String &&
            double.TryParse(gs.StringResult().Trim(),
              System.Globalization.NumberStyles.Any,
              System.Globalization.CultureInfo.InvariantCulture, out double sv) && sv >= 0)
          _layoutSpacing = sv;
      }
      if (option != null && option.Index == state.XExtentsIndex)
      {
        var gs = new GetString();
        gs.SetCommandPrompt("X extents limit (0 = unlimited)");
        gs.SetDefaultString($"{_xExtents:G}");
        gs.AcceptNothing(true);
        if (gs.Get() == GetResult.String &&
            double.TryParse(gs.StringResult().Trim(),
              System.Globalization.NumberStyles.Any,
              System.Globalization.CultureInfo.InvariantCulture, out double xv) && xv >= 0)
          _xExtents = xv;
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

    private static Point3d? LabelPoint(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        var area = AreaMassProperties.Compute(brep);
        Point3d point;
        if (area != null) point = area.Centroid;
        else point = brep.GetBoundingBox(true).Center;
        try { return brep.ClosestPoint(point); }
        catch { return point; }
      }
      if (brepHint != null) return null;
      var obj = doc.Objects.FindId(objId);
      var bbox = obj?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
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

    private static List<Point3d> BoundaryPoints(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var pts = new List<Point3d>();
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        pts.AddRange(brep.Vertices.Select(v => v.Location));
        foreach (var curve in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          pts.AddRange(CurveSamples(curve, TextBoundarySamples));
      }
      if (pts.Count == 0)
      {
        var geom = (GeometryBase?)brepHint ?? doc.Objects.FindId(objId)?.Geometry;
        var bbox = geom?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (bbox.IsValid) pts.AddRange(bbox.GetCorners());
      }
      return pts;
    }

    private static LabelFrame? SurfaceLabelFrame(RhinoDoc doc, Guid objId, double height, Brep? brepHint = null)
    {
      var point = LabelPoint(doc, objId, brepHint);
      if (!point.HasValue)
        return null;

      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      var tol = doc.ModelAbsoluteTolerance;
      var faceHit = brep != null ? ClosestFaceHit(brep, point.Value, tol) : null;
      var normal = faceHit?.Normal ?? (brep != null ? ClosestNormal(brep, point.Value, Vector3d.ZAxis, tol) : Vector3d.ZAxis);
      var y = ProjectToPlane(Vector3d.ZAxis, normal, tol)
              ?? ProjectToPlane(Vector3d.YAxis, normal, tol)
              ?? Vector3d.YAxis;
      var x = Unit(Vector3d.CrossProduct(y, normal), tol) ?? Vector3d.XAxis;
      y = Unit(y, tol) ?? Vector3d.YAxis;
      normal = Unit(normal, tol) ?? Vector3d.ZAxis;

      var step = Math.Max(height * TextUpStepRatio, tol * 20.0);
      var upPoint = faceHit != null
        ? SameFaceStepPoint(faceHit, y, step, tol)
        : point.Value + y * step;
      var rightPoint = faceHit != null
        ? SameFaceStepPoint(faceHit, x, step, tol)
        : point.Value + x * step;

      var actualY = Unit(upPoint - point.Value, tol) ?? y;
      var actualX = Unit(rightPoint - point.Value, tol) ?? x;
      return new LabelFrame(point.Value, upPoint, rightPoint, actualX, actualY, normal, height, step);
    }

    private class FaceHit
    {
      public BrepFace Face { get; }
      public double U { get; }
      public double V { get; }
      public Point3d Point { get; }
      public Vector3d Normal { get; }

      public FaceHit(BrepFace face, double u, double v, Point3d point, Vector3d normal)
      {
        Face = face;
        U = u;
        V = v;
        Point = point;
        Normal = normal;
      }
    }

    private static FaceHit? ClosestFaceHit(Brep brep, Point3d point, double tol)
    {
      FaceHit? best = null;
      double bestDistance = double.MaxValue;
      foreach (var face in brep.Faces)
      {
        double u, v;
        if (!face.ClosestPoint(point, out u, out v))
          continue;
        var facePoint = face.PointAt(u, v);
        var distance = point.DistanceToSquared(facePoint);
        var normal = Unit(face.NormalAt(u, v), tol) ?? Vector3d.ZAxis;
        if (distance < bestDistance)
        {
          bestDistance = distance;
          best = new FaceHit(face, u, v, facePoint, normal);
        }
      }
      return best;
    }

    private static Point3d SameFaceStepPoint(FaceHit hit, Vector3d direction, double step, double tol)
    {
      var face = hit.Face;
      var uDomain = face.Domain(0);
      var vDomain = face.Domain(1);
      var epsU = Math.Max(uDomain.Length * 1.0e-6, RhinoMath.ZeroTolerance);
      var epsV = Math.Max(vDomain.Length * 1.0e-6, RhinoMath.ZeroTolerance);
      var u0 = Math.Max(uDomain.T0, hit.U - epsU);
      var u1 = Math.Min(uDomain.T1, hit.U + epsU);
      var v0 = Math.Max(vDomain.T0, hit.V - epsV);
      var v1 = Math.Min(vDomain.T1, hit.V + epsV);

      var su = u1 > u0 ? (face.PointAt(u1, hit.V) - face.PointAt(u0, hit.V)) / (u1 - u0) : Vector3d.XAxis;
      var sv = v1 > v0 ? (face.PointAt(hit.U, v1) - face.PointAt(hit.U, v0)) / (v1 - v0) : Vector3d.YAxis;
      var target = (Unit(direction, tol) ?? Vector3d.YAxis) * step;

      double a = su * su;
      double b = su * sv;
      double c = sv * sv;
      double d = su * target;
      double e = sv * target;
      double det = a * c - b * b;
      if (Math.Abs(det) <= 1.0e-16)
        return hit.Point + (Unit(direction, tol) ?? Vector3d.YAxis) * step;

      double du = (d * c - b * e) / det;
      double dv = (a * e - b * d) / det;

      for (int i = 0; i < 8; i++)
      {
        double scale = 1.0 / Math.Pow(2.0, i);
        double u = Math.Max(uDomain.T0, Math.Min(uDomain.T1, hit.U + du * scale));
        double v = Math.Max(vDomain.T0, Math.Min(vDomain.T1, hit.V + dv * scale));
        try
        {
          var relation = face.IsPointOnFace(u, v);
          if (relation != PointFaceRelation.Exterior)
          {
            var point = face.PointAt(u, v);
            if (point.DistanceTo(hit.Point) > tol * 2.0)
              return point;
          }
        }
        catch
        {
          var point = face.PointAt(u, v);
          if (point.DistanceTo(hit.Point) > tol * 2.0)
            return point;
        }
      }

      return hit.Point + (Unit(direction, tol) ?? Vector3d.YAxis) * step;
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

    private static double HeightCandidate(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var point = LabelPoint(doc, objId, brepHint);
      var pts   = BoundaryPoints(doc, objId, brepHint);
      if (!point.HasValue || pts.Count == 0)
      {
        var bbox = brepHint?.GetBoundingBox(true)
          ?? doc.Objects.FindId(objId)?.Geometry.GetBoundingBox(true)
          ?? BoundingBox.Empty;
        if (!bbox.IsValid)
          return 1.0;
        var spans = new[] { bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y, bbox.Max.Z - bbox.Min.Z }
          .Where(s => s > doc.ModelAbsoluteTolerance).ToList();
        return spans.Count > 0 ? spans.Min() * 0.04 : 1.0;
      }

      var frame = SurfaceLabelFrame(doc, objId, 1.0, brepHint);
      if (frame == null)
        return 1.0;
      var ys = pts.Select(p => (p - point.Value) * frame.Y);
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

    private static Guid AddFailureMarker(RhinoDoc doc, SourceSurface src, LabelFrame? frame)
    {
      frame ??= SurfaceLabelFrame(doc, src.Id, ItemTextHeight(doc, src.Id, FailureMarkerText, src.Brep), src.Brep);

      if (frame != null)
      {
        var height = Math.Max(frame.Height, doc.ModelAbsoluteTolerance * 10.0);
        return AddFailureText(doc, frame.Point, frame.Y, frame.Normal, height, src.Id);
      }

      var point = LabelPoint(doc, src.Id, src.Brep);
      if (!point.HasValue)
      {
        var bbox = src.Brep.GetBoundingBox(true);
        if (bbox.IsValid)
          point = bbox.Center;
      }
      if (!point.HasValue)
        return Guid.Empty;

      var fallbackHeight = Math.Max(ItemTextHeight(doc, src.Id, FailureMarkerText, src.Brep), doc.ModelAbsoluteTolerance * 10.0);
      var normal = ClosestNormal(src.Brep, point.Value, Vector3d.ZAxis, doc.ModelAbsoluteTolerance);
      var up = ProjectToPlane(Vector3d.YAxis, normal, doc.ModelAbsoluteTolerance) ?? Vector3d.YAxis;
      return AddFailureText(doc, point.Value, up, normal, fallbackHeight, src.Id);
    }

    private static Guid AddFailureText(RhinoDoc doc, Point3d point, Vector3d yDirection, Vector3d normal, double height, Guid sourceId)
    {
      var n = Unit(normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
      var lift = Math.Max(height * TextLiftRatio, doc.ModelAbsoluteTolerance * 2.0);
      var plane = TextPlane(point + n * lift, yDirection, n, doc.ModelAbsoluteTolerance);
      var attrs = FailureMarkerAttributes(doc, sourceId);

      try
      {
        var te = new TextEntity
        {
          PlainText = FailureMarkerText,
          Plane = plane,
          TextHeight = height,
          Justification = TextJustification.MiddleCenter,
          Font = Font.FromQuartetProperties(TextFont, false, false)
        };
        return doc.Objects.AddText(te, attrs);
      }
      catch
      {
        return doc.Objects.AddText(FailureMarkerText, plane, height, TextFont, false, false, attrs);
      }
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

    private static ObjectAttributes FailureMarkerAttributes(RhinoDoc doc, Guid sourceId)
    {
      var attrs = doc.Objects.FindId(sourceId)?.Attributes.Duplicate() ?? new ObjectAttributes();
      attrs.Name = FailureMarkerName;
      attrs.LayerIndex = ReferenceLayerIndex(doc);
      attrs.ObjectColor = System.Drawing.Color.Red;
      attrs.ColorSource = ObjectColorSource.ColorFromObject;
      attrs.SetUserString(FailedUnrollKey, "true");
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
          result.SkippedIds.Add(item.Id);
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

    // ── Per-part label height ──────────────────────────────────────────────────
    private static double ItemTextHeight(RhinoDoc doc, Guid objId, string display, Brep? brepHint = null)
    {
      double tol = doc.ModelAbsoluteTolerance;
      double baseH = HeightCandidate(doc, objId, brepHint);
      double height = Math.Max(baseH, tol * 8.0);
      var caps = new List<double>();

      // Edge cap: shortest meaningful naked edge * 0.50
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        var lengths = brep.DuplicateEdgeCurves(true)?
          .Where(c => c != null)
          .Select(c => c!.GetLength())
          .Where(l => l > tol)
          .ToList() ?? new List<double>();
        if (lengths.Count > 0)
        {
          double longest = lengths.Max();
          double minMean = Math.Max(tol * 20.0, longest * 0.08);
          var meaningful = lengths.Where(l => l >= minMean).ToList();
          if (meaningful.Count > 0) caps.Add(meaningful.Min() * 0.50);
        }
      }

      // Span caps: x_span * 0.45 / width_factor and y_span * 0.28
      var pt = LabelPoint(doc, objId, brepHint);
      var pts = BoundaryPoints(doc, objId, brepHint);
      if (pt.HasValue && pts.Count > 0)
      {
        var frame = SurfaceLabelFrame(doc, objId, 1.0, brepHint);
        if (frame != null)
        {
          double wf = Math.Max(1.0, display.Length * 0.65);
          var xs = pts.Select(p => (p - pt.Value) * frame.X);
          var ys = pts.Select(p => (p - pt.Value) * frame.Y);
          double xSpan = CenteredSpan(xs);
          double ySpan = CenteredSpan(ys);
          if (xSpan > tol) caps.Add(xSpan * 0.45 / wf);
          if (ySpan > tol) caps.Add(ySpan * 0.28);
        }
      }

      if (caps.Count > 0) height = Math.Min(height, caps.Min());
      return Math.Max(height, tol * 8.0) * TextHeightScale;
    }

    // ── Orientation curve resolution ───────────────────────────────────────────
    /// <summary>
    /// Identifies the two orientation helper curves among the unrolled curves.
    /// Uses shared-endpoint detection (15% length filter + 1% shared-dist threshold)
    /// to find the correct pair regardless of Rhino output index reordering.
    /// </summary>
    private static bool ResolveOrientationCurves(
      Curve[] curves, LabelFrame frame, double tol,
      out int upIdx, out int rightIdx,
      out Point3d center, out Point3d upEnd, out Point3d rightEnd)
    {
      upIdx = rightIdx = -1;
      center = upEnd = rightEnd = Point3d.Unset;
      if (curves == null || frame == null) return false;

      // Use the 3D chord lengths separately for up and right (matches Python's approach).
      // On curved surfaces the up and right chords can differ substantially, so a single
      // arc-length step is a poor filter for the shorter curve.
      double upLen    = frame.Point.DistanceTo(frame.UpPoint);
      double rightLen = frame.Point.DistanceTo(frame.RightPoint);
      const double lenTol = 0.15;

      int    bI = -1, bJ = -1;
      double bScore = double.MaxValue;
      Point3d bCenter = Point3d.Unset, bUp = Point3d.Unset, bRight = Point3d.Unset;

      for (int i = 0; i < curves.Length; i++)
      {
        var ci = curves[i]; if (ci == null) continue;
        double li = ci.GetLength();
        if (upLen > tol && Math.Abs(li - upLen) / upLen > lenTol) continue;

        for (int j = 0; j < curves.Length; j++)
        {
          if (i == j) continue;
          var cj = curves[j]; if (cj == null) continue;
          double lj = cj.GetLength();
          if (rightLen > tol && Math.Abs(lj - rightLen) / rightLen > lenTol) continue;

          var (sd, ctr, ue, re) = EndpointPairDistance(ci, cj);
          double sharedMax = Math.Max(Math.Max(upLen, rightLen), tol * 100.0) * 0.01;
          if (sd > sharedMax) continue;

          double lenScore = 0.0;
          if (upLen    > tol) lenScore += Math.Abs(li - upLen)    / upLen;
          if (rightLen > tol) lenScore += Math.Abs(lj - rightLen) / rightLen;
          double scale = Math.Max(Math.Max(upLen, rightLen), tol * 100.0);
          double score = lenScore + sd / scale * 10.0;
          if (score < bScore)
          {
            bScore = score; bI = i; bJ = j;
            bCenter = ctr; bUp = ue; bRight = re;
          }
        }
      }

      if (bI < 0) return false;
      upIdx = bI; rightIdx = bJ;
      center = bCenter; upEnd = bUp; rightEnd = bRight;
      return true;
    }

    /// <summary>Returns (sharedDist, sharedPtOnC1, otherEndOfC1, otherEndOfC2).</summary>
    private static (double dist, Point3d ctr, Point3d upEnd, Point3d rightEnd)
      EndpointPairDistance(Curve c1, Curve c2)
    {
      var s1 = c1.PointAtStart; var e1 = c1.PointAtEnd;
      var s2 = c2.PointAtStart; var e2 = c2.PointAtEnd;
      var pairs = new (double d, Point3d ctr, Point3d up, Point3d rt)[]
      {
        (s1.DistanceTo(s2), s1, e1, e2),
        (s1.DistanceTo(e2), s1, e1, s2),
        (e1.DistanceTo(s2), e1, s1, e2),
        (e1.DistanceTo(e2), e1, s1, s2),
      };
      var best = pairs.OrderBy(p => p.d).First();
      return (best.d, best.ctr, best.up, best.rt);
    }

    // ── Edge mate data types ───────────────────────────────────────────────────
    private class EdgeMateRecord
    {
      public string  MateId        = "";
      public int     EdgeIndex;
      public Curve?  Curve;
      public Point3d Marker;
      public int     MatePartIndex;
      public int     MatePartNumber;
      public int     MateEdgeIndex;
      public bool    Reversed;
    }

    private class EdgeMateInfo
    {
      public EdgeMateRecord Record          = new EdgeMateRecord();
      public int            CurveOutputIndex;
      public bool           Shared;
    }

    private class EdgePairResult
    {
      public double  MaxDist;
      public double  AvgDist;
      public bool    Reversed;
      public bool    Full;        // length_ratio >= 0.90
      public bool    ShortFirst;  // ea is the shorter curve
      public Point3d Point1;
      public Point3d Point2;
    }

    // ── Edge mate system ───────────────────────────────────────────────────────
    private static List<List<EdgeMateRecord>> BuildEdgeMates(
      List<SourceSurface> sources, double tol)
    {
      var result = new List<List<EdgeMateRecord>>(sources.Count);
      for (int i = 0; i < sources.Count; i++)
        result.Add(new List<EdgeMateRecord>());

      // Collect naked edges per source, filtering out degenerate ones
      var allEdges = sources
        .Select(s => (s.Brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          .Select((c, idx) => (idx, c))
          .Where(x => x.c != null && x.c.GetLength() > tol)
          .ToArray())
        .ToArray();

      // Build and score all candidate pairs
      var candidates = new List<(double maxD, double avgD, int i, int ei, int j, int ej, EdgePairResult score)>();
      for (int i = 0; i < sources.Count; i++)
        for (int j = i + 1; j < sources.Count; j++)
          foreach (var (ei, ci) in allEdges[i])
            foreach (var (ej, cj) in allEdges[j])
            {
              var score = TestEdgePair(ci, cj, tol);
              if (score != null)
                candidates.Add((score.MaxDist, score.AvgDist, i, ei, j, ej, score));
            }

      // Sort best matches first, then deduplicate using full/short tracking
      candidates.Sort((a, b) => {
        int c = a.maxD.CompareTo(b.maxD);
        return c != 0 ? c : a.avgD.CompareTo(b.avgD);
      });

      var usedFull  = new HashSet<(int, int)>();
      var usedShort = new HashSet<(int, int)>();
      int counter = 0;

      foreach (var (_, __, i, ei, j, ej, score) in candidates)
      {
        var key1     = (i, ei);
        var key2     = (j, ej);
        var shortKey = score.ShortFirst ? key1 : key2;

        if (score.Full)
        {
          if (usedFull.Contains(key1) || usedFull.Contains(key2) ||
              usedShort.Contains(key1) || usedShort.Contains(key2)) continue;
          usedFull.Add(key1);
          usedFull.Add(key2);
        }
        else
        {
          if (usedFull.Contains(shortKey) || usedShort.Contains(shortKey)) continue;
          usedShort.Add(shortKey);
        }

        counter++;
        string mateId = $"{EdgeMatePrefix}{counter:D2}";
        var edgeCi = allEdges[i].First(x => x.idx == ei).c;
        var edgeCj = allEdges[j].First(x => x.idx == ej).c;
        result[i].Add(new EdgeMateRecord
        {
          MateId = mateId, EdgeIndex = ei, Curve = edgeCi, Marker = score.Point1,
          MatePartIndex = j, MatePartNumber = j + 1, MateEdgeIndex = ej, Reversed = score.Reversed
        });
        result[j].Add(new EdgeMateRecord
        {
          MateId = mateId, EdgeIndex = ej, Curve = edgeCj, Marker = score.Point2,
          MatePartIndex = i, MatePartNumber = i + 1, MateEdgeIndex = ei, Reversed = !score.Reversed
        });
      }
      return result;
    }

    /// <summary>
    /// Port of Python edge_pair_score: samples the shorter curve and projects each point
    /// onto the longer curve via ClosestPoint. Handles partial edge overlaps.
    /// </summary>
    private static EdgePairResult? TestEdgePair(Curve ea, Curve eb, double absTol)
    {
      if (ea == null || eb == null) return null;
      double lenA = ea.GetLength(); if (lenA <= absTol) return null;
      double lenB = eb.GetLength(); if (lenB <= absTol) return null;

      bool   shortFirst = lenA <= lenB;
      var    shortCrv   = shortFirst ? ea : eb;
      var    longCrv    = shortFirst ? eb : ea;
      double shortLen   = Math.Min(lenA, lenB);
      double longLen    = Math.Max(lenA, lenB);
      double ratio      = shortLen / longLen;

      double matchTol = Math.Max(absTol * EdgeMateTolFactor,
                                 Math.Max(longLen, shortLen) * EdgeMateDiagFactor);

      var distances = new List<double>(EdgeMateSamples * 2);

      // Sample SHORT → project onto LONG
      for (int k = 0; k < EdgeMateSamples; k++)
      {
        var p = shortCrv.PointAt(shortCrv.Domain.ParameterAt((k + 0.5) / EdgeMateSamples));
        if (!longCrv.ClosestPoint(p, out double tl)) return null;
        distances.Add(p.DistanceTo(longCrv.PointAt(tl)));
      }
      // For full-length pairs, also sample LONG → project onto SHORT
      if (ratio >= 0.90)
      {
        for (int k = 0; k < EdgeMateSamples; k++)
        {
          var p = longCrv.PointAt(longCrv.Domain.ParameterAt((k + 0.5) / EdgeMateSamples));
          if (!shortCrv.ClosestPoint(p, out double ts)) return null;
          distances.Add(p.DistanceTo(shortCrv.PointAt(ts)));
        }
      }

      double maxD = distances.Max();
      double avgD = distances.Average();
      if (maxD > matchTol) return null;

      // Verify both endpoints of the short edge project onto the long edge
      foreach (var ep in new[] { shortCrv.PointAtStart, shortCrv.PointAtEnd })
      {
        if (!longCrv.ClosestPoint(ep, out double tl)) return null;
        if (ep.DistanceTo(longCrv.PointAt(tl)) > matchTol) return null;
      }

      // Marker: arc-length midpoint of short, closest point on long
      var shortMid = CurveMidpoint(shortCrv);
      longCrv.ClosestPoint(shortMid, out double tlMid);
      var longMid = longCrv.PointAt(tlMid);
      var pt1 = shortFirst ? shortMid : longMid;
      var pt2 = shortFirst ? longMid  : shortMid;

      // Reversed: compare tangent directions at the marker points
      bool reversed = false;
      if (ea.ClosestPoint(pt1, out double ta) && eb.ClosestPoint(pt2, out double tb))
      {
        var t1 = ea.TangentAt(ta);
        var t2 = eb.TangentAt(tb);
        if (!t1.IsZero && !t2.IsZero) reversed = t1 * t2 < 0.0;
      }

      return new EdgePairResult
      {
        MaxDist = maxD, AvgDist = avgD, Reversed = reversed,
        Full = ratio >= 0.90, ShortFirst = shortFirst,
        Point1 = pt1, Point2 = pt2
      };
    }

    private static Point3d CurveMidpoint(Curve c)
    {
      if (c.LengthParameter(c.GetLength() * 0.5, out double t))
        return c.PointAt(t);
      return c.PointAt(c.Domain.Mid);
    }

    /// <summary>
    /// Adds edge mate curves to the unroller. Deduplicates by EdgeIndex so each
    /// physical edge is added only once; later records sharing the same edge reuse
    /// the same output curve index.
    /// </summary>
    private static List<EdgeMateInfo> AddEdgeMateCurves(
      Unroller unroller, List<EdgeMateRecord> records, int startIdx)
    {
      var infos   = new List<EdgeMateInfo>();
      var seenEdge = new Dictionary<int, int>(); // edge_index → curveOutputIndex
      int offset  = 0;
      foreach (var rec in records)
      {
        if (seenEdge.TryGetValue(rec.EdgeIndex, out int existing))
        {
          infos.Add(new EdgeMateInfo { Record = rec, CurveOutputIndex = existing, Shared = true });
        }
        else
        {
          int idx = startIdx + offset++;
          if (rec.Curve != null) unroller.AddFollowingGeometry(rec.Curve);
          seenEdge[rec.EdgeIndex] = idx;
          infos.Add(new EdgeMateInfo { Record = rec, CurveOutputIndex = idx, Shared = false });
        }
      }
      return infos;
    }

    /// <summary>
    /// Matches edge mate output curves back to flat positions by scanning all unrolled curves
    /// for the best length match (Rhino does not preserve AddFollowingGeometry order).
    /// Hides matched curve indices in <paramref name="hiddenIdxs"/>.
    /// </summary>
    private static Dictionary<(string, int, int), Point3d> MatchEdgeCurveOutputs(
      Curve[]? curves, List<EdgeMateInfo> infos, HashSet<int> hiddenIdxs, double tol)
    {
      var result      = new Dictionary<(string, int, int), Point3d>();
      var matchedByEdge = new Dictionary<int, (int idx, Curve crv)>(); // edge_index → (outputIdx, curve)
      var usedIdxs    = new HashSet<int>(hiddenIdxs);

      foreach (var info in infos)
      {
        var rec = info.Record;
        if (rec.Curve == null) continue;
        double srcLen = rec.Curve.GetLength();
        if (srcLen <= tol) continue;

        int outIdx; Curve outCrv;

        if (matchedByEdge.TryGetValue(rec.EdgeIndex, out var existing))
        {
          outIdx = existing.idx;
          outCrv = existing.crv;
        }
        else
        {
          // Scan all unrolled curves (length-based — order not guaranteed by Rhino)
          int bestIdx = -1; Curve? bestCrv = null; double bestScore = double.MaxValue;
          if (curves != null)
          {
            for (int ci = 0; ci < curves.Length; ci++)
            {
              if (usedIdxs.Contains(ci) || curves[ci] == null) continue;
              double s = Math.Abs(curves[ci].GetLength() - srcLen) / Math.Max(srcLen, tol);
              if (s < bestScore) { bestScore = s; bestIdx = ci; bestCrv = curves[ci]; }
            }
          }
          if (bestIdx < 0 || bestScore > 0.20) continue;
          outIdx = bestIdx; outCrv = bestCrv!;
          usedIdxs.Add(outIdx);
          hiddenIdxs.Add(outIdx);
          matchedByEdge[rec.EdgeIndex] = (outIdx, outCrv);
        }

        // Map 3D marker position to its fractional parameter on the source curve,
        // then sample the same fraction on the unrolled curve for the flat position.
        double fraction = CurveFractionOfPoint(rec.Curve, rec.Marker, tol);
        result[(rec.MateId, rec.EdgeIndex, rec.MatePartIndex)] =
          outCrv.PointAt(outCrv.Domain.ParameterAt(fraction));
      }
      return result;
    }

    private static double CurveFractionOfPoint(Curve c, Point3d pt, double tol)
    {
      if (!c.ClosestPoint(pt, out double t)) return 0.5;
      return c.Domain.NormalizedParameterAt(t);
    }

    private static IEnumerable<EdgeMateInfo> UniqueEdgeMateInfos(List<EdgeMateInfo> infos)
    {
      var seen = new HashSet<(string, int, int)>();
      foreach (var info in infos)
        if (seen.Add((info.Record.MateId, info.Record.EdgeIndex, info.Record.MatePartIndex)))
          yield return info;
    }

    private static Guid AddEdgeMateDot(
      RhinoDoc doc, EdgeMateRecord rec, Point3d position, int partNumber, int layerIdx)
    {
      var dot  = new TextDot(rec.MateId, position) { FontHeight = EdgeMateDotSize };
      var attr = new ObjectAttributes();
      attr.Name = EdgeMateName;
      if (layerIdx >= 0) attr.LayerIndex = layerIdx;
      attr.SetUserString(EdgeMateIdKey,      rec.MateId);
      attr.SetUserString(EdgePartNumKey,      partNumber.ToString());
      attr.SetUserString(EdgeMatePartNumKey,  rec.MatePartNumber.ToString());
      attr.SetUserString(EdgeMateReversedKey, rec.Reversed ? "true" : "false");
      return doc.Objects.AddTextDot(dot, attr);
    }

    private class LayoutOptions
    {
      public Point3d StartPoint;
      public LabelMode LabelMode;
      public bool RotateFlatParts;
      public bool Explode;
      public bool SplitFaces;
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
      public Point3d RightPoint { get; }
      public Vector3d X { get; }
      public Vector3d Y { get; }
      public Vector3d Normal { get; }
      public double Height { get; }
      public double Step { get; }

      public LabelFrame(Point3d point, Point3d upPoint, Point3d rightPoint, Vector3d x, Vector3d y, Vector3d normal, double height, double step)
      {
        Point = point;
        UpPoint = upPoint;
        RightPoint = rightPoint;
        X = x;
        Y = y;
        Normal = normal;
        Height = height;
        Step = step;
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
      public List<Guid> SkippedIds { get; } = new List<Guid>();
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
