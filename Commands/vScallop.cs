using System;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native scallop-arc command ported from Scallop.py.
/// </summary>
public sealed class vScallop : Command
{
  private const string OptionsSectionName = "vScallop";
  private const string SizeKey = "size";
  private const string DeleteOriginalKey = "deleteOriginal";
  private const string FreeKey = "free";
  private const string AutoKey = "auto";
  private const string SizePercentKey = "sizePercent";

  private const double DefaultSize = 1.0;
  private const double DefaultSizePercent = 5.0;

  private static double _size = DefaultSize;
  private static bool _deleteOriginal;
  private static bool _free;
  private static bool _auto;
  private static double _sizePercent = DefaultSizePercent;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vScallop";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    while (true)
    {
      // ── Outer prompt: select curve or choose Points ──────────────────
      var go = new GetObject();
      go.SetCommandPrompt("Select curve");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.AcceptNumber(true, false);

      var autoOpt   = new OptionToggle(_auto, "No", "Yes");
      var deleteOpt = new OptionToggle(_deleteOriginal, "No", "Yes");
      var freeOpt   = new OptionToggle(_free, "No", "Yes");
      go.AddOptionToggle("Auto", ref autoOpt);
      var idxSize   = go.AddOption("Size", FormatSize());
      go.AddOptionToggle("DeleteOriginal", ref deleteOpt);
      go.AddOptionToggle("Free", ref freeOpt);
      var idxPoints = go.AddOption("Points");

      var outerResult = go.Get();

      if (go.CommandResult() != Result.Success)
      {
        SavePersistedOptions();
        return Result.Success;
      }

      _auto           = autoOpt.CurrentValue;
      _deleteOriginal = deleteOpt.CurrentValue;
      _free           = freeOpt.CurrentValue;

      if (outerResult == GetResult.Number)
      {
        if (_auto) _sizePercent = Math.Max(go.Number(), 0.001);
        else       _size = Math.Max(go.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      // ── Handle options ────────────────────────────────────────────────
      var twoPointMode = false;
      if (outerResult == GetResult.Option)
      {
        var opt = go.Option();
        if (opt != null)
        {
          if (opt.Index == idxPoints)
            twoPointMode = true;
          else if (opt.Index == idxSize)
          {
            var v = GetSizeSubprompt(doc);
            if (v != null)
            {
              if (_auto) _sizePercent = v.Value;
              else _size = v.Value;
              SavePersistedOptions();
            }
            continue;
          }
          else
          {
            SavePersistedOptions();
            continue;
          }
        }
        else
        {
          SavePersistedOptions();
          continue;
        }
      }

      // ── Resolve input points ───────────────────────────────────────────
      Point3d pointA, pointB;
      Guid sourceLineId;

      if (twoPointMode)
      {
        if (!TryPickPoint(doc, "Pick first point", null, out pointA))
          continue;
        if (!TryPickPoint(doc, "Pick second point", pointA, out pointB))
          continue;
        sourceLineId = Guid.Empty;
      }
      else if (outerResult == GetResult.Object)
      {
        var objRef = go.Object(0);
        if (objRef?.Curve() is not Curve curve)
          continue;

        if (curve.IsClosed)
        {
          RhinoApp.WriteLine("vScallop: selected curve is closed; need an open curve.");
          doc.Objects.UnselectAll();
          doc.Views.Redraw();
          continue;
        }

        pointA = curve.PointAtStart;
        pointB = curve.PointAtEnd;
        sourceLineId = objRef.ObjectId;
      }
      else
        continue;

      if (pointA.DistanceTo(pointB) <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine("vScallop: points are too close together.");
        continue;
      }

      // ── Side / bulge ───────────────────────────────────────────────────
      if (!TryGetSideAndBulge(doc, pointA, pointB, out var sideDirection, out var bulgeSize))
        continue;

      // ── Create arc ────────────────────────────────────────────────────
      var arcCurve = CreateScallopArc(pointA, pointB, sideDirection, bulgeSize);
      if (arcCurve == null || !arcCurve.IsValid)
      {
        RhinoApp.WriteLine("vScallop: failed to create arc.");
        continue;
      }

      var arcId = doc.Objects.AddCurve(arcCurve);
      if (arcId == Guid.Empty)
      {
        RhinoApp.WriteLine("vScallop: failed to add arc to document.");
        continue;
      }

      if (_deleteOriginal && sourceLineId != Guid.Empty)
        doc.Objects.Delete(sourceLineId, true);

      doc.Objects.UnselectAll();
      SavePersistedOptions();
      doc.Views.Redraw();
    }
  }

  private static void LoadPersistedOptions()
  {
    var values = vToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var size = _size;
        var deleteOriginal = _deleteOriginal;
        var free = _free;
        var auto = _auto;
        var sizePercent = _sizePercent;

        if (vToolsOptionStore.TryGetDouble(section, SizeKey, out var persistedSize) && persistedSize > RhinoMath.ZeroTolerance)
          size = persistedSize;
        if (vToolsOptionStore.TryGetBool(section, DeleteOriginalKey, out var persistedDelete))
          deleteOriginal = persistedDelete;
        if (vToolsOptionStore.TryGetBool(section, FreeKey, out var persistedFree))
          free = persistedFree;
        if (vToolsOptionStore.TryGetBool(section, AutoKey, out var persistedAuto))
          auto = persistedAuto;
        if (vToolsOptionStore.TryGetDouble(section, SizePercentKey, out var persistedSizePercent) && persistedSizePercent > 0)
          sizePercent = persistedSizePercent;

        return (size, deleteOriginal, free, auto, sizePercent);
      });

    _size = Math.Max(values.size, RhinoMath.ZeroTolerance);
    _deleteOriginal = values.deleteOriginal;
    _free = values.free;
    _auto = values.auto;
    _sizePercent = Math.Max(values.sizePercent, 0.001);
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[SizeKey] = _size;
        section[DeleteOriginalKey] = _deleteOriginal;
        section[FreeKey] = _free;
        section[AutoKey] = _auto;
        section[SizePercentKey] = _sizePercent;
      });
  }

  private static string FormatSize() =>
    _auto ? $"{_sizePercent:G}%" : $"{_size:G}";

  private static double? GetSizeSubprompt(RhinoDoc doc)
  {
    double current = _auto ? _sizePercent : _size;
    double def = _auto ? DefaultSizePercent : DefaultSize;
    double minVal = _auto ? 0.001 : doc.ModelAbsoluteTolerance;
    string unit = _auto ? "% (d=reset to 5)" : $"(d=reset to {DefaultSize:G})";

    var gs = new GetString();
    gs.SetCommandPrompt($"Size {unit} <{current:G}>");
    gs.AcceptNumber(true, false);
    var r = gs.Get();
    if (gs.CommandResult() != Result.Success)
      return null;
    if (r == GetResult.Number)
      return Math.Max(gs.Number(), minVal);
    if (r == GetResult.String)
    {
      var s = gs.StringResult()?.Trim().ToLowerInvariant() ?? "";
      while (s.StartsWith("_", StringComparison.Ordinal))
        s = s[1..];
      if (s is "d" or "r")
        return def;
      if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v))
        return Math.Max(v, minVal);
    }
    return null;
  }

  private static bool TryPickPoint(RhinoDoc doc, string prompt, Point3d? basePoint, out Point3d point)
  {
    point = Point3d.Unset;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt(prompt);
      if (basePoint.HasValue && basePoint.Value.IsValid)
        gp.DrawLineFromPoint(basePoint.Value, true);

      gp.AcceptNumber(true, false);

      var autoOption = new OptionToggle(_auto, "No", "Yes");
      var freeOption = new OptionToggle(_free, "No", "Yes");

      gp.AddOptionToggle("Auto", ref autoOption);
      var idxSize = gp.AddOption("Size", FormatSize());
      gp.AddOptionToggle("Free", ref freeOption);

      var result = gp.Get();
      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _auto = autoOption.CurrentValue;
        _free = freeOption.CurrentValue;
        var opt = gp.Option();
        if (opt?.Index == idxSize)
        {
          var v = GetSizeSubprompt(doc);
          if (v != null) { if (_auto) _sizePercent = v.Value; else _size = v.Value; }
        }
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        if (_auto) _sizePercent = Math.Max(gp.Number(), 0.001);
        else       _size = Math.Max(gp.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Point)
      {
        point = gp.Point();
        return true;
      }

      return false;
    }
  }

  private static bool TryGetSideAndBulge(
    RhinoDoc doc,
    Point3d pointA,
    Point3d pointB,
    out Vector3d sideDirection,
    out double bulgeSize)
  {
    sideDirection = Vector3d.Unset;
    bulgeSize = 0.0;

    var mid = 0.5 * (pointA + pointB);

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick side for arc bulge");
      gp.SetBasePoint(mid, true);
      gp.AcceptNumber(true, false);

      var autoOption = new OptionToggle(_auto, "No", "Yes");
      var freeOption = new OptionToggle(_free, "No", "Yes");

      gp.AddOptionToggle("Auto", ref autoOption);
      var idxSize = gp.AddOption("Size", FormatSize());
      gp.AddOptionToggle("Free", ref freeOption);

      EventHandler<GetPointDrawEventArgs> draw = (_, e) =>
      {
        var preview = BuildPreviewArc(doc, pointA, pointB, e.CurrentPoint);
        if (preview != null)
          e.Display.DrawCurve(preview, Color.DeepSkyBlue, 2);
      };

      gp.DynamicDraw += draw;
      var result = gp.Get();
      gp.DynamicDraw -= draw;

      if (gp.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.Option)
      {
        _auto = autoOption.CurrentValue;
        _free = freeOption.CurrentValue;
        var opt = gp.Option();
        if (opt?.Index == idxSize)
        {
          var v = GetSizeSubprompt(doc);
          if (v != null) { if (_auto) _sizePercent = v.Value; else _size = v.Value; }
        }
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Number)
      {
        if (_auto) _sizePercent = Math.Max(gp.Number(), 0.001);
        else       _size = Math.Max(gp.Number(), doc.ModelAbsoluteTolerance);
        SavePersistedOptions();
        continue;
      }

      if (result != GetResult.Point)
        return false;

      var sidePoint = gp.Point();
      if (!TryResolvePerpDirection(doc, pointA, pointB, sidePoint, out sideDirection))
      {
        RhinoApp.WriteLine("vScallop: unable to resolve side direction.");
        continue;
      }

      bulgeSize = ResolveBulgeSize(pointA, pointB, sidePoint, sideDirection);
      if (bulgeSize <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine("vScallop: bulge size is too small.");
        continue;
      }

      return true;
    }
  }

  private static bool TryResolvePerpDirection(RhinoDoc doc, Point3d pointA, Point3d pointB, Point3d sidePoint, out Vector3d direction)
  {
    direction = Vector3d.Unset;

    var chord = pointB - pointA;
    if (!chord.Unitize())
      return false;

    var activeView = doc.Views.ActiveView;
    var zAxis = activeView != null
      ? activeView.ActiveViewport.ConstructionPlane().ZAxis
      : Vector3d.ZAxis;

    var perp = Vector3d.CrossProduct(zAxis, chord);
    if (!perp.Unitize())
      return false;

    var mid = 0.5 * (pointA + pointB);
    var toSide = sidePoint - mid;
    if (Vector3d.Multiply(perp, toSide) < 0.0)
      perp.Reverse();

    direction = perp;
    return true;
  }

  private static double ResolveBulgeSize(Point3d pointA, Point3d pointB, Point3d sidePoint, Vector3d sideDirection)
  {
    if (!_free)
    {
      if (_auto)
        return Math.Max(pointA.DistanceTo(pointB) * _sizePercent / 100.0, 0.0);
      return _size;
    }

    var mid = 0.5 * (pointA + pointB);
    var toSide = sidePoint - mid;
    return Math.Abs(Vector3d.Multiply(toSide, sideDirection));
  }

  private static Curve? BuildPreviewArc(RhinoDoc doc, Point3d pointA, Point3d pointB, Point3d sidePoint)
  {
    if (!TryResolvePerpDirection(doc, pointA, pointB, sidePoint, out var direction))
      return null;

    var bulge = ResolveBulgeSize(pointA, pointB, sidePoint, direction);
    if (bulge <= doc.ModelAbsoluteTolerance)
      return null;

    return CreateScallopArc(pointA, pointB, direction, bulge);
  }

  private static ArcCurve? CreateScallopArc(Point3d pointA, Point3d pointB, Vector3d sideDirection, double sizeValue)
  {
    var mid = 0.5 * (pointA + pointB);
    var arcMid = mid + (sideDirection * sizeValue);
    var arc = new Arc(pointA, arcMid, pointB);
    if (!arc.IsValid)
      return null;

    return new ArcCurve(arc);
  }
}
