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

  private static double _angleDeg = 45.0;
  private static double _minLength;

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
    var suppressedCorners = new HashSet<SplitPointKey>();

    var conduit = new CornerPreviewConduit(doc, selectedIds, angleOpt.CurrentValue, minLenOpt.CurrentValue, manualCorners, suppressedCorners);
    conduit.Enabled = true;

    try
    {
      while (true)
      {
        conduit.UpdateThresholds(angleOpt.CurrentValue, minLenOpt.CurrentValue);

        var gp = new GetPoint();
        gp.SetCommandPrompt("Click highlighted corner to toggle split. Enter to apply");
        gp.AcceptNothing(true);
        gp.AcceptString(true);

        var idxAngle = gp.AddOptionDouble("Angle", ref angleOpt);
        var idxMinLen = gp.AddOptionDouble("MinLength", ref minLenOpt);
        var idxClearManual = gp.AddOption("ClearManual");
        var idxClearSuppressed = gp.AddOption("ClearSuppressed");
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
            if (opt.Index == idxClearManual)
              manualCorners.Clear();
            else if (opt.Index == idxClearSuppressed)
              suppressedCorners.Clear();
            else if (opt.Index == idxClearAll)
            {
              manualCorners.Clear();
              suppressedCorners.Clear();
            }
          }

          continue;
        }

        if (result == GetResult.String)
        {
          var token = (gp.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
          if (token is "clear" or "clearall")
          {
            manualCorners.Clear();
            suppressedCorners.Clear();
            continue;
          }

          if (token is "manual")
          {
            RhinoApp.WriteLine("vSplitAtCorners: click near a corner to add manual split.");
            continue;
          }

          if (token is "suppress")
          {
            RhinoApp.WriteLine("vSplitAtCorners: click near a highlighted corner to suppress split.");
            continue;
          }

          continue;
        }

        if (result == GetResult.Nothing)
          break;

        if (result != GetResult.Point)
          continue;

        var pick = gp.Point();

        if (!conduit.TryFindNearestCandidate(pick, out var candidate, out var key, out var isAutoCandidate))
          continue;

        if (isAutoCandidate)
        {
          if (suppressedCorners.Contains(key))
            suppressedCorners.Remove(key);
          else
            suppressedCorners.Add(key);

          manualCorners.Remove(key);
        }
        else
        {
          if (manualCorners.Contains(key))
            manualCorners.Remove(key);
          else
            manualCorners.Add(key);

          suppressedCorners.Remove(key);
        }

        conduit.UpdateThresholds(angleOpt.CurrentValue, minLenOpt.CurrentValue);
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
        if (obj?.CurveGeometry == null)
          continue;

        var curve = obj.CurveGeometry.DuplicateCurve();
        if (curve == null)
          continue;

        var basePoints = DetectCornerCandidates(curve, _angleDeg, _minLength);

        var autoPoints = basePoints
          .Where(p => !suppressedCorners.Contains(new SplitPointKey(id, p.Parameter)))
          .ToList();

        var manualPointsForCurve = manualCorners
          .Where(m => m.CurveId == id)
          .Select(m => new CornerCandidate(m.Parameter, Point3d.Unset, 0.0, true))
          .ToList();

        var allParams = new List<double>();
        foreach (var c in autoPoints)
          allParams.Add(c.Parameter);
        foreach (var c in manualPointsForCurve)
          allParams.Add(c.Parameter);

        if (allParams.Count == 0)
          continue;

        var domain = curve.Domain;
        var tol = Math.Max(doc.ModelAbsoluteTolerance * 1e-4, RhinoMath.ZeroTolerance);
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
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var angle = _angleDeg;
        var minLength = _minLength;

        if (vToolsOptionStore.TryGetDouble(section, AngleKey, out var persistedAngle))
          angle = persistedAngle;
        if (vToolsOptionStore.TryGetDouble(section, MinLengthKey, out var persistedMin))
          minLength = persistedMin;

        return (angle, minLength);
      });

    _angleDeg = Math.Max(0.01, Math.Min(179.99, values.angle));
    _minLength = Math.Max(0.0, values.minLength);
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[AngleKey] = _angleDeg;
        section[MinLengthKey] = _minLength;
      });
  }

  private static List<CornerCandidate> DetectCornerCandidates(Curve curve, double angleDeg, double minLength)
  {
    var candidates = new List<CornerCandidate>();

    if (!curve.IsValid)
      return candidates;

    var kinkParams = curve.GetNextDiscontinuity(
      Continuity.G1_continuous,
      curve.Domain.T0,
      curve.Domain.T1,
      out var t) ? new List<double> { t } : new List<double>();

    var seekStart = curve.Domain.T0;
    while (curve.GetNextDiscontinuity(Continuity.G1_continuous, seekStart, curve.Domain.T1, out t))
    {
      kinkParams.Add(t);
      seekStart = t + RhinoMath.SqrtEpsilon;
      if (seekStart >= curve.Domain.T1)
        break;
    }

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
    var left = Math.Max(t0, param - eps);
    var right = Math.Min(t1, param + eps);

    if (right <= left)
      return false;

    var tanA = curve.TangentAt(left);
    var tanB = curve.TangentAt(right);
    if (!tanA.Unitize() || !tanB.Unitize())
      return false;

    var dot = Math.Max(-1.0, Math.Min(1.0, tanA * tanB));
    var angle = RhinoMath.ToDegrees(Math.Acos(dot));

    if (angle < angleThresholdDeg)
      return false;

    if (minLength > RhinoMath.ZeroTolerance)
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
    private readonly HashSet<SplitPointKey> _suppressedCorners;

    private readonly Dictionary<Guid, List<CornerCandidate>> _autoByCurve = new();

    private double _angleDeg;
    private double _minLength;

    public CornerPreviewConduit(
      RhinoDoc doc,
      List<Guid> curveIds,
      double angleDeg,
      double minLength,
      HashSet<SplitPointKey> manualCorners,
      HashSet<SplitPointKey> suppressedCorners)
    {
      _doc = doc;
      _curveIds = curveIds;
      _angleDeg = angleDeg;
      _minLength = minLength;
      _manualCorners = manualCorners;
      _suppressedCorners = suppressedCorners;
      RebuildAutoCandidates();
    }

    public void UpdateThresholds(double angleDeg, double minLength)
    {
      var changed = Math.Abs(_angleDeg - angleDeg) > 1e-9 || Math.Abs(_minLength - minLength) > 1e-9;
      _angleDeg = angleDeg;
      _minLength = minLength;
      if (changed)
        RebuildAutoCandidates();
    }

    public bool TryFindNearestCandidate(Point3d pick, out CornerCandidate candidate, out SplitPointKey key, out bool isAutoCandidate)
    {
      candidate = default;
      key = default;
      isAutoCandidate = false;

      // Use screen-pixel distance so tolerance is scale- and zoom-independent.
      const double PixelTol = 20.0;
      var viewport = _doc.Views.ActiveView?.ActiveViewport;
      double searchTol;
      if (viewport != null && viewport.GetWorldToScreenScale(pick, out var ppu) && ppu > 0.0)
        searchTol = PixelTol / ppu;
      else
        searchTol = Math.Max(_doc.ModelAbsoluteTolerance * 8.0, 1.0);

      var bestD = double.MaxValue;
      bool found = false;

      foreach (var kv in _autoByCurve)
      {
        var curveId = kv.Key;
        foreach (var c in kv.Value)
        {
          var k = new SplitPointKey(curveId, c.Parameter);
          var d = pick.DistanceTo(c.Point);
          if (d <= searchTol && d < bestD)
          {
            bestD = d;
            candidate = c;
            key = k;
            isAutoCandidate = true;
            found = true;
          }
        }
      }

      foreach (var m in _manualCorners)
      {
        var obj = _doc.Objects.FindId(m.CurveId) as CurveObject;
        if (obj?.CurveGeometry == null)
          continue;

        var curve = obj.CurveGeometry;
        var p = curve.PointAt(m.Parameter);
        var d = pick.DistanceTo(p);
        if (d <= searchTol && d < bestD)
        {
          bestD = d;
          candidate = new CornerCandidate(m.Parameter, p, 180.0, true);
          key = m;
          isAutoCandidate = false;
          found = true;
        }
      }

      return found;
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

      var autoColor = Color.OrangeRed;
      var manualColor = Color.Cyan;
      var suppressedColor = Color.Gray;

      foreach (var curveId in _curveIds)
      {
        var obj = _doc.Objects.FindId(curveId) as CurveObject;
        if (obj?.CurveGeometry == null)
          continue;

        display.DrawCurve(obj.CurveGeometry, Color.FromArgb(120, 200, 200, 200), 1);
      }

      foreach (var kv in _autoByCurve)
      {
        var curveId = kv.Key;

        foreach (var candidate in kv.Value)
        {
          var key = new SplitPointKey(curveId, candidate.Parameter);
          var color = _suppressedCorners.Contains(key) ? suppressedColor : autoColor;
          var size = _suppressedCorners.Contains(key) ? 2 : 3;
          display.DrawPoint(candidate.Point, PointStyle.RoundSimple, size, color);
        }
      }

      foreach (var m in _manualCorners)
      {
        var obj = _doc.Objects.FindId(m.CurveId) as CurveObject;
        if (obj?.CurveGeometry == null)
          continue;

        var p = obj.CurveGeometry.PointAt(m.Parameter);
        display.DrawPoint(p, PointStyle.X, 3, manualColor);
      }
    }

    private void RebuildAutoCandidates()
    {
      _autoByCurve.Clear();

      foreach (var id in _curveIds)
      {
        var obj = _doc.Objects.FindId(id) as CurveObject;
        if (obj?.CurveGeometry == null)
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
