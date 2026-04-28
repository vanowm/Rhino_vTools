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
  private const string ModeKey = "mode";
  private const string ChainModeKey = "chainMode";
  private const string BothSidesKey = "bothSides";
  private const string LengthKey = "length";
  private const string PriorityKey = "priority";

  private static readonly string[] ModeValues = { "Normal", "Perp", "Tangent", "Auto", "PerpNear", "TanNear" };
  private static readonly string[] ChainModeValues = { "Single", "Multiple", "Chained", "Polyline" };
  private static readonly string[] PriorityValues = { "Closest", "PerpFirst", "TanFirst" };

  private const int ModeNormal = 0;
  private const int ModePerp = 1;
  private const int ModeTangent = 2;
  private const int ModeAuto = 3;
  private const int ModePerpNear = 4;
  private const int ModeTanNear = 5;

  private const int ChainSingle = 0;
  private const int ChainMultiple = 1;
  private const int ChainChained = 2;
  private const int ChainPolyline = 3;

  private const int PriorityClosest = 0;
  private const int PriorityPerpFirst = 1;
  private const int PriorityTanFirst = 2;

  private static int _mode = ModeNormal;
  private static int _chainMode = ChainSingle;
  private static bool _bothSides;
  private static double _length;
  private static int _priority = PriorityClosest;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLine";

  /// <summary>
  /// Draws line segments with optional perpendicular/tangent endpoint solving.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    Point3d? currentStart = null;
    var polylinePoints = new List<Point3d>();

    while (true)
    {
      if (!currentStart.HasValue)
      {
        if (!TryGetStartPoint(doc, out var startPoint))
          break;

        currentStart = startPoint;
        if (_chainMode == ChainPolyline)
        {
          polylinePoints.Clear();
          polylinePoints.Add(startPoint);
        }
      }

      var curveCache = CollectCurveCache(doc);
      if (!TryGetEndPoint(doc, currentStart.Value, curveCache, out var resolvedEndPoint, out var accepted))
      {
        if (!accepted)
          break;

        continue;
      }

      if (_chainMode == ChainPolyline)
      {
        if (resolvedEndPoint.DistanceTo(currentStart.Value) <= doc.ModelAbsoluteTolerance)
        {
          RhinoApp.WriteLine("vLine: second point too close to start.");
          continue;
        }

        polylinePoints.Add(resolvedEndPoint);
      }
      else
      {
        if (!TryCreateLine(doc, currentStart.Value, resolvedEndPoint))
        {
          RhinoApp.WriteLine("vLine: failed to create line.");
          continue;
        }
      }

      SavePersistedOptions();

      if (_chainMode == ChainSingle)
        break;

      if (_chainMode == ChainMultiple)
      {
        currentStart = null;
        continue;
      }

      currentStart = resolvedEndPoint;
    }

    if (_chainMode == ChainPolyline && polylinePoints.Count > 1)
    {
      var polyline = new Polyline(polylinePoints);
      if (polyline.IsValid)
      {
        var id = doc.Objects.AddPolyline(polyline);
        if (id == Guid.Empty)
          RhinoApp.WriteLine("vLine: failed to create polyline.");
      }
    }

    doc.Views.Redraw();
    return Result.Success;
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var mode = _mode;
        var chainMode = _chainMode;
        var bothSides = _bothSides;
        var length = _length;
        var priority = _priority;

        if (vToolsOptionStore.TryGetDouble(section, ModeKey, out var persistedMode))
          mode = ClampIndex((int)Math.Round(persistedMode, MidpointRounding.AwayFromZero), ModeValues.Length);
        if (vToolsOptionStore.TryGetDouble(section, ChainModeKey, out var persistedChainMode))
          chainMode = ClampIndex((int)Math.Round(persistedChainMode, MidpointRounding.AwayFromZero), ChainModeValues.Length);
        if (vToolsOptionStore.TryGetBool(section, BothSidesKey, out var persistedBothSides))
          bothSides = persistedBothSides;
        if (vToolsOptionStore.TryGetDouble(section, LengthKey, out var persistedLength))
          length = Math.Max(0.0, persistedLength);
        if (vToolsOptionStore.TryGetDouble(section, PriorityKey, out var persistedPriority))
          priority = ClampIndex((int)Math.Round(persistedPriority, MidpointRounding.AwayFromZero), PriorityValues.Length);

        return (mode, chainMode, bothSides, length, priority);
      });

    _mode = values.mode;
    _chainMode = values.chainMode;
    _bothSides = values.bothSides;
    _length = Math.Max(0.0, values.length);
    _priority = values.priority;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[ModeKey] = _mode;
        section[ChainModeKey] = _chainMode;
        section[BothSidesKey] = _bothSides;
        section[LengthKey] = _length;
        section[PriorityKey] = _priority;
      });
  }

  private static bool TryGetStartPoint(RhinoDoc doc, out Point3d startPoint)
  {
    startPoint = Point3d.Unset;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Start of line");
      gp.AcceptNothing(true);

      var options = AddCommonOptions(gp);

      var result = gp.Get();
      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        ApplyCommonOptionState(options, gp.Option());

        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        _length = Math.Max(0.0, gp.Number());
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Nothing || result == GetResult.Cancel)
        return false;

      if (result == GetResult.Point)
      {
        startPoint = gp.Point();
        return true;
      }
    }
  }

  private static bool TryGetEndPoint(
    RhinoDoc doc,
    Point3d startPoint,
    IReadOnlyList<CurveCacheItem> curveCache,
    out Point3d resolvedEndPoint,
    out bool accepted)
  {
    resolvedEndPoint = Point3d.Unset;
    accepted = false;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("End of line");
      gp.SetBasePoint(startPoint, true);
      gp.DrawLineFromPoint(startPoint, true);
      gp.AcceptNothing(true);
      gp.AcceptNumber(true, false);

      var options = AddCommonOptions(gp);

      EventHandler<GetPointDrawEventArgs> draw = (sender, e) =>
      {
        if (!TryResolveEndpoint(doc, startPoint, e.CurrentPoint, curveCache, out var previewEnd, out var previewMode))
          previewEnd = e.CurrentPoint;

        if (!TryBuildOutputSegment(startPoint, previewEnd, out var lineFrom, out var lineTo))
          return;

        e.Display.DrawLine(lineFrom, lineTo, Color.Cyan, 2);
      };

      gp.DynamicDraw += draw;
      var result = gp.Get();
      gp.DynamicDraw -= draw;

      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        ApplyCommonOptionState(options, gp.Option());

        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        _length = Math.Max(0.0, gp.Number());
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Nothing || result == GetResult.Cancel)
        return false;

      if (result != GetResult.Point)
        continue;

      accepted = true;
      var pickPoint = gp.Point();
      if (!TryResolveEndpoint(doc, startPoint, pickPoint, curveCache, out resolvedEndPoint, out var solvedMode))
        resolvedEndPoint = pickPoint;

      return true;
    }
  }

  private static CommonOptionState AddCommonOptions(GetPoint gp)
  {
    var options = new CommonOptionState
    {
      BothSidesOption = new OptionToggle(_bothSides, "No", "Yes"),
      LengthOption = new OptionDouble(_length, 0.0, 1.0e300)
    };

    options.ModeOptionIndex = gp.AddOptionList("Mode", ModeValues, _mode);
    gp.AddOptionToggle("BothSides", ref options.BothSidesOption);
    gp.AddOptionDouble("Length", ref options.LengthOption);
    options.ChainModeOptionIndex = gp.AddOptionList("ChainMode", ChainModeValues, _chainMode);
    options.PriorityOptionIndex = gp.AddOptionList("Priority", PriorityValues, _priority);

    return options;
  }

  private static void ApplyCommonOptionState(CommonOptionState options, CommandLineOption? option)
  {
    _bothSides = options.BothSidesOption.CurrentValue;
    _length = Math.Max(0.0, options.LengthOption.CurrentValue);

    if (option != null)
    {
      if (option.Index == options.ModeOptionIndex)
        _mode = ClampIndex(option.CurrentListOptionIndex, ModeValues.Length);
      else if (option.Index == options.ChainModeOptionIndex)
        _chainMode = ClampIndex(option.CurrentListOptionIndex, ChainModeValues.Length);
      else if (option.Index == options.PriorityOptionIndex)
        _priority = ClampIndex(option.CurrentListOptionIndex, PriorityValues.Length);
    }

    // Polyline works on chained endpoints; both-sides segmenting conflicts with polyline continuity.
    if (_chainMode == ChainPolyline && _bothSides)
      _bothSides = false;

    if (_length < 0.0)
      _length = 0.0;
  }

  private static bool TryCreateLine(RhinoDoc doc, Point3d startPoint, Point3d endPoint)
  {
    if (!TryBuildOutputSegment(startPoint, endPoint, out var lineFrom, out var lineTo))
      return false;

    var id = doc.Objects.AddLine(lineFrom, lineTo);
    if (id == Guid.Empty)
      return false;

    doc.Views.Redraw();
    return true;
  }

  private static bool TryBuildOutputSegment(Point3d startPoint, Point3d endPoint, out Point3d lineFrom, out Point3d lineTo)
  {
    lineFrom = startPoint;
    lineTo = endPoint;

    var direction = endPoint - startPoint;
    if (!direction.Unitize())
      return false;

    if (_length > RhinoMath.ZeroTolerance)
    {
      lineTo = startPoint + direction * _length;
    }

    if (_bothSides)
    {
      var halfLength = _length > RhinoMath.ZeroTolerance ? _length : startPoint.DistanceTo(endPoint);
      lineFrom = startPoint - direction * halfLength;
      lineTo = startPoint + direction * halfLength;
    }

    return lineFrom.DistanceTo(lineTo) > RhinoMath.ZeroTolerance;
  }

  private static bool TryResolveEndpoint(
    RhinoDoc doc,
    Point3d startPoint,
    Point3d hintPoint,
    IReadOnlyList<CurveCacheItem> curveCache,
    out Point3d endpoint,
    out string solvedMode)
  {
    endpoint = hintPoint;
    solvedMode = ModeValues[_mode];

    if (_mode == ModeNormal)
      return true;

    var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    var hasPerp = TryResolveConstrainedPoint(startPoint, hintPoint, curveCache, plane, wantPerp: true, preferNear: _mode == ModePerpNear, out var perpPoint, out var perpScore, out var perpHintDistanceSq);
    var hasTan = TryResolveConstrainedPoint(startPoint, hintPoint, curveCache, plane, wantPerp: false, preferNear: _mode == ModeTanNear, out var tanPoint, out var tanScore, out var tanHintDistanceSq);

    switch (_mode)
    {
      case ModePerp:
      case ModePerpNear:
        if (hasPerp)
        {
          endpoint = perpPoint;
          solvedMode = "Perp";
          return true;
        }
        return false;

      case ModeTangent:
      case ModeTanNear:
        if (hasTan)
        {
          endpoint = tanPoint;
          solvedMode = "Tangent";
          return true;
        }
        return false;

      case ModeAuto:
        if (hasPerp && hasTan)
        {
          if (_priority == PriorityPerpFirst)
          {
            endpoint = perpPoint;
            solvedMode = "Auto-Perp";
            return true;
          }

          if (_priority == PriorityTanFirst)
          {
            endpoint = tanPoint;
            solvedMode = "Auto-Tangent";
            return true;
          }

          // Closest strategy: prefer lower geometric error, then nearest to cursor hint.
          var perpWins = perpScore < tanScore ||
                         (Math.Abs(perpScore - tanScore) <= 1.0e-6 && perpHintDistanceSq <= tanHintDistanceSq);
          endpoint = perpWins ? perpPoint : tanPoint;
          solvedMode = perpWins ? "Auto-Perp" : "Auto-Tangent";
          return true;
        }

        if (hasPerp)
        {
          endpoint = perpPoint;
          solvedMode = "Auto-Perp";
          return true;
        }

        if (hasTan)
        {
          endpoint = tanPoint;
          solvedMode = "Auto-Tangent";
          return true;
        }

        return false;

      default:
        return true;
    }
  }

  private static bool TryResolveConstrainedPoint(
    Point3d startPoint,
    Point3d hintPoint,
    IReadOnlyList<CurveCacheItem> curveCache,
    Plane cplane,
    bool wantPerp,
    bool preferNear,
    out Point3d endpoint,
    out double score,
    out double hintDistanceSq)
  {
    endpoint = Point3d.Unset;
    score = double.MaxValue;
    hintDistanceSq = double.MaxValue;

    if (curveCache.Count == 0)
      return false;

    var shortlist = BuildCurveShortlist(curveCache, hintPoint, 12);

    foreach (var item in shortlist)
    {
      if (!TrySolvePointOnCurve(item.Curve, startPoint, hintPoint, cplane, wantPerp, out var candidate, out var candidateScore))
        continue;

      var candidateHintDistSq = candidate.DistanceToSquared(hintPoint);

      var better = false;
      if (preferNear)
      {
        better = candidateHintDistSq < hintDistanceSq ||
                 (Math.Abs(candidateHintDistSq - hintDistanceSq) <= 1.0e-8 && candidateScore < score);
      }
      else
      {
        better = candidateScore < score ||
                 (Math.Abs(candidateScore - score) <= 1.0e-8 && candidateHintDistSq < hintDistanceSq);
      }

      if (!better)
        continue;

      endpoint = candidate;
      score = candidateScore;
      hintDistanceSq = candidateHintDistSq;
    }

    if (!endpoint.IsValid)
      return false;

    // Tolerances mirror the Python thresholds loosely.
    var threshold = wantPerp ? 0.025 : 0.020;
    return score <= threshold || preferNear;
  }

  private static bool TrySolvePointOnCurve(
    Curve curve,
    Point3d startPoint,
    Point3d hintPoint,
    Plane cplane,
    bool wantPerp,
    out Point3d endpoint,
    out double bestScore)
  {
    endpoint = Point3d.Unset;
    bestScore = double.MaxValue;

    if (curve == null)
      return false;

    if (TryLineLikeSolve(curve, startPoint, hintPoint, wantPerp, out endpoint, out bestScore))
      return true;

    var domain = curve.Domain;
    var span = domain.T1 - domain.T0;
    if (span <= RhinoMath.ZeroTolerance)
      return false;

    const int samples = 220;
    for (var i = 0; i <= samples; i++)
    {
      var t = domain.T0 + span * (i / (double)samples);
      var point = curve.PointAt(t);
      var tangent = curve.TangentAt(t);
      if (!tangent.Unitize())
        continue;

      var vectorToStart = startPoint - point;
      var score = ConstraintScore2d(cplane, vectorToStart, tangent, wantPerp);
      if (score >= bestScore)
        continue;

      bestScore = score;
      endpoint = point;
    }

    return endpoint.IsValid;
  }

  private static bool TryLineLikeSolve(
    Curve curve,
    Point3d startPoint,
    Point3d hintPoint,
    bool wantPerp,
    out Point3d endpoint,
    out double score)
  {
    endpoint = Point3d.Unset;
    score = double.MaxValue;

    Line line;
    if (curve is LineCurve lineCurve)
    {
      line = lineCurve.Line;
    }
    else if (curve.IsLinear(RhinoMath.SqrtEpsilon))
    {
      line = new Line(curve.PointAtStart, curve.PointAtEnd);
    }
    else
    {
      return false;
    }

    if (!line.IsValid || line.Length <= RhinoMath.ZeroTolerance)
      return false;

    if (wantPerp)
    {
      endpoint = line.ClosestPoint(startPoint, false);
      score = 0.0;
      return endpoint.IsValid;
    }

    endpoint = line.ClosestPoint(hintPoint, false);
    score = 0.0;
    return endpoint.IsValid;
  }

  private static double ConstraintScore2d(Plane cplane, Vector3d vectorToStart, Vector3d tangent, bool wantPerp)
  {
    var v2 = ProjectToPlane(vectorToStart, cplane);
    var t2 = ProjectToPlane(tangent, cplane);

    if (!v2.Unitize() || !t2.Unitize())
      return 1.0;

    if (wantPerp)
      return Math.Abs(v2.X * t2.X + v2.Y * t2.Y);

    var cross = v2.X * t2.Y - v2.Y * t2.X;
    return Math.Abs(cross);
  }

  private static Vector2d ProjectToPlane(Vector3d vector, Plane plane)
  {
    return new Vector2d(
      Vector3d.Multiply(vector, plane.XAxis),
      Vector3d.Multiply(vector, plane.YAxis));
  }

  private static List<CurveCacheItem> CollectCurveCache(RhinoDoc doc)
  {
    var cache = new List<CurveCacheItem>();

    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj.ObjectType != ObjectType.Curve)
        continue;

      if (obj.Geometry is not Curve source)
        continue;

      var duplicate = source.DuplicateCurve();
      if (duplicate == null)
        continue;

      cache.Add(new CurveCacheItem(duplicate, duplicate.GetBoundingBox(true)));
    }

    return cache;
  }

  private static List<CurveCacheItem> BuildCurveShortlist(IReadOnlyList<CurveCacheItem> cache, Point3d hintPoint, int maxCount)
  {
    var indexed = new List<(CurveCacheItem Item, double DistanceSq)>(cache.Count);
    foreach (var item in cache)
      indexed.Add((item, BoundingBoxDistanceSquared(item.BoundingBox, hintPoint)));

    indexed.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

    var shortlist = new List<CurveCacheItem>();
    for (var i = 0; i < indexed.Count && i < maxCount; i++)
      shortlist.Add(indexed[i].Item);

    return shortlist;
  }

  private static double BoundingBoxDistanceSquared(BoundingBox bbox, Point3d point)
  {
    if (!bbox.IsValid)
      return double.MaxValue;

    var dx = point.X < bbox.Min.X ? bbox.Min.X - point.X : point.X > bbox.Max.X ? point.X - bbox.Max.X : 0.0;
    var dy = point.Y < bbox.Min.Y ? bbox.Min.Y - point.Y : point.Y > bbox.Max.Y ? point.Y - bbox.Max.Y : 0.0;
    var dz = point.Z < bbox.Min.Z ? bbox.Min.Z - point.Z : point.Z > bbox.Max.Z ? point.Z - bbox.Max.Z : 0.0;

    return dx * dx + dy * dy + dz * dz;
  }

  private static int ClampIndex(int value, int count)
  {
    if (count <= 0)
      return 0;
    if (value < 0)
      return 0;
    return value >= count ? count - 1 : value;
  }

  private sealed class CommonOptionState
  {
    public OptionToggle BothSidesOption = null!;
    public OptionDouble LengthOption = null!;
    public int ModeOptionIndex;
    public int ChainModeOptionIndex;
    public int PriorityOptionIndex;
  }

  private readonly record struct CurveCacheItem(Curve Curve, BoundingBox BoundingBox);
}
