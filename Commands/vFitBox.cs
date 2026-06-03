using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native fit-box command ported from FitBox.py.
/// </summary>
public sealed class vFitBox : Command
{
  private const string OptionsSectionName = "vFitBox";
  private const string AngleStepKey = "angleStepDeg";
  private const string RotateKey = "rotate";
  private const string FitModeKey = "fitMode";

  private const double MinAngleStepDeg = 0.1;
  private const double MaxAngleStepDeg = 45.0;
  private const int MaxNormalSamples = 5000;
  private const double MinYawRefineStepDeg = 0.01;
  private const double Min2dFinalStepDeg = 0.001;

  private static double _angleStepDeg = 5.0;
  private static bool _rotate = false;
  private static string _fitMode = "height";

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vFitBox";

  /// <summary>
  /// Executes fit-box solve and adds resulting geometry.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    if (!TryPickObjectsWithOptions(doc, out var objectIds, out var angleStepDeg, out var rotate, out var fitMode))
    {
      _angleStepDeg = Clamp(angleStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
      _rotate = rotate;
      _fitMode = NormalizeFitMode(fitMode);
      SavePersistedOptions();
      return Result.Cancel;
    }

    _angleStepDeg = Clamp(angleStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
    _rotate = rotate;
    _fitMode = NormalizeFitMode(fitMode);
    SavePersistedOptions();

    Log.Write("vFitBox", $"RunCommand: {objectIds.Count} object(s) selected, angleStep={_angleStepDeg} fitMode={_fitMode} rotate={_rotate}");
    var geometries = CollectGeometries(doc, objectIds);
    if (geometries.Count == 0)
    {
      RhinoApp.WriteLine("vFitBox: no valid geometry found in selection.");
      return Result.Failure;
    }

    var basePlane = ActiveBasePlane(doc);
    var bestFit = FindBestFit(doc, geometries, basePlane, _angleStepDeg, _fitMode);
    if (bestFit == null)
    {
      RhinoApp.WriteLine("vFitBox: failed to calculate a fit box.");
      return Result.Failure;
    }
    RefineToAccurateBounds(bestFit, geometries);

    var fitId = AddFitGeometry(doc, bestFit);
    if (fitId == Guid.Empty)
    {
      RhinoApp.WriteLine("vFitBox: failed to add fit box geometry.");
      return Result.Failure;
    }

    var outputObjectIds = new List<Guid>(objectIds);

    if (rotate)
    {
      var sourcePlane = BuildRotationSourcePlane(bestFit, basePlane, doc.ModelAbsoluteTolerance);

      var pivotPoint = FitCenterPoint(bestFit);
      var transform = PlaneToPlaneRotation(sourcePlane, basePlane, pivotPoint);

      if (!transform.IsIdentity)
      {
        if (!TransformSelectedObjects(doc, objectIds, transform, out var transformedObjectIds))
        {
          RhinoApp.WriteLine("vFitBox: failed to rotate selected objects.");
          return Result.Failure;
        }

        outputObjectIds = transformedObjectIds;

        var transformedFitId = TransformObjectId(doc, fitId, transform);
        if (transformedFitId == Guid.Empty)
        {
          RhinoApp.WriteLine("vFitBox: failed to rotate fit box.");
          return Result.Failure;
        }

        fitId = transformedFitId;
      }
    }

    RhinoApp.WriteLine(FormatFitSizes(doc, bestFit));

    SelectFitResultObjects(doc, outputObjectIds, fitId);
    doc.Views.Redraw();
    return Result.Success;
  }

  /// <summary>
  /// Loads persisted command options from the shared config.
  /// </summary>
  private static void LoadPersistedOptions()
  {
    var options = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var angleStep = _angleStepDeg;
        var rotate = _rotate;
        var fitMode = _fitMode;

        if (ToolsOptionStore.TryGetDouble(section, AngleStepKey, out var persistedAngleStep))
          angleStep = Clamp(persistedAngleStep, MinAngleStepDeg, MaxAngleStepDeg);

        if (ToolsOptionStore.TryGetBool(section, RotateKey, out var persistedRotate))
          rotate = persistedRotate;

        if (ToolsOptionStore.TryGetString(section, FitModeKey, out var persistedFitMode))
          fitMode = NormalizeFitMode(persistedFitMode);

        return (angleStep, rotate, fitMode);
      });

    _angleStepDeg = options.angleStep;
    _rotate = options.rotate;
    _fitMode = NormalizeFitMode(options.fitMode);
  }

  /// <summary>
  /// Saves current command options to the shared config.
  /// </summary>
  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[AngleStepKey] = _angleStepDeg;
        section[RotateKey] = _rotate;
        section[FitModeKey] = _fitMode;
      });
  }

  // -- Selection preview conduit --------------------------------------------

  private sealed class SelectionPreviewConduit : DisplayConduit
  {
    public Box PreviewBox = Box.Unset;

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (!PreviewBox.IsValid) return;
      e.Display.DrawBox(PreviewBox, System.Drawing.Color.Gray, 1);
    }
  }

  private static void UpdatePreviewBox(
    RhinoDoc doc, SelectionPreviewConduit conduit, string fitMode, out string sizes)
  {
    sizes = string.Empty;
    var ids = CollectIdsFromDocSelection(doc);
    if (ids.Count == 0) { conduit.PreviewBox = Box.Unset; return; }
    var geoms = CollectGeometries(doc, ids);
    if (geoms.Count == 0) { conduit.PreviewBox = Box.Unset; return; }
    var basePlane = ActiveBasePlane(doc);
    // Use a coarse step for a fast preview; result is the same fitted box, just rough.
    var fit = FindBestFit(doc, geoms, basePlane, 5.0, fitMode);
    if (fit == null) { conduit.PreviewBox = Box.Unset; return; }
    RefineToAccurateBounds(fit, geoms);
    conduit.PreviewBox = new Box(
      fit.Plane,
      new Interval(fit.MinX, fit.MaxX),
      new Interval(fit.MinY, fit.MaxY),
      new Interval(fit.MinZ, fit.MaxZ));
    sizes = FormatFitSizes(doc, fit);
  }

  private static string FormatFitSizes(RhinoDoc doc, FitCandidate fit)
  {
    var outWidth = fit.Mode == "2d" ? Math.Max(fit.Width, fit.Depth) : fit.Width;
    var outHeight = fit.Mode == "2d" ? Math.Min(fit.Width, fit.Depth) : fit.Height;
    if (outWidth < outHeight)
      (outWidth, outHeight) = (outHeight, outWidth);
    var primaryIsFractional = IsFractionalDisplayMode(doc);
    var wP = FormatLengthByMode(doc, outWidth, primaryIsFractional);
    var hP = FormatLengthByMode(doc, outHeight, primaryIsFractional);
    var wA = FormatLengthByMode(doc, outWidth, !primaryIsFractional);
    var hA = FormatLengthByMode(doc, outHeight, !primaryIsFractional);
    return $"{wP} x {hP} ({wA} x {hA})";
  }

  /// <summary>
  /// Prompts for object selection and fit options.
  /// </summary>
  private static bool TryPickObjectsWithOptions(
    RhinoDoc doc,
    out List<Guid> objectIds,
    out double angleStepDeg,
    out bool rotate,
    out string fitMode)
  {
    objectIds = new List<Guid>();
    angleStepDeg = Clamp(_angleStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
    rotate = _rotate;
    fitMode = NormalizeFitMode(_fitMode);

    var go = new GetObject();
    go.SetCommandPrompt("Select objects");
    go.AcceptNothing(true);
    go.AcceptNumber(true, false);
    go.EnablePreSelect(true, true);
    go.AlreadySelectedObjectSelect = true;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var angleOption = new OptionDouble(angleStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
    var rotateToggle = new OptionToggle(rotate, "No", "Yes");
    var fitToggle = new OptionToggle(string.Equals(fitMode, "area", StringComparison.OrdinalIgnoreCase), "Height", "Area");
    var preselectedWaitingForConfirmation = false;
    var conduit = new SelectionPreviewConduit();
    conduit.Enabled = true;

    var lastPrintedSizes = string.Empty;

    // WinForms Timer fires on the UI message-pump thread, which continues running
    // during GetMultiple's modal loop — so it reliably fires 90 ms after the last
    // SelectObjects event, giving group expansion time to finish.
    var debounceTimer = new System.Windows.Forms.Timer { Interval = 90 };
    debounceTimer.Tick += (_, _) =>
    {
      debounceTimer.Stop();
      UpdatePreviewBox(doc, conduit, fitToggle.CurrentValue ? "area" : "height", out var sizes);
      doc.Views.Redraw();
      if (!string.IsNullOrEmpty(sizes) && sizes != lastPrintedSizes)
      {
        lastPrintedSizes = sizes;
        RhinoApp.WriteLine(sizes);
      }
    };

    // SelectObjects / DeselectObjects: restart the debounce window — no expensive work here.
    // Both use the same handler so group expand/collapse (which fires one event per object)
    // coalesces into a single UpdatePreviewBox call after the burst settles.
    EventHandler<RhinoObjectSelectionEventArgs> onSelectionChanged = (_, _) =>
    {
      debounceTimer.Stop();
      debounceTimer.Start();
    };

    RhinoDoc.SelectObjects   += onSelectionChanged;
    RhinoDoc.DeselectObjects += onSelectionChanged;

    try
    {
    while (true)
    {
      go.ClearCommandOptions();
      go.AddOptionDouble("AngleStep", ref angleOption);
      go.AddOptionToggle("Rotate", ref rotateToggle);
      go.AddOptionToggle("Fit", ref fitToggle);

      var result = go.GetMultiple(1, 0);
      debounceTimer.Stop();  // cancel any pending debounced update
      var currentFitMode = fitToggle.CurrentValue ? "area" : "height";
      UpdatePreviewBox(doc, conduit, currentFitMode, out var loopSizes);
      doc.Views.Redraw();
      if (!string.IsNullOrEmpty(loopSizes) && loopSizes != lastPrintedSizes)
      {
        lastPrintedSizes = loopSizes;
        RhinoApp.WriteLine(loopSizes);
      }
      if (go.CommandResult() != Result.Success)
      {
        angleStepDeg = angleOption.CurrentValue;
        rotate = rotateToggle.CurrentValue;
        fitMode = fitToggle.CurrentValue ? "area" : "height";
        return false;
      }

      if (result == GetResult.Option)
      {
        _angleStepDeg = Clamp(angleOption.CurrentValue, MinAngleStepDeg, MaxAngleStepDeg);
        _rotate = rotateToggle.CurrentValue;
        _fitMode = NormalizeFitMode(fitToggle.CurrentValue ? "area" : "height");
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        angleOption.CurrentValue = Clamp(go.Number(), MinAngleStepDeg, MaxAngleStepDeg);
        _angleStepDeg = angleOption.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Object && go.ObjectCount > 0)
      {
        if (go.ObjectsWerePreselected && !preselectedWaitingForConfirmation)
        {
          preselectedWaitingForConfirmation = true;
          go.EnablePreSelect(false, true);
          continue;
        }

        angleStepDeg = angleOption.CurrentValue;
        rotate = rotateToggle.CurrentValue;
        fitMode = fitToggle.CurrentValue ? "area" : "height";
        objectIds = CollectIdsFromDocSelection(doc);
        return objectIds.Count > 0;
      }

      if (result == GetResult.Nothing)
      {
        angleStepDeg = angleOption.CurrentValue;
        rotate = rotateToggle.CurrentValue;
        fitMode = fitToggle.CurrentValue ? "area" : "height";
        objectIds = CollectIdsFromDocSelection(doc);
        return objectIds.Count > 0;
      }

      angleStepDeg = angleOption.CurrentValue;
      rotate = rotateToggle.CurrentValue;
      fitMode = fitToggle.CurrentValue ? "area" : "height";
      return false;
    }
    }
    finally
    {
      RhinoDoc.SelectObjects   -= onSelectionChanged;
      RhinoDoc.DeselectObjects -= onSelectionChanged;
      debounceTimer.Stop();
      debounceTimer.Dispose();
      conduit.Enabled = false;
      doc.Views.Redraw();
    }
  }

  /// <summary>
  /// Returns deduplicated object ids from current document selection.
  /// </summary>
  private static List<Guid> CollectIdsFromDocSelection(RhinoDoc doc)
  {
    var ids = new List<Guid>();
    var seen = new HashSet<Guid>();

    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj == null)
        continue;

      var id = obj.Id;
      if (id == Guid.Empty || !seen.Add(id))
        continue;

      ids.Add(id);
    }

    return ids;
  }

  /// <summary>
  /// Collects geometry payload from selected object ids.
  /// </summary>
  private static List<GeometryBase> CollectGeometries(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var geometries = new List<GeometryBase>();

    foreach (var id in objectIds)
    {
      if (id == Guid.Empty)
        continue;

      var obj = doc.Objects.FindId(id);
      var geometry = obj?.Geometry;
      if (geometry != null)
      {
        var bb = geometry.GetBoundingBox(true);
        Log.Write("vFitBox", $"  geom {geometry.GetType().Name} bbox=({bb.Min.X:F3},{bb.Min.Y:F3},{bb.Min.Z:F3})-({bb.Max.X:F3},{bb.Max.Y:F3},{bb.Max.Z:F3})");
        geometries.Add(geometry);
      }
    }

    Log.Write("vFitBox", $"CollectGeometries: {geometries.Count} object(s)");
    return geometries;
  }

  /// <summary>
  /// Returns the active construction plane, falling back to WorldXY.
  /// </summary>
  private static Plane ActiveBasePlane(RhinoDoc doc)
  {
    var view = doc.Views.ActiveView;
    if (view == null)
      return Plane.WorldXY;

    try
    {
      var plane = view.ActiveViewport.ConstructionPlane();
      if (plane.IsValid)
        return plane;
    }
    catch
    {
    }

    return Plane.WorldXY;
  }

  /// <summary>
  /// Main fit solver matching the Python command behavior.
  /// </summary>
  private static FitCandidate? FindBestFit(
    RhinoDoc doc,
    IReadOnlyList<GeometryBase> geometries,
    Plane basePlane,
    double angleStepDeg,
    string fitMode)
  {
    if (geometries.Count == 0)
      return null;

    var normalizedFitMode = NormalizeFitMode(fitMode);
    var objective3d = normalizedFitMode == "area" ? ScoreMode.Area : ScoreMode.Height;
    var objective2d = normalizedFitMode == "area" ? ScoreMode.Area : ScoreMode.Strip;

    var (requestedStepDeg, stableStepDeg) = StabilizedStepDeg(angleStepDeg);
    var planarTolerance = Math.Max(doc.ModelAbsoluteTolerance * 5.0, 1.0e-9);

    var isBasePlanar = false;
    if (TryUnionBoundsInPlane(geometries, basePlane, out var baseBounds))
    {
      var baseHeight = Math.Max(0.0, baseBounds.MaxZ - baseBounds.MinZ);
      isBasePlanar = baseHeight <= planarTolerance;
    }

    if (isBasePlanar)
    {
      var planarBest = FindBestYawForPlane2dRefined(geometries, basePlane, requestedStepDeg, objective2d);
      if (planarBest == null)
        return null;

      planarBest.Normal = basePlane.ZAxis;
      planarBest.TestedNormals = 1;
      planarBest.RequestedStepDeg = requestedStepDeg;
      planarBest.EffectiveStepDeg = requestedStepDeg;
      planarBest.Mode = "2d";
      planarBest.FitMode = normalizedFitMode;
      CanonicalizeCandidate(planarBest);
      Log.Write("vFitBox", $"FindBestFit 2d: angle={planarBest.AngleDeg:F4}° W={planarBest.Width:F4} D={planarBest.Depth:F4} H={planarBest.Height:F4} area={planarBest.Area:F4}");
      return planarBest;
    }

    var candidateNormals = GenerateCandidateNormals(basePlane, stableStepDeg);
    var testedNormals = 0;
    Plane? bestNormalPlane = null;
    Vector3d? bestNormal = null;
    FitCandidate? bestNormalCandidate = null;

    foreach (var normal in candidateNormals)
    {
      var plane = BuildPlaneFromNormal(basePlane.Origin, normal, basePlane.XAxis, basePlane.YAxis);
      if (!plane.IsValid)
        continue;

      if (!TryUnionBoundsInPlane(geometries, plane, out var bounds))
        continue;

      testedNormals++;
      var candidate = BestFromBounds(plane, 0.0, bounds);

      if (bestNormalCandidate == null || IsCandidateBetter(candidate, bestNormalCandidate, objective3d))
      {
        bestNormalCandidate = candidate;
        bestNormalPlane = plane;
        bestNormal = normal;
      }
    }

    if (!bestNormalPlane.HasValue || !bestNormal.HasValue)
      return null;

    var best = FindBestYawForPlane(geometries, bestNormalPlane.Value, stableStepDeg, objective3d);
    if (best == null)
      return null;

    // Refine yaw with high precision when resulting orientation is effectively planar.
    if (best.Height <= planarTolerance)
    {
      var refined = FindBestYawForPlane2dRefined(geometries, bestNormalPlane.Value, requestedStepDeg, objective2d);
      if (refined != null)
        best = refined;
    }

    best.Normal = bestNormal.Value;
    best.TestedNormals = testedNormals;
    best.RequestedStepDeg = requestedStepDeg;
    best.EffectiveStepDeg = stableStepDeg;
    best.Mode = "3d";
    best.FitMode = normalizedFitMode;
    CanonicalizeCandidate(best);
    Log.Write("vFitBox", $"FindBestFit 3d: angle={best.AngleDeg:F4}° normals={testedNormals} W={best.Width:F4} D={best.Depth:F4} H={best.Height:F4} area={best.Area:F4} normal=({best.Normal.X:F4},{best.Normal.Y:F4},{best.Normal.Z:F4})");
    return best;
  }

  /// <summary>
  /// Ensures Width >= Depth by rotating the fit plane 90° around ZAxis when needed.
  /// Recomputes fit candidate bounds using accurate per-geometry transforms.
  /// GetBoundingBox(Plane) uses control-point approximation for curves, which
  /// overshoots the actual geometry. This fixes the placed box size.
  /// </summary>
  private static void RefineToAccurateBounds(FitCandidate c, IReadOnlyList<GeometryBase> geometries)
  {
    var toLocal = Transform.PlaneToPlane(c.Plane, Plane.WorldXY);
    var first = true;
    var minX = 0.0; var maxX = 0.0;
    var minY = 0.0; var maxY = 0.0;
    var minZ = 0.0; var maxZ = 0.0;

    foreach (var g in geometries)
    {
      BoundingBox bb;
      try
      {
        // AnnotationBase (TextEntity etc.) returns wrong bbox after plane transform;
        // use world bbox projection instead.
        if (g is AnnotationBase)
        {
          var world = g.GetBoundingBox(true);
          if (!world.IsValid) continue;
          bb = WorldBBoxToPlaneBBox(world, c.Plane);
          if (!bb.IsValid) continue;
        }
        else
        {
          var copy = g.Duplicate();
          if (copy == null || !copy.Transform(toLocal)) continue;
          bb = copy.GetBoundingBox(true);
          if (!bb.IsValid) continue;
        }
      }
      catch { continue; }

      Log.Write("vFitBox", $"  refine {g.GetType().Name}: localY=[{bb.Min.Y:F3},{bb.Max.Y:F3}] localX=[{bb.Min.X:F3},{bb.Max.X:F3}]");

      if (first)
      {
        minX = bb.Min.X; maxX = bb.Max.X;
        minY = bb.Min.Y; maxY = bb.Max.Y;
        minZ = bb.Min.Z; maxZ = bb.Max.Z;
        first = false;
      }
      else
      {
        minX = Math.Min(minX, bb.Min.X); maxX = Math.Max(maxX, bb.Max.X);
        minY = Math.Min(minY, bb.Min.Y); maxY = Math.Max(maxY, bb.Max.Y);
        minZ = Math.Min(minZ, bb.Min.Z); maxZ = Math.Max(maxZ, bb.Max.Z);
      }
    }

    if (first) return;
    c.MinX = minX; c.MaxX = maxX;
    c.MinY = minY; c.MaxY = maxY;
    c.MinZ = minZ; c.MaxZ = maxZ;
    c.Width  = Math.Max(0.0, maxX - minX);
    c.Depth  = Math.Max(0.0, maxY - minY);
    c.Height = Math.Max(0.0, maxZ - minZ);
    c.Area   = c.Width * c.Depth;
    c.Volume = c.Area * c.Height;
    Log.Write("vFitBox", $"RefineToAccurateBounds: W={c.Width:F4} D={c.Depth:F4} H={c.Height:F4} X=[{c.MinX:F4},{c.MaxX:F4}] Y=[{c.MinY:F4},{c.MaxY:F4}] Z=[{c.MinZ:F4},{c.MaxZ:F4}]");
  }

  /// <summary>
  /// This makes the result canonical regardless of which of two equivalent solver
  /// solutions (30° vs 120°) was returned, so rotation direction and reported
  /// dimensions are always deterministic and a second run produces an identical result.
  /// </summary>
  private static void CanonicalizeCandidate(FitCandidate c)
  {
    if (c.Depth <= c.Width + 1e-10) return;  // already canonical

    // Rotate plane 90° CCW around ZAxis: new.XAxis = old.YAxis, new.YAxis = -old.XAxis.
    // Under this rotation point coordinates transform as: new_x = old_y, new_y = -old_x.
    c.Plane = RotatePlane(c.Plane, Math.PI / 2.0);
    var oldMinX = c.MinX; var oldMaxX = c.MaxX;
    c.MinX = c.MinY;  c.MaxX = c.MaxY;
    c.MinY = -oldMaxX; c.MaxY = -oldMinX;
    (c.Width, c.Depth) = (c.Depth, c.Width);
  }

  /// <summary>
  /// Converts a solved fit candidate into Rhino geometry.
  /// </summary>
  private static Guid AddFitGeometry(RhinoDoc doc, FitCandidate fit)
  {
    if (fit.Height <= doc.ModelAbsoluteTolerance)
    {
      var p0 = fit.Plane.PointAt(fit.MinX, fit.MinY, fit.MinZ);
      var p1 = fit.Plane.PointAt(fit.MaxX, fit.MinY, fit.MinZ);
      var p2 = fit.Plane.PointAt(fit.MaxX, fit.MaxY, fit.MinZ);
      var p3 = fit.Plane.PointAt(fit.MinX, fit.MaxY, fit.MinZ);
      var polyline = new Polyline(new[] { p0, p1, p2, p3, p0 });
      return doc.Objects.AddPolyline(polyline);
    }

    var box = new Box(
      fit.Plane,
      new Interval(fit.MinX, fit.MaxX),
      new Interval(fit.MinY, fit.MaxY),
      new Interval(fit.MinZ, fit.MaxZ));

    if (!box.IsValid)
      return Guid.Empty;

    var brep = box.ToBrep();
    if (brep == null)
      return Guid.Empty;

    return doc.Objects.AddBrep(brep);
  }

  /// <summary>
  /// Returns center point of fit bounds in fit plane coordinates.
  /// </summary>
  private static Point3d? FitCenterPoint(FitCandidate fit)
  {
    if (!fit.Plane.IsValid)
      return null;

    var centerX = 0.5 * (fit.MinX + fit.MaxX);
    var centerY = 0.5 * (fit.MinY + fit.MaxY);
    var centerZ = 0.5 * (fit.MinZ + fit.MaxZ);
    var center = fit.Plane.PointAt(centerX, centerY, centerZ);
    return center.IsValid ? center : null;
  }

  /// <summary>
  /// Builds a final-rotation source plane that maps the fit plane directly to the
  /// target (base) plane, optionally flipping 180° to avoid unnecessary reversals.
  /// No axis swap is done — swapping Width/Depth breaks idempotency (a second run
  /// after rotation would find swapped dimensions).
  /// </summary>
  private static Plane BuildRotationSourcePlane(FitCandidate fit, Plane targetPlane, double tolerance)
  {
    if (!fit.Plane.IsValid || !targetPlane.IsValid)
      return fit.Plane.IsValid ? fit.Plane : Plane.WorldXY;

    var primary = new Plane(fit.Plane.Origin, fit.Plane.XAxis, fit.Plane.YAxis);
    if (!primary.IsValid)
      return fit.Plane;

    var flipped = new Plane(fit.Plane.Origin, -fit.Plane.XAxis, -fit.Plane.YAxis);
    if (!flipped.IsValid)
      return primary;

    var primaryScore = AxisAlignmentScore(primary, targetPlane);
    var flippedScore = AxisAlignmentScore(flipped, targetPlane);
    return flippedScore > primaryScore ? flipped : primary;
  }

  /// <summary>
  /// Scores in-plane axis alignment between two planes.
  /// </summary>
  private static double AxisAlignmentScore(Plane sourcePlane, Plane targetPlane)
  {
    return Vector3d.Multiply(sourcePlane.XAxis, targetPlane.XAxis)
      + Vector3d.Multiply(sourcePlane.YAxis, targetPlane.YAxis);
  }

  /// <summary>
  /// Builds a stable plane-to-plane transform around an optional shared pivot.
  /// </summary>
  private static Transform PlaneToPlaneRotation(Plane fromPlane, Plane toPlane, Point3d? pivotPoint)
  {
    if (!fromPlane.IsValid || !toPlane.IsValid)
      return Transform.Identity;

    var sourcePlane = fromPlane;
    var targetPlane = toPlane;

    if (pivotPoint.HasValue)
    {
      try
      {
        sourcePlane = new Plane(pivotPoint.Value, fromPlane.XAxis, fromPlane.YAxis);
        targetPlane = new Plane(pivotPoint.Value, toPlane.XAxis, toPlane.YAxis);
      }
      catch
      {
        sourcePlane = fromPlane;
        targetPlane = toPlane;
      }
    }

    try
    {
      var transform = Transform.PlaneToPlane(sourcePlane, targetPlane);
      if (transform.IsValid)
        return transform;
    }
    catch
    {
    }

    try
    {
      var transform = Transform.PlaneToPlane(fromPlane, toPlane);
      if (transform.IsValid)
        return transform;
    }
    catch
    {
    }

    return Transform.Identity;
  }

  /// <summary>
  /// Transforms one object id in place.
  /// </summary>
  private static Guid TransformObjectId(RhinoDoc doc, Guid objectId, Transform transform)
  {
    if (objectId == Guid.Empty)
      return Guid.Empty;

    try
    {
      return doc.Objects.Transform(objectId, transform, true);
    }
    catch
    {
      return Guid.Empty;
    }
  }

  /// <summary>
  /// Transforms selected objects in place, returning transformed ids.
  /// </summary>
  private static bool TransformSelectedObjects(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds,
    Transform transform,
    out List<Guid> transformedObjectIds)
  {
    transformedObjectIds = new List<Guid>();
    var seen = new HashSet<Guid>();

    foreach (var objectId in objectIds)
    {
      if (objectId == Guid.Empty)
        continue;

      var transformedId = TransformObjectId(doc, objectId, transform);
      if (transformedId == Guid.Empty)
        return false;

      if (seen.Add(transformedId))
        transformedObjectIds.Add(transformedId);
    }

    return true;
  }

  /// <summary>
  /// Selects the transformed source objects and fit object as final command output.
  /// </summary>
  private static void SelectFitResultObjects(RhinoDoc doc, IEnumerable<Guid> sourceObjectIds, Guid fitId)
  {
    doc.Objects.UnselectAll();

    var seen = new HashSet<Guid>();
    foreach (var objectId in sourceObjectIds)
    {
      if (objectId == Guid.Empty || !seen.Add(objectId))
        continue;

      doc.Objects.Select(objectId);
    }

    if (fitId != Guid.Empty)
      doc.Objects.Select(fitId);
  }

  /// <summary>
  /// Chooses decimal or fractional formatting path.
  /// </summary>
  private static string FormatLengthByMode(RhinoDoc doc, double value, bool fractional)
  {
    return fractional ? FormatLengthFractional(doc, value) : FormatLengthDecimal(doc, value);
  }

  /// <summary>
  /// Detects whether current workspace distance display is fractional-like.
  /// </summary>
  private static bool IsFractionalDisplayMode(RhinoDoc doc)
  {
    try
    {
      var sample = doc.FormatNumber(1.5);
      if (!string.IsNullOrWhiteSpace(sample))
        return LooksFractional(sample);
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Formats one value in workspace decimal style.
  /// </summary>
  private static string FormatLengthDecimal(RhinoDoc doc, double value)
  {
    try
    {
      var formatted = doc.FormatNumber(value);
      if (!string.IsNullOrWhiteSpace(formatted) && !LooksFractional(formatted))
      {
        var trimmed = formatted.Trim();
        return trimmed == "-0" ? "0" : trimmed;
      }
    }
    catch
    {
    }

    var decimals = Math.Max(0, Math.Min(10, doc.DistanceDisplayPrecision));
    var text = value.ToString($"F{decimals}", CultureInfo.CurrentCulture);

    if (text.IndexOf(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal) >= 0)
      text = text.TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray());

    if (text == "-0")
      text = "0";

    return text;
  }

  /// <summary>
  /// Formats one value in fractional style and normalizes for command-line copy/paste.
  /// </summary>
  private static string FormatLengthFractional(RhinoDoc doc, double value)
  {
    try
    {
      var formatted = doc.FormatNumber(value);
      if (!string.IsNullOrWhiteSpace(formatted) && LooksFractional(formatted))
        return NormalizeFractionalForCommandInput(formatted);
    }
    catch
    {
    }

    var denominator = FractionDenominatorFromPrecision(doc.DistanceDisplayPrecision);
    return FormatFractionalWithPlus(value, denominator);
  }

  /// <summary>
  /// Returns true when a formatted number string looks fractional or feet/inches style.
  /// </summary>
  private static bool LooksFractional(string text)
  {
    return text.Contains('/', StringComparison.Ordinal)
      || text.Contains("'", StringComparison.Ordinal)
      || text.Contains('"', StringComparison.Ordinal);
  }

  /// <summary>
  /// Converts precision index to power-of-two denominator.
  /// </summary>
  private static int FractionDenominatorFromPrecision(int precision)
  {
    var p = Math.Max(0, Math.Min(8, precision));
    return p <= 0 ? 1 : 1 << p;
  }

  /// <summary>
  /// Formats one decimal value as mixed fraction using '+' between whole and fraction.
  /// </summary>
  private static string FormatFractionalWithPlus(double value, int denominator)
  {
    if (denominator <= 0)
      denominator = 1;

    var negative = value < 0.0;
    var absoluteValue = Math.Abs(value);
    var whole = (long)Math.Floor(absoluteValue);
    var fraction = absoluteValue - whole;

    var numerator = (int)Math.Round(fraction * denominator, MidpointRounding.AwayFromZero);
    if (numerator >= denominator)
    {
      whole += 1;
      numerator = 0;
    }

    string core;
    if (numerator == 0)
    {
      core = whole.ToString(CultureInfo.CurrentCulture);
    }
    else
    {
      var gcd = GreatestCommonDivisor(numerator, denominator);
      var reducedNumerator = numerator / gcd;
      var reducedDenominator = denominator / gcd;
      core = whole > 0
        ? whole.ToString(CultureInfo.CurrentCulture) + "+" + reducedNumerator.ToString(CultureInfo.CurrentCulture) + "/" + reducedDenominator.ToString(CultureInfo.CurrentCulture)
        : reducedNumerator.ToString(CultureInfo.CurrentCulture) + "/" + reducedDenominator.ToString(CultureInfo.CurrentCulture);
    }

    return negative ? "-" + core : core;
  }

  /// <summary>
  /// Returns greatest common divisor for integer reduction.
  /// </summary>
  private static int GreatestCommonDivisor(int a, int b)
  {
    a = Math.Abs(a);
    b = Math.Abs(b);
    while (b != 0)
    {
      var t = a % b;
      a = b;
      b = t;
    }

    return a == 0 ? 1 : a;
  }

  /// <summary>
  /// Normalizes fractional text to Rhino command-line friendly syntax.
  /// </summary>
  private static string NormalizeFractionalForCommandInput(string text)
  {
    var normalized = (text ?? string.Empty).Trim();
    if (normalized.Length == 0)
      return normalized;

    if (!normalized.Contains('/', StringComparison.Ordinal))
      return normalized;

    // Normalize fraction bars and convert mixed-number separators to plus form.
    normalized = Regex.Replace(normalized, @"\s*/\s*", "/");
    normalized = Regex.Replace(normalized, @"(?<=\d)\s*-\s*(?=\d+/\d+)", "+");
    normalized = Regex.Replace(normalized, @"(?<=\d)\s+(?=\d+/\d+)", "+");

    // Command-line paste should contain no spaces.
    normalized = normalized.Replace(" ", string.Empty);
    return normalized;
  }

  /// <summary>
  /// Clamps one numeric value to an inclusive range.
  /// </summary>
  private static double Clamp(double value, double min, double max)
  {
    return Math.Max(min, Math.Min(max, value));
  }

  /// <summary>
  /// Normalizes fit mode token.
  /// </summary>
  private static string NormalizeFitMode(string mode)
  {
    return string.Equals(mode, "area", StringComparison.OrdinalIgnoreCase) ? "area" : "height";
  }

  /// <summary>
  /// Stabilizes angular step to keep 3D normal sampling below cap.
  /// </summary>
  private static (double RequestedStepDeg, double StableStepDeg) StabilizedStepDeg(double requestedStepDeg)
  {
    var requested = Clamp(requestedStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
    var stable = requested;

    while (stable < MaxAngleStepDeg)
    {
      var azCount = Math.Max(1, (int)Math.Ceiling(360.0 / stable));
      var tiltCount = (int)Math.Floor(90.0 / stable) + 1;
      var approx = 1 + Math.Max(0, tiltCount - 1) * azCount;
      if (approx <= MaxNormalSamples)
        break;

      stable *= 1.2;
    }

    return (requested, Clamp(stable, MinAngleStepDeg, MaxAngleStepDeg));
  }

  /// <summary>
  /// Generates unit candidate normals over a hemispherical angular grid.
  /// </summary>
  private static List<Vector3d> GenerateCandidateNormals(Plane basePlane, double stepDeg)
  {
    var normals = new List<Vector3d>();
    var tiltDeg = 0.0;

    while (tiltDeg <= 90.0 + (0.5 * stepDeg))
    {
      var tiltRad = RhinoMath.ToRadians(tiltDeg);
      var sinTilt = Math.Sin(tiltRad);
      var cosTilt = Math.Cos(tiltRad);

      List<double> azimuthValues;
      if (tiltDeg <= 0.5 * stepDeg)
      {
        azimuthValues = new List<double> { 0.0 };
      }
      else
      {
        azimuthValues = new List<double>();
        var azimuthDeg = 0.0;
        while (azimuthDeg < 360.0 - (0.5 * stepDeg))
        {
          azimuthValues.Add(azimuthDeg);
          azimuthDeg += stepDeg;
        }
      }

      foreach (var azimuthDeg in azimuthValues)
      {
        var azimuthRad = RhinoMath.ToRadians(azimuthDeg);
        var xWeight = sinTilt * Math.Cos(azimuthRad);
        var yWeight = sinTilt * Math.Sin(azimuthRad);
        var zWeight = cosTilt;

        var normal = new Vector3d(
          basePlane.XAxis.X * xWeight + basePlane.YAxis.X * yWeight + basePlane.ZAxis.X * zWeight,
          basePlane.XAxis.Y * xWeight + basePlane.YAxis.Y * yWeight + basePlane.ZAxis.Y * zWeight,
          basePlane.XAxis.Z * xWeight + basePlane.YAxis.Z * yWeight + basePlane.ZAxis.Z * zWeight);

        if (normal.IsTiny() || !normal.Unitize())
          continue;

        normals.Add(normal);
      }

      tiltDeg += stepDeg;
    }

    return normals;
  }

  /// <summary>
  /// Builds a valid plane from explicit normal and fallback reference axes.
  /// </summary>
  private static Plane BuildPlaneFromNormal(Point3d origin, Vector3d normal, Vector3d referenceX, Vector3d referenceY)
  {
    var zAxis = new Vector3d(normal);
    if (zAxis.IsTiny() || !zAxis.Unitize())
      return Plane.Unset;

    var xAxis = Vector3d.CrossProduct(referenceY, zAxis);
    if (xAxis.IsTiny())
      xAxis = Vector3d.CrossProduct(referenceX, zAxis);
    if (xAxis.IsTiny())
      xAxis = Vector3d.CrossProduct(Vector3d.ZAxis, zAxis);
    if (xAxis.IsTiny())
      xAxis = Vector3d.CrossProduct(Vector3d.XAxis, zAxis);
    if (xAxis.IsTiny() || !xAxis.Unitize())
      return Plane.Unset;

    var yAxis = Vector3d.CrossProduct(zAxis, xAxis);
    if (yAxis.IsTiny() || !yAxis.Unitize())
      return Plane.Unset;

    var plane = new Plane(origin, xAxis, yAxis);
    return plane.IsValid ? plane : Plane.Unset;
  }

  /// <summary>
  /// Rotates one plane around its Z axis by yaw angle.
  /// </summary>
  private static Plane RotatePlane(Plane plane, double angleRadians)
  {
    var rotated = new Plane(plane);
    var transform = Transform.Rotation(angleRadians, plane.ZAxis, plane.Origin);
    return rotated.Transform(transform) ? rotated : Plane.Unset;
  }

  /// <summary>
  /// Converts world-space bounding box corners into plane-local bounds.
  /// </summary>
  private static BoundingBox WorldBBoxToPlaneBBox(BoundingBox worldBBox, Plane plane)
  {
    if (!worldBBox.IsValid || !plane.IsValid)
      return BoundingBox.Empty;

    var corners = worldBBox.GetCorners();
    if (corners == null || corners.Length == 0)
      return BoundingBox.Empty;

    var hasBounds = false;
    var minX = 0.0;
    var maxX = 0.0;
    var minY = 0.0;
    var maxY = 0.0;
    var minZ = 0.0;
    var maxZ = 0.0;

    foreach (var point in corners)
    {
      var vector = point - plane.Origin;
      var x = Vector3d.Multiply(vector, plane.XAxis);
      var y = Vector3d.Multiply(vector, plane.YAxis);
      var z = Vector3d.Multiply(vector, plane.ZAxis);

      if (!hasBounds)
      {
        minX = maxX = x;
        minY = maxY = y;
        minZ = maxZ = z;
        hasBounds = true;
      }
      else
      {
        minX = Math.Min(minX, x);
        maxX = Math.Max(maxX, x);
        minY = Math.Min(minY, y);
        maxY = Math.Max(maxY, y);
        minZ = Math.Min(minZ, z);
        maxZ = Math.Max(maxZ, z);
      }
    }

    if (!hasBounds)
      return BoundingBox.Empty;

    return new BoundingBox(
      new Point3d(minX, minY, minZ),
      new Point3d(maxX, maxY, maxZ));
  }

  /// <summary>
  /// Gets one geometry bounding box in given plane coordinates.
  /// </summary>
  private static bool TryGeometryBBoxInPlane(GeometryBase geometry, Plane plane, out BoundingBox bbox)
  {
    bbox = BoundingBox.Empty;
    if (geometry == null || !plane.IsValid)
      return false;

    // AnnotationBase (TextEntity, Leader, etc.) returns an unreliable bbox after
    // plane transformation — the text-height/layout is recomputed in the new frame
    // and inflates the box. Use the world bbox projection fallback for these types.
    if (geometry is not AnnotationBase)
    {
    // Transform a duplicate to plane-local space and measure accurately.
    // GetBoundingBox(Plane) uses control-point hulls for curves, which overshoots
    // and causes the solver to minimize a different objective than the real area.
    try
    {
      var toLocal = Transform.PlaneToPlane(plane, Plane.WorldXY);
      var copy = geometry.Duplicate();
      if (copy != null && copy.Transform(toLocal))
      {
        var bb = copy.GetBoundingBox(true);
        if (bb.IsValid)
        {
          bbox = bb;
          return true;
        }
      }
    }
    catch
    {
    }
    } // end if (geometry is not AnnotationBase)

    try
    {
      var world = geometry.GetBoundingBox(true);
      if (world.IsValid)
      {
        var projected = WorldBBoxToPlaneBBox(world, plane);
        if (projected.IsValid)
        {
          bbox = projected;
          return true;
        }
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Returns merged bounds across all geometries in one plane.
  /// </summary>
  private static bool TryUnionBoundsInPlane(IReadOnlyList<GeometryBase> geometries, Plane plane, out PlaneBounds bounds)
  {
    bounds = default;

    var hasBounds = false;
    var minX = 0.0;
    var maxX = 0.0;
    var minY = 0.0;
    var maxY = 0.0;
    var minZ = 0.0;
    var maxZ = 0.0;

    foreach (var geometry in geometries)
    {
      if (!TryGeometryBBoxInPlane(geometry, plane, out var bbox))
        continue;

      if (!hasBounds)
      {
        minX = bbox.Min.X;
        maxX = bbox.Max.X;
        minY = bbox.Min.Y;
        maxY = bbox.Max.Y;
        minZ = bbox.Min.Z;
        maxZ = bbox.Max.Z;
        hasBounds = true;
      }
      else
      {
        minX = Math.Min(minX, bbox.Min.X);
        maxX = Math.Max(maxX, bbox.Max.X);
        minY = Math.Min(minY, bbox.Min.Y);
        maxY = Math.Max(maxY, bbox.Max.Y);
        minZ = Math.Min(minZ, bbox.Min.Z);
        maxZ = Math.Max(maxZ, bbox.Max.Z);
      }
    }

    if (!hasBounds)
      return false;

    bounds = new PlaneBounds(minX, maxX, minY, maxY, minZ, maxZ);
    return true;
  }

  /// <summary>
  /// Normalizes yaw to [0,180).
  /// </summary>
  private static double NormalizeYawDeg(double angleDeg)
  {
    var yaw = angleDeg % 180.0;
    if (yaw < 0.0)
      yaw += 180.0;
    return yaw;
  }

  /// <summary>
  /// Builds candidate model from solved plane bounds.
  /// </summary>
  private static FitCandidate BestFromBounds(Plane plane, double angleDeg, PlaneBounds bounds)
  {
    var width = Math.Max(0.0, bounds.MaxX - bounds.MinX);
    var depth = Math.Max(0.0, bounds.MaxY - bounds.MinY);
    var height = Math.Max(0.0, bounds.MaxZ - bounds.MinZ);
    var area = width * depth;

    return new FitCandidate
    {
      Plane = plane,
      AngleDeg = angleDeg,
      MinX = bounds.MinX,
      MaxX = bounds.MaxX,
      MinY = bounds.MinY,
      MaxY = bounds.MaxY,
      MinZ = bounds.MinZ,
      MaxZ = bounds.MaxZ,
      Width = width,
      Depth = depth,
      Height = height,
      Area = area,
      Volume = area * height,
      Mode = "3d",
      FitMode = "height"
    };
  }

  /// <summary>
  /// Evaluates one yaw angle around a fixed plane normal.
  /// </summary>
  private static FitCandidate? EvaluateYawCandidate(IReadOnlyList<GeometryBase> geometries, Plane basePlane, double yawDeg)
  {
    var plane = RotatePlane(basePlane, RhinoMath.ToRadians(yawDeg));
    if (!plane.IsValid)
      return null;

    if (!TryUnionBoundsInPlane(geometries, plane, out var bounds))
      return null;

    return BestFromBounds(plane, NormalizeYawDeg(yawDeg), bounds);
  }

  /// <summary>
  /// Finds local best yaw around center angle within a finite window.
  /// </summary>
  private static (FitCandidate? Candidate, int Tested) FindBestYawLocal(
    IReadOnlyList<GeometryBase> geometries,
    Plane basePlane,
    double centerDeg,
    double halfWindowDeg,
    double stepDeg,
    ScoreMode scoreMode)
  {
    var step = Math.Max(MinYawRefineStepDeg, stepDeg);
    var span = Math.Max(step, halfWindowDeg);
    var best = default(FitCandidate);
    var tested = 0;

    var offsetDeg = -span;
    while (offsetDeg <= span + (0.5 * step))
    {
      var candidate = EvaluateYawCandidate(geometries, basePlane, centerDeg + offsetDeg);
      if (candidate != null)
      {
        tested++;
        if (best == null || IsCandidateBetter(candidate, best, scoreMode))
          best = candidate;
      }

      offsetDeg += step;
    }

    return (best, tested);
  }

  /// <summary>
  /// Finds best yaw over 0-180 degrees for one fixed plane normal.
  /// </summary>
  private static FitCandidate? FindBestYawForPlane(
    IReadOnlyList<GeometryBase> geometries,
    Plane basePlane,
    double stepDeg,
    ScoreMode scoreMode)
  {
    var yawDeg = 0.0;
    var best = default(FitCandidate);
    var tested = 0;

    while (yawDeg < 180.0 + (0.5 * stepDeg))
    {
      var candidate = EvaluateYawCandidate(geometries, basePlane, yawDeg);
      if (candidate != null)
      {
        tested++;
        if (best == null || IsCandidateBetter(candidate, best, scoreMode))
          best = candidate;
      }

      yawDeg += stepDeg;
    }

    if (best == null)
      return null;

    best.TestedYaws = tested;
    return best;
  }

  /// <summary>
  /// Refines planar yaw search to high precision around coarse minimum.
  /// </summary>
  private static FitCandidate? FindBestYawForPlane2dRefined(
    IReadOnlyList<GeometryBase> geometries,
    Plane basePlane,
    double stepDeg,
    ScoreMode scoreMode)
  {
    var coarseStep = Clamp(Math.Min(stepDeg, 0.25), MinAngleStepDeg, 0.25);
    var best = FindBestYawForPlane(geometries, basePlane, coarseStep, scoreMode);
    if (best == null)
      return null;

    Log.Write("vFitBox", $"  coarse best: angle={best.AngleDeg:F4}° W={best.Width:F4} D={best.Depth:F4} strip={Math.Min(best.Width, best.Depth):F4} yaws={best.TestedYaws}");

    var testedTotal = best.TestedYaws;
    var refineStep = Math.Max(MinYawRefineStepDeg, Math.Min(0.25, coarseStep * 0.25));
    var halfWindow = Math.Max(0.5, coarseStep * 1.5);

    while (true)
    {
      var (localBest, localTested) = FindBestYawLocal(
        geometries,
        basePlane,
        best.AngleDeg,
        halfWindow,
        refineStep,
        scoreMode);

      testedTotal += localTested;
      if (localBest != null && IsCandidateBetter(localBest, best, scoreMode))
        best = localBest;

      if (refineStep <= Min2dFinalStepDeg + 1.0e-12)
        break;

      refineStep = Math.Max(Min2dFinalStepDeg, refineStep * 0.2);
      halfWindow = Math.Max(refineStep * 3.0, MinYawRefineStepDeg);
    }

    best.TestedYaws = testedTotal;
    return best;
  }

  /// <summary>
  /// Score modes used for lexicographic candidate comparison.
  /// </summary>
  private enum ScoreMode
  {
    Height,
    Area,
    Strip
  }

  /// <summary>
  /// Returns true when candidate score is lexicographically better than current.
  /// </summary>
  private static bool IsCandidateBetter(FitCandidate candidate, FitCandidate current, ScoreMode mode)
  {
    var left = BuildScore(candidate, mode);
    var right = BuildScore(current, mode);
    const double epsilon = 1.0e-12;

    for (var i = 0; i < left.Length; i++)
    {
      if (left[i] < right[i] - epsilon)
        return true;
      if (left[i] > right[i] + epsilon)
        return false;
    }

    return false;
  }

  /// <summary>
  /// Builds score tuple for chosen optimization mode.
  /// </summary>
  private static double[] BuildScore(FitCandidate candidate, ScoreMode mode)
  {
    if (mode == ScoreMode.Strip)
    {
      var stripHeight = Math.Min(candidate.Width, candidate.Depth);
      var stripLength = Math.Max(candidate.Width, candidate.Depth);
      return new[] { stripHeight, stripLength, candidate.Area, candidate.Height };
    }

    if (mode == ScoreMode.Area)
    {
      var maxSide = Math.Max(candidate.Width, candidate.Depth);
      var minSide = Math.Min(candidate.Width, candidate.Depth);
      return new[] { candidate.Area, maxSide, minSide, candidate.Height };
    }

    return new[] { candidate.Height, candidate.Area, candidate.Volume };
  }

  /// <summary>
  /// Immutable bounds tuple in plane coordinates.
  /// </summary>
  private readonly struct PlaneBounds
  {
    public PlaneBounds(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
    {
      MinX = minX;
      MaxX = maxX;
      MinY = minY;
      MaxY = maxY;
      MinZ = minZ;
      MaxZ = maxZ;
    }

    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }
    public double MinZ { get; }
    public double MaxZ { get; }
  }

  /// <summary>
  /// Solved fit candidate payload.
  /// </summary>
  private sealed class FitCandidate
  {
    public Plane Plane { get; set; } = Plane.WorldXY;
    public Vector3d Normal { get; set; } = Vector3d.ZAxis;
    public double AngleDeg { get; set; }
    public double MinX { get; set; }
    public double MaxX { get; set; }
    public double MinY { get; set; }
    public double MaxY { get; set; }
    public double MinZ { get; set; }
    public double MaxZ { get; set; }
    public double Width { get; set; }
    public double Depth { get; set; }
    public double Height { get; set; }
    public double Area { get; set; }
    public double Volume { get; set; }
    public int TestedNormals { get; set; }
    public int TestedYaws { get; set; }
    public double RequestedStepDeg { get; set; }
    public double EffectiveStepDeg { get; set; }
    public string Mode { get; set; } = "3d";
    public string FitMode { get; set; } = "height";
  }
}