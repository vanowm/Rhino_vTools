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
/// Native split-at-corners command ported from SplitAtCorners.py.
/// </summary>
public sealed class vSplitAtCorners : Command
{
  private const string OptionsSectionName = "vSplitAtCorners";
  private const string AngleKey = "angle";
  private const string MinLengthKey = "minLength";
  private const string IgnoreNotchesKey = "ignoreNotches";

  private static double _angleDeg = 45.0;
  private static double _minLength;
  private static bool _ignoreNotches = true;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vSplitAtCorners";

  /// <summary>
  /// Executes interactive corner splitting with manual and suppressed corner controls.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var getObject = new GetObject();
    getObject.EnableTransparentCommands(true);
    getObject.SetCommandPrompt("Select curve(s) to split at corners");
    getObject.GeometryFilter = ObjectType.Curve;
    getObject.SubObjectSelect = false;
    getObject.GroupSelect = true;
    getObject.AcceptNothing(false);
    getObject.EnableClearObjectsOnEntry(false);
    getObject.EnableUnselectObjectsOnExit(false);
    getObject.EnablePreSelect(true, true);
    getObject.GetMultiple(1, 0);

    if (getObject.CommandResult() != Result.Success)
      return getObject.CommandResult();

    var selectedIds = new List<Guid>();
    for (var i = 0; i < getObject.ObjectCount; i++)
      selectedIds.Add(getObject.Object(i).ObjectId);

    if (selectedIds.Count == 0)
      return Result.Cancel;

    var angleOpt = new OptionDouble(_angleDeg, 0.01, 179.99);
    var minLenOpt = new OptionDouble(_minLength, 0.0, 1e12);

    var manualCorners = new HashSet<SplitPointKey>();
    var removedAutoCorners = new HashSet<SplitPointKey>();

    var conduit = new CornerPreviewConduit(doc, selectedIds, angleOpt.CurrentValue,
      minLenOpt.CurrentValue, _ignoreNotches, manualCorners, removedAutoCorners);
    conduit.Enabled = true;

    try
    {
      while (true)
      {
        conduit.UpdateSettings(angleOpt.CurrentValue, minLenOpt.CurrentValue, _ignoreNotches);

        var gp = new GetPoint();
        gp.EnableTransparentCommands(true);
        gp.SetCommandPrompt("Click to toggle split points. Enter to apply");
        gp.AcceptNothing(true);
        gp.AcceptNumber(true, false);

        var idxAngle = gp.AddOptionDouble("Angle", ref angleOpt);
        var idxMinLen = gp.AddOptionDouble("MinLength", ref minLenOpt);
        var idxIgnoreNotches = gp.AddOption("IgnoreNotches", _ignoreNotches ? "Yes" : "No", true);
        var idxClearManual = manualCorners.Count > 0 ? gp.AddOption("ClearManual") : -1;
        var idxClearAll = gp.AddOption("ClearAll");

        EventHandler<GetPointDrawEventArgs> drawHandler = (_, e) => conduit.OnDynamicDraw(e);
        gp.DynamicDraw += drawHandler;

        var result = gp.Get();

        gp.DynamicDraw -= drawHandler;

        if (gp.CommandResult() != Result.Success)
          return gp.CommandResult();

        if (result == GetResult.Option)
        {
          var opt = gp.Option();
          if (opt != null)
          {
            if (idxClearManual >= 0 && opt.Index == idxClearManual)
              manualCorners.Clear();
            else if (opt.Index == idxClearAll)
            {
              manualCorners.Clear();
              removedAutoCorners.Clear();
            }
            else if (opt.Index == idxIgnoreNotches)
            {
              _ignoreNotches = !_ignoreNotches;
              SavePersistedOptions();
              conduit.UpdateSettings(angleOpt.CurrentValue, minLenOpt.CurrentValue, _ignoreNotches);
              doc.Views.Redraw();
            }
            else if (opt.Index == idxAngle || opt.Index == idxMinLen)
            {
              _angleDeg = angleOpt.CurrentValue;
              _minLength = minLenOpt.CurrentValue;
              SavePersistedOptions();
            }
          }
          continue;
        }

        if (result == GetResult.Nothing)
          break;

        if (result == GetResult.Number)
        {
          angleOpt.CurrentValue = Math.Clamp(gp.Number(), 0.01, 179.99);
          _angleDeg = angleOpt.CurrentValue;
          SavePersistedOptions();
          conduit.UpdateSettings(angleOpt.CurrentValue, minLenOpt.CurrentValue, _ignoreNotches);
          doc.Views.Redraw();
          continue;
        }

        if (result != GetResult.Point)
          continue;

        var pick = gp.Point();
        conduit.TryToggleNear(pick);
        doc.Views.Redraw();
      }

      _angleDeg = angleOpt.CurrentValue;
      _minLength = minLenOpt.CurrentValue;
      SavePersistedOptions();

      var totalCreated = 0;
      var anyFailed = false;

      foreach (var id in selectedIds)
      {
        var obj = doc.Objects.FindId(id) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        var curve = obj.CurveGeometry.DuplicateCurve();
        if (curve == null)
          continue;

        var basePoints = DetectCornerCandidates(curve, _angleDeg, _minLength);

        var activeAutoPoints = basePoints
          .Where(p => !removedAutoCorners.Contains(new SplitPointKey(id, p.Parameter)))
          .ToList();

        var manualPointsForCurve = manualCorners
          .Where(m => m.CurveId == id)
          .Select(m => new CornerCandidate(m.Parameter, Point3d.Unset, 0.0, true))
          .ToList();

        var allParams = new List<double>();
        foreach (var c in activeAutoPoints)
          allParams.Add(c.Parameter);
        foreach (var c in manualPointsForCurve)
          allParams.Add(c.Parameter);

        if (allParams.Count == 0)
          continue;

        var domain = curve.Domain;
        var tol = Math.Max(doc.ModelAbsoluteTolerance * 1e-4, RhinoMath.ZeroTolerance);

        // For closed curves, Curve.Split cannot split at the domain boundary (T0/T1).
        // If the seam corner is a split point, move the seam to a non-corner location
        // and remap all params so the seam corner becomes an interior parameter.
        if (curve.IsClosed)
        {
          var seamEps = Math.Max((domain.T1 - domain.T0) * 1e-6, RhinoMath.ZeroTolerance * 10.0);
          if (allParams.Any(p => Math.Abs(p - domain.T0) <= seamEps))
          {
            var interiorParams = allParams
              .Where(p => Math.Abs(p - domain.T0) > seamEps)
              .OrderBy(p => p)
              .ToList();

            var span = domain.T1 - domain.T0;
            var newSeamT = interiorParams.Count > 0
              ? (domain.T0 + interiorParams[0]) / 2.0
              : domain.T0 + span / 2.0;

            var seamCurve = curve.DuplicateCurve();
            if (seamCurve.ChangeClosedCurveSeam(newSeamT))
            {
              curve = seamCurve;
              domain = curve.Domain;
              allParams = allParams
                .Select(p => domain.T0 + ((p - newSeamT + span) % span))
                .ToList();
            }
          }
        }
        var usableParams = allParams
          .Where(t => t > domain.T0 + tol && t < domain.T1 - tol)
          .Distinct(new DoubleApproxComparer(doc.ModelAbsoluteTolerance * 1e-3))
          .OrderBy(t => t)
          .ToList();

        if (usableParams.Count == 0)
          continue;

        var splitCurves = curve.Split(usableParams);
        if (splitCurves == null || splitCurves.Length == 0)
        {
          anyFailed = true;
          continue;
        }

        var attr = obj.Attributes.Duplicate();
        var newIds = new List<Guid>();

        foreach (var piece in splitCurves)
        {
          if (piece == null || !piece.IsValid)
            continue;

          if (_minLength > RhinoMath.ZeroTolerance && piece.GetLength() < _minLength)
          {
            piece.Dispose();
            continue;
          }

          var newId = doc.Objects.AddCurve(piece, attr);
          piece.Dispose();
          if (newId != Guid.Empty)
            newIds.Add(newId);
        }

        if (newIds.Count == 0)
        {
          anyFailed = true;
          continue;
        }

        if (!doc.Objects.Delete(id, true))
          anyFailed = true;

        totalCreated += newIds.Count;
      }

      doc.Views.Redraw();

      if (totalCreated == 0)
      {
        if (anyFailed)
          return Result.Failure;

        RhinoApp.WriteLine("vSplitAtCorners: no corners found to split.");
      }

      return anyFailed ? Result.Success : Result.Success;
    }
    finally
    {
      conduit.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static void LoadPersistedOptions()
  {
    var values = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var angle = _angleDeg;
        var minLength = _minLength;
        var ignoreNotches = _ignoreNotches;

        if (ToolsOptionStore.TryGetDouble(section, AngleKey, out var persistedAngle))
          angle = persistedAngle;
        if (ToolsOptionStore.TryGetDouble(section, MinLengthKey, out var persistedMin))
          minLength = persistedMin;
        if (ToolsOptionStore.TryGetBool(section, IgnoreNotchesKey, out var persistedIgnoreNotches))
          ignoreNotches = persistedIgnoreNotches;

        return (angle, minLength, ignoreNotches);
      });

    _angleDeg = Math.Max(0.01, Math.Min(179.99, values.angle));
    _minLength = Math.Max(0.0, values.minLength);
    _ignoreNotches = values.ignoreNotches;
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[AngleKey] = _angleDeg;
        section[MinLengthKey] = _minLength;
        section[IgnoreNotchesKey] = _ignoreNotches;
      });
  }

  private static bool IsIgnoredNotch(RhinoObject obj, bool ignoreNotches)
  {
    return ignoreNotches && string.Equals(
      obj.Attributes.Name, "NOTCH", StringComparison.OrdinalIgnoreCase);
  }

  private static List<CornerCandidate> DetectCornerCandidates(Curve curve, double angleDeg, double minLength)
  {
    var candidates = new List<CornerCandidate>();

    if (!curve.IsValid)
      return candidates;

    var domain = curve.Domain;
    var kinkParams = new List<double>();

    var seekStart = domain.T0;
    while (curve.GetNextDiscontinuity(Continuity.G1_continuous, seekStart, domain.T1, out var t))
    {
      kinkParams.Add(t);
      seekStart = t + RhinoMath.SqrtEpsilon;
      if (seekStart >= domain.T1)
        break;
    }

    // GetNextDiscontinuity only searches strictly between T0 and T1, so the
    // seam corner of a closed curve (where the last segment meets the first)
    // is never returned. Test it explicitly.
    if (curve.IsClosed)
      kinkParams.Add(domain.T0);

    kinkParams = kinkParams
      .Distinct(new DoubleApproxComparer(RhinoMath.ZeroTolerance * 10.0))
      .OrderBy(v => v)
      .ToList();

    foreach (var param in kinkParams)
    {
      if (!TryCornerAtParameter(curve, param, angleDeg, minLength, out var point, out var cornerAngle))
        continue;

      candidates.Add(new CornerCandidate(param, point, cornerAngle, false));
    }

    return candidates;
  }

  private static bool TryCornerAtParameter(Curve curve, double param, double angleThresholdDeg, double minLength, out Point3d point, out double cornerAngle)
  {
    point = Point3d.Unset;
    cornerAngle = 0.0;

    var domain = curve.Domain;
    var t0 = domain.T0;
    var t1 = domain.T1;

    var eps = Math.Max((t1 - t0) * 1e-6, RhinoMath.ZeroTolerance * 10.0);

    Vector3d tanA, tanB;

    // Seam of a closed curve: the kink straddles T0==T1, so sample
    // inbound from the end of the last segment and outbound from the
    // start of the first segment.
    if (curve.IsClosed && Math.Abs(param - t0) < eps)
    {
      tanA = curve.TangentAt(t1 - eps);
      tanB = curve.TangentAt(t0 + eps);
    }
    else
    {
      var left = Math.Max(t0, param - eps);
      var right = Math.Min(t1, param + eps);
      if (right <= left)
        return false;
      tanA = curve.TangentAt(left);
      tanB = curve.TangentAt(right);
    }
    if (!tanA.Unitize() || !tanB.Unitize())
      return false;

    var dot = Math.Max(-1.0, Math.Min(1.0, tanA * tanB));
    var angle = RhinoMath.ToDegrees(Math.Acos(dot));

    if (angle < angleThresholdDeg)
      return false;

    if (minLength > RhinoMath.ZeroTolerance && !(curve.IsClosed && Math.Abs(param - t0) < eps))
    {
      var leftSeg = curve.Trim(t0, param);
      var rightSeg = curve.Trim(param, t1);

      try
      {
        if (leftSeg == null || rightSeg == null)
          return false;

        if (leftSeg.GetLength() < minLength || rightSeg.GetLength() < minLength)
          return false;
      }
      finally
      {
        leftSeg?.Dispose();
        rightSeg?.Dispose();
      }
    }

    point = curve.PointAt(param);
    cornerAngle = angle;
    return true;
  }

  private readonly record struct CornerCandidate(double Parameter, Point3d Point, double AngleDegrees, bool Manual);

  private readonly record struct SplitPointKey(Guid CurveId, double Parameter)
  {
    public bool Equals(SplitPointKey other)
    {
      if (CurveId != other.CurveId)
        return false;

      return Math.Abs(Parameter - other.Parameter) <= 1e-7;
    }

    public override int GetHashCode()
    {
      var rounded = Math.Round(Parameter, 7);
      return HashCode.Combine(CurveId, rounded);
    }
  }

  private sealed class CornerPreviewConduit : Rhino.Display.DisplayConduit
  {
    private readonly RhinoDoc _doc;
    private readonly List<Guid> _curveIds;

    private readonly HashSet<SplitPointKey> _manualCorners;
    private readonly HashSet<SplitPointKey> _removedAutoCorners;

    private readonly Dictionary<Guid, List<CornerCandidate>> _autoByCurve = new();

    private double _angleDeg;
    private double _minLength;
    private bool _ignoreNotches;

    public CornerPreviewConduit(
      RhinoDoc doc,
      List<Guid> curveIds,
      double angleDeg,
      double minLength,
      bool ignoreNotches,
      HashSet<SplitPointKey> manualCorners,
      HashSet<SplitPointKey> removedAutoCorners)
    {
      _doc = doc;
      _curveIds = curveIds;
      _angleDeg = angleDeg;
      _minLength = minLength;
      _ignoreNotches = ignoreNotches;
      _manualCorners = manualCorners;
      _removedAutoCorners = removedAutoCorners;
      RebuildAutoCandidates();
    }

    public void UpdateSettings(double angleDeg, double minLength, bool ignoreNotches)
    {
      var changed = Math.Abs(_angleDeg - angleDeg) > 1e-9 ||
        Math.Abs(_minLength - minLength) > 1e-9 ||
        _ignoreNotches != ignoreNotches;
      _angleDeg = angleDeg;
      _minLength = minLength;
      _ignoreNotches = ignoreNotches;
      if (changed)
        RebuildAutoCandidates();
    }

    /// <summary>
    /// Toggles the nearest split point:
    /// - Near a manual corner → removes it.
    /// - Near an auto corner (active or removed) → toggles removed state.
    /// - Near a curve but no corner → adds a manual split at that location.
    /// </summary>
    public void TryToggleNear(Point3d pick)
    {
      const double PixelTol = 20.0;
      var viewport = _doc.Views.ActiveView?.ActiveViewport;
      double searchTol;
      if (viewport != null && viewport.GetWorldToScreenScale(pick, out var ppu) && ppu > 0.0)
        searchTol = PixelTol / ppu;
      else
        searchTol = Math.Max(_doc.ModelAbsoluteTolerance * 8.0, 1.0);

      // 1. Check near manual corners → remove.
      var bestD = double.MaxValue;
      SplitPointKey bestManualKey = default;
      bool foundManual = false;

      foreach (var m in _manualCorners)
      {
        var obj = _doc.Objects.FindId(m.CurveId) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        var p = obj.CurveGeometry.PointAt(m.Parameter);
        var d = pick.DistanceTo(p);
        if (d <= searchTol && d < bestD)
        {
          bestD = d;
          bestManualKey = m;
          foundManual = true;
        }
      }

      if (foundManual)
      {
        _manualCorners.Remove(bestManualKey);
        return;
      }

      // 2. Check near auto corners → toggle removed.
      bestD = double.MaxValue;
      SplitPointKey bestAutoKey = default;
      bool foundAuto = false;

      foreach (var kv in _autoByCurve)
      {
        foreach (var c in kv.Value)
        {
          var d = pick.DistanceTo(c.Point);
          if (d <= searchTol && d < bestD)
          {
            bestD = d;
            bestAutoKey = new SplitPointKey(kv.Key, c.Parameter);
            foundAuto = true;
          }
        }
      }

      if (foundAuto)
      {
        if (_removedAutoCorners.Contains(bestAutoKey))
          _removedAutoCorners.Remove(bestAutoKey);
        else
          _removedAutoCorners.Add(bestAutoKey);
        return;
      }

      // 3. Snap to nearest curve → add manual split.
      bestD = double.MaxValue;
      var bestCurveId = Guid.Empty;
      var bestT = 0.0;

      foreach (var id in _curveIds)
      {
        var obj = _doc.Objects.FindId(id) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        var curve = obj.CurveGeometry;
        if (!curve.ClosestPoint(pick, out var t))
          continue;

        var d = pick.DistanceTo(curve.PointAt(t));
        if (d <= searchTol && d < bestD)
        {
          bestD = d;
          bestCurveId = id;
          bestT = t;
        }
      }

      if (bestCurveId != Guid.Empty)
        _manualCorners.Add(new SplitPointKey(bestCurveId, bestT));
    }

    public void OnDynamicDraw(GetPointDrawEventArgs e)
    {
      DrawPreview(e.Display);
    }

    protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
    {
      base.DrawForeground(e);
      DrawPreview(e.Display);
    }

    private void DrawPreview(DisplayPipeline display)
    {
      foreach (var curveId in _curveIds)
      {
        var obj = _doc.Objects.FindId(curveId) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        display.DrawCurve(obj.CurveGeometry, Color.FromArgb(120, 200, 200, 200), 1);
      }

      foreach (var kv in _autoByCurve)
      {
        foreach (var candidate in kv.Value)
        {
          var k = new SplitPointKey(kv.Key, candidate.Parameter);
          var color = _removedAutoCorners.Contains(k) ? Color.Gray : Color.OrangeRed;
          display.DrawPoint(candidate.Point, PointStyle.RoundSimple, 3, color);
        }
      }

      foreach (var m in _manualCorners)
      {
        var obj = _doc.Objects.FindId(m.CurveId) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        var p = obj.CurveGeometry.PointAt(m.Parameter);
        display.DrawPoint(p, PointStyle.X, 3, Color.Cyan);
      }
    }

    private void RebuildAutoCandidates()
    {
      _autoByCurve.Clear();

      foreach (var id in _curveIds)
      {
        var obj = _doc.Objects.FindId(id) as CurveObject;
        if (obj?.CurveGeometry == null || IsIgnoredNotch(obj, _ignoreNotches))
          continue;

        var dup = obj.CurveGeometry.DuplicateCurve();
        if (dup == null)
          continue;

        var corners = DetectCornerCandidates(dup, _angleDeg, _minLength);
        _autoByCurve[id] = corners;
      }
    }
  }

  private sealed class DoubleApproxComparer : IEqualityComparer<double>
  {
    private readonly double _eps;

    public DoubleApproxComparer(double eps)
    {
      _eps = Math.Max(eps, 1e-12);
    }

    public bool Equals(double x, double y)
    {
      return Math.Abs(x - y) <= _eps;
    }

    public int GetHashCode(double obj)
    {
      var q = Math.Round(obj / _eps);
      return q.GetHashCode();
    }
  }
}
