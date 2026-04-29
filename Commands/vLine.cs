using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native line command ported from LinePlus.py.
/// </summary>
public sealed class vLine : Command
{
  private const string OptionsSectionName = "vLine";
  private const string ChainModeKey = "chainMode";
  private const string PriorityKey = "priority";
  private const string PersistConstraintKey = "persistConstraint";
  private const string LengthKey = "length";
  private const string AngleLockKey = "angleLock";
  private const string AngleKey = "angle";
  private const string AngleRelativeKey = "angleRelative";

  private static readonly string[] ChainModeValues = { "Single", "Multiple", "Chained", "Polyline" };
  private static readonly string[] PriorityValues = { "Closest", "PerpFirst", "TanFirst", "KeepCurrent" };

  private const int ModeSingle = 0;
  private const int ModeMultiple = 1;
  private const int ModeChained = 2;
  private const int ModePolyline = 3;

  private const int PriorityClosest = 0;
  private const int PriorityPerpFirst = 1;
  private const int PriorityTanFirst = 2;
  private const int PriorityKeepCurrent = 3;

  private static int _chainMode = ModeSingle;
  private static int _priority = PriorityClosest;
  private static bool _persistConstraint;
  private static double _length;
  private static bool _angleLock;
  private static double _angle;
  private static bool _angleRelative;

  private static string? _pendingNativeLineMode;
  private static EventHandler? _pendingNativeLineLaunchIdleHandler;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLine";

  /// <summary>
  /// Executes interactive line drawing with chain modes and curve-based constraints.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (_pendingNativeLineLaunchIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingNativeLineLaunchIdleHandler;
      _pendingNativeLineLaunchIdleHandler = null;
    }
    _pendingNativeLineMode = null;
    LoadPersistedOptions();

    var startResult = ResolveFirstPoint(doc, initialBothSides: false, initialChainMode: _chainMode);
    if (startResult.DelegatedToNative)
    {
      vToolsPlugIn.TryLog($"[DBG vLine] startResult.DelegatedToNative=true, calling LaunchNativeLineMode");
      LaunchNativeLineMode();
      return Result.Success;
    }
    if (!startResult.HasPoint)
      return Result.Cancel;

    var currentStart = startResult.Point;
    var firstSegment = true;
    var chainModeState = startResult.ChainMode;
    var initialBothSides = startResult.BothSides;

    string? constraintModeState = null;
    var persistConstraintState = _persistConstraint;
    var priorityState = _priority;
    var lengthState = _length;
    var angleLockState = _angleLock;
    var angleState = _angle;
    var angleRelativeState = _angleRelative;

    Vector3d? lastSegmentVector = null;

    List<Point3d>? polylinePoints = null;
    Guid tempPolylineId = Guid.Empty;

    var continueChain = true;
    while (continueChain)
    {
      var segmentBothDefault = firstSegment ? initialBothSides : false;
      // Endpoint constraint options are single-use in multi-segment modes; only Single mode inherits.
      var modeSeed = chainModeState == ModeSingle && (persistConstraintState || firstSegment) ? constraintModeState : null;

      var secondResult = ResolveSecondPoint(
        doc,
        currentStart,
        segmentBothDefault,
        lengthState,
        modeSeed,
        chainModeState,
        persistConstraintState,
        priorityState,
        angleLockState,
        angleState,
        angleRelativeState,
        lastSegmentVector);

      if (secondResult.State != null)
      {
        var state = secondResult.State.Value;
        constraintModeState = state.Mode;
        persistConstraintState = state.PersistConstraint;
        priorityState = state.Priority;
        lengthState = state.Length;
        angleLockState = state.AngleLock;
        angleState = state.Angle;
        angleRelativeState = state.AngleRelative;

        _persistConstraint = persistConstraintState;
        _priority = priorityState;
        _length = lengthState;
        _angleLock = angleLockState;
        _angle = angleState;
        _angleRelative = angleRelativeState;
      }

      if (!secondResult.HasPoint)
      {
        if (tempPolylineId != Guid.Empty)
          doc.Views.Redraw();
        SavePersistedOptions();
        return Result.Success;
      }

      var endPoint = secondResult.Point;
      var bothSides = secondResult.BothSides;
      var selectedChainMode = secondResult.ChainMode;

      if (selectedChainMode == ModePolyline)
      {
        polylinePoints ??= new List<Point3d> { currentStart };
        var prevPoints = new List<Point3d>(polylinePoints);
        polylinePoints.Add(endPoint);

        DeleteObjectIfValid(doc, tempPolylineId);
        tempPolylineId = doc.Objects.AddPolyline(new Polyline(polylinePoints));

        if (polylinePoints.Count >= 2)
          lastSegmentVector = polylinePoints[^1] - polylinePoints[^2];
      }
      else
      {
        if (tempPolylineId != Guid.Empty)
          tempPolylineId = Guid.Empty;

        if (polylinePoints is { Count: > 1 })
          polylinePoints = null;

        Guid lineId;
        if (bothSides)
        {
          var vec = endPoint - currentStart;
          if (vec.IsTiny())
            return Result.Cancel;

          var startA = currentStart - vec;
          var startB = currentStart + vec;
          lineId = doc.Objects.AddLine(startA, startB);
        }
        else
        {
          lineId = doc.Objects.AddLine(currentStart, endPoint);
        }

        lastSegmentVector = endPoint - currentStart;
      }

      firstSegment = false;

      if (selectedChainMode == ModeMultiple)
      {
        while (true)
        {
          var newStartResult = ResolveFirstPoint(doc, initialBothSides, selectedChainMode);

          if (newStartResult.DelegatedToNative)
          {
            LaunchNativeLineMode();
            return Result.Success;
          }

          if (!newStartResult.HasPoint)
            return Result.Success;

          currentStart = newStartResult.Point;
          firstSegment = true;
          lastSegmentVector = null;
          continueChain = true;
          break;
        }

        SavePersistedOptions();
        continue;
      }

      currentStart = endPoint;
      chainModeState = selectedChainMode;
      if (!persistConstraintState)
        constraintModeState = null;

      continueChain = chainModeState != ModeSingle;
      SavePersistedOptions();
    }

    if (polylinePoints is { Count: > 1 } && (tempPolylineId == Guid.Empty || doc.Objects.FindId(tempPolylineId) == null))
      _ = doc.Objects.AddPolyline(new Polyline(polylinePoints));

    doc.Views.Redraw();
    return Result.Success;
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var chainMode = _chainMode;
        var priority = _priority;
        var persistConstraint = _persistConstraint;
        var length = _length;
        var angleLock = _angleLock;
        var angle = _angle;
        var angleRelative = _angleRelative;

        if (vToolsOptionStore.TryGetDouble(section, ChainModeKey, out var persistedChain))
          chainMode = ClampIndex((int)Math.Round(persistedChain, MidpointRounding.AwayFromZero), ChainModeValues.Length);
        if (vToolsOptionStore.TryGetDouble(section, PriorityKey, out var persistedPriority))
          priority = ClampIndex((int)Math.Round(persistedPriority, MidpointRounding.AwayFromZero), PriorityValues.Length);
        if (vToolsOptionStore.TryGetBool(section, PersistConstraintKey, out var persistedPersist))
          persistConstraint = persistedPersist;
        if (vToolsOptionStore.TryGetDouble(section, LengthKey, out var persistedLength))
          length = persistedLength;
        if (vToolsOptionStore.TryGetBool(section, AngleLockKey, out var persistedAngleLock))
          angleLock = persistedAngleLock;
        if (vToolsOptionStore.TryGetDouble(section, AngleKey, out var persistedAngle))
          angle = persistedAngle;
        if (vToolsOptionStore.TryGetBool(section, AngleRelativeKey, out var persistedAngleRelative))
          angleRelative = persistedAngleRelative;

        return (chainMode, priority, persistConstraint, length, angleLock, angle, angleRelative);
      });

    _chainMode = ClampIndex(values.chainMode, ChainModeValues.Length);
    _priority = ClampIndex(values.priority, PriorityValues.Length);
    _persistConstraint = values.persistConstraint;
    _length = values.length;
    _angleLock = values.angleLock;
    _angle = values.angle;
    _angleRelative = values.angleRelative;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[ChainModeKey] = _chainMode;
        section[PriorityKey] = _priority;
        section[PersistConstraintKey] = _persistConstraint;
        section[LengthKey] = _length;
        section[AngleLockKey] = _angleLock;
        section[AngleKey] = _angle;
        section[AngleRelativeKey] = _angleRelative;
      });
  }

  private static FirstPointResult ResolveFirstPoint(RhinoDoc doc, bool initialBothSides, int initialChainMode)
  {
    var getPoint = new GetPoint();
    getPoint.SetCommandPrompt("Start of line");
    getPoint.AcceptNothing(true);

    var bothSides = new OptionToggle(initialBothSides, "No", "Yes");
    var chainModeIndex = ClampIndex(initialChainMode, ChainModeValues.Length);
    var chainModeOptionIndex = getPoint.AddOptionList("Mode", ChainModeValues, chainModeIndex);
    getPoint.AddOptionToggle("BothSides", ref bothSides);

    var idxNormal = getPoint.AddOption("Normal");
    var idxAngled = getPoint.AddOption("Angled");
    var idxVertical = getPoint.AddOption("Vertical");
    var idxFourPoint = getPoint.AddOption("FourPoint");
    var idxBisector = getPoint.AddOption("Bisector");
    var idxPerp = getPoint.AddOption("Perpendicular");
    var idxTangent = getPoint.AddOption("Tangent");
    var idxBiTangent = getPoint.AddOption("BiTangent");
    var idxExtension = getPoint.AddOption("Extension");

    var delegatedModes = new Dictionary<int, string>
    {
      [idxNormal] = "Normal",
      [idxAngled] = "Angled",
      [idxVertical] = "Vertical",
      [idxFourPoint] = "FourPoint",
      [idxBisector] = "Bisector",
      [idxPerp] = "Perpendicular",
      [idxTangent] = "Tangent",
      [idxBiTangent] = "BiTangent",
      [idxExtension] = "Extension"
    };

    while (true)
    {
      var result = getPoint.Get();

      if (result == GetResult.Point)
      {
        _chainMode = chainModeIndex;
        return FirstPointResult.WithPoint(getPoint.Point(), bothSides.CurrentValue, chainModeIndex);
      }

      if (result == GetResult.Nothing)
        return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);

      if (result == GetResult.Option)
      {
        var option = getPoint.Option();
        if (option == null)
          continue;

        if (delegatedModes.TryGetValue(option.Index, out var modeKeyword))
        {
          vToolsPlugIn.TryLog($"[DBG vLine] Delegating to native: {modeKeyword}");
          _pendingNativeLineMode = modeKeyword;
          return FirstPointResult.Delegated(bothSides.CurrentValue, chainModeIndex);
        }

        if (option.Index == chainModeOptionIndex)
          chainModeIndex = ClampIndex(option.CurrentListOptionIndex, ChainModeValues.Length);

        continue;
      }

      return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);
    }
  }

  private static SecondPointResult ResolveSecondPoint(
    RhinoDoc doc,
    Point3d startPoint,
    bool initialBothSides,
    double initialLength,
    string? initialMode,
    int initialChainMode,
    bool initialPersistConstraint,
    int initialPriority,
    bool initialAngleLock,
    double initialAngle,
    bool initialAngleRelative,
    Vector3d? referenceVector)
  {
    var getPoint = new GetPoint();
    getPoint.SetBasePoint(startPoint, true);
    getPoint.AcceptNumber(true, true);
    getPoint.AcceptNothing(true);

    var bothSides = new OptionToggle(initialBothSides, "No", "Yes");
    var chainModeIndex = ClampIndex(initialChainMode, ChainModeValues.Length);
    var priorityIndex = ClampIndex(initialPriority, PriorityValues.Length);

    var lengthOption = new OptionDouble(initialLength);
    var angleOption = new OptionDouble(initialAngle);
    var persistConstraint = new OptionToggle(initialPersistConstraint, "No", "Yes");
    var angleLock = new OptionToggle(initialAngleLock, "No", "Yes");
    var angleRelative = new OptionToggle(initialAngleRelative, "Absolute", "Relative");

    var idxChainMode = getPoint.AddOptionList("Mode", ChainModeValues, chainModeIndex);
    getPoint.AddOptionToggle("BothSides", ref bothSides);
    var idxPerp = getPoint.AddOption("Perp");
    var idxTan = getPoint.AddOption("Tangent");
    var idxPerpNear = getPoint.AddOption("PerpNear");
    var idxTanNear = getPoint.AddOption("TanNear");
    var idxAuto = getPoint.AddOption("Auto");
    var idxPriority = getPoint.AddOptionList("Priority", PriorityValues, priorityIndex);
    getPoint.AddOptionToggle("PersistConstraint", ref persistConstraint);
    var idxLength = getPoint.AddOptionDouble("Length", ref lengthOption);
    getPoint.AddOptionToggle("AngleLock", ref angleLock);
    var idxAngle = getPoint.AddOptionDouble("Angle", ref angleOption);
    getPoint.AddOptionToggle("AngleRef", ref angleRelative);

    var mode = initialMode;

    var cacheState = new CurveCacheState(CollectCurveCache(doc), DateTime.UtcNow.AddMilliseconds(500));
    string? lastAutoChoice = null;
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    void ApplyModePrompt()
    {
      if (mode == "perp")
        getPoint.SetCommandPrompt("End point of line (Perp mode: hover near curve, click to accept)");
      else if (mode == "tangent")
        getPoint.SetCommandPrompt("End point of line (Tangent mode: hover near curve, click to accept)");
      else if (mode == "perp_any")
        getPoint.SetCommandPrompt("End point of line (PerpNear mode: solves against nearest curve)");
      else if (mode == "tangent_any")
        getPoint.SetCommandPrompt("End point of line (TanNear mode: solves against nearest curve)");
      else if (mode == "auto")
        getPoint.SetCommandPrompt("End point of line (Auto mode: priority chooses Perp/Tangent)");
      else
        getPoint.SetCommandPrompt("End point of line");
    }

    void MaybeRefreshCurveCache(bool force)
    {
      var now = DateTime.UtcNow;
      if (!force && now < cacheState.NextRefreshUtc)
        return;

      cacheState.CurveCache = CollectCurveCache(doc);
      cacheState.NextRefreshUtc = now.AddMilliseconds(500);
    }

    Point3d ApplyAnglePointFromCurrent(Point3d currentPoint)
    {
      if (!angleLock.CurrentValue)
        return currentPoint;

      var baseVec = cplane.XAxis;
      if (angleRelative.CurrentValue && referenceVector.HasValue)
      {
        var rv = referenceVector.Value;
        if (!rv.IsTiny())
          baseVec = rv;
      }

      var base2 = ToCPlane2d(baseVec, cplane);
      if (!TryUnitize2d(base2, out var base2u))
        base2u = new Vector2d(1.0, 0.0);

      var radians = RhinoMath.ToRadians(angleOption.CurrentValue);
      var cosA = Math.Cos(radians);
      var sinA = Math.Sin(radians);
      var dir2 = new Vector2d((base2u.X * cosA) - (base2u.Y * sinA), (base2u.X * sinA) + (base2u.Y * cosA));
      if (!TryUnitize2d(dir2, out var dir2u))
        return currentPoint;

      var dir3 = (cplane.XAxis * dir2u.X) + (cplane.YAxis * dir2u.Y);
      if (dir3.IsTiny())
        return currentPoint;

      var toCursor = currentPoint - startPoint;
      var dist = toCursor.Length;
      if (dist < doc.ModelAbsoluteTolerance)
        dist = doc.ModelAbsoluteTolerance;

      var sign = Vector3d.Multiply(toCursor, dir3) < 0.0 ? -1.0 : 1.0;
      return startPoint + (dir3 * (dist * sign));
    }

    Point3d PreviewEndFromCurrent(Point3d currentPoint)
    {
      var endPoint = ApplyAnglePointFromCurrent(currentPoint);
      if (Math.Abs(lengthOption.CurrentValue) > doc.ModelAbsoluteTolerance)
      {
        var direction = endPoint - startPoint;
        if (direction.Unitize())
          endPoint = startPoint + direction * lengthOption.CurrentValue;
      }

      return endPoint;
    }

    Point3d? EndpointForMode(string? modeName, Point3d cursorPoint, bool preview)
    {
      MaybeRefreshCurveCache(false);
      var curveCache = cacheState.CurveCache;

      if (curveCache.Count == 0 || string.IsNullOrWhiteSpace(modeName))
        return null;

      var curve = NearestCurveToPoint(cursorPoint, curveCache);

      if (curve == null)
        return null;

      if (modeName is "perp" or "perp_any")
      {
        var pt = PerpPointFromStartWithHint(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 8 : 18);
        if (pt.HasValue)
          return pt.Value;

        return PerpFallbackToPointedSegment(startPoint, curve, cursorPoint, preview);
      }

      if (modeName is "tangent" or "tangent_any")
        return TangentPointFromStart(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 8 : 18);

      if (modeName == "auto")
      {
        var perp = PerpPointFromStartWithHint(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 8 : 18);
        var tan = TangentPointFromStart(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 8 : 18);

        if (!perp.HasValue)
          perp = PerpFallbackToPointedSegment(startPoint, curve, cursorPoint, preview);

        if (!perp.HasValue && !tan.HasValue)
          return null;

        if (priorityIndex == PriorityPerpFirst)
        {
          if (perp.HasValue)
          {
            lastAutoChoice = "perp";
            return perp.Value;
          }

          lastAutoChoice = "tangent";
          return tan;
        }

        if (priorityIndex == PriorityTanFirst)
        {
          if (tan.HasValue)
          {
            lastAutoChoice = "tangent";
            return tan.Value;
          }

          lastAutoChoice = "perp";
          return perp;
        }

        if (priorityIndex == PriorityKeepCurrent)
        {
          if (lastAutoChoice == "perp" && perp.HasValue)
            return perp.Value;
          if (lastAutoChoice == "tangent" && tan.HasValue)
            return tan.Value;
        }

        if (!perp.HasValue)
        {
          lastAutoChoice = "tangent";
          return tan;
        }

        if (!tan.HasValue)
        {
          lastAutoChoice = "perp";
          return perp;
        }

        if (perp.Value.DistanceToSquared(cursorPoint) <= tan.Value.DistanceToSquared(cursorPoint))
        {
          lastAutoChoice = "perp";
          return perp.Value;
        }

        lastAutoChoice = "tangent";
        return tan.Value;
      }

      return null;
    }

    Color CurrentPreviewColor()
    {
      var baseColor = Color.White;
      try
      {
        baseColor = doc.Layers.CurrentLayer?.Color ?? Color.White;
      }
      catch
      {
      }

      return Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B);
    }

    EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
    {
      var previewColor = CurrentPreviewColor();
      Point3d? endPoint;

      if (!string.IsNullOrWhiteSpace(mode))
      {
        var endpointForMode = EndpointForMode(mode, e.CurrentPoint, preview: true);
        if (!endpointForMode.HasValue)
          return;

        endPoint = PreviewEndFromCurrent(endpointForMode.Value);
      }
      else
      {
        endPoint = PreviewEndFromCurrent(e.CurrentPoint);
      }

      var ep = endPoint.Value;
      if (bothSides.CurrentValue)
      {
        var vec = ep - startPoint;
        if (vec.IsTiny())
          return;

        var a = startPoint - vec;
        var b = startPoint + vec;
        e.Display.DrawLine(a, b, previewColor, 1);
        e.Display.DrawDottedLine(a, b, previewColor);
      }
      else
      {
        e.Display.DrawLine(startPoint, ep, previewColor, 1);
        e.Display.DrawDottedLine(startPoint, ep, previewColor);
      }

      e.Display.DrawPoint(ep, Rhino.Display.PointStyle.RoundSimple, 2, previewColor);
    };

    getPoint.DynamicDraw += drawPreview;
    ApplyModePrompt();

    try
    {
      while (true)
      {
        var result = getPoint.Get();

        if (result == GetResult.Point)
        {
          Point3d endPoint;
          if (!string.IsNullOrWhiteSpace(mode))
          {
            var endpointForMode = EndpointForMode(mode, getPoint.Point(), preview: false);
            if (!endpointForMode.HasValue)
            {
              RhinoApp.WriteLine($"vLine: no valid {mode} solution found on nearby curves at this cursor location.");
              continue;
            }

            endPoint = PreviewEndFromCurrent(endpointForMode.Value);
          }
          else
          {
            endPoint = PreviewEndFromCurrent(getPoint.Point());
          }

          var state = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
          return SecondPointResult.WithPoint(endPoint, bothSides.CurrentValue, chainModeIndex, state);
        }

        if (result == GetResult.Option)
        {
          var option = getPoint.Option();
          if (option == null)
            continue;

          if (option.Index == idxPerp)
          {
            mode = "perp";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxTan)
          {
            mode = "tangent";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxPerpNear)
          {
            mode = "perp_any";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxTanNear)
          {
            mode = "tangent_any";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxAuto)
          {
            mode = "auto";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxPriority)
          {
            priorityIndex = ClampIndex(option.CurrentListOptionIndex, PriorityValues.Length);
            continue;
          }

          if (option.Index == idxLength)
            continue;

          if (option.Index == idxAngle)
            continue;

          if (option.Index == idxChainMode)
          {
            chainModeIndex = ClampIndex(option.CurrentListOptionIndex, ChainModeValues.Length);
            continue;
          }

          continue;
        }

        if (result == GetResult.Number)
        {
          var numberValue = getPoint.Number();
          return ResolveSecondPoint(
            doc,
            startPoint,
            bothSides.CurrentValue,
            numberValue,
            mode,
            chainModeIndex,
            persistConstraint.CurrentValue,
            priorityIndex,
            angleLock.CurrentValue,
            angleOption.CurrentValue,
            angleRelative.CurrentValue,
            referenceVector);
        }

        var fallbackState = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
        return SecondPointResult.None(bothSides.CurrentValue, chainModeIndex, fallbackState);
      }
    }
    finally
    {
      getPoint.DynamicDraw -= drawPreview;
    }
  }

  private static List<CurveCacheItem> CollectCurveCache(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false
    };

    var cache = new List<CurveCacheItem>();

    foreach (var rhObj in doc.Objects.GetObjectList(settings))
    {
      if (rhObj.ObjectType != ObjectType.Curve)
        continue;

      if (rhObj.Geometry is not Curve curve)
        continue;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        continue;

      cache.Add(new CurveCacheItem(duplicate, duplicate.GetBoundingBox(true)));
    }

    return cache;
  }

  private static Curve? NearestCurveToPoint(Point3d point, IReadOnlyList<CurveCacheItem> curveCache)
  {
    if (curveCache.Count == 0)
      return null;

    var shortlist = BuildCurveShortlist(point, curveCache, 8);

    Curve? best = null;
    var bestD2 = double.MaxValue;

    foreach (var (curve, _) in shortlist)
    {
      if (!curve.ClosestPoint(point, out var t))
        continue;

      var cp = curve.PointAt(t);
      var d2 = point.DistanceToSquared(cp);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      best = curve;
    }

    return best;
  }

  private static Curve? CurveAtCursorPoint(Point3d point, IReadOnlyList<CurveCacheItem> curveCache, double captureTolerance)
  {
    if (curveCache.Count == 0)
      return null;

    var shortlist = BuildCurveShortlist(point, curveCache, 12);

    Curve? best = null;
    var bestD2 = double.MaxValue;

    foreach (var (curve, _) in shortlist)
    {
      if (!curve.ClosestPoint(point, out var t))
        continue;

      var cp = curve.PointAt(t);
      var d2 = point.DistanceToSquared(cp);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      best = curve;
    }

    if (best == null)
      return null;

    return bestD2 <= captureTolerance * captureTolerance ? best : null;
  }

  private static List<CurveCacheItem> BuildCurveShortlist(Point3d point, IReadOnlyList<CurveCacheItem> curveCache, int count)
  {
    var sorted = new List<(CurveCacheItem Item, double DistanceSq)>();
    foreach (var item in curveCache)
      sorted.Add((item, BoundingBoxDistanceSquared(item.BoundingBox, point)));

    sorted.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

    var shortlist = new List<CurveCacheItem>();
    for (var i = 0; i < sorted.Count && i < count; i++)
      shortlist.Add(sorted[i].Item);

    return shortlist;
  }

  private static double BoundingBoxDistanceSquared(BoundingBox bbox, Point3d point)
  {
    if (!bbox.IsValid)
      return 1e300;

    double dx;
    if (point.X < bbox.Min.X)
      dx = bbox.Min.X - point.X;
    else if (point.X > bbox.Max.X)
      dx = point.X - bbox.Max.X;
    else
      dx = 0.0;

    double dy;
    if (point.Y < bbox.Min.Y)
      dy = bbox.Min.Y - point.Y;
    else if (point.Y > bbox.Max.Y)
      dy = point.Y - bbox.Max.Y;
    else
      dy = 0.0;

    double dz;
    if (point.Z < bbox.Min.Z)
      dz = bbox.Min.Z - point.Z;
    else if (point.Z > bbox.Max.Z)
      dz = point.Z - bbox.Max.Z;
    else
      dz = 0.0;

    return (dx * dx) + (dy * dy) + (dz * dz);
  }

  private static Vector2d ToCPlane2d(Vector3d vector, Plane plane)
  {
    return new Vector2d(Vector3d.Multiply(vector, plane.XAxis), Vector3d.Multiply(vector, plane.YAxis));
  }

  private static bool TryUnitize2d(Vector2d value, out Vector2d unit)
  {
    unit = value;
    if (unit.IsTiny())
      return false;

    unit.Unitize();
    return true;
  }

  private static bool CurveIsLinear(Curve curve, out Line line)
  {
    if (curve is LineCurve lineCurve)
    {
      line = lineCurve.Line;
      return true;
    }

    if (curve.IsLinear(RhinoMath.SqrtEpsilon))
    {
      line = new Line(curve.PointAtStart, curve.PointAtEnd);
      return line.IsValid;
    }

    line = Line.Unset;
    return false;
  }

  private static double PerpScore2d(Curve curve, double t, Point3d startPoint, Plane cplane)
  {
    var point = curve.PointAt(t);
    var tangent = curve.TangentAt(t);

    var v2 = ToCPlane2d(startPoint - point, cplane);
    var t2 = new Vector2d(Vector3d.Multiply(tangent, cplane.XAxis), Vector3d.Multiply(tangent, cplane.YAxis));

    if (!TryUnitize2d(v2, out var v2u) || !TryUnitize2d(t2, out var t2u))
      return 1.0;

    var dot = (t2u.X * v2u.X) + (t2u.Y * v2u.Y);
    return Math.Abs(dot);
  }

  private static (double Parameter, double Error) RefinePerpParameter(Curve curve, Point3d startPoint, Plane cplane, double t0, double t1, int iterations)
  {
    const double phi = 0.61803398875;
    var a = Math.Min(t0, t1);
    var b = Math.Max(t0, t1);

    var c = b - ((b - a) * phi);
    var d = a + ((b - a) * phi);
    var fc = PerpScore2d(curve, c, startPoint, cplane);
    var fd = PerpScore2d(curve, d, startPoint, cplane);

    for (var i = 0; i < iterations; i++)
    {
      if (fc < fd)
      {
        b = d;
        d = c;
        fd = fc;
        c = b - ((b - a) * phi);
        fc = PerpScore2d(curve, c, startPoint, cplane);
      }
      else
      {
        a = c;
        c = d;
        fc = fd;
        d = a + ((b - a) * phi);
        fd = PerpScore2d(curve, d, startPoint, cplane);
      }
    }

    var best = 0.5 * (a + b);
    return (best, PerpScore2d(curve, best, startPoint, cplane));
  }

  private static Point3d? PerpPointFromStartWithHint(Point3d startPoint, Curve curve, Point3d hintPoint, int samples, int refineIterations)
  {
    if (CurveIsLinear(curve, out var line))
    {
      var t = line.ClosestParameter(startPoint);
      return line.PointAt(t);
    }

    var cplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    var domain = curve.Domain;
    var a = domain.T0;
    var b = domain.T1;
    if (b <= a)
      return null;

    double hintT;
    if (!curve.ClosestPoint(hintPoint, out hintT))
      hintT = 0.5 * (a + b);

    var dt = (b - a) / samples;
    if (dt <= 0.0)
      return null;

    var candidates = new List<(double T, double Error)>();
    var values = new List<double>();
    var parameters = new List<double>();

    for (var i = 0; i <= samples; i++)
    {
      var t = a + dt * i;
      parameters.Add(t);
      values.Add(PerpScore2d(curve, t, startPoint, cplane));
    }

    for (var i = 1; i < values.Count - 1; i++)
    {
      if (values[i] <= values[i - 1] && values[i] <= values[i + 1])
        candidates.Add((parameters[i], values[i]));
    }

    if (candidates.Count == 0)
    {
      var bestIndex = 0;
      var bestValue = values[0];
      for (var i = 1; i < values.Count; i++)
      {
        if (values[i] < bestValue)
        {
          bestValue = values[i];
          bestIndex = i;
        }
      }

      candidates.Add((parameters[bestIndex], values[bestIndex]));
    }

    candidates.Sort((x, y) => x.Error.CompareTo(y.Error));

    var refined = new List<(double T, double Error, Point3d Point)>();
    for (var i = 0; i < candidates.Count && i < 16; i++)
    {
      var seedT = candidates[i].T;
      var window = Math.Max(dt * 4.0, (b - a) * 0.01);
      var t0 = Math.Max(a, seedT - window);
      var t1 = Math.Min(b, seedT + window);
      var refinedResult = RefinePerpParameter(curve, startPoint, cplane, t0, t1, refineIterations);
      refined.Add((refinedResult.Parameter, refinedResult.Error, curve.PointAt(refinedResult.Parameter)));
    }

    if (refined.Count == 0)
      return null;

    refined.Sort((x, y) => x.T.CompareTo(y.T));
    var unique = new List<(double T, double Error, Point3d Point)>();
    var paramTol = Math.Max((b - a) * 1e-6, 1e-9);

    foreach (var item in refined)
    {
      if (unique.Count == 0 || Math.Abs(item.T - unique[^1].T) > paramTol)
      {
        unique.Add(item);
      }
      else if (item.Error < unique[^1].Error)
      {
        unique[^1] = item;
      }
    }

    var valid = unique.FindAll(v => v.Error <= 0.02);
    if (valid.Count == 0)
      return null;

    valid.Sort((x, y) =>
    {
      var dx = x.Point.DistanceToSquared(hintPoint).CompareTo(y.Point.DistanceToSquared(hintPoint));
      if (dx != 0)
        return dx;

      var de = x.Error.CompareTo(y.Error);
      if (de != 0)
        return de;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    return valid[0].Point;
  }

  private static Point3d? PerpFallbackToPointedSegment(Point3d startPoint, Curve curve, Point3d hintPoint, bool preview)
  {
    var segments = curve.DuplicateSegments();
    if (segments == null || segments.Length == 0)
      return null;

    Curve? bestSeg = null;
    var bestD2 = double.MaxValue;

    foreach (var seg in segments)
    {
      if (seg == null)
        continue;

      if (!seg.ClosestPoint(hintPoint, out var t))
        continue;

      var cp = seg.PointAt(t);
      var d2 = cp.DistanceToSquared(hintPoint);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      bestSeg = seg;
    }

    if (bestSeg == null)
      return null;

    var pt = PerpPointFromStartWithHint(startPoint, bestSeg, hintPoint, preview ? 80 : 240, preview ? 8 : 18);
    if (pt.HasValue)
      return pt;

    if (CurveIsLinear(bestSeg, out var line))
      return line.PointAt(line.ClosestParameter(startPoint));

    return null;
  }

  private static double TangentScore2d(Curve curve, double t, Point3d startPoint, Plane cplane)
  {
    var point = curve.PointAt(t);
    var tangent = curve.TangentAt(t);

    var v2 = ToCPlane2d(startPoint - point, cplane);
    var t2 = new Vector2d(Vector3d.Multiply(tangent, cplane.XAxis), Vector3d.Multiply(tangent, cplane.YAxis));

    if (!TryUnitize2d(v2, out var v2u) || !TryUnitize2d(t2, out var t2u))
      return 1.0;

    var cross = (t2u.X * v2u.Y) - (t2u.Y * v2u.X);
    return Math.Abs(cross);
  }

  private static (double Parameter, double Error) RefineTangentParameter(Curve curve, Point3d startPoint, Plane cplane, double t0, double t1, int iterations)
  {
    const double phi = 0.61803398875;
    var a = Math.Min(t0, t1);
    var b = Math.Max(t0, t1);

    var c = b - ((b - a) * phi);
    var d = a + ((b - a) * phi);
    var fc = TangentScore2d(curve, c, startPoint, cplane);
    var fd = TangentScore2d(curve, d, startPoint, cplane);

    for (var i = 0; i < iterations; i++)
    {
      if (fc < fd)
      {
        b = d;
        d = c;
        fd = fc;
        c = b - ((b - a) * phi);
        fc = TangentScore2d(curve, c, startPoint, cplane);
      }
      else
      {
        a = c;
        c = d;
        fc = fd;
        d = a + ((b - a) * phi);
        fd = TangentScore2d(curve, d, startPoint, cplane);
      }
    }

    var best = 0.5 * (a + b);
    return (best, TangentScore2d(curve, best, startPoint, cplane));
  }

  private static Point3d? TangentPointFromStart(Point3d startPoint, Curve curve, Point3d hintPoint, int samples, int refineIterations)
  {
    var cplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    var domain = curve.Domain;
    var a = domain.T0;
    var b = domain.T1;
    if (b <= a)
      return null;

    double hintT;
    if (!curve.ClosestPoint(hintPoint, out hintT))
      hintT = 0.5 * (a + b);

    var dt = (b - a) / samples;
    if (dt <= 0.0)
      return null;

    var candidates = new List<(double T, double Error)>();
    var values = new List<double>();
    var parameters = new List<double>();

    for (var i = 0; i <= samples; i++)
    {
      var t = a + dt * i;
      parameters.Add(t);
      values.Add(TangentScore2d(curve, t, startPoint, cplane));
    }

    for (var i = 1; i < values.Count - 1; i++)
    {
      if (values[i] <= values[i - 1] && values[i] <= values[i + 1])
        candidates.Add((parameters[i], values[i]));
    }

    if (candidates.Count == 0)
    {
      var bestIndex = 0;
      var bestValue = values[0];
      for (var i = 1; i < values.Count; i++)
      {
        if (values[i] < bestValue)
        {
          bestValue = values[i];
          bestIndex = i;
        }
      }

      candidates.Add((parameters[bestIndex], values[bestIndex]));
    }

    candidates.Sort((x, y) => x.Error.CompareTo(y.Error));

    var refined = new List<(double T, double Error, Point3d Point)>();
    for (var i = 0; i < candidates.Count && i < 16; i++)
    {
      var seedT = candidates[i].T;
      var window = Math.Max(dt * 4.0, (b - a) * 0.01);
      var t0 = Math.Max(a, seedT - window);
      var t1 = Math.Min(b, seedT + window);
      var refinedResult = RefineTangentParameter(curve, startPoint, cplane, t0, t1, refineIterations);
      refined.Add((refinedResult.Parameter, refinedResult.Error, curve.PointAt(refinedResult.Parameter)));
    }

    if (refined.Count == 0)
      return null;

    refined.Sort((x, y) => x.T.CompareTo(y.T));
    var unique = new List<(double T, double Error, Point3d Point)>();
    var paramTol = Math.Max((b - a) * 1e-6, 1e-9);

    foreach (var item in refined)
    {
      if (unique.Count == 0 || Math.Abs(item.T - unique[^1].T) > paramTol)
      {
        unique.Add(item);
      }
      else if (item.Error < unique[^1].Error)
      {
        unique[^1] = item;
      }
    }

    var valid = unique.FindAll(v => v.Error <= 0.015);
    if (valid.Count == 0)
      return null;

    valid.Sort((x, y) =>
    {
      var dx = x.Point.DistanceToSquared(hintPoint).CompareTo(y.Point.DistanceToSquared(hintPoint));
      if (dx != 0)
        return dx;

      var de = x.Error.CompareTo(y.Error);
      if (de != 0)
        return de;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    return valid[0].Point;
  }

  private static void LaunchNativeLineMode()
  {
    var mode = _pendingNativeLineMode;
    _pendingNativeLineMode = null;
    vToolsPlugIn.TryLog($"[DBG vLine] LaunchNativeLineMode: mode={mode ?? "(null)"}");
    if (mode == null)
      return;

    if (_pendingNativeLineLaunchIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingNativeLineLaunchIdleHandler;
      _pendingNativeLineLaunchIdleHandler = null;
    }

    var script = mode == "BiTangent" ? "_Line _Tangent _Tangent" : $"_Line _{mode}";

    EventHandler handler = null!;
    handler = (_, _) =>
    {
      RhinoApp.Idle -= handler;
      if (_pendingNativeLineLaunchIdleHandler == handler)
        _pendingNativeLineLaunchIdleHandler = null;
      vToolsPlugIn.TryLog($"[DBG vLine] Idle fired, RunScript: {script}");
      var ok = RhinoApp.RunScript(script, false);
      vToolsPlugIn.TryLog($"[DBG vLine] RunScript returned: {ok}");
    };

    _pendingNativeLineLaunchIdleHandler = handler;
    RhinoApp.Idle += handler;
    vToolsPlugIn.TryLog($"[DBG vLine] Idle handler registered for: {script}");
  }

  private static void DeleteObjectIfValid(RhinoDoc doc, Guid id)
  {
    if (id == Guid.Empty)
      return;

    if (doc.Objects.FindId(id) != null)
      _ = doc.Objects.Delete(id, true);
  }

  private static int ClampIndex(int value, int count)
  {
    if (count <= 0)
      return 0;
    if (value < 0)
      return 0;
    return value >= count ? count - 1 : value;
  }

  private sealed class CurveCacheState
  {
    public CurveCacheState(List<CurveCacheItem> curveCache, DateTime nextRefreshUtc)
    {
      CurveCache = curveCache;
      NextRefreshUtc = nextRefreshUtc;
    }

    public List<CurveCacheItem> CurveCache { get; set; }
    public DateTime NextRefreshUtc { get; set; }
  }

  private readonly record struct CurveCacheItem(Curve Curve, BoundingBox BoundingBox);

  private readonly record struct ConstraintState(
    string? Mode,
    bool PersistConstraint,
    int Priority,
    double Length,
    bool AngleLock,
    double Angle,
    bool AngleRelative);

  private readonly record struct FirstPointResult(
    bool HasPoint,
    Point3d Point,
    bool BothSides,
    int ChainMode,
    bool DelegatedToNative)
  {
    public static FirstPointResult WithPoint(Point3d point, bool bothSides, int chainMode)
      => new(true, point, bothSides, chainMode, false);

    public static FirstPointResult Delegated(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, true);

    public static FirstPointResult None(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, false);
  }

  private readonly record struct SecondPointResult(
    bool HasPoint,
    Point3d Point,
    bool BothSides,
    int ChainMode,
    ConstraintState? State)
  {
    public static SecondPointResult WithPoint(Point3d point, bool bothSides, int chainMode, ConstraintState state)
      => new(true, point, bothSides, chainMode, state);

    public static SecondPointResult None(bool bothSides, int chainMode, ConstraintState state)
      => new(false, Point3d.Unset, bothSides, chainMode, state);
  }
}
