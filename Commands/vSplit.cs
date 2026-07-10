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
/// Native interactive split-at-picked-points command ported from splitAt.py.
/// </summary>
public sealed class vSplit : Command
{
  private const string OptionsSectionName = "vSplit";
  private const string GripsKey = "grips";
  private const int PointSize = 3;

  private static readonly Color SetPointColor = Color.Red;
  private static readonly Color RemovePointColor = Color.Cyan;

  public override string EnglishName => "vSplit";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var showGrips = LoadPersistedGrips();

    var targets = GetTargetCurves(doc);
    if (targets == null || targets.Count == 0)
      return Result.Cancel;

    var completed = CollectSplitPoints(doc, targets, showGrips);
    if (!completed)
    {
      RestoreTargetGrips(doc, targets);
      SelectExistingTargets(doc, targets);
      return Result.Cancel;
    }

    var newIds = ApplySplits(doc, targets);
    if (newIds.Count > 0)
      RhinoApp.WriteLine($"vSplit: split {newIds.Count} curve piece{(newIds.Count == 1 ? "" : "s")}.");

    return Result.Success;
  }

  private static bool LoadPersistedGrips()
  {
    return ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, GripsKey, out var grips) ? grips : true);
  }

  private static void SavePersistedOptions(bool showGrips)
  {
    _ = ToolsOptionStore.Update(OptionsSectionName, section => section[GripsKey] = showGrips);
  }

  private static double ModelTolerance(RhinoDoc doc) => Math.Max(doc.ModelAbsoluteTolerance, 1e-6);

  private static double ParameterTolerance(Curve curve)
  {
    var domain = curve.Domain;
    return Math.Max(Math.Abs(domain.T1 - domain.T0) * 1e-9, 1e-10);
  }

  private static double PointHitTolerance(RhinoDoc doc, Curve curve)
  {
    var length = 0.0;
    try { length = curve.GetLength(); }
    catch { /* ignored */ }

    return Math.Max(Math.Max(ModelTolerance(doc) * 4.0, length * 1e-8), 1e-6);
  }

  private static List<Curve> SplitClosedCurveForConstraint(Curve curve)
  {
    var domain = curve.Domain;
    var span = domain.T1 - domain.T0;
    if (Math.Abs(span) <= 1e-12)
      return new List<Curve>();

    Curve[]? pieces;
    try
    {
      pieces = curve.Split(new[]
      {
        domain.T0 + span / 3.0,
        domain.T0 + span * 2.0 / 3.0
      });
    }
    catch
    {
      return new List<Curve>();
    }

    if (pieces == null || pieces.Length == 0)
      return new List<Curve>();

    return pieces
      .Where(piece => piece != null && !piece.IsClosed)
      .Select(piece => piece.DuplicateCurve())
      .Where(piece => piece != null)
      .Cast<Curve>()
      .ToList();
  }

  private static List<Curve> CurveConstraintSegments(Curve curve)
  {
    Curve[]? segments;
    try { segments = curve.DuplicateSegments(); }
    catch { segments = null; }

    if (segments == null || segments.Length == 0)
    {
      var duplicate = curve.DuplicateCurve();
      segments = duplicate != null ? new[] { duplicate } : Array.Empty<Curve>();
    }

    var result = new List<Curve>();
    foreach (var segment in segments)
    {
      if (segment == null)
        continue;

      if (!segment.IsClosed)
      {
        result.Add(segment);
        continue;
      }

      var openedSegments = SplitClosedCurveForConstraint(segment);
      if (openedSegments.Count == 0)
        return new List<Curve>();

      result.AddRange(openedSegments);
    }

    return result;
  }

  private static Curve? BuildConstraintCurve(IReadOnlyList<SplitTarget> targets)
  {
    if (targets.Count == 1)
      return targets[0].Curve;

    var polycurve = new PolyCurve();
    var appended = 0;

    foreach (var target in targets)
    {
      var segments = CurveConstraintSegments(target.Curve);
      if (segments.Count == 0)
        return null;

      foreach (var segment in segments)
      {
        try
        {
          if (!polycurve.AppendSegment(segment))
            return null;
        }
        catch
        {
          return null;
        }

        appended++;
      }
    }

    return appended > 0 ? polycurve : null;
  }

  private static void ConfigurePointGetter(GetPoint gp, IEnumerable<SplitTarget> targets, Curve? constraintCurve)
  {
    if (constraintCurve != null)
    {
      try
      {
        gp.Constrain(constraintCurve, false);
        return;
      }
      catch
      {
        // Fall through to individual target constraints.
      }
    }

    foreach (var target in targets)
    {
      try
      {
        gp.Constrain(target.Curve, false);
        return;
      }
      catch
      {
      }
    }
  }

  private static IEnumerable<Point3d> SplitPointLocations(IEnumerable<SplitTarget> targets)
  {
    foreach (var target in targets)
    {
      foreach (var parameter in target.Parameters)
      {
        var point = target.Curve.PointAt(parameter);
        if (point.IsValid)
          yield return point;
      }
    }
  }

  private static void AddSplitPointSnaps(GetPoint gp, IEnumerable<SplitTarget> targets)
  {
    var addSnapPoint = typeof(GetPoint).GetMethod("AddSnapPoint", new[] { typeof(Point3d) });
    if (addSnapPoint == null)
      return;

    foreach (var point in SplitPointLocations(targets))
    {
      try { addSnapPoint.Invoke(gp, new object[] { point }); }
      catch { }
    }
  }

  private static GetResult GetWithPointOsnap(GetPoint gp)
  {
    var previous = Rhino.ApplicationSettings.ModelAidSettings.OsnapModes;
    var restore = false;

    try
    {
      Rhino.ApplicationSettings.ModelAidSettings.OsnapModes =
        previous | Rhino.ApplicationSettings.OsnapModes.Point;
      restore = true;
    }
    catch
    {
    }

    try
    {
      return gp.Get();
    }
    finally
    {
      if (restore)
      {
        try { Rhino.ApplicationSettings.ModelAidSettings.OsnapModes = previous; }
        catch { }
      }
    }
  }

  private static List<SplitTarget>? GetTargetCurves(RhinoDoc doc)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to split");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(false);

    var result = go.GetMultiple(1, 0);
    if (result != GetResult.Object || go.CommandResult() != Result.Success)
      return null;

    var targets = new List<SplitTarget>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      var rhObj = objRef?.Object();
      var curve = objRef?.Curve();
      if (rhObj == null || curve == null || !seen.Add(rhObj.Id))
        continue;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        continue;

      targets.Add(new SplitTarget(
        doc,
        rhObj.Id,
        duplicate,
        rhObj.Attributes?.Duplicate(),
        rhObj.GripsOn));
    }

    if (targets.Count == 0)
    {
      RhinoApp.WriteLine("vSplit: no curves selected.");
      return null;
    }

    return targets;
  }

  private static bool AddUniqueParameter(SplitTarget target, double parameter, bool reportSkipped)
  {
    var curve = target.Curve;
    var domain = curve.Domain;
    var tol = target.ParameterTolerance;

    if (!curve.IsClosed &&
        (Math.Abs(parameter - domain.T0) <= tol || Math.Abs(parameter - domain.T1) <= tol))
    {
      if (reportSkipped)
        RhinoApp.WriteLine("vSplit: skipped endpoint split point.");
      return false;
    }

    foreach (var existing in target.Parameters)
    {
      if (Math.Abs(existing - parameter) <= tol)
      {
        if (reportSkipped)
          RhinoApp.WriteLine("vSplit: skipped duplicate split point.");
        return false;
      }
    }

    target.Parameters.Add(parameter);
    return true;
  }

  private static bool RemoveParameter(SplitTarget target, double parameter)
  {
    var tol = target.ParameterTolerance;
    for (var i = 0; i < target.Parameters.Count; i++)
    {
      if (Math.Abs(target.Parameters[i] - parameter) <= tol)
      {
        target.Parameters.RemoveAt(i);
        return true;
      }
    }

    return false;
  }

  private static List<SplitPointItem> SplitPointsNearTargets(IEnumerable<SplitTarget> targets, Point3d point)
  {
    var items = new List<SplitPointItem>();
    foreach (var target in targets)
    {
      foreach (var parameter in target.Parameters)
      {
        if (point.DistanceTo(target.Curve.PointAt(parameter)) <= target.HitTolerance)
          items.Add(new SplitPointItem(target, parameter));
      }
    }

    return items;
  }

  private static void SelectExistingTargets(RhinoDoc doc, IEnumerable<SplitTarget> targets)
  {
    doc.Objects.UnselectAll();
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      rhObj?.Select(true);
    }
    doc.Views.Redraw();
  }

  private static void SelectFinalObjects(RhinoDoc doc, IEnumerable<SplitTarget> targets, IEnumerable<Guid> newIds)
  {
    doc.Objects.UnselectAll();

    foreach (var id in newIds)
      doc.Objects.FindId(id)?.Select(true);

    foreach (var target in targets)
      doc.Objects.FindId(target.ObjectId)?.Select(true);

    doc.Views.Redraw();
  }

  private static void SetTargetGrips(RhinoDoc doc, IEnumerable<SplitTarget> targets, bool enabled)
  {
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      if (rhObj != null)
        rhObj.GripsOn = enabled;
    }
    doc.Views.Redraw();
  }

  private static void RestoreTargetGrips(RhinoDoc doc, IEnumerable<SplitTarget> targets)
  {
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      if (rhObj != null)
        rhObj.GripsOn = target.InitialGrips;
    }
    doc.Views.Redraw();
  }

  private static List<(SplitTarget Target, double Parameter)> TargetCandidates(
    IEnumerable<SplitTarget> targets,
    Point3d point)
  {
    var candidates = new List<(SplitTarget Target, double Parameter)>();

    foreach (var target in targets)
    {
      if (!target.Curve.ClosestPoint(point, out var parameter))
        continue;

      var curvePoint = target.Curve.PointAt(parameter);
      var distance = point.DistanceTo(curvePoint);
      if (distance <= target.HitTolerance)
        candidates.Add((target, parameter));
    }

    return candidates;
  }

  private static (SplitAction? Action, bool PointWasOnTarget) AddActionFromPick(
    IEnumerable<SplitTarget> targets,
    Point3d point)
  {
    var candidates = TargetCandidates(targets, point);
    var added = new List<SplitPointItem>();
    foreach (var (target, parameter) in candidates)
    {
      if (AddUniqueParameter(target, parameter, reportSkipped: false))
        added.Add(new SplitPointItem(target, parameter));
    }

    return added.Count > 0
      ? (new SplitAction(SplitActionKind.Add, added), true)
      : (null, candidates.Count > 0);
  }

  private static SplitAction? ApplyPointAction(SplitAction? action, bool reverse)
  {
    if (action == null)
      return null;

    var remove = (action.Kind == SplitActionKind.Add && reverse) ||
                 (action.Kind == SplitActionKind.Remove && !reverse);
    var completed = new List<SplitPointItem>();

    foreach (var item in action.Items)
    {
      var success = remove
        ? RemoveParameter(item.Target, item.Parameter)
        : AddUniqueParameter(item.Target, item.Parameter, reportSkipped: false);

      if (success)
        completed.Add(item);
    }

    return completed.Count > 0 ? new SplitAction(action.Kind, completed) : null;
  }

  private static bool CollectSplitPoints(RhinoDoc doc, List<SplitTarget> targets, bool showGrips)
  {
    var conduit = new SplitPointConduit(targets)
    {
      Enabled = true
    };

    SelectExistingTargets(doc, targets);

    SetTargetGrips(doc, targets, showGrips);

    var undoStack = new Stack<SplitAction>();
    var redoStack = new Stack<SplitAction>();
    var constraintCurve = BuildConstraintCurve(targets);

    try
    {
      while (true)
      {
        conduit.SetRemoveAction(null);

        var gp = new GetPoint();
        gp.SetCommandPrompt("Point to split at. Press Enter when done");
        gp.AcceptNothing(true);
        ConfigurePointGetter(gp, targets, constraintCurve);
        AddSplitPointSnaps(gp, targets);

        var gripsOption = new OptionToggle(showGrips, "Hide", "Show");
        gp.AddOptionToggle("Grips", ref gripsOption);
        var undoOptionIndex = undoStack.Count > 0 ? gp.AddOption("Undo") : -1;
        var redoOptionIndex = redoStack.Count > 0 ? gp.AddOption("Redo") : -1;

        gp.DynamicDraw += (_, e) =>
        {
          var previewItems = SplitPointsNearTargets(targets, e.CurrentPoint);
          var previewAction = previewItems.Count > 0
            ? new SplitAction(SplitActionKind.Remove, previewItems)
            : null;

          conduit.SetRemoveAction(previewAction);

          foreach (var item in previewItems)
          {
            e.Display.DrawPoint(
              item.Target.Curve.PointAt(item.Parameter),
              PointStyle.RoundSimple,
              PointSize,
              RemovePointColor);
          }
        };

        var result = GetWithPointOsnap(gp);
        if (gp.CommandResult() != Result.Success)
          return false;

        if (result == GetResult.Nothing)
          return true;

        if (result == GetResult.Option)
        {
          var selectedOption = gp.OptionIndex();
          if (undoStack.Count > 0 && selectedOption == undoOptionIndex)
          {
            var undoAction = ApplyPointAction(undoStack.Pop(), reverse: true);
            if (undoAction != null)
              redoStack.Push(undoAction);
            doc.Views.Redraw();
            RhinoApp.WriteLine("vSplit: undo split point.");
            continue;
          }

          if (redoStack.Count > 0 && selectedOption == redoOptionIndex)
          {
            var redoAction = ApplyPointAction(redoStack.Pop(), reverse: false);
            if (redoAction != null)
              undoStack.Push(redoAction);
            doc.Views.Redraw();
            RhinoApp.WriteLine("vSplit: redo split point.");
            continue;
          }

          showGrips = gripsOption.CurrentValue;
          SavePersistedOptions(showGrips);
          SetTargetGrips(doc, targets, showGrips);
          continue;
        }

        if (result != GetResult.Point)
          return false;

        var point = gp.Point();
        if (!point.IsValid)
        {
          RhinoApp.WriteLine("vSplit: could not read split point.");
          continue;
        }

        var previewAction = conduit.RemoveAction;
        SplitAction? action;
        bool pointWasOnTarget;
        if (previewAction != null)
        {
          action = ApplyPointAction(previewAction, reverse: false);
          pointWasOnTarget = true;
        }
        else
        {
          (action, pointWasOnTarget) = AddActionFromPick(targets, point);
        }

        conduit.SetRemoveAction(null);
        if (action == null)
        {
          RhinoApp.WriteLine(pointWasOnTarget
            ? "vSplit: no new split point added."
            : "vSplit: snap or click near one of the selected curves.");
          continue;
        }

        undoStack.Push(action);
        redoStack.Clear();
        RhinoApp.WriteLine(action.Kind == SplitActionKind.Add
          ? "vSplit: added split point."
          : "vSplit: removed split point.");
        doc.Views.Redraw();
      }
    }
    finally
    {
      conduit.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static bool ValidPiece(RhinoDoc doc, Curve? piece)
  {
    if (piece == null || !piece.IsValid)
      return false;

    try { return piece.GetLength() > ModelTolerance(doc); }
    catch { return true; }
  }

  private static List<Curve> SplitCurve(RhinoDoc doc, SplitTarget target)
  {
    if (target.Parameters.Count == 0)
      return new List<Curve>();

    var parameters = target.Parameters.OrderBy(p => p).ToArray();
    var pieces = target.Curve.Split(parameters);
    if (pieces == null || pieces.Length < 1)
      return new List<Curve>();

    return pieces
      .Where(piece => ValidPiece(doc, piece))
      .Select(piece => piece.DuplicateCurve())
      .Where(piece => piece != null)
      .Cast<Curve>()
      .ToList();
  }

  private static List<Guid> AddSplitPieces(RhinoDoc doc, SplitTarget target, IEnumerable<Curve> pieces)
  {
    var newIds = new List<Guid>();
    foreach (var piece in pieces)
    {
      var objectId = target.Attributes != null
        ? doc.Objects.AddCurve(piece, target.Attributes.Duplicate())
        : doc.Objects.AddCurve(piece);

      if (objectId == Guid.Empty)
        continue;

      var rhObj = doc.Objects.FindId(objectId);
      if (rhObj != null)
        rhObj.GripsOn = target.InitialGrips;

      newIds.Add(objectId);
    }

    return newIds;
  }

  private static List<Guid> ApplySplits(RhinoDoc doc, List<SplitTarget> targets)
  {
    var splitTargets = targets.Where(target => target.Parameters.Count > 0).ToList();
    if (splitTargets.Count == 0)
    {
      RhinoApp.WriteLine("vSplit: no split points were added.");
      RestoreTargetGrips(doc, targets);
      SelectExistingTargets(doc, targets);
      return new List<Guid>();
    }

    var undoRecord = doc.BeginUndoRecord("vSplit");
    var newIds = new List<Guid>();

    try
    {
      foreach (var target in splitTargets)
      {
        var pieces = SplitCurve(doc, target);
        if (pieces.Count < 1)
        {
          RhinoApp.WriteLine("vSplit: could not split one curve.");
          continue;
        }

        var addedIds = AddSplitPieces(doc, target, pieces);
        foreach (var piece in pieces)
          piece.Dispose();

        if (addedIds.Count < 1)
        {
          RhinoApp.WriteLine("vSplit: could not add split pieces for one curve.");
          continue;
        }

        doc.Objects.Delete(target.ObjectId, true);
        newIds.AddRange(addedIds);
      }
    }
    finally
    {
      if (undoRecord > 0)
        doc.EndUndoRecord(undoRecord);
    }

    RestoreTargetGrips(doc, targets);
    SelectFinalObjects(doc, targets, newIds);
    return newIds;
  }

  private sealed class SplitTarget
  {
    public SplitTarget(RhinoDoc doc, Guid objectId, Curve curve, ObjectAttributes? attributes, bool initialGrips)
    {
      ObjectId = objectId;
      Curve = curve;
      Attributes = attributes;
      InitialGrips = initialGrips;
      ParameterTolerance = vSplit.ParameterTolerance(curve);
      HitTolerance = vSplit.PointHitTolerance(doc, curve);
    }

    public Guid ObjectId { get; }
    public Curve Curve { get; }
    public ObjectAttributes? Attributes { get; }
    public bool InitialGrips { get; }
    public double ParameterTolerance { get; }
    public double HitTolerance { get; }
    public List<double> Parameters { get; } = new();
  }

  private readonly record struct SplitPointItem(SplitTarget Target, double Parameter);

  private enum SplitActionKind
  {
    Add,
    Remove
  }

  private sealed record SplitAction(SplitActionKind Kind, List<SplitPointItem> Items);

  private sealed class SplitPointConduit : DisplayConduit
  {
    private readonly List<SplitTarget> _targets;
    private SplitAction? _removeAction;

    public SplitPointConduit(List<SplitTarget> targets)
    {
      _targets = targets;
    }

    public SplitAction? RemoveAction => _removeAction;

    public void SetRemoveAction(SplitAction? action)
    {
      _removeAction = action;
    }

    private bool IsRemovePreview(SplitTarget target, double parameter)
    {
      if (_removeAction == null)
        return false;

      foreach (var item in _removeAction.Items)
      {
        if (ReferenceEquals(item.Target, target) &&
            Math.Abs(item.Parameter - parameter) <= target.ParameterTolerance)
        {
          return true;
        }
      }

      return false;
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      e.Display.EnableDepthTesting(false);
      e.Display.EnableDepthWriting(false);
      try
      {
        foreach (var target in _targets)
        {
          foreach (var parameter in target.Parameters)
          {
            e.Display.DrawPoint(
              target.Curve.PointAt(parameter),
              PointStyle.RoundSimple,
              PointSize,
              IsRemovePreview(target, parameter) ? RemovePointColor : SetPointColor);
          }
        }
      }
      finally
      {
        e.Display.EnableDepthWriting(true);
        e.Display.EnableDepthTesting(true);
      }
    }
  }
}
