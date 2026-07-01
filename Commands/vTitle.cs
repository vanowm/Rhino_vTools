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

  // ── Active placement tracking (for live update) ───────────────────────
  private static Guid _activeTextId  = Guid.Empty;
  private static Guid _activeBoxId   = Guid.Empty;
  private static int  _activeGrpIdx  = -1;
  private static bool _internalReplace = false;

  // ── External-edit event subscription ───────────────────────────────
  private static readonly System.Collections.Generic.HashSet<Guid> _pendingBoxUpdates = new();

  static vTitle()
  {
    RhinoDoc.ReplaceRhinoObject += OnRhinoObjectReplaced;
  }

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
    _activeTextId = Guid.Empty;
    _activeBoxId  = Guid.Empty;
    _activeGrpIdx = -1;

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Title center");

      int idxText    = gp.AddOption("Text",    string.IsNullOrEmpty(_text) ? "-" : _text);
      int idxSize    = gp.AddOption("Size",    $"{_size:G}");
      int idxPadding = gp.AddOption("Padding", $"{_padding:G}");
      var optBox     = new OptionToggle(_box, "Off", "On");
      gp.AddOptionToggle("Box", ref optBox);
      gp.AcceptNothing(false);

      gp.DynamicDraw += (_, e) =>
      {
        DrawPreview(e, _text, _size, _padding, _box);
        DrawHoverHighlight(doc, e);
      };

      var res = gp.Get();
      _box = optBox.CurrentValue;

      if (gp.CommandResult() == Result.Cancel)
      {
        SelectGroup(doc, _activeGrpIdx, false);
        doc.Views.Redraw();
        break;
      }

      if (res == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt == null) { UpdateActive(doc); SaveSettings(); continue; }

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
          UpdateActive(doc); SaveSettings();
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
          UpdateActive(doc); SaveSettings();
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
          UpdateActive(doc); SaveSettings();
          continue;
        }

        // Box toggle
        UpdateActive(doc); SaveSettings();
        continue;
      }

      if (res == GetResult.Point)
      {
        var pt = gp.Point();
        var hit = FindVTitleAt(doc, pt);

        if (hit.HasValue)
        {
          if (_activeGrpIdx >= 0 && _activeGrpIdx != hit.Value.grpIdx)
            SelectGroup(doc, _activeGrpIdx, false);

          _activeTextId = hit.Value.textId;
          _activeBoxId  = hit.Value.boxId;
          _activeGrpIdx = hit.Value.grpIdx;

          if (doc.Objects.FindId(_activeTextId)?.Geometry is TextEntity et)
          {
            _text = et.PlainText ?? _text;
            _size = et.TextHeight;
            _box  = _activeBoxId != Guid.Empty;
            SaveSettings();
          }
          SelectGroup(doc, _activeGrpIdx, true);
          doc.Views.Redraw();
        }
        else
        {
          SelectGroup(doc, _activeGrpIdx, false);
          if (string.IsNullOrEmpty(_text)) continue;
          PlaceTitle(doc, pt, _text, _size, _padding, _box);
          // New placement does NOT enter edit mode — reset so UpdateActive is a no-op.
          // _activeGrpIdx is kept so the newly placed group stays highlighted.
          _activeTextId = Guid.Empty;
          _activeBoxId  = Guid.Empty;
          SelectGroup(doc, _activeGrpIdx, true);
          doc.Views.Redraw();
        }
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

  private static void PlaceTitle(RhinoDoc doc, Point3d center,
    string text, double size, double padding, bool box)
  {
    var vp = doc.Views.ActiveView?.ActiveViewport;
    var cp = vp?.GetConstructionPlane();
    var xAxis = cp?.Plane.XAxis ?? Vector3d.XAxis;
    var yAxis = cp?.Plane.YAxis ?? Vector3d.YAxis;
    var textPlane = new Plane(center, xAxis, yAxis);

    var te = new TextEntity
    {
      Plane          = textPlane,
      PlainText      = text,
      TextHeight     = size,
      Justification  = TextJustification.MiddleCenter,
      DimensionScale = 1.0,
    };

    var attr = new ObjectAttributes();
    attr.SetUserString("vTitle",        "1");
    attr.SetUserString("vTitlePadding", padding.ToString(CultureInfo.InvariantCulture));
    _activeTextId = doc.Objects.AddText(te, attr);
    _activeBoxId  = Guid.Empty;
    _activeGrpIdx = -1;
    if (_activeTextId == Guid.Empty) return;

    if (box)
      _activeBoxId = doc.Objects.AddCurve(BoxCurve(center, xAxis, yAxis, text, size, padding));

    // Group text + box together
    var toGroup = new System.Collections.Generic.List<Guid> { _activeTextId };
    if (_activeBoxId != Guid.Empty) toGroup.Add(_activeBoxId);
    if (toGroup.Count > 1)
    {
      _activeGrpIdx = doc.Groups.Add();
      foreach (var id in toGroup)
      {
        var obj2 = doc.Objects.FindId(id);
        if (obj2 == null) continue;
        var grpAttr = obj2.Attributes.Duplicate();
        grpAttr.AddToGroup(_activeGrpIdx);
        doc.Objects.ModifyAttributes(obj2, grpAttr, true);
      }
    }
  }

  // ── Find existing vTitle at a point ───────────────────────────────────

  private static (Guid textId, Guid boxId, int grpIdx)? FindVTitleAt(
    RhinoDoc doc, Point3d pt)
  {
    foreach (var obj in doc.Objects)
    {
      if (obj.IsLocked || obj.IsHidden) continue;
      if (obj.Geometry is not TextEntity te) continue;
      if (obj.Attributes.GetUserString("vTitle") != "1") continue;
      if (!GetTitleHalfExtents(doc, obj, te, out double hw, out double hh)) continue;

      var rel = pt - te.Plane.Origin;
      if (Math.Abs(rel * te.Plane.XAxis) > hw) continue;
      if (Math.Abs(rel * te.Plane.YAxis) > hh) continue;

      var grpList = obj.Attributes.GetGroupList();
      int grpIdx = grpList?.Length > 0 ? grpList[0] : -1;
      Guid boxId = Guid.Empty;
      if (grpIdx >= 0)
      {
        foreach (var other in doc.Objects)
        {
          if (other.Id == obj.Id || other.Geometry is not PolylineCurve) continue;
          var gl = other.Attributes.GetGroupList();
          if (gl != null && Array.IndexOf(gl, grpIdx) >= 0) { boxId = other.Id; break; }
        }
      }
      return (obj.Id, boxId, grpIdx);
    }
    return null;
  }

  /// <summary>Gets half-extents of a title's box in text-plane coordinates.</summary>
  private static bool GetTitleHalfExtents(RhinoDoc doc, RhinoObject textRhObj,
    TextEntity te, out double hw, out double hh)
  {
    hw = hh = 0;
    if (te == null) return false;

    // Try the associated box curve first
    var grpList = textRhObj.Attributes.GetGroupList();
    int grpIdx = grpList?.Length > 0 ? grpList[0] : -1;
    if (grpIdx >= 0)
    {
      var center = te.Plane.Origin;
      foreach (var obj in doc.Objects)
      {
        if (obj.Geometry is not PolylineCurve poly) continue;
        var gl = obj.Attributes.GetGroupList();
        if (gl == null || Array.IndexOf(gl, grpIdx) < 0) continue;
        double maxU = 0, maxV = 0;
        foreach (var corner in poly.ToPolyline())
        {
          var r = corner - center;
          maxU = Math.Max(maxU, Math.Abs(r * te.Plane.XAxis));
          maxV = Math.Max(maxV, Math.Abs(r * te.Plane.YAxis));
        }
        if (maxU > 0 && maxV > 0) { hw = maxU; hh = maxV; return true; }
      }
    }

    // Fallback: approximate from stored padding
    double padding = 10.0;
    if (double.TryParse(textRhObj.Attributes.GetUserString("vTitlePadding"),
          NumberStyles.Any, CultureInfo.InvariantCulture, out double sp))
      padding = sp;
    var (tw, th) = ApproxBounds(te.PlainText ?? "", te.TextHeight);
    double padFactor = 1.0 + padding * 2.0 / 100.0;
    hw = tw * padFactor / 2.0;
    hh = th * padFactor / 2.0;
    return true;
  }

  // ── Select / deselect a group ─────────────────────────────────────────

  private static void SelectGroup(RhinoDoc doc, int grpIdx, bool select)
  {
    if (grpIdx < 0) return;
    foreach (var obj in doc.Objects)
    {
      var gl = obj.Attributes.GetGroupList();
      if (gl != null && Array.IndexOf(gl, grpIdx) >= 0)
        obj.Select(select);
    }
  }

  // ── Hover highlight ───────────────────────────────────────────────────────

  private static void DrawHoverHighlight(RhinoDoc doc, GetPointDrawEventArgs e)
  {
    var pt = e.CurrentPoint;
    foreach (var obj in doc.Objects)
    {
      if (obj.IsLocked || obj.IsHidden) continue;
      if (obj.Geometry is not TextEntity te) continue;
      if (obj.Attributes.GetUserString("vTitle") != "1") continue;
      if (!GetTitleHalfExtents(doc, obj, te, out double hw, out double hh)) continue;

      var rel = pt - te.Plane.Origin;
      if (Math.Abs(rel * te.Plane.XAxis) > hw) continue;
      if (Math.Abs(rel * te.Plane.YAxis) > hh) continue;

      var o  = te.Plane.Origin;
      var xa = te.Plane.XAxis;
      var ya = te.Plane.YAxis;
      var hc = Color.FromArgb(220, 255, 220, 40);
      e.Display.DrawLine(o + xa*(-hw) + ya*(-hh), o + xa*(hw) + ya*(-hh), hc, 2);
      e.Display.DrawLine(o + xa*( hw) + ya*(-hh), o + xa*(hw) + ya*( hh), hc, 2);
      e.Display.DrawLine(o + xa*( hw) + ya*( hh), o + xa*(-hw) + ya*( hh), hc, 2);
      e.Display.DrawLine(o + xa*(-hw) + ya*( hh), o + xa*(-hw) + ya*(-hh), hc, 2);
    }
  }

  // ── External-edit handler ───────────────────────────────────────────────

  private static void OnRhinoObjectReplaced(object? sender,
    RhinoReplaceObjectEventArgs e)
  {
    if (_internalReplace) return;
    if (e.OldRhinoObject?.Attributes.GetUserString("vTitle") != "1") return;
    _pendingBoxUpdates.Add(e.OldRhinoObject.Id);
    RhinoApp.Idle -= OnIdleUpdateBoxes;
    RhinoApp.Idle += OnIdleUpdateBoxes;
  }

  private static void OnIdleUpdateBoxes(object? sender, EventArgs e)
  {
    RhinoApp.Idle -= OnIdleUpdateBoxes;
    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || _pendingBoxUpdates.Count == 0) { _pendingBoxUpdates.Clear(); return; }
    foreach (var id in _pendingBoxUpdates)
      UpdateBoxForTitle(doc, id);
    _pendingBoxUpdates.Clear();
    doc.Views.Redraw();
  }

  private static void UpdateBoxForTitle(RhinoDoc doc, Guid textId)
  {
    if (textId == Guid.Empty) return;
    var textObj = doc.Objects.FindId(textId);
    if (textObj?.Geometry is not TextEntity te) return;
    if (textObj.Attributes.GetUserString("vTitle") != "1") return;

    double padding = 10.0;
    if (double.TryParse(textObj.Attributes.GetUserString("vTitlePadding"),
          NumberStyles.Any, CultureInfo.InvariantCulture, out double sp))
      padding = sp;

    var grpList = textObj.Attributes.GetGroupList();
    int grpIdx = grpList?.Length > 0 ? grpList[0] : -1;
    if (grpIdx < 0) return;

    foreach (var obj in doc.Objects)
    {
      if (obj.Geometry is not PolylineCurve) continue;
      var gl = obj.Attributes.GetGroupList();
      if (gl == null || Array.IndexOf(gl, grpIdx) < 0) continue;
      var newBox = BoxCurve(te.Plane.Origin, te.Plane.XAxis, te.Plane.YAxis,
        te.PlainText ?? "", te.TextHeight, padding);
      _internalReplace = true;
      doc.Objects.Replace(obj.Id, newBox);
      _internalReplace = false;
      break;
    }
  }

  // ── Live update of last placed group ─────────────────────────────────

  private static void UpdateActive(RhinoDoc doc)
  {
    if (_activeTextId == Guid.Empty) return;
    var textObj = doc.Objects.FindId(_activeTextId);
    if (textObj?.Geometry is not TextEntity oldTe) { _activeTextId = Guid.Empty; return; }

    // Update text content and size
    var newTe = (TextEntity)oldTe.Duplicate();
    newTe.PlainText  = _text;
    newTe.TextHeight = _size;
    _internalReplace = true;
    doc.Objects.Replace(_activeTextId, newTe);
    _internalReplace = false;
    // Keep padding in sync on the text object's attributes
    var tobj0 = doc.Objects.FindId(_activeTextId);
    if (tobj0 != null)
    {
      var ta0 = tobj0.Attributes.Duplicate();
      ta0.SetUserString("vTitlePadding", _padding.ToString(CultureInfo.InvariantCulture));
      doc.Objects.ModifyAttributes(tobj0, ta0, true);
    }

    var center = oldTe.Plane.Origin;
    var xAxis  = oldTe.Plane.XAxis;
    var yAxis  = oldTe.Plane.YAxis;

    if (_box)
    {
      var newCurve = BoxCurve(center, xAxis, yAxis, _text, _size, _padding);
      if (_activeBoxId != Guid.Empty)
      {
        doc.Objects.Replace(_activeBoxId, newCurve);
      }
      else
      {
        // Box was off — create it and add to the existing group
        _activeBoxId = doc.Objects.AddCurve(newCurve);
        if (_activeBoxId != Guid.Empty)
        {
          if (_activeGrpIdx < 0)
          {
            // Promote to a group now that there are two objects
            _activeGrpIdx = doc.Groups.Add();
            var tobj = doc.Objects.FindId(_activeTextId);
            if (tobj != null)
            {
              var ta = tobj.Attributes.Duplicate(); ta.AddToGroup(_activeGrpIdx);
              doc.Objects.ModifyAttributes(tobj, ta, true);
            }
          }
          var bobj = doc.Objects.FindId(_activeBoxId);
          if (bobj != null)
          {
            var ba = bobj.Attributes.Duplicate(); ba.AddToGroup(_activeGrpIdx);
            doc.Objects.ModifyAttributes(bobj, ba, true);
          }
        }
      }
    }
    else if (_activeBoxId != Guid.Empty)
    {
      doc.Objects.Delete(_activeBoxId, true);
      _activeBoxId = Guid.Empty;
    }

    doc.Views.Redraw();
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  private static PolylineCurve BoxCurve(Point3d center,
    Vector3d xAxis, Vector3d yAxis, string text, double size, double padding)
  {
    var (tw, th) = ApproxBounds(text, size);
    double bw = tw * (1.0 + padding * 2.0 / 100.0);
    double bh = th * (1.0 + padding * 2.0 / 100.0);
    var c0 = center + xAxis * (-bw / 2) + yAxis * (-bh / 2);
    var c1 = center + xAxis * ( bw / 2) + yAxis * (-bh / 2);
    var c2 = center + xAxis * ( bw / 2) + yAxis * ( bh / 2);
    var c3 = center + xAxis * (-bw / 2) + yAxis * ( bh / 2);
    return new PolylineCurve(new[] { c0, c1, c2, c3, c0 });
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
