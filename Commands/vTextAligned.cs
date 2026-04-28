using System;
using System.Collections.Generic;
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

  private static readonly string[] RotateModes = { "0", "90", "180", "270" };

  private static string _text = "Text";
  private static double _height = 5.0;
  private static double _offset;
  private static int _rotate90;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTextAligned";

  /// <summary>
  /// Places text entities aligned to selected curve tangents.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    if (!TryPickCurve(doc, out var curveRef))
      return Result.Cancel;

    if (curveRef?.Curve() is not Curve curve)
      return Result.Failure;

    var undoStack = new Stack<TextRecord>();
    var redoStack = new Stack<TextEntity>();

    while (true)
    {
      var gp = new GetPoint();
      gp.SetCommandPrompt("Pick point near curve to place aligned text");
      gp.AcceptNothing(true);
      gp.AcceptString(true);

      var heightOption = new OptionDouble(_height, doc.ModelAbsoluteTolerance, 1.0e300);
      var offsetOption = new OptionDouble(_offset, -1.0e300, 1.0e300);

      var textOptionIndex = gp.AddOption("Text");
      gp.AddOptionDouble("Height", ref heightOption);
      gp.AddOptionDouble("Offset", ref offsetOption);
      var rotateOptionIndex = gp.AddOptionList("Rotate", RotateModes, _rotate90);
      var undoOptionIndex = gp.AddOption("Undo");
      var redoOptionIndex = gp.AddOption("Redo");

      var result = gp.Get();
      if (gp.CommandResult() != Result.Success)
        return Result.Cancel;

      if (result == GetResult.Nothing || result == GetResult.Cancel)
        break;

      if (result == GetResult.String)
      {
        var text = (gp.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
        if (text is "u" or "undo")
        {
          ApplyUndo(doc, undoStack, redoStack);
          continue;
        }

        if (text is "r" or "redo")
        {
          ApplyRedo(doc, undoStack, redoStack);
          continue;
        }

        RhinoApp.WriteLine("vTextAligned: hidden keywords are 'u'/'undo' and 'r'/'redo'.");
        continue;
      }

      if (result == GetResult.Option)
      {
        _height = Math.Max(heightOption.CurrentValue, doc.ModelAbsoluteTolerance);
        _offset = offsetOption.CurrentValue;

        var option = gp.Option();
        if (option != null && option.Index == textOptionIndex)
        {
          var proposed = _text;
          if (RhinoGet.GetString("Text", true, ref proposed) == Result.Success && !string.IsNullOrWhiteSpace(proposed))
            _text = proposed;
          SavePersistedOptions();
          continue;
        }

        if (option != null && option.Index == rotateOptionIndex)
          _rotate90 = ClampRotate(option.CurrentListOptionIndex);

        if (gp.OptionIndex() == undoOptionIndex)
        {
          ApplyUndo(doc, undoStack, redoStack);
          continue;
        }

        if (gp.OptionIndex() == redoOptionIndex)
        {
          ApplyRedo(doc, undoStack, redoStack);
          continue;
        }

        SavePersistedOptions();
        continue;
      }

      if (result != GetResult.Point)
        continue;

      _height = Math.Max(heightOption.CurrentValue, doc.ModelAbsoluteTolerance);
      _offset = offsetOption.CurrentValue;
      SavePersistedOptions();

      var pickPoint = gp.Point();
      if (!TryBuildTextEntity(doc, curve, pickPoint, out var entity))
      {
        RhinoApp.WriteLine("vTextAligned: failed to build aligned text at this location.");
        continue;
      }

      var id = doc.Objects.AddText(entity);
      if (id == Guid.Empty)
      {
        RhinoApp.WriteLine("vTextAligned: failed to add text.");
        continue;
      }

      undoStack.Push(new TextRecord(id, entity.Duplicate() as TextEntity ?? entity));
      redoStack.Clear();
      doc.Views.Redraw();
    }

    SavePersistedOptions();
    doc.Views.Redraw();
    return Result.Success;
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
          rotate90 = ClampRotate((int)Math.Round(persistedRotate, MidpointRounding.AwayFromZero));

        return (text, height, offset, rotate90);
      });

    _text = values.text;
    _height = Math.Max(values.height, RhinoMath.ZeroTolerance);
    _offset = values.offset;
    _rotate90 = ClampRotate(values.rotate90);
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
      });
  }

  private static bool TryPickCurve(RhinoDoc doc, out ObjRef? curveRef)
  {
    curveRef = null;

    while (true)
    {
      var go = new GetObject();
      go.SetCommandPrompt("Select curve to align text");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.AcceptString(true);
      go.AcceptNothing(false);

      var heightOption = new OptionDouble(_height, doc.ModelAbsoluteTolerance, 1.0e300);
      var offsetOption = new OptionDouble(_offset, -1.0e300, 1.0e300);

      var textOptionIndex = go.AddOption("Text");
      go.AddOptionDouble("Height", ref heightOption);
      go.AddOptionDouble("Offset", ref offsetOption);
      var rotateOptionIndex = go.AddOptionList("Rotate", RotateModes, _rotate90);

      var result = go.Get();
      if (go.CommandResult() != Result.Success)
        return false;

      if (result == GetResult.String)
      {
        var typed = go.StringResult();
        if (!string.IsNullOrWhiteSpace(typed))
        {
          _text = typed;
          SavePersistedOptions();
        }
        continue;
      }

      if (result == GetResult.Option)
      {
        _height = Math.Max(heightOption.CurrentValue, doc.ModelAbsoluteTolerance);
        _offset = offsetOption.CurrentValue;

        var option = go.Option();
        if (option != null && option.Index == textOptionIndex)
        {
          var proposed = _text;
          if (RhinoGet.GetString("Text", true, ref proposed) == Result.Success && !string.IsNullOrWhiteSpace(proposed))
            _text = proposed;
        }

        if (option != null && option.Index == rotateOptionIndex)
          _rotate90 = ClampRotate(option.CurrentListOptionIndex);

        SavePersistedOptions();
        continue;
      }

      if (result != GetResult.Object)
        return false;

      curveRef = go.Object(0);
      return curveRef != null;
    }
  }

  private static bool TryBuildTextEntity(RhinoDoc doc, Curve curve, Point3d pickPoint, out TextEntity textEntity)
  {
    textEntity = new TextEntity();

    if (!curve.ClosestPoint(pickPoint, out var t))
      return false;

    var curvePoint = curve.PointAt(t);
    var tangent = curve.TangentAt(t);
    if (!tangent.Unitize())
      return false;

    var normal = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    if (!normal.Unitize())
      normal = Vector3d.ZAxis;

    var side = Vector3d.CrossProduct(normal, tangent);
    if (!side.Unitize())
      return false;

    var toPick = pickPoint - curvePoint;
    if (Vector3d.Multiply(side, toPick) < 0.0)
      side.Reverse();

    var xAxis = new Vector3d(tangent);
    var yAxis = Vector3d.CrossProduct(normal, xAxis);
    if (!yAxis.Unitize())
      return false;

    var quarterTurns = ClampRotate(_rotate90);
    if (quarterTurns != 0)
    {
      var angle = quarterTurns * Math.PI * 0.5;
      xAxis.Rotate(angle, normal);
      yAxis.Rotate(angle, normal);
      xAxis.Unitize();
      yAxis.Unitize();
    }

    var center = curvePoint + side * _offset;
    var plane = new Plane(center, xAxis, yAxis);
    if (!plane.IsValid)
      return false;

    textEntity.PlainText = _text;
    textEntity.TextHeight = Math.Max(_height, doc.ModelAbsoluteTolerance);
    textEntity.Plane = plane;
    textEntity.Justification = TextJustification.MiddleCenter;
    return true;
  }

  private static void ApplyUndo(RhinoDoc doc, Stack<TextRecord> undoStack, Stack<TextEntity> redoStack)
  {
    if (undoStack.Count == 0)
    {
      RhinoApp.WriteLine("vTextAligned: nothing to undo.");
      return;
    }

    var record = undoStack.Pop();
    if (!doc.Objects.Delete(record.ObjectId, true))
    {
      RhinoApp.WriteLine("vTextAligned: undo failed.");
      return;
    }

    redoStack.Push(record.EntitySnapshot.Duplicate() as TextEntity ?? record.EntitySnapshot);
    doc.Views.Redraw();
  }

  private static void ApplyRedo(RhinoDoc doc, Stack<TextRecord> undoStack, Stack<TextEntity> redoStack)
  {
    if (redoStack.Count == 0)
    {
      RhinoApp.WriteLine("vTextAligned: nothing to redo.");
      return;
    }

    var entity = redoStack.Pop();
    var newId = doc.Objects.AddText(entity);
    if (newId == Guid.Empty)
    {
      RhinoApp.WriteLine("vTextAligned: redo failed.");
      return;
    }

    undoStack.Push(new TextRecord(newId, entity.Duplicate() as TextEntity ?? entity));
    doc.Views.Redraw();
  }

  private static int ClampRotate(int rotate90)
  {
    var value = rotate90 % 4;
    if (value < 0)
      value += 4;
    return value;
  }

  private readonly record struct TextRecord(Guid ObjectId, TextEntity EntitySnapshot);
}
