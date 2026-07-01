// vTitle — place annotation text with optional bounding box.
// Preview moves live with cursor; text/options persist across sessions.
using System;
using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vTitle : Command
{
  // ── Persistent settings ───────────────────────────────────────────────
  private static string _text    = "";
  private static double _size    = 20.0;
  private static double _padding = 10.0;   // percent per side
  private static bool   _box     = true;

  private const string SectionName = "vTitle";
  private const string KeyText    = "text";
  private const string KeySize    = "size";
  private const string KeyPadding = "padding";
  private const string KeyBox     = "box";

  public override string EnglishName => "vTitle";

  // ── Entry point ───────────────────────────────────────────────────────
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadSettings();

    while (true)
    {
      var gp = new GetPoint();
      string textHint = string.IsNullOrEmpty(_text) ? "(no text)" : $"\"{_text}\"";
      gp.SetCommandPrompt($"Title center  {textHint}");

      int idxText    = gp.AddOption("Text");
      int idxSize    = gp.AddOption("Size",    $"{_size:G}");
      int idxPadding = gp.AddOption("Padding", $"{_padding:G}");
      var optBox     = new OptionToggle(_box, "Off", "On");
      gp.AddOptionToggle("Box", ref optBox);
      gp.AcceptString(true);          // quick single-word text update
      gp.AcceptNumber(true, false);   // quick size update
      gp.AcceptNothing(false);

      gp.DynamicDraw += (_, e) => DrawPreview(e, _text, _size, _padding, _box);

      var res = gp.Get();
      _box = optBox.CurrentValue;

      if (gp.CommandResult() == Result.Cancel) break;

      if (res == GetResult.String)
      {
        string s = gp.StringResult()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(s)) { _text = s; SaveSettings(); }
        continue;
      }

      if (res == GetResult.Number)
      {
        double v = gp.Number();
        if (v > 0) { _size = v; SaveSettings(); }
        continue;
      }

      if (res == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt == null) { SaveSettings(); continue; }

        if (opt.Index == idxText)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Title text (spaces allowed)");
          gs.SetDefaultString(_text);
          gs.AcceptNothing(true);
          gs.GetLiteralString();
          if (gs.CommandResult() != Result.Cancel)
          {
            string s = gs.StringResult()?.Trim() ?? "";
            if (!string.IsNullOrEmpty(s)) _text = s;
          }
          SaveSettings();
          continue;
        }

        if (opt.Index == idxSize)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Text size");
          gs.SetDefaultString($"{_size:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double sv) && sv > 0)
            _size = sv;
          SaveSettings();
          continue;
        }

        if (opt.Index == idxPadding)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Padding % per side");
          gs.SetDefaultString($"{_padding:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double pv) && pv >= 0)
            _padding = pv;
          SaveSettings();
          continue;
        }

        // Box toggle already applied via optBox above
        SaveSettings();
        continue;
      }

      if (res == GetResult.Point)
      {
        if (string.IsNullOrEmpty(_text)) continue;
        PlaceTitle(doc, gp.Point(), _text, _size, _padding, _box);
        doc.Views.Redraw();
      }
    }

    return Result.Success;
  }

  // ── Dynamic preview ───────────────────────────────────────────────────

  private static void DrawPreview(GetPointDrawEventArgs e,
    string text, double size, double padding, bool box)
  {
    if (string.IsNullOrEmpty(text)) return;

    var pt = e.CurrentPoint;
    var cpNative = e.Viewport.GetConstructionPlane();
    var xAxis     = cpNative.Plane.XAxis;
    var yAxis     = cpNative.Plane.YAxis;
    var textPlane = new Plane(pt, xAxis, yAxis);

    var te = new TextEntity
    {
      Plane          = textPlane,
      PlainText      = text,
      TextHeight     = size,
      Justification  = TextJustification.MiddleCenter,
      DimensionScale = 1.0,
    };
    try { te.DrawForward = false; } catch { }
    try { e.Display.DrawAnnotation(te, Color.FromArgb(220, 255, 255, 80)); }
    catch
    {
      try { e.Display.Draw3dText(new Text3d(text, textPlane, size), Color.Yellow); }
      catch { e.Display.DrawDot(pt, text, Color.FromArgb(200, 60, 60, 60), Color.Yellow); }
    }

    if (!box) return;

    var (tw, th) = ApproxBounds(text, size);
    double bw = tw * (1.0 + padding * 2.0 / 100.0);
    double bh = th * (1.0 + padding * 2.0 / 100.0);

    var c0 = pt + xAxis * (-bw / 2) + yAxis * (-bh / 2);
    var c1 = pt + xAxis * ( bw / 2) + yAxis * (-bh / 2);
    var c2 = pt + xAxis * ( bw / 2) + yAxis * ( bh / 2);
    var c3 = pt + xAxis * (-bw / 2) + yAxis * ( bh / 2);

    var boxColor = Color.FromArgb(180, 180, 220, 60);
    e.Display.DrawLine(c0, c1, boxColor, 1);
    e.Display.DrawLine(c1, c2, boxColor, 1);
    e.Display.DrawLine(c2, c3, boxColor, 1);
    e.Display.DrawLine(c3, c0, boxColor, 1);
  }

  // ── Placement ─────────────────────────────────────────────────────────

  private static void PlaceTitle(RhinoDoc doc, Point3d center,
    string text, double size, double padding, bool box)
  {
    var vp = doc.Views.ActiveView?.ActiveViewport;
    var cpNative = vp?.GetConstructionPlane();
    var xAxis = cpNative?.Plane.XAxis ?? Vector3d.XAxis;
    var yAxis = cpNative?.Plane.YAxis ?? Vector3d.YAxis;
    var textPlane = new Plane(center, xAxis, yAxis);

    var te = new TextEntity
    {
      Plane          = textPlane,
      PlainText      = text,
      TextHeight     = size,
      Justification  = TextJustification.MiddleCenter,
      DimensionScale = 1.0,
    };
    var titleId = doc.Objects.AddText(te);

    if (!box) return;

    // Use actual bounding box of placed text for accurate sizing
    double tw, th;
    var rhObj = doc.Objects.FindId(titleId);
    var bb = rhObj?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
    if (bb.IsValid && (bb.Max.X - bb.Min.X) > doc.ModelAbsoluteTolerance)
    {
      // Project bounding box corners onto text plane axes (dot product = local coords)
      var xDir = xAxis; xDir.Unitize();
      var yDir = yAxis; yDir.Unitize();
      var origin = textPlane.Origin;
      double minU = double.MaxValue, maxU = double.MinValue;
      double minV = double.MaxValue, maxV = double.MinValue;
      foreach (var corner in bb.GetCorners())
      {
        var rel = corner - origin;
        double u = rel * xDir;
        double v = rel * yDir;
        if (u < minU) minU = u; if (u > maxU) maxU = u;
        if (v < minV) minV = v; if (v > maxV) maxV = v;
      }
      tw = maxU - minU;
      th = maxV - minV;
    }
    else
    {
      (tw, th) = TextBounds(te, text, size);
    }
    double bw = tw * (1.0 + padding * 2.0 / 100.0);
    double bh = th * (1.0 + padding * 2.0 / 100.0);

    var c0 = center + xAxis * (-bw / 2) + yAxis * (-bh / 2);
    var c1 = center + xAxis * ( bw / 2) + yAxis * (-bh / 2);
    var c2 = center + xAxis * ( bw / 2) + yAxis * ( bh / 2);
    var c3 = center + xAxis * (-bw / 2) + yAxis * ( bh / 2);

    doc.Objects.AddCurve(new PolylineCurve(new[] { c0, c1, c2, c3, c0 }));
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>
  /// Returns text extents from the entity's bounding box if available,
  /// falling back to a character-count approximation.
  /// </summary>
  private static (double w, double h) TextBounds(TextEntity te, string text, double size)
  {
    try
    {
      var bb = te.GetBoundingBox(true);
      if (bb.IsValid)
      {
        double w = bb.Max.X - bb.Min.X;
        double h = bb.Max.Y - bb.Min.Y;
        if (w > 0 && h > 0) return (w, h);
      }
    }
    catch { }
    return ApproxBounds(text, size);
  }

  /// <summary>Approximate text extents based on size and character count.</summary>
  private static (double w, double h) ApproxBounds(string text, double size)
  {
    double h = size * 1.4;
    double w = Math.Max(text.Length * size * 0.75, size);
    return (w, h);
  }

  // ── Settings ──────────────────────────────────────────────────────────

  private static void LoadSettings()
  {
    ToolsOptionStore.Read<int>(SectionName, s =>
    {
      if (ToolsOptionStore.TryGetString(s, KeyText,    out var t)) _text    = t;
      if (ToolsOptionStore.TryGetDouble(s, KeySize,    out var v)) _size    = v;
      if (ToolsOptionStore.TryGetDouble(s, KeyPadding, out v))     _padding = v;
      if (ToolsOptionStore.TryGetBool  (s, KeyBox,     out var b)) _box     = b;
      return 0;
    });
  }

  private static void SaveSettings()
  {
    ToolsOptionStore.Update(SectionName, s =>
    {
      s[KeyText]    = _text;
      s[KeySize]    = _size;
      s[KeyPadding] = _padding;
      s[KeyBox]     = _box;
    });
  }
}
