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

  private const string LayerPlot = "PLOT";
  private const string LayerCut  = "CUT1";
  private const string LayerRef  = "Reference";

  private static readonly Color PlotColor = Color.FromArgb(0x0F, 0x8A, 0x8A);
  private static readonly Color CutColor  = Color.FromArgb(0xCC, 0x33, 0x33);
  private static readonly Color RefColor  = Color.White;

  private const double DefaultWidth  = 2.0;
  private const double DefaultHeight = 2.0;
  private const int    DefaultCW     = 3;
  private const int    DefaultCH     = 3;
  private const double LabelGap      = 0.125;

  private static double _width  = DefaultWidth;
  private static double _height = DefaultHeight;
  private static int    _cw     = DefaultCW;
  private static int    _ch     = DefaultCH;

  public override string EnglishName => "vDiamonds";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadSettings();
    EnsureLayer(doc, LayerPlot, PlotColor);
    EnsureLayer(doc, LayerCut,  CutColor);
    EnsureLayer(doc, LayerRef,  RefColor);

    while (true)
    {
      var (plotCurves, cutCurve, labelTe, basePt) = BuildGeometry(_width, _height, _cw, _ch);
      CalibrateTextHeight(doc, labelTe, _width * _cw);

      var fadedPlot = FadeColor(LayerColor(doc, LayerPlot));
      var fadedCut  = FadeColor(LayerColor(doc, LayerCut));
      var fadedRef  = FadeColor(LayerColor(doc, LayerRef));

      var previewItems = new List<(GeometryBase Geom, Color Color)>();
      foreach (var crv in plotCurves)
        previewItems.Add((crv.DuplicateCurve(), fadedPlot));
      previewItems.Add((cutCurve.DuplicateCurve(), fadedCut));
      previewItems.Add((labelTe.Duplicate(), fadedRef));

      var capturedBase  = basePt;
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

      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick diamond pattern placement point");
      var idxW  = gp.AddOption("Width",       FmtOpt(_width));
      var idxH  = gp.AddOption("Height",      FmtOpt(_height));
      var idxCW = gp.AddOption("CountWidth",  _cw.ToString());
      var idxCH = gp.AddOption("CountHeight", _ch.ToString());

      gp.DynamicDraw += onDraw;
      var result = gp.Get();
      gp.DynamicDraw -= onDraw;

      if (result == GetResult.Cancel)
        return Result.Cancel;

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
          var v = GetIntSubprompt("Number of diamonds wide", _cw);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1) _cw = v.Value;
        }
        else if (opt.Index == idxCH)
        {
          var v = GetIntSubprompt("Number of diamonds tall", _ch);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1) _ch = v.Value;
        }

        SaveSettings();
        continue;
      }

      if (result == GetResult.Point)
      {
        var xform = Transform.Translation(gp.Point() - basePt);
        AddToDoc(doc, plotCurves, cutCurve, labelTe, xform, _width, _height, _cw, _ch);
        SaveSettings();
        doc.Views.Redraw();
        return Result.Success;
      }
    }
  }

  // ── Geometry ────────────────────────────────────────────────────────────────

  private static (List<NurbsCurve> PlotCurves, PolylineCurve CutCurve, TextEntity LabelTe, Point3d BasePt)
    BuildGeometry(double width, double height, int cw, int ch)
  {
    double W = cw * width;
    double H = ch * height;
    double s = height / width;

    var plotCurves = new List<NurbsCurve>();

    // ↗ family (slope +s): y = s*x + b_n
    for (int n = 0; n < cw + ch; n++)
    {
      double b = H - ((n + 0.5) * height);
      var r = ClipLineToBbox(s, b, W, H);
      if (r.HasValue)
        plotCurves.Add(new Line(r.Value.A, r.Value.B).ToNurbsCurve());
    }

    // ↘ family (slope -s): y = -s*x + c_k
    for (int k = -ch; k < cw; k++)
    {
      double c = H + ((k + 0.5) * height);
      var r = ClipLineToBbox(-s, c, W, H);
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
    var labelTe = new TextEntity
    {
      Plane         = labelPlane,
      PlainText     = labelText,
      TextHeight    = textHeight,
      Justification = TextJustification.BottomCenter,
    };

    return (plotCurves, cutCurve, labelTe, new Point3d(0.0, H, 0.0));
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

  private static void CalibrateTextHeight(RhinoDoc doc, TextEntity labelTe, double targetWidth)
  {
    // TextModelWidth returns the rendered text width in model units at the current TextHeight.
    double textWidth = labelTe.TextModelWidth;
    if (textWidth > 0.0)
      labelTe.TextHeight *= targetWidth / textWidth;
  }

  private static void AddToDoc(
    RhinoDoc doc,
    List<NurbsCurve> plotCurves,
    PolylineCurve cutCurve,
    TextEntity labelTe,
    Transform xform,
    double width, double height, int cw, int ch)
  {
    var plotAttr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, LayerPlot, PlotColor) };
    var cutAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, LayerCut,  CutColor)  };
    var refAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, LayerRef,  RefColor)  };

    var addedIds = new List<Guid>();

    foreach (var crv in plotCurves)
    {
      var c = crv.DuplicateCurve();
      c.Transform(xform);
      var id = doc.Objects.AddCurve(c, plotAttr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    var cut = cutCurve.DuplicateCurve();
    cut.Transform(xform);
    var cutId = doc.Objects.AddCurve(cut, cutAttr);
    if (cutId != Guid.Empty) addedIds.Add(cutId);

    if (labelTe.Duplicate() is TextEntity te)
    {
      te.Transform(xform);
      var teId = doc.Objects.AddText(te, refAttr);
      if (teId != Guid.Empty) addedIds.Add(teId);
    }

    if (addedIds.Count > 1)
    {
      var shortId   = Guid.NewGuid().ToString()[..8];
      var groupName = $"Diamonds_{FmtFrac(height)}x{FmtFrac(width)}_({cw}x{ch})_{shortId}";
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
    (_width, _height, _cw, _ch) = vToolsOptionStore.Read(SettingsSection, section =>
    {
      var w  = _width;
      var h  = _height;
      var cw = _cw;
      var ch = _ch;

      if (vToolsOptionStore.TryGetDouble(section, WidthKey,       out var pw)  && pw  > 0.0) w  = pw;
      if (vToolsOptionStore.TryGetDouble(section, HeightKey,      out var ph)  && ph  > 0.0) h  = ph;
      if (vToolsOptionStore.TryGetDouble(section, CountWidthKey,  out var pcw) && pcw >= 1.0) cw = (int)Math.Round(pcw);
      if (vToolsOptionStore.TryGetDouble(section, CountHeightKey, out var pch) && pch >= 1.0) ch = (int)Math.Round(pch);

      return (w, h, cw, ch);
    });
  }

  private static void SaveSettings() =>
    vToolsOptionStore.Update(SettingsSection, section =>
    {
      section[WidthKey]       = _width;
      section[HeightKey]      = _height;
      section[CountWidthKey]  = _cw;
      section[CountHeightKey] = _ch;
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
      if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0.0)
        return v;
      return current;
    }

    return null;
  }

  private static int? GetIntSubprompt(string prompt, int current)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({current})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return current;
    if (res == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (string.IsNullOrEmpty(raw)) return current;
      if (int.TryParse(raw, out var v) && v >= 1)
        return v;
      return current;
    }

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
