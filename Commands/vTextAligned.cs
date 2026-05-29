using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native text-on-curve alignment command ported from TextAligned.py.
/// </summary>
public sealed class vTextAligned : Command
{
  private const string OptionsSectionName = "vTextAligned";
  private const string TextKey = "text";
  private const string HeightKey = "height";
  private const string OffsetKey = "offset";
  private const string Rotate90Key = "rotate90";
  private const string BothSidesKey = "bothSides";

  private static string _text = "Text";
  private static double _height = 5.0;
  private static double _offset;
  private static int _rotate90;
  private static bool _bothSides;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTextAligned";

  /// <summary>
  /// Executes interactive text alignment and live text move workflow.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    Guid? activeCurveId = null;
    Guid? activeTextId = null;
    var curveIsLocked = false;
    TextEntity? activeMoveStartGeo = null;

    var undoStack = new Stack<TextAction>();
    var redoStack = new Stack<TextAction>();

    // Values read from the most-recently-selected text object.
    // Command options (_text/_height) are NOT overwritten when the user picks
    // an existing text; only changed if the user explicitly uses Text/Height.
    string? selectedObjText  = null;
    double  selectedObjHeight = 0.0;
    bool    textUserChanged   = false;
    bool    heightUserChanged = false;

    var optHeight = new OptionDouble(_height, RhinoMath.ZeroTolerance, 1e9);
    var optOffset = new OptionDouble(_offset);
    var optBothSides = new OptionToggle(_bothSides, "No", "Yes");

    while (true)
    {
      var curveCache = CollectCurveObjects(doc);
      var textIds = CollectTextIds(doc);

      // Use the selected object's text/height unless the user explicitly changed
      // them via Text/Height options since the object was picked.
      var effText   = textUserChanged   || selectedObjText == null                       ? _text   : selectedObjText;
      var effHeight = heightUserChanged || selectedObjHeight <= RhinoMath.ZeroTolerance  ? _height : selectedObjHeight;

      var getter = new MainPointGetter(doc, effText, effHeight, _offset, _rotate90, _bothSides, curveCache, textIds, activeCurveId, activeTextId, curveIsLocked);

      getter.SetCommandPrompt(curveIsLocked && activeCurveId.HasValue
        ? "Curve locked. Click to set text position, or click text to switch active text. Enter to finish"
        : "Click curve to lock orientation base, or click text to use it. Enter to finish");

      getter.AcceptNothing(true);
      getter.AcceptString(true);

      var idxText = getter.AddOption("Text", _text);
      var idxHeight = getter.AddOptionDouble("Height", ref optHeight);
      var idxOffset = getter.AddOptionDouble("Offset", ref optOffset);
      var idxRotate = getter.AddOption("Rotate");
      var idxBothSides = getter.AddOptionToggle("BothSides", ref optBothSides);

      var result = getter.Get();
      var commandResult = getter.CommandResult();

      if (commandResult != Result.Success)
      {
        if (curveIsLocked && activeTextId.HasValue && activeMoveStartGeo != null)
        {
          _ = RestoreTextGeometry(doc, activeTextId.Value, activeMoveStartGeo);
          doc.Views.Redraw();
        }

        if (commandResult == Result.Cancel)
          return Result.Cancel;

        SavePersistedOptions();
        return Result.Success;
      }

      if (result == GetResult.Option)
      {
        var option = getter.Option();
        if (option != null)
        {
          if (option.Index == idxText)
          {
            var proposed = _text;
            if (RhinoGet.GetString("Text", true, ref proposed) == Result.Success && proposed != null)
            {
              _text = proposed;
              textUserChanged = true;
            }
          }
          else if (option.Index == idxRotate)
          {
            _rotate90 = (_rotate90 + 1) % 4;
            RhinoApp.WriteLine($"Rotate={_rotate90 * 90}");
          }
        }

        var prevHeight = _height;
        _height = Math.Max(optHeight.CurrentValue, RhinoMath.ZeroTolerance);
        if (Math.Abs(_height - prevHeight) > RhinoMath.ZeroTolerance)
          heightUserChanged = true;
        _offset = optOffset.CurrentValue;
        _bothSides = optBothSides.CurrentValue;

        if (activeTextId.HasValue)
        {
          var obj = doc.Objects.FindId(activeTextId.Value);
          if (obj?.Geometry is TextEntity te)
          {
            var updated = te.Duplicate() as TextEntity;
            if (updated != null)
            {
              var effTextOpt   = textUserChanged   || selectedObjText == null                      ? _text   : selectedObjText;
              var effHeightOpt = heightUserChanged || selectedObjHeight <= RhinoMath.ZeroTolerance ? _height : selectedObjHeight;
              SetTextEntityValue(updated, effTextOpt);
              ApplyHeightOverride(doc, updated, effHeightOpt);
              _ = doc.Objects.Replace(activeTextId.Value, updated);
              doc.Views.Redraw();
            }
          }
        }

        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.String)
      {
        var token = (getter.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
        if (token is "u" or "undo" or "_undo" or "z")
        {
          if (undoStack.Count == 0)
          {
            RhinoApp.WriteLine("vTextAligned: nothing to undo.");
          }
          else
          {
            var action = undoStack.Pop();
            if (ApplyUndoAction(doc, action, _height))
            {
              redoStack.Push(action);
              doc.Views.Redraw();
            }
            else
            {
              RhinoApp.WriteLine("vTextAligned: undo failed.");
            }
          }

          continue;
        }

        if (token is "r" or "redo" or "_redo" or "y")
        {
          if (redoStack.Count == 0)
          {
            RhinoApp.WriteLine("vTextAligned: nothing to redo.");
          }
          else
          {
            var action = redoStack.Pop();
            if (ApplyRedoAction(doc, action, _height))
            {
              undoStack.Push(action);
              doc.Views.Redraw();
            }
            else
            {
              RhinoApp.WriteLine("vTextAligned: redo failed.");
            }
          }

          continue;
        }

        RhinoApp.WriteLine($"vTextAligned: unknown command token {token} (use u/undo, r/redo)");
        continue;
      }

      if (result == GetResult.Nothing)
      {
        if (curveIsLocked && activeTextId.HasValue && activeMoveStartGeo != null)
        {
          _ = RestoreTextGeometry(doc, activeTextId.Value, activeMoveStartGeo);
          doc.Views.Redraw();
        }

        SavePersistedOptions();
        return Result.Success;
      }

      if (result != GetResult.Point)
        continue;

      var clickPoint = getter.Point();

      // Use hover state: click always selects whatever was highlighted on last mouse move.
      var curveHit = getter.HoverCurve;
      var textHit = getter.HoverText;

      Guid? chosenTextId = null;
      if (getter.HoverIntentIsText && textHit != null)
      {
        var hitId = textHit.Value.ObjectId;
        if (!(curveIsLocked && activeTextId.HasValue && hitId == activeTextId.Value))
          chosenTextId = hitId;
      }

      if (chosenTextId.HasValue)
      {
        if (curveIsLocked && activeTextId.HasValue && activeMoveStartGeo != null && chosenTextId.Value != activeTextId.Value)
          _ = RestoreTextGeometry(doc, activeTextId.Value, activeMoveStartGeo);

        var obj = doc.Objects.FindId(chosenTextId.Value);
        if (obj?.Geometry is TextEntity textObj)
        {
          activeTextId = chosenTextId.Value;

          // Record the selected object's text/height WITHOUT overwriting command
          // options.  _text/_height only change if the user explicitly edits them
          // via the Text/Height option menus after this selection.
          selectedObjText   = TextEntityValue(textObj, _text);
          var selH = textObj.TextHeight;
          selectedObjHeight = selH > RhinoMath.ZeroTolerance ? selH : _height;
          textUserChanged   = false;
          heightUserChanged = false;

          // Move the selected text to the current active layer.
          var layerAttr = obj.Attributes.Duplicate();
          layerAttr.LayerIndex = doc.Layers.CurrentLayerIndex;
          doc.Objects.ModifyAttributes(chosenTextId.Value, layerAttr, true);

          activeMoveStartGeo = DupTextGeometry(doc, chosenTextId.Value);
          SavePersistedOptions();
          RhinoApp.WriteLine("vTextAligned: active text selected.");
          doc.Views.Redraw();
        }

        continue;
      }

      if (!curveIsLocked)
      {
        if (curveHit == null)
        {
          RhinoApp.WriteLine("vTextAligned: click a curve to lock orientation base.");
          continue;
        }

        activeCurveId = curveHit.Value.ObjectId;
        curveIsLocked = true;
        if (activeTextId.HasValue)
          activeMoveStartGeo = DupTextGeometry(doc, activeTextId.Value);

        RhinoApp.WriteLine("vTextAligned: curve locked. Click again to set text.");
        continue;
      }

      var curveToUse = curveCache.FirstOrDefault(c => c.ObjectId == activeCurveId).Curve;
      if (curveToUse == null)
      {
        RhinoApp.WriteLine("vTextAligned: locked curve is no longer available. Select curve again.");
        curveIsLocked = false;
        activeCurveId = null;
        continue;
      }

      if (!curveToUse.ClosestPoint(clickPoint, out var t))
      {
        RhinoApp.WriteLine("vTextAligned: could not evaluate position on locked curve.");
        continue;
      }

      var upAxis = getter.View()?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
      var previewTemplateTextId = getter.PreviewTemplateTextId;

      // Compute effective text/height here so plane calculation and placement use the same values.
      var effTextPlace   = textUserChanged   || selectedObjText == null                      ? _text   : selectedObjText;
      var effHeightPlace = heightUserChanged || selectedObjHeight <= RhinoMath.ZeroTolerance ? _height : selectedObjHeight;

      // Use the last stable side sign from the preview to match what was shown.
      var placeSideSign = getter.LastSideSign != 0 ? getter.LastSideSign : 0;
      var placeSideDeadband = getter.LastSideSign != 0 ? Math.Max(doc.ModelAbsoluteTolerance * 4.0, effHeightPlace * 0.1) : 0.0;

      if (!BuildPlaneFromCurve(doc, curveToUse, t, clickPoint, _offset, effHeightPlace, _rotate90, upAxis, out var plane, out var primarySideSign, sideSignHint: placeSideSign, sideDeadband: placeSideDeadband, previewTemplateTextId))
      {
        RhinoApp.WriteLine("vTextAligned: could not compute text plane.");
        continue;
      }

      if (activeTextId.HasValue)
      {
        if (ApplySettingsToTextObject(doc, activeTextId.Value, effTextPlace, effHeightPlace, plane))
        {
          doc.Views.Redraw();

          var afterGeo = DupTextGeometry(doc, activeTextId.Value);
          if (activeMoveStartGeo != null && afterGeo != null)
          {
            undoStack.Push(TextAction.CreateMove(activeTextId.Value, activeMoveStartGeo, afterGeo));
            redoStack.Clear();
          }
        }
        else
        {
          RhinoApp.WriteLine("vTextAligned: active text is no longer valid.");
        }

        activeTextId = null;
        activeMoveStartGeo = null;
        selectedObjText   = null;
        selectedObjHeight = 0.0;
        textUserChanged   = false;
        heightUserChanged = false;
        curveIsLocked = false;
        SavePersistedOptions();
        continue;
      }

      var entity = BuildTextEntity(doc, _text, _height, plane);
      var newId = doc.Objects.AddText(entity);
      if (newId == Guid.Empty)
      {
        RhinoApp.WriteLine("vTextAligned: failed to add text.");
        continue;
      }

      _ = ForceTextObjectHeight(doc, newId, _height);

      if (_bothSides)
      {
        // Compute the opposite-side cursor by mirroring across the curve along the side-base vector.
        var tanVec = curveToUse.TangentAt(t);
        var normVec = upAxis;
        if (!normVec.Unitize()) normVec = Vector3d.ZAxis;
        tanVec.Unitize();
        var sideBaseVec = Vector3d.CrossProduct(normVec, tanVec);
        if (sideBaseVec.Unitize())
        {
          var curvePoint = curveToUse.PointAt(t);
          var oppCursor = curvePoint - sideBaseVec * (primarySideSign * 1000.0);
          if (BuildPlaneFromCurve(doc, curveToUse, t, oppCursor, _offset, _height,
                NormalizeRotate(_rotate90 + 2), upAxis,
                out var oppPlane, out _, sideSignHint: 0, sideDeadband: 0.0, previewTemplateTextId))
          {
            var secEntity = BuildTextEntity(doc, _text, _height, oppPlane);
            var secId = doc.Objects.AddText(secEntity);
            if (secId != Guid.Empty)
            {
              _ = ForceTextObjectHeight(doc, secId, _height);
              var secGeo = DupTextGeometry(doc, secId);
              if (secGeo != null)
                undoStack.Push(TextAction.CreateAdd(secId, secGeo));
            }
          }
        }
      }

      var addedGeo = DupTextGeometry(doc, newId);
      if (addedGeo != null)
      {
        undoStack.Push(TextAction.CreateAdd(newId, addedGeo));
        redoStack.Clear();
      }

      activeTextId = null;
      curveIsLocked = false;
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
        var text = _text;
        var height = _height;
        var offset = _offset;
        var rotate90 = _rotate90;

        if (vToolsOptionStore.TryGetString(section, TextKey, out var persistedText) && !string.IsNullOrWhiteSpace(persistedText))
          text = persistedText;
        if (vToolsOptionStore.TryGetDouble(section, HeightKey, out var persistedHeight) && persistedHeight > RhinoMath.ZeroTolerance)
          height = persistedHeight;
        if (vToolsOptionStore.TryGetDouble(section, OffsetKey, out var persistedOffset))
          offset = persistedOffset;
        if (vToolsOptionStore.TryGetDouble(section, Rotate90Key, out var persistedRotate))
          rotate90 = NormalizeRotate((int)Math.Round(persistedRotate, MidpointRounding.AwayFromZero));

        var bothSides = _bothSides;
        if (vToolsOptionStore.TryGetBool(section, BothSidesKey, out var persistedBothSides))
          bothSides = persistedBothSides;

        return (text, height, offset, rotate90, bothSides);
      });

    _text = values.text;
    _height = Math.Max(values.height, RhinoMath.ZeroTolerance);
    _offset = values.offset;
    _rotate90 = NormalizeRotate(values.rotate90);
    _bothSides = values.bothSides;
  }

  private static void SavePersistedOptions()
  {
    _ = vToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[TextKey] = _text;
        section[HeightKey] = _height;
        section[OffsetKey] = _offset;
        section[Rotate90Key] = _rotate90;
        section[BothSidesKey] = _bothSides ? 1.0 : 0.0;
      });
  }

  private static List<CurveObjectCacheItem> CollectCurveObjects(RhinoDoc doc)
  {
    var curves = new List<CurveObjectCacheItem>();

    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      DeletedObjects = false,
      ObjectTypeFilter = ObjectType.Curve
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj?.Geometry is not Curve curve)
        continue;

      curves.Add(new CurveObjectCacheItem(obj.Id, curve));
    }

    return curves;
  }

  private static List<Guid> CollectTextIds(RhinoDoc doc)
  {
    var ids = new List<Guid>();

    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      DeletedObjects = false,
      ObjectTypeFilter = ObjectType.Annotation
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj?.Geometry is TextEntity)
        ids.Add(obj.Id);
    }

    return ids;
  }

  private static CurveHit? FindClosestCurveHit(List<CurveObjectCacheItem> curveCache, Point3d point)
  {
    CurveHit? best = null;

    foreach (var item in curveCache)
    {
      if (!item.Curve.ClosestPoint(point, out var t))
        continue;

      var cpt = item.Curve.PointAt(t);
      var distance = point.DistanceTo(cpt);

      if (best == null || distance < best.Value.Distance)
        best = new CurveHit(item.ObjectId, item.Curve, t, distance);
    }

    return best;
  }

  private static bool IsCurveSnapped(CurveHit? curveHit, double snapTolerance)
  {
    return curveHit.HasValue && curveHit.Value.Distance <= snapTolerance;
  }

  private static TextHit? FindClosestTextHit(RhinoDoc doc, IReadOnlyList<Guid> textIds, Point3d point, double toleranceScale, bool requireInside)
  {
    Guid? bestId = null;
    double? bestDistance = null;

    foreach (var id in textIds)
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not TextEntity text)
        continue;

      var metrics = TextEntityPickMetrics(text, point);
      if (metrics == null)
        continue;

      var tol = TextEntityPickTolerance(doc, text, toleranceScale);

      if (requireInside)
      {
        if (!metrics.Value.Inside)
          continue;
      }
      else if (!metrics.Value.Inside && metrics.Value.PlanarOutside > tol)
      {
        continue;
      }

      var dist = metrics.Value.PlanarOutside;
      if (bestDistance == null || dist < bestDistance.Value)
      {
        bestDistance = dist;
        bestId = id;
      }
    }

    if (!bestId.HasValue || !bestDistance.HasValue)
      return null;

    return new TextHit(bestId.Value, bestDistance.Value);
  }

  private static bool PreferTextHit(CurveHit? curveHit, TextHit? textHit, double curveSnapTolerance)
  {
    if (textHit == null)
      return false;

    if (curveHit == null)
      return true;

    var curveDist = curveHit.Value.Distance;
    var textDist = textHit.Value.Distance;
    var tol = Math.Max(RhinoMath.ZeroTolerance, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? RhinoMath.ZeroTolerance);

    if (textDist <= Math.Max(tol * 2.0, curveSnapTolerance * 0.10))
      return true;

    var margin = Math.Max(tol * 2.0, curveSnapTolerance * 0.15);
    return textDist < (curveDist - margin);
  }

  private static PickMetrics? TextEntityPickMetrics(TextEntity textEntity, Point3d point)
  {
    var bounds = CenteredLocalTextBounds(textEntity);
    if (bounds == null)
      return null;

    var (plane, minx, maxx, miny, maxy) = bounds.Value;

    if (!plane.ClosestParameter(point, out var u, out var v))
      return null;

    var insideU = u >= minx && u <= maxx;
    var insideV = v >= miny && v <= maxy;
    var inside = insideU && insideV;

    var du = insideU ? 0.0 : Math.Min(Math.Abs(u - minx), Math.Abs(u - maxx));
    var dv = insideV ? 0.0 : Math.Min(Math.Abs(v - miny), Math.Abs(v - maxy));
    var planarOutside = Math.Sqrt((du * du) + (dv * dv));

    var planeDist = Math.Abs(plane.DistanceTo(point));
    var total = Math.Sqrt((planarOutside * planarOutside) + (planeDist * planeDist));

    return new PickMetrics(inside, total, planarOutside, planeDist);
  }

  private static double TextEntityPickTolerance(RhinoDoc doc, TextEntity textEntity, double toleranceScale)
  {
    var h = textEntity.TextHeight;
    if (h <= RhinoMath.ZeroTolerance)
      h = 1.0;

    var baseTol = Math.Max(doc.ModelAbsoluteTolerance * 2.0, Math.Min(0.20 * h, 0.08));
    return baseTol * Math.Max(toleranceScale, 0.1);
  }

  private static (Plane Plane, double MinX, double MaxX, double MinY, double MaxY)? CenteredLocalTextBounds(TextEntity textEntity)
  {
    try
    {
      var plane = textEntity.Plane;

      // Probe: duplicate and reset to WorldXY so the bbox reflects actual
      // offsets from plane.Origin regardless of text justification.
      try
      {
        var probe = textEntity.Duplicate() as TextEntity;
        if (probe != null)
        {
          probe.Plane = Plane.WorldXY;
          var wb = probe.GetBoundingBox(Plane.WorldXY);
          if (wb.IsValid)
          {
            var ww = wb.Max.X - wb.Min.X;
            var hh = wb.Max.Y - wb.Min.Y;
            if (ww > RhinoMath.ZeroTolerance && hh > RhinoMath.ZeroTolerance)
              return (plane, wb.Min.X, wb.Max.X, wb.Min.Y, wb.Max.Y);
          }
        }
      }
      catch
      {
      }

      // Fallback: GetBoundingBox in the text's own plane (may be inaccurate for
      // rotated text, but better than nothing).
      var lb = textEntity.GetBoundingBox(plane);
      if (!lb.IsValid)
        return null;

      var w = lb.Max.X - lb.Min.X;
      var h = lb.Max.Y - lb.Min.Y;

      if (w <= RhinoMath.ZeroTolerance || h <= RhinoMath.ZeroTolerance)
        return null;

      return (plane, lb.Min.X, lb.Max.X, lb.Min.Y, lb.Max.Y);
    }
    catch
    {
      return null;
    }
  }

  private static bool BuildPlaneFromCurve(
    RhinoDoc doc,
    Curve curve,
    double parameter,
    Point3d cursorPoint,
    double offsetValue,
    double heightValue,
    int rotate90,
    Vector3d upAxis,
    out Plane plane,
    out int sideSign,
    int sideSignHint,
    double sideDeadband,
    Guid? templateTextId)
  {
    plane = Plane.Unset;
    sideSign = 0;

    var curvePoint = curve.PointAt(parameter);
    var tangent = curve.TangentAt(parameter);
    if (!tangent.Unitize())
      return false;

    var normal = upAxis;
    if (!normal.Unitize())
      normal = Vector3d.ZAxis;

    var sideBase = Vector3d.CrossProduct(normal, tangent);
    if (!sideBase.Unitize())
      return false;

    var cursorVec = cursorPoint - curvePoint;
    var sideMetric = Vector3d.Multiply(cursorVec, sideBase);
    var resolvedSideSign = sideMetric >= 0.0 ? 1.0 : -1.0;

    if (sideSignHint is 1 or -1)
    {
      var db = Math.Max(sideDeadband, RhinoMath.ZeroTolerance);
      if (sideSignHint > 0 && sideMetric > -db)
        resolvedSideSign = 1.0;
      else if (sideSignHint < 0 && sideMetric < db)
        resolvedSideSign = -1.0;
    }

    var sideVec = new Vector3d(sideBase);
    if (resolvedSideSign < 0.0)
      sideVec.Reverse();

    var yAxis = new Vector3d(sideVec);
    var xAxis = Vector3d.CrossProduct(yAxis, normal);

    if (!yAxis.Unitize() || !xAxis.Unitize())
      return false;

    // Align xAxis with the curve tangent direction so orientation stays
    // consistent around the full curve regardless of which side the text is on.
    if (Vector3d.Multiply(xAxis, tangent) < 0.0)
    {
      xAxis.Reverse();
      yAxis.Reverse();
    }

    var quarterTurns = NormalizeRotate(rotate90);
    if (quarterTurns != 0)
    {
      var angle = quarterTurns * (Math.PI * 0.5);
      xAxis.Rotate(angle, normal);
      yAxis.Rotate(angle, normal);
      xAxis.Unitize();
      yAxis.Unitize();
    }

    var offsetNumber = offsetValue;
    Point3d origin;

    if (Math.Abs(offsetNumber) <= RhinoMath.ZeroTolerance)
    {
      origin = cursorPoint;
    }
    else
    {
      var targetGap = Math.Abs(offsetNumber);
      origin = curvePoint + sideVec * targetGap;

      var textValue = _text;
      var textHeight = Math.Max(heightValue, RhinoMath.ZeroTolerance);

      try
      {
        for (var i = 0; i < 2; i++)
        {
          var probePlane = new Plane(origin, xAxis, yAxis);
          var probe = BuildProbeTextEntity(doc, textValue, textHeight, probePlane, templateTextId);

          var details = SideRayHitOnTextRect(curvePoint, sideVec, probe, returnDetails: true, clampNonnegative: false);
          if (details == null)
            break;

          var measuredRaw = details.Value.Gap;
          var delta = targetGap - measuredRaw;
          if (Math.Abs(delta) <= doc.ModelAbsoluteTolerance)
            break;

          origin += sideVec * delta;
        }
      }
      catch
      {
      }
    }

    plane = new Plane(origin, xAxis, yAxis);
    sideSign = (int)resolvedSideSign;
    return plane.IsValid;
  }

  private static TextEntity BuildTextEntity(RhinoDoc doc, string textValue, double heightValue, Plane plane)
  {
    var text = new TextEntity
    {
      Plane = plane,
      TextHeight = Math.Max(heightValue, RhinoMath.ZeroTolerance),
      Justification = TextJustification.MiddleCenter
    };

    SetTextEntityValue(text, textValue);
    return text;
  }

  private static TextEntity BuildProbeTextEntity(RhinoDoc doc, string textValue, double heightValue, Plane plane, Guid? templateTextId)
  {
    return BuildTextEntity(doc, textValue, heightValue, plane);
  }

  private static SideRayResult? SideRayHitOnTextRect(Point3d curvePoint, Vector3d sideVec, TextEntity textEntity, bool returnDetails, bool clampNonnegative)
  {
    var sv = sideVec;
    if (!sv.Unitize())
      return null;

    var bounds = CenteredLocalTextBounds(textEntity);
    if (bounds == null)
      return null;

    var (plane, minx, maxx, miny, maxy) = bounds.Value;

    var ux = plane.XAxis;
    var uy = plane.YAxis;
    if (!ux.Unitize() || !uy.Unitize())
      return null;

    var center = plane.Origin;
    var centerDist = Vector3d.Multiply(center - curvePoint, sv);

    var halfW = 0.5 * (maxx - minx);
    var halfH = 0.5 * (maxy - miny);
    var du = Math.Abs(Vector3d.Multiply(sv, ux));
    var dv = Math.Abs(Vector3d.Multiply(sv, uy));
    var halfSpan = (du * halfW) + (dv * halfH);

    var rawGap = centerDist - halfSpan;
    var gap = rawGap;
    if (clampNonnegative && gap < 0.0)
      gap = 0.0;

    var hit = curvePoint + (sv * gap);
    return new SideRayResult(hit, gap, halfW, halfH, du, dv, halfSpan, centerDist, rawGap);
  }

  private static bool SetTextEntityValue(TextEntity textEntity, string value)
  {
    try
    {
      textEntity.PlainText = value;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static bool ApplySettingsToTextObject(RhinoDoc doc, Guid textId, string textValue, double heightValue, Plane plane)
  {
    var obj = doc.Objects.FindId(textId);
    if (obj?.Geometry is not TextEntity source)
      return false;

    var updated = source.Duplicate() as TextEntity;
    if (updated == null)
      return false;

    updated.Plane = plane;
    SetTextEntityValue(updated, textValue);
    ApplyHeightOverride(doc, updated, heightValue);

    if (!doc.Objects.Replace(textId, updated))
      return false;

    return true;
  }

  private static bool RestoreTextGeometry(RhinoDoc doc, Guid textId, TextEntity snapshot)
  {
    try
    {
      var dup = snapshot.Duplicate() as TextEntity;
      if (dup == null)
        return false;
      return doc.Objects.Replace(textId, dup);
    }
    catch
    {
      return false;
    }
  }

  private static TextEntity? DupTextGeometry(RhinoDoc doc, Guid objectId)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not TextEntity text)
      return null;

    try
    {
      return text.Duplicate() as TextEntity;
    }
    catch
    {
      return null;
    }
  }

  private static void ApplyHeightOverride(RhinoDoc doc, TextEntity te, double height)
  {
    var baseStyleId = te.DimensionStyleId != Guid.Empty ? te.DimensionStyleId : doc.DimStyles.Current.Id;
    var baseStyle = doc.DimStyles.FindId(baseStyleId) ?? doc.DimStyles.Current;
    var overrideStyle = baseStyle.Duplicate();
    overrideStyle.TextHeight = Math.Max(height, RhinoMath.ZeroTolerance);
    // Force in-plane orientation so placed text matches the preview (which has no dim style override).
    overrideStyle.TextOrientation = TextOrientation.InPlane;
    overrideStyle.TextRotation = 0.0;
    te.SetOverrideDimStyle(overrideStyle);
  }

  private static bool ForceTextObjectHeight(RhinoDoc doc, Guid objectId, double height)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not TextEntity te)
      return false;

    var dup = te.Duplicate() as TextEntity;
    if (dup == null)
      return false;

    ApplyHeightOverride(doc, dup, height);
    return doc.Objects.Replace(objectId, dup);
  }

  private static bool ApplyUndoAction(RhinoDoc doc, TextAction action, double currentHeight)
  {
    if (action.Kind == TextActionKind.Add)
      return doc.Objects.Delete(action.ObjectId, true);

    if (action.Kind == TextActionKind.Move && action.Before != null)
    {
      var ok = doc.Objects.Replace(action.ObjectId, action.Before.Duplicate() as TextEntity);
      if (ok)
        _ = ForceTextObjectHeight(doc, action.ObjectId, currentHeight);
      return ok;
    }

    return false;
  }

  private static bool ApplyRedoAction(RhinoDoc doc, TextAction action, double currentHeight)
  {
    if (action.Kind == TextActionKind.Add && action.Geo != null)
    {
      var newId = doc.Objects.AddText(action.Geo.Duplicate() as TextEntity);
      if (newId == Guid.Empty)
        return false;

      action.ObjectId = newId;
      _ = ForceTextObjectHeight(doc, newId, currentHeight);
      return true;
    }

    if (action.Kind == TextActionKind.Move && action.After != null)
    {
      var ok = doc.Objects.Replace(action.ObjectId, action.After.Duplicate() as TextEntity);
      if (ok)
        _ = ForceTextObjectHeight(doc, action.ObjectId, currentHeight);
      return ok;
    }

    return false;
  }

  private static void UpdateSettingsFromTextObject(TextEntity textObj, ref string textValue, ref double heightValue)
  {
    textValue = TextEntityValue(textObj, textValue);

    var h = textObj.TextHeight;
    if (h > RhinoMath.ZeroTolerance)
      heightValue = h;
  }

  private static string TextEntityValue(TextEntity textEntity, string fallback)
  {
    if (!string.IsNullOrWhiteSpace(textEntity.PlainText))
      return textEntity.PlainText;
    if (!string.IsNullOrWhiteSpace(textEntity.RichText))
      return textEntity.RichText;
    return fallback;
  }

  private static int NormalizeRotate(int rotate90)
  {
    var value = rotate90 % 4;
    if (value < 0)
      value += 4;
    return value;
  }

  private readonly record struct CurveObjectCacheItem(Guid ObjectId, Curve Curve);

  private readonly record struct CurveHit(Guid ObjectId, Curve Curve, double Parameter, double Distance);

  private readonly record struct TextHit(Guid ObjectId, double Distance);

  private readonly record struct PickMetrics(bool Inside, double TotalDistance, double PlanarOutside, double PlaneDistance);

  private readonly record struct SideRayResult(
    Point3d Hit,
    double Gap,
    double HalfW,
    double HalfH,
    double Du,
    double Dv,
    double HalfSize,
    double CenterDistance,
    double RawGap);

  private enum TextActionKind
  {
    Add,
    Move
  }

  private sealed class TextAction
  {
    public TextActionKind Kind { get; private init; }
    public Guid ObjectId { get; set; }
    public TextEntity? Geo { get; private init; }
    public TextEntity? Before { get; private init; }
    public TextEntity? After { get; private init; }

    public static TextAction CreateAdd(Guid id, TextEntity geo)
    {
      return new TextAction
      {
        Kind = TextActionKind.Add,
        ObjectId = id,
        Geo = geo.Duplicate() as TextEntity
      };
    }

    public static TextAction CreateMove(Guid id, TextEntity before, TextEntity after)
    {
      return new TextAction
      {
        Kind = TextActionKind.Move,
        ObjectId = id,
        Before = before.Duplicate() as TextEntity,
        After = after.Duplicate() as TextEntity
      };
    }
  }

  private sealed class MainPointGetter : GetPoint
  {
    private readonly RhinoDoc _doc;
    private readonly string _text;
    private readonly double _height;
    private readonly double _offset;
    private readonly int _rotate90;

    private readonly List<CurveObjectCacheItem> _curveCache;
    private readonly List<Guid> _textIds;

    private readonly Guid? _activeCurveId;
    private readonly Guid? _activeTextId;
    private readonly bool _curveIsLocked;
    private readonly bool _bothSides;

    private int _lastSideSign;

    public MainPointGetter(
      RhinoDoc doc,
      string text,
      double height,
      double offset,
      int rotate90,
      bool bothSides,
      List<CurveObjectCacheItem> curveCache,
      List<Guid> textIds,
      Guid? activeCurveId,
      Guid? activeTextId,
      bool curveIsLocked)
    {
      _doc = doc;
      _text = text;
      _height = height;
      _offset = offset;
      _rotate90 = rotate90;

      _curveCache = curveCache;
      _textIds = textIds;
      _activeCurveId = activeCurveId;
      _activeTextId = activeTextId;
      _curveIsLocked = curveIsLocked;
      _bothSides = bothSides;

      SnapTolerance = Math.Max(doc.ModelAbsoluteTolerance * 3.0, 0.25);
      HoverSnapTolerance = SnapTolerance;
      PreviewTemplateTextId = _activeTextId ?? (_textIds.Count > 0 ? _textIds[0] : (Guid?)null);
    }

    public CurveHit? HoverCurve { get; private set; }
    public TextHit? HoverText { get; private set; }
    public bool HoverIntentIsText { get; private set; }
    public Plane? PreviewPlane { get; private set; }
    public Plane? PreviewPlaneOpp { get; private set; }
    public Guid? PreviewTemplateTextId { get; }
    public Point3d? LastCursorPoint { get; private set; }
    public int LastSideSign => _lastSideSign;

    public double SnapTolerance { get; }
    public double HoverSnapTolerance { get; }

    private Curve? CurveById(Guid objectId)
    {
      foreach (var item in _curveCache)
      {
        if (item.ObjectId == objectId)
          return item.Curve;
      }

      return null;
    }

    private void UpdateState(Point3d point)
    {
      LastCursorPoint = point;

      var curveHit = FindClosestCurveHit(_curveCache, point);
      var textHit = FindClosestTextHit(_doc, _textIds, point, toleranceScale: 1.25, requireInside: false);
      var snappedCurveHit = IsCurveSnapped(curveHit, HoverSnapTolerance) ? curveHit : null;

      HoverCurve = snappedCurveHit;
      HoverText = textHit;

      if (!_curveIsLocked && HoverText.HasValue)
        HoverCurve = null;

      // Lock pick intent: click will select whatever object was highlighted here.
      HoverIntentIsText = HoverText.HasValue && (_curveIsLocked ? true : !HoverCurve.HasValue);

      PreviewPlane = null;
      PreviewPlaneOpp = null;

      if (!_curveIsLocked || !_activeCurveId.HasValue)
        return;

      var curveToUse = CurveById(_activeCurveId.Value);
      if (curveToUse == null)
        return;

      if (!curveToUse.ClosestPoint(point, out var t))
        return;

      HoverCurve = new CurveHit(_activeCurveId.Value, curveToUse, t, point.DistanceTo(curveToUse.PointAt(t)));

      var upAxis = View()?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
      var sideDeadband = Math.Max(_doc.ModelAbsoluteTolerance * 4.0, _height * 0.1);

      if (BuildPlaneFromCurve(
            _doc,
            curveToUse,
            t,
            point,
            _offset,
            _height,
            _rotate90,
            upAxis,
            out var plane,
            out var sideSign,
            _lastSideSign,
            sideDeadband,
            PreviewTemplateTextId))
      {
        PreviewPlane = plane;
        if (sideSign is 1 or -1)
          _lastSideSign = sideSign;

        if (_bothSides && sideSign is 1 or -1)
        {
          var tanVec = curveToUse.TangentAt(t);
          var normVec = upAxis;
          if (!normVec.Unitize()) normVec = Vector3d.ZAxis;
          tanVec.Unitize();
          var sideBaseVec = Vector3d.CrossProduct(normVec, tanVec);
          if (sideBaseVec.Unitize())
          {
            var curvePoint = curveToUse.PointAt(t);
            var oppCursor = curvePoint - sideBaseVec * (sideSign * 1000.0);
            if (BuildPlaneFromCurve(
                  _doc, curveToUse, t, oppCursor,
                  _offset, _height, NormalizeRotate(_rotate90 + 2),
                  upAxis, out var oppPlane, out _,
                  sideSignHint: 0, sideDeadband: 0.0,
                  PreviewTemplateTextId))
            {
              PreviewPlaneOpp = oppPlane;
            }
          }
        }
      }
    }

    protected override void OnMouseMove(GetPointMouseEventArgs e)
    {
      UpdateState(e.Point);

      if (PreviewPlane.HasValue && _activeTextId.HasValue && _curveIsLocked)
      {
        _ = ApplySettingsToTextObject(_doc, _activeTextId.Value, _text, _height, PreviewPlane.Value);
        try
        {
          _doc.Views.Redraw();
        }
        catch
        {
        }
      }

      base.OnMouseMove(e);
    }

    protected override void OnDynamicDraw(GetPointDrawEventArgs e)
    {
      UpdateState(e.CurrentPoint);

      if (HoverCurve.HasValue)
        e.Display.DrawCurve(HoverCurve.Value.Curve, System.Drawing.Color.Orange, 3);

      // Overdraw hovered text in gold to override Rhino's layer-color pre-selection display.
      if (HoverIntentIsText)
      {
        var hoverObj = _doc.Objects.FindId(HoverText.Value.ObjectId);
        if (hoverObj?.Geometry is TextEntity hoverAnnotation)
        {
          try { e.Display.DrawAnnotation(hoverAnnotation, System.Drawing.Color.Gold); }
          catch { }
        }
      }

      if (_activeTextId.HasValue)
      {
        var activeObj = _doc.Objects.FindId(_activeTextId.Value);
        if (activeObj?.Geometry is TextEntity activeText)
        {
          try
          {
            var bounds = CenteredLocalTextBounds(activeText);
            if (bounds.HasValue)
            {
              var (_, minx, maxx, miny, maxy) = bounds.Value;
              // Use the current-frame preview plane so the box stays in sync with
              // the floating cursor preview, not the one-frame-behind doc object.
              var boxPlane = PreviewPlane ?? activeText.Plane;
              var localBbox = new BoundingBox(minx, miny, 0, maxx, maxy, 0);
              e.Display.DrawBox(new Box(boxPlane, localBbox), System.Drawing.Color.Cyan, 2);
            }
          }
          catch
          {
          }
        }
      }

      // Draw gold box around the text object currently under cursor.
      if (PreviewPlane.HasValue)
      {
        if (_activeTextId.HasValue)
        {
          e.Display.DrawPoint(PreviewPlane.Value.Origin, Rhino.Display.PointStyle.RoundSimple, 3, System.Drawing.Color.Cyan);
        }
        else
        {
          try
          {
            e.Display.DrawAnnotation(BuildPreviewText(PreviewPlane.Value), System.Drawing.Color.Cyan);
          }
          catch
          {
          }
        }
      }

      if (PreviewPlaneOpp.HasValue)
      {
        try
        {
          e.Display.DrawAnnotation(BuildPreviewText(PreviewPlaneOpp.Value), System.Drawing.Color.Cyan);
        }
        catch
        {
        }
      }

      base.OnDynamicDraw(e);
    }

    private TextEntity BuildPreviewText(Plane plane)
    {
      var text = BuildTextEntity(_doc, _text, _height, plane);
      ApplyHeightOverride(_doc, text, _height);
      return text;
    }
  }
}
