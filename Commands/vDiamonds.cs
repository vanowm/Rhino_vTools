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
/// Draws an argyle diamond pattern on PLOT, a bounding rectangle on CUT1,
/// and a size label on Reference.  Ported from Diamonds.py.
/// </summary>
public sealed class vDiamonds : Command
{
  private const string SettingsSection = "vDiamonds";
  private const string WidthKey        = "width";
  private const string HeightKey       = "height";
  private const string CountWidthKey   = "countWidth";
  private const string CountHeightKey  = "countHeight";
  private const string ShowBoundaryKey = "showBoundary";
  private const string ShowSizeKey     = "showSize";
  private const string ShowCountKey    = "showCount";
  private const string BySizeWKey      = "bySizeW";
  private const string BySizeHKey      = "bySizeH";
  private const string LayerPlotKey    = "layerPlot";
  private const string LayerCutKey     = "layerCut";
  private const string LayerRefKey     = "layerRef";

  private static string _layerPlot = "PLOT";
  private static string _layerCut  = "CUT1";
  private static string _layerRef  = "Reference";

  private static readonly Color PlotColor = Color.FromArgb(0x0F, 0x8A, 0x8A);
  private static readonly Color CutColor  = Color.FromArgb(0xCC, 0x33, 0x33);
  private static readonly Color RefColor  = Color.White;

  private const double DefaultWidth  = 2.0;
  private const double DefaultHeight = 2.0;
  private const double DefaultCW     = 3.0;
  private const double DefaultCH     = 3.0;
  private const double LabelGap      = 0.125;

  private static double _width        = DefaultWidth;
  private static double _height       = DefaultHeight;
  private static double _cw           = DefaultCW;
  private static double _ch           = DefaultCH;
  private static bool   _showBoundary = true;
  private static bool   _showSize     = true;
  private static bool   _showCount    = true;
  private static double _bySizeW      = 0.0;   // stored dims (always positive when ever set)
  private static double _bySizeH      = 0.0;
  private static bool   _bySizeActive = false; // transient: true only during the current invocation

  public override string EnglishName => "vDiamonds";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadSettings();
    _bySizeActive = false;  // never carry over across command invocations
    EnsureLayer(doc, _layerPlot, PlotColor);
    EnsureLayer(doc, _layerCut,  CutColor);
    EnsureLayer(doc, _layerRef,  RefColor);

    while (true)
    {
      // Effective geometry parameters
      double W, H, patOffX, patOffY, byCW, byCH;
      if (_bySizeActive && _bySizeW > 0.0 && _bySizeH > 0.0)
      {
        W       = _bySizeW;
        H       = _bySizeH;
        byCW    = Math.Max(1.0, Math.Floor(W / _width));
        byCH    = Math.Max(1.0, Math.Floor(H / _height));
        patOffX = (W - byCW * _width)  / 2.0;
        patOffY = (H - byCH * _height) / 2.0;
      }
      else
      {
        byCW = _cw; byCH = _ch;
        W = byCW * _width; H = byCH * _height;
        patOffX = patOffY = 0.0;
      }

      // Build diamond pattern curves; in BySize mode the grid is centered and all
      // lines are clipped to the full bbox (extending to touch all four edges).
      var (plotCurves, _, sizeLabelTe, _) = BuildGeometry(
        _width, _height, byCW, byCH,
        patOffX, patOffY,
        _bySizeActive ? W : 0.0,
        _bySizeActive ? H : 0.0);

      // Full bbox CUT1 curve (may be larger than pattern area in bySize mode)
      var cutCurve = new PolylineCurve(new[]
      {
        new Point3d(0, 0, 0), new Point3d(W, 0, 0),
        new Point3d(W, H, 0), new Point3d(0, H, 0),
        new Point3d(0, 0, 0),
      });

      // Reposition size label to centre on full bbox width, then calibrate
      sizeLabelTe.Plane = new Plane(new Point3d(W / 2.0, H + LabelGap, 0.0), Vector3d.XAxis, Vector3d.YAxis);
      CalibrateTextHeight(sizeLabelTe, W);

      // Build count label (shows effective byCW × byCH), calibrate independently
      TextEntity? countLabelTe = null;
      if (_showCount)
      {
        countLabelTe = new TextEntity
        {
          Plane         = new Plane(new Point3d(W / 2.0, H + LabelGap, 0.0), Vector3d.XAxis, Vector3d.YAxis),
          PlainText     = $"({FmtFrac(byCW)} x {FmtFrac(byCH)})",
          TextHeight    = sizeLabelTe.TextHeight,
          Justification = TextJustification.BottomCenter,
        };
        CalibrateTextHeight(countLabelTe, W);
      }

      // Stack: size label sits above count label when both are shown
      if (_showSize && countLabelTe != null)
      {
        double countT = countLabelTe.TextHeight;
        sizeLabelTe.Plane = new Plane(
          new Point3d(W / 2.0, H + LabelGap + countT * 1.2, 0.0),
          Vector3d.XAxis, Vector3d.YAxis);
      }

      // Print bbox size to command history
      var bySizeNote = _bySizeActive ? $"  (centered {(int)byCW} x {(int)byCH} diamonds)" : "";
      RhinoApp.WriteLine($"Boundary box: {FmtFrac(W)} x {FmtFrac(H)}{bySizeNote}");

      var fadedPlot = FadeColor(LayerColor(doc, _layerPlot));
      var fadedCut  = FadeColor(LayerColor(doc, _layerCut));
      var fadedRef  = FadeColor(LayerColor(doc, _layerRef));

      var previewItems = new List<(GeometryBase Geom, Color Color)>();
      foreach (var crv in plotCurves)
        previewItems.Add((crv.DuplicateCurve(), fadedPlot));
      if (_showBoundary)
        previewItems.Add((cutCurve.DuplicateCurve(), fadedCut));
      if (_showSize)
        previewItems.Add((sizeLabelTe.Duplicate(), fadedRef));
      if (countLabelTe != null)
        previewItems.Add((countLabelTe.Duplicate(), fadedRef));

      var capturedBase  = new Point3d(0.0, H, 0.0);
      var capturedItems = previewItems;

      EventHandler<GetPointDrawEventArgs> onDraw = (_, e) =>
      {
        var xform = Transform.Translation(e.CurrentPoint - capturedBase);
        foreach (var (geom, color) in capturedItems)
        {
          var g = geom.Duplicate();
          if (g == null) continue;
          g.Transform(xform);
          if (g is Curve c)
            e.Display.DrawCurve(c, color, 1);
          else if (g is AnnotationBase ann)
            e.Display.DrawAnnotation(ann, color);
        }
      };

      var togBoundary = new OptionToggle(_showBoundary, "No", "Yes");
      var togSize     = new OptionToggle(_showSize,     "No", "Yes");
      var togCount    = new OptionToggle(_showCount,    "No", "Yes");

      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick diamond pattern placement point");
      gp.AcceptString(true);
      var idxW        = gp.AddOption("Width",       FmtOpt(_width));
      var idxH        = gp.AddOption("Height",      FmtOpt(_height));
      var idxCW       = gp.AddOption("CountWidth",  FmtOpt(_cw));
      var idxCH       = gp.AddOption("CountHeight", FmtOpt(_ch));
      var idxBySize   = _bySizeActive
        ? gp.AddOption("BySize", $"{FmtOpt(_bySizeW)}x{FmtOpt(_bySizeH)}")
        : gp.AddOption("BySize");
      var idxBoundary = gp.AddOptionToggle("Boundary", ref togBoundary);
      var idxSize     = gp.AddOptionToggle("Size",     ref togSize);
      var idxCount    = gp.AddOptionToggle("Count",    ref togCount);

      gp.DynamicDraw += onDraw;
      var result = gp.Get();
      gp.DynamicDraw -= onDraw;

      if (result == GetResult.Cancel)
        return Result.Cancel;

      // Direct string input at placement: "heightxwidth" sets diamond size
      if (result == GetResult.String)
      {
        var raw = gp.StringResult().Trim();
        var xi  = raw.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (xi > 0)
        {
          var a = ParseFrac(raw[..xi]);
          var b = ParseFrac(raw[(xi + 1)..]);
          if (a.HasValue && b.HasValue)
          {
            if (a.Value > 0.0) _height = a.Value;
            if (b.Value > 0.0) _width  = b.Value;
          }
        }
        else
        {
          var v = ParseFrac(raw);
          if (v.HasValue && v.Value > 0.0) _width = v.Value;
        }
        SaveSettings();
        continue;
      }

      if (result == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt == null) continue;

        if (opt.Index == idxW)
        {
          var v = GetDoubleSubprompt("Diamond width", _width);
          if (v == null) return Result.Cancel;
          if (v.Value > 0.0) _width = v.Value;
        }
        else if (opt.Index == idxH)
        {
          var v = GetDoubleSubprompt("Diamond height", _height);
          if (v == null) return Result.Cancel;
          if (v.Value > 0.0) _height = v.Value;
        }
        else if (opt.Index == idxCW)
        {
          var v = GetDoubleSubprompt("Number of diamonds wide", _cw);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1.0) _cw = v.Value;
        }
        else if (opt.Index == idxCH)
        {
          var v = GetDoubleSubprompt("Number of diamonds tall", _ch);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1.0) _ch = v.Value;
        }
        else if (opt.Index == idxBySize)
        {
          double curBsW = _bySizeW > 0.0 ? _bySizeW : W;
          double curBsH = _bySizeH > 0.0 ? _bySizeH : H;
          var v = GetPairSubprompt("Boundary box size (0 to deactivate)", curBsW, curBsH);
          if (v == null) return Result.Cancel;
          if (v.Value.A <= 0.0 || v.Value.B <= 0.0)
            _bySizeActive = false;  // deactivate only; stored dims are preserved
          else
          {
            _bySizeW = v.Value.A; _bySizeH = v.Value.B;
            _bySizeActive = true;
          }
        }
        else if (opt.Index == idxBoundary) _showBoundary = togBoundary.CurrentValue;
        else if (opt.Index == idxSize)     _showSize     = togSize.CurrentValue;
        else if (opt.Index == idxCount)    _showCount    = togCount.CurrentValue;

        SaveSettings();
        continue;
      }

      if (result == GetResult.Point)
      {
        var xform = Transform.Translation(gp.Point() - capturedBase);
        AddToDoc(doc, plotCurves,
                 _showBoundary ? cutCurve    : null,
                 _showSize     ? sizeLabelTe : null,
                 countLabelTe,
                 xform, _width, _height, byCW, byCH);
        _bySizeActive = false;  // BySize is one-time; deactivate after placement (dims preserved)
        SaveSettings();
        doc.Views.Redraw();
        return Result.Success;
      }
    }
  }

  // ── Geometry ────────────────────────────────────────────────────────────────

  private static (List<NurbsCurve> PlotCurves, PolylineCurve CutCurve, TextEntity SizeLabelTe, Point3d BasePt)
    BuildGeometry(double width, double height, double cw, double ch,
                  double offsetX = 0.0, double offsetY = 0.0,
                  double clipW = 0.0, double clipH = 0.0)
  {
    // clipW/clipH > 0: clip lines to this full bbox (BySize mode).
    // offsetX/offsetY: shift the diamond grid origin within that bbox.
    double innerH = ch * height;
    double W = clipW > 0.0 ? clipW : cw * width;
    double H = clipH > 0.0 ? clipH : innerH;
    double s = height / width;

    int cwCeil = (int)Math.Ceiling(cw);
    int chCeil = (int)Math.Ceiling(ch);
    // Widen the loop range in BySize mode so lines reach all four bbox edges.
    int extra = clipW > 0.0 ? 2 : 0;

    var plotCurves = new List<NurbsCurve>();

    // ↗ family (slope +s): y = s*x + b_n, intercept adjusted for grid offset
    for (int n = -extra; n < cwCeil + chCeil + extra; n++)
    {
      double b        = innerH - ((n + 0.5) * height);
      double bShifted = b + offsetY - s * offsetX;
      var r = ClipLineToBbox(s, bShifted, W, H);
      if (r.HasValue)
        plotCurves.Add(new Line(r.Value.A, r.Value.B).ToNurbsCurve());
    }

    // ↘ family (slope -s): y = -s*x + c_k, intercept adjusted for grid offset
    for (int k = -chCeil - extra; k < cwCeil + extra; k++)
    {
      double c        = innerH + ((k + 0.5) * height);
      double cShifted = c + offsetY + s * offsetX;
      var r = ClipLineToBbox(-s, cShifted, W, H);
      if (r.HasValue)
        plotCurves.Add(new Line(r.Value.A, r.Value.B).ToNurbsCurve());
    }

    var corners = new List<Point3d>
    {
      new(0, 0, 0), new(W, 0, 0),
      new(W, H, 0), new(0, H, 0),
      new(0, 0, 0),
    };
    var cutCurve = new PolylineCurve(corners);

    var labelText  = $"{FmtFrac(height)} x {FmtFrac(width)}";
    var textHeight = W / 10.0;
    var origin     = new Point3d(W / 2.0, H + LabelGap, 0.0);
    var labelPlane = new Plane(origin, Vector3d.XAxis, Vector3d.YAxis);
    var sizeLabelTe = new TextEntity
    {
      Plane         = labelPlane,
      PlainText     = labelText,
      TextHeight    = textHeight,
      Justification = TextJustification.BottomCenter,
    };

    return (plotCurves, cutCurve, sizeLabelTe, new Point3d(0.0, H, 0.0));
  }

  private static (Point3d A, Point3d B)? ClipLineToBbox(double slope, double intercept, double W, double H)
  {
    const double tol = 1e-9;
    var pts = new List<Point3d>();

    // x = 0
    double y = intercept;
    if (y >= -tol && y <= H + tol)
      pts.Add(new Point3d(0.0, Math.Max(0.0, Math.Min(H, y)), 0.0));

    // x = W
    y = (slope * W) + intercept;
    if (y >= -tol && y <= H + tol)
      pts.Add(new Point3d(W, Math.Max(0.0, Math.Min(H, y)), 0.0));

    if (Math.Abs(slope) > tol)
    {
      // y = 0 (strict interior in x)
      double x = -intercept / slope;
      if (x > tol && x < W - tol)
        pts.Add(new Point3d(x, 0.0, 0.0));

      // y = H (strict interior in x)
      x = (H - intercept) / slope;
      if (x > tol && x < W - tol)
        pts.Add(new Point3d(x, H, 0.0));
    }

    var unique = new List<Point3d>();
    foreach (var p in pts)
    {
      if (!unique.Exists(u => p.DistanceTo(u) < 1e-6))
        unique.Add(p);
    }

    if (unique.Count < 2 || unique[0].DistanceTo(unique[1]) < 1e-6)
      return null;

    return (unique[0], unique[1]);
  }

  private static void CalibrateTextHeight(TextEntity labelTe, double targetWidth)
  {
    // TextModelWidth returns the rendered text width in model units at the current TextHeight.
    double textWidth = labelTe.TextModelWidth;
    if (textWidth > 0.0)
      labelTe.TextHeight *= targetWidth / textWidth;
  }

  private static void AddToDoc(
    RhinoDoc doc,
    List<NurbsCurve> plotCurves,
    PolylineCurve? cutCurve,
    TextEntity? sizeLabelTe,
    TextEntity? countLabelTe,
    Transform xform,
    double width, double height, double cw, double ch)
  {
    var plotAttr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerPlot, PlotColor) };
    var cutAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerCut,  CutColor)  };
    var refAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerRef,  RefColor)  };

    var addedIds = new List<Guid>();

    foreach (var crv in plotCurves)
    {
      var c = crv.DuplicateCurve();
      c.Transform(xform);
      var id = doc.Objects.AddCurve(c, plotAttr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    if (cutCurve != null)
    {
      var cut = cutCurve.DuplicateCurve();
      cut.Transform(xform);
      var cutId = doc.Objects.AddCurve(cut, cutAttr);
      if (cutId != Guid.Empty) addedIds.Add(cutId);
    }

    foreach (var te in new[] { sizeLabelTe, countLabelTe })
    {
      if (te == null) continue;
      if (te.Duplicate() is TextEntity copy)
      {
        copy.Transform(xform);
        var teId = doc.Objects.AddText(copy, refAttr);
        if (teId != Guid.Empty) addedIds.Add(teId);
      }
    }

    if (addedIds.Count > 1)
    {
      var shortId   = Guid.NewGuid().ToString()[..8];
      var groupName = $"Diamonds_{FmtFrac(height)}x{FmtFrac(width)}_({FmtFrac(cw)}x{FmtFrac(ch)})_{shortId}";
      var groupIdx  = doc.Groups.Add(groupName);
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        var a = obj.Attributes.Duplicate();
        a.AddToGroup(groupIdx);
        doc.Objects.ModifyAttributes(id, a, false);
      }
    }
  }

  // ── Settings ─────────────────────────────────────────────────────────────

  private static void LoadSettings()
  {
    (_width, _height, _cw, _ch, _showBoundary, _showSize, _showCount, _bySizeW, _bySizeH, _layerPlot, _layerCut, _layerRef) = vToolsOptionStore.Read(SettingsSection, section =>
    {
      var w   = _width;
      var h   = _height;
      var cw  = _cw;
      var ch  = _ch;
      var sb  = _showBoundary;
      var ss  = _showSize;
      var sc  = _showCount;
      var bsW = _bySizeW;
      var bsH = _bySizeH;
      var lp  = _layerPlot;
      var lc  = _layerCut;
      var lr  = _layerRef;

      if (vToolsOptionStore.TryGetDouble(section, WidthKey,       out var pw)  && pw  > 0.0) w   = pw;
      if (vToolsOptionStore.TryGetDouble(section, HeightKey,      out var ph)  && ph  > 0.0) h   = ph;
      if (vToolsOptionStore.TryGetDouble(section, CountWidthKey,  out var pcw) && pcw >= 1.0) cw  = pcw;
      if (vToolsOptionStore.TryGetDouble(section, CountHeightKey, out var pch) && pch >= 1.0) ch  = pch;
      if (vToolsOptionStore.TryGetBool(section, ShowBoundaryKey, out var psb)) sb  = psb;
      if (vToolsOptionStore.TryGetBool(section, ShowSizeKey,     out var pss)) ss  = pss;
      if (vToolsOptionStore.TryGetBool(section, ShowCountKey,    out var psc)) sc  = psc;
      if (vToolsOptionStore.TryGetDouble(section, BySizeWKey,    out var pbsW) && pbsW > 0.0) bsW = pbsW;
      if (vToolsOptionStore.TryGetDouble(section, BySizeHKey,    out var pbsH) && pbsH > 0.0) bsH = pbsH;
      if (vToolsOptionStore.TryGetString(section, LayerPlotKey,  out var plp) && !string.IsNullOrWhiteSpace(plp)) lp = plp;
      if (vToolsOptionStore.TryGetString(section, LayerCutKey,   out var plc) && !string.IsNullOrWhiteSpace(plc)) lc = plc;
      if (vToolsOptionStore.TryGetString(section, LayerRefKey,   out var plr) && !string.IsNullOrWhiteSpace(plr)) lr = plr;

      return (w, h, cw, ch, sb, ss, sc, bsW, bsH, lp, lc, lr);
    });
  }

  private static void SaveSettings() =>
    vToolsOptionStore.Update(SettingsSection, section =>
    {
      section[WidthKey]        = _width;
      section[HeightKey]       = _height;
      section[CountWidthKey]   = _cw;
      section[CountHeightKey]  = _ch;
      section[ShowBoundaryKey] = _showBoundary;
      section[ShowSizeKey]     = _showSize;
      section[ShowCountKey]    = _showCount;
      section[BySizeWKey]      = _bySizeW;
      section[BySizeHKey]      = _bySizeH;
      section[LayerPlotKey]    = _layerPlot;
      section[LayerCutKey]     = _layerCut;
      section[LayerRefKey]     = _layerRef;
    });

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static int EnsureLayer(RhinoDoc doc, string name, Color createColor)
  {
    var idx = doc.Layers.FindByFullPath(name, -1);
    if (idx >= 0) return idx;
    var layer = new Layer { Name = name, Color = createColor };
    var added = doc.Layers.Add(layer);
    return added >= 0 ? added : doc.Layers.CurrentLayerIndex;
  }

  private static Color LayerColor(RhinoDoc doc, string name)
  {
    var idx = doc.Layers.FindByFullPath(name, -1);
    return idx >= 0 ? doc.Layers[idx].Color : Color.Gray;
  }

  private static Color FadeColor(Color c) =>
    Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);

  private static double? GetDoubleSubprompt(string prompt, double current)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({FmtFrac(current)})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return current;
    if (res == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (string.IsNullOrEmpty(raw)) return current;
      var v = ParseFrac(raw);
      return v.HasValue ? v.Value : current;
    }
    return null;
  }

  private static (double A, double B)? GetPairSubprompt(string prompt, double curA, double curB)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({FmtFrac(curA)} x {FmtFrac(curB)})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return (curA, curB);
    if (res == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (string.IsNullOrEmpty(raw)) return (curA, curB);
      var xi = raw.IndexOf('x', StringComparison.OrdinalIgnoreCase);
      if (xi > 0)
      {
        var a = ParseFrac(raw[..xi]);
        var b = ParseFrac(raw[(xi + 1)..]);
        if (a.HasValue && b.HasValue && a.Value > 0.0 && b.Value > 0.0)
          return (a.Value, b.Value);
      }
      var single = ParseFrac(raw);
      if (single.HasValue)
      {
        if (single.Value <= 0.0) return (0.0, 0.0);  // 0 = deactivate signal
        return (single.Value, curB);
      }
      return (curA, curB);
    }
    return null;
  }

  /// <summary>
  /// Parses decimal, fraction (N+P/Q or N-P/Q or P/Q), or plain integer.
  /// Returns null if input cannot be parsed.
  /// </summary>
  private static double? ParseFrac(string s)
  {
    s = s.Trim();
    if (string.IsNullOrEmpty(s)) return null;

    // Plain decimal / integer
    if (double.TryParse(s, System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var d))
      return d;

    // N+P/Q or N-P/Q  (dash must not be leading minus)
    int sep = s.IndexOf('+');
    if (sep < 0) { var di = s.IndexOf('-', 1); if (di > 0) sep = di; }
    if (sep > 0)
    {
      var sl = s.IndexOf('/', sep + 1);
      if (sl > sep + 1 &&
          double.TryParse(s[..sep], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var whole) &&
          double.TryParse(s[(sep + 1)..sl], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var num) &&
          double.TryParse(s[(sl + 1)..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var den) &&
          den != 0)
        return whole + num / den;
    }

    // Pure fraction P/Q
    var psl = s.IndexOf('/');
    if (psl > 0 &&
        double.TryParse(s[..psl], System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var pn) &&
        double.TryParse(s[(psl + 1)..], System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var pd) &&
        pd != 0)
      return pn / pd;

    return null;
  }


  private static (int Whole, int Num, int Den) ToFraction(double v, int den = 16)
  {
    int whole = (int)v;
    int num   = (int)Math.Round((v - whole) * den);
    if (num >= den) { whole++; num = 0; }
    if (num == 0) return (whole, 0, 1);
    int a = num, b = den;
    while (b != 0) { int t = b; b = a % b; a = t; }
    return (whole, num / a, den / a);
  }

  private static string FmtFrac(double v)
  {
    var (whole, num, den) = ToFraction(v);
    return num == 0 ? whole.ToString() : $"{whole}+{num}/{den}";
  }

  private static string FmtOpt(double v)
  {
    var (whole, num, den) = ToFraction(v);
    return num == 0 ? whole.ToString() : $"{whole}-{num}/{den}";
  }
}
