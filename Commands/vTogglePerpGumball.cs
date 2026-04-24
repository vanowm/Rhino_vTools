using System;
using System.Globalization;
using System.Reflection;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Toggles the perpendicular gumball monitor on and off.
/// </summary>
public sealed class vTogglePerpGumball : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTogglePerpGumball";

  /// <summary>
  /// Executes toggle for the monitor that auto-orients gumball for one selected grip.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var enabled = PerpGumballMonitor.Toggle(doc);
    RhinoApp.WriteLine(enabled ? "Perpendicular Gumball: ON" : "Perpendicular Gumball: OFF");
    return Result.Success;
  }
}

internal static class PerpGumballMonitor
{
  private const int IdlePollMs = 100;
  private const int IdleSettleTicks = 2;
  private const string BadgeLabel = "PG";
  private const int BadgeFontSize = 14;

  private static readonly object Sync = new();
  private static readonly Point2d BadgePos = new(2, 20);
  private static readonly Point2d[] BadgeOutlinePoints =
  {
    new Point2d(BadgePos.X - 1, BadgePos.Y - 1),
    new Point2d(BadgePos.X - 1, BadgePos.Y + 1),
    new Point2d(BadgePos.X + 1, BadgePos.Y - 1),
    new Point2d(BadgePos.X + 1, BadgePos.Y + 1)
  };

  private static bool _enabled;

  private static EventHandler? _idleHandler;
  private static EventHandler<RhinoObjectSelectionEventArgs>? _selectHandler;
  private static EventHandler<RhinoObjectSelectionEventArgs>? _deselectHandler;

  private static string? _lastGripKey;
  private static string? _lastViewId;
  private static string? _lastPlaneKey;
  private static string? _lastCameraKey;
  private static string? _lastGripPointKey;

  private static int? _lastIdleTick;
  private static int _idleSettle;

  private static StatusBadgeConduit? _badge;
  private static OrientationDebugData _lastDebugData = OrientationDebugData.Empty;

  internal static bool Toggle(RhinoDoc doc)
  {
    lock (Sync)
    {
      if (_enabled)
        Disable(doc);
      else
        Enable(doc);

      return _enabled;
    }
  }

  private static void Enable(RhinoDoc doc)
  {
    if (_enabled)
      return;

    AttachHandlers();
    AttachBadge(doc);
    ClearCache();
    _enabled = true;

    UpdateGumballForActiveGrip(doc, force: true, allowCommandFallback: true);
    doc.Views.Redraw();
  }

  private static void Disable(RhinoDoc doc)
  {
    if (!_enabled)
      return;

    _enabled = false;
    DetachHandlers();
    DetachBadge(doc);
    ResetGumballDefault(doc);
    ClearCache();
    doc.Views.Redraw();
  }

  private static void ClearCache()
  {
    _lastGripKey = null;
    _lastViewId = null;
    _lastPlaneKey = null;
    _lastCameraKey = null;
    _lastGripPointKey = null;
    _lastIdleTick = null;
    _idleSettle = 0;
    _lastDebugData = OrientationDebugData.Empty;
  }

  private static void AttachHandlers()
  {
    if (_idleHandler == null)
      _idleHandler = OnIdle;
    if (_selectHandler == null)
      _selectHandler = OnSelect;
    if (_deselectHandler == null)
      _deselectHandler = OnDeselect;

    RhinoApp.Idle -= _idleHandler;
    RhinoApp.Idle += _idleHandler;

    RhinoDoc.SelectObjects -= _selectHandler;
    RhinoDoc.SelectObjects += _selectHandler;

    RhinoDoc.DeselectObjects -= _deselectHandler;
    RhinoDoc.DeselectObjects += _deselectHandler;
  }

  private static void DetachHandlers()
  {
    if (_idleHandler != null)
      RhinoApp.Idle -= _idleHandler;
    if (_selectHandler != null)
      RhinoDoc.SelectObjects -= _selectHandler;
    if (_deselectHandler != null)
      RhinoDoc.DeselectObjects -= _deselectHandler;
  }

  private static void OnSelect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    if (!_enabled)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc != null)
      UpdateGumballForActiveGrip(doc, force: false, allowCommandFallback: true);
  }

  private static void OnDeselect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    if (!_enabled)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc != null)
      UpdateGumballForActiveGrip(doc, force: false, allowCommandFallback: true);
  }

  private static void OnIdle(object? sender, EventArgs e)
  {
    if (!_enabled)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return;

    var now = System.Environment.TickCount;
    if (_lastIdleTick.HasValue)
    {
      var elapsed = unchecked(now - _lastIdleTick.Value);
      if (elapsed < IdlePollMs)
        return;
    }

    _lastIdleTick = now;

    if (IsCommandRunning(doc))
      return;

    if (!TryGetActiveGripContext(doc, out var ctx))
    {
      InvalidateLastApplyState();
      return;
    }

    var cameraKey = CameraKey(ctx.Viewport);
    var cameraChanged = !string.Equals(cameraKey, _lastCameraKey, StringComparison.Ordinal);
    _lastCameraKey = cameraKey;

    var gripOrViewChanged =
      !string.Equals(_lastGripKey, ctx.GripKey, StringComparison.Ordinal) ||
      !string.Equals(_lastViewId, ctx.ViewId, StringComparison.Ordinal);

    var gripPointKey = PointKey(ctx.Grip.CurrentLocation);
    var gripPointChanged = !string.Equals(_lastGripPointKey, gripPointKey, StringComparison.Ordinal);

    if (cameraChanged)
      _idleSettle = IdleSettleTicks;
    else if (_idleSettle > 0)
      _idleSettle -= 1;

    if (!cameraChanged && !gripOrViewChanged && !gripPointChanged && _idleSettle <= 0)
      return;

    UpdateGumballForActiveGrip(doc, force: cameraChanged || _idleSettle > 0, ctx, allowCommandFallback: true);
  }

  private static bool IsCommandRunning(RhinoDoc doc)
  {
    try
    {
      var inCommand = typeof(RhinoApp).GetMethod("InCommand", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
      if (inCommand != null)
      {
        var value = inCommand.Invoke(null, null);
        if (value is bool b && b)
          return true;
        if (value is int i && i != 0)
          return true;
      }
    }
    catch
    {
    }

    try
    {
      var prop = typeof(RhinoDoc).GetProperty("IsCommandRunning", BindingFlags.Public | BindingFlags.Instance);
      if (prop != null && prop.GetValue(doc) is bool b && b)
        return true;
    }
    catch
    {
    }

    return false;
  }

  private static bool TryGetActiveGripContext(RhinoDoc doc, out GripContext context)
  {
    context = default;

    if (!TryGetSingleSelectedGrip(doc, out var grip) || grip == null)
      return false;

    var owner = doc.Objects.FindId(grip.OwnerId);
    if (owner == null)
      return false;

    var view = doc.Views.ActiveView;
    if (view == null)
      return false;

    var viewport = view.ActiveViewport;
    if (viewport == null || !IsSupportedViewport(viewport))
      return false;

    var gripKey = string.Create(
      CultureInfo.InvariantCulture,
      $"{grip.OwnerId:N}:{grip.Index}");

    var viewId = viewport.Id.ToString("N", CultureInfo.InvariantCulture);

    context = new GripContext(grip, owner, view, viewport, gripKey, viewId);
    return true;
  }

  private static bool TryGetSingleSelectedGrip(RhinoDoc doc, out GripObject? grip)
  {
    grip = null;
    var count = 0;

    foreach (var obj in doc.Objects.GetSelectedObjects(false, true))
    {
      if (obj is not GripObject gripObj)
        continue;

      count += 1;
      if (count > 1)
      {
        grip = null;
        return false;
      }

      grip = gripObj;
    }

    return count == 1 && grip != null;
  }

  private static void UpdateGumballForActiveGrip(RhinoDoc doc, bool force, bool allowCommandFallback)
  {
    if (!TryGetActiveGripContext(doc, out var ctx))
    {
      InvalidateLastApplyState();
      return;
    }

    UpdateGumballForActiveGrip(doc, force, ctx, allowCommandFallback);
  }

  private static void UpdateGumballForActiveGrip(RhinoDoc doc, bool force, GripContext ctx, bool allowCommandFallback)
  {
    if (!_enabled)
      return;

    var plane = ComputePlaneForGrip(ctx.Viewport, ctx.Grip, ctx.Owner.Geometry, doc.ModelAbsoluteTolerance, ctx.ViewId, out var debugData);
    _lastDebugData = debugData;

    if (!plane.IsValid)
      return;

    var planeKey = PlaneKey(plane);

    if (!force &&
        string.Equals(_lastGripKey, ctx.GripKey, StringComparison.Ordinal) &&
        string.Equals(_lastViewId, ctx.ViewId, StringComparison.Ordinal) &&
        string.Equals(_lastPlaneKey, planeKey, StringComparison.Ordinal))
    {
      return;
    }

    var applied = TrySetGumballFrame(ctx.Viewport, plane);
    if (!applied && allowCommandFallback)
      applied = RelocateGumballByCommand(plane);

    if (!applied)
      return;

    _lastGripKey = ctx.GripKey;
    _lastViewId = ctx.ViewId;
    _lastPlaneKey = planeKey;
    _lastGripPointKey = PointKey(ctx.Grip.CurrentLocation);

    ctx.View.Redraw();
  }

  private static void InvalidateLastApplyState()
  {
    _lastGripKey = null;
    _lastViewId = null;
    _lastPlaneKey = null;
    _lastCameraKey = null;
    _lastGripPointKey = null;
    _idleSettle = 0;
    _lastDebugData = OrientationDebugData.Empty;
  }

  private static Plane ComputePlaneForGrip(RhinoViewport viewport, GripObject grip, GeometryBase geometry, double tolerance, string viewId, out OrientationDebugData debugData)
  {
    debugData = OrientationDebugData.Empty;

    var cameraRight = Unit(viewport.CameraX);
    var cameraUp = Unit(viewport.CameraY);

    if (cameraRight.IsTiny())
      cameraRight = Unit(viewport.ConstructionPlane().XAxis);
    if (cameraUp.IsTiny())
      cameraUp = Unit(viewport.ConstructionPlane().YAxis);

    var viewDirection = Unit(viewport.CameraDirection);
    if (viewDirection.IsTiny())
      viewDirection = Unit(viewport.ConstructionPlane().ZAxis);

    var origin = grip.CurrentLocation;

    if (geometry is Curve curve)
    {
      var z = viewDirection;
      Vector3d perp2d;
      Vector3d basisDirection;
      var hasFootPoint = false;
      var footPoint = Point3d.Unset;
      var usedGripToCurve = false;
      var hasCurveReference = false;
      var curveReferencePoint = Point3d.Unset;
      var curveReferenceTangent = Vector3d.Zero;

      // For off-curve grips, use the direction from grip point to its closest point on curve.
      if (TryGripToCurvePerpendicularDirection(curve, origin, tolerance, out var gripToCurve, out footPoint, out var footTangent))
      {
        usedGripToCurve = true;
        hasFootPoint = true;
        hasCurveReference = true;
        curveReferencePoint = footPoint;
        curveReferenceTangent = footTangent;
        basisDirection = gripToCurve;

        perp2d = ProjectToPlane(gripToCurve, z);
        if (perp2d.IsTiny())
          perp2d = gripToCurve;
      }
      else
      {
        var tangent = CurveTangentAtGrip(curve, grip, tolerance);
        if (tangent.IsTiny())
        {
          var fallback = new Plane(origin, cameraRight, cameraUp);
          debugData = BuildDebugData(viewId, origin, hasFootPoint, footPoint, hasCurveReference, curveReferencePoint, curveReferenceTangent, usedGripToCurve, Vector3d.Zero, Vector3d.Zero, cameraRight, cameraUp, fallback, tolerance);
          return fallback;
        }

        var tangent2d = ProjectToPlane(tangent, z);
        if (tangent2d.IsTiny())
          tangent2d = cameraRight;
        tangent2d = Unit(tangent2d);

        hasCurveReference = true;
        curveReferencePoint = origin;
        curveReferenceTangent = tangent;
        basisDirection = tangent2d;

        perp2d = Vector3d.CrossProduct(tangent2d, z);
      }

      if (perp2d.IsTiny())
        perp2d = cameraUp;
      perp2d = Unit(perp2d);

      var px = perp2d * cameraRight;
      var py = perp2d * cameraUp;
      var perpDeg = RadToDeg(Math.Atan2(py, px));

      var rotX = Fold180(perpDeg);
      var rotY = Fold180(perpDeg - 90.0);

      var rotDeg = Math.Abs(rotX) < Math.Abs(rotY)
        ? rotX
        : (Math.Abs(rotY) < Math.Abs(rotX) ? rotY : rotY);

      var angle = DegToRad(rotDeg);

      var x = Unit((Math.Cos(angle) * cameraRight) + (Math.Sin(angle) * cameraUp));
      var y = Unit((-Math.Sin(angle) * cameraRight) + (Math.Cos(angle) * cameraUp));
      if (y.IsTiny())
        y = cameraUp;

      var result = new Plane(origin, x, y);
      debugData = BuildDebugData(viewId, origin, hasFootPoint, footPoint, hasCurveReference, curveReferencePoint, curveReferenceTangent, usedGripToCurve, basisDirection, perp2d, cameraRight, cameraUp, result, tolerance);
      return result;
    }

    var hasNormal = TrySurfaceNormalAtPoint(geometry, origin, out var normal);
    var yAxis = hasNormal && !normal.IsTiny() ? Unit(normal) : cameraUp;
    var basisNormal = yAxis;

    var zAxis = viewDirection;
    var xAxis = Unit(Vector3d.CrossProduct(yAxis, zAxis));

    if (xAxis.IsTiny())
      xAxis = Unit(ProjectToPlane(cameraRight, yAxis));
    if (xAxis.IsTiny())
      xAxis = cameraRight;

    yAxis = Unit(Vector3d.CrossProduct(zAxis, xAxis));
    if (yAxis.IsTiny())
      yAxis = cameraUp;

    if ((xAxis * cameraRight) < 0)
    {
      xAxis = -xAxis;
      yAxis = -yAxis;
    }

    yAxis = -yAxis;
    var nonCurveResult = new Plane(origin, xAxis, yAxis);
    debugData = BuildDebugData(viewId, origin, false, Point3d.Unset, false, Point3d.Unset, Vector3d.Zero, false, basisNormal, yAxis, cameraRight, cameraUp, nonCurveResult, tolerance);
    return nonCurveResult;
  }

  private static OrientationDebugData BuildDebugData(
    string viewId,
    Point3d origin,
    bool hasFootPoint,
    Point3d footPoint,
    bool hasCurveReference,
    Point3d curveReferencePoint,
    Vector3d curveReferenceTangent,
    bool usedGripToCurve,
    Vector3d basisDirection,
    Vector3d perpDirection,
    Vector3d cameraRight,
    Vector3d cameraUp,
    Plane resultPlane,
    double tolerance)
  {
    var drawScale = Math.Max(tolerance * 100.0, 1.0);

    if (hasFootPoint)
    {
      var footDistance = origin.DistanceTo(footPoint);
      if (footDistance > RhinoMath.ZeroTolerance)
        drawScale = Math.Max(drawScale, footDistance);
    }

    return new OrientationDebugData(
      isValid: true,
      viewId: viewId,
      origin: origin,
      hasFootPoint: hasFootPoint,
      footPoint: footPoint,
      hasCurveReference: hasCurveReference,
      curveReferencePoint: curveReferencePoint,
      curveReferenceTangent: Unit(curveReferenceTangent),
      usedGripToCurve: usedGripToCurve,
      basisDirection: Unit(basisDirection),
      perpDirection: Unit(perpDirection),
      cameraRight: Unit(cameraRight),
      cameraUp: Unit(cameraUp),
      resultPlane: resultPlane,
      drawScale: drawScale);
  }

  private static bool TryGripToCurvePerpendicularDirection(Curve curve, Point3d gripPoint, double tolerance, out Vector3d direction, out Point3d footPoint, out Vector3d footTangent)
  {
    direction = Vector3d.Zero;
    footPoint = Point3d.Unset;
    footTangent = Vector3d.Zero;

    if (!curve.ClosestPoint(gripPoint, out var t))
      return false;

    footPoint = curve.PointAt(t);
    var toCurve = footPoint - gripPoint;
    if (toCurve.Length <= tolerance)
      return false;

    direction = toCurve;
    footTangent = curve.TangentAt(t);
    return true;
  }

  private static Vector3d CurveTangentAtGrip(Curve curve, GripObject grip, double tolerance)
  {
    var gripPoint = grip.CurrentLocation;

    if (gripPoint.DistanceTo(curve.PointAtStart) < tolerance)
      return curve.TangentAtStart;

    if (gripPoint.DistanceTo(curve.PointAtEnd) < tolerance)
    {
      var tanStart = curve.TangentAtStart;
      var tanEnd = curve.TangentAtEnd;
      if ((tanStart * tanEnd) < 0.0)
        tanEnd = -tanEnd;
      return tanEnd;
    }

    if (curve.ClosestPoint(gripPoint, out var t))
      return curve.TangentAt(t);

    return Vector3d.Zero;
  }

  private static bool TrySurfaceNormalAtPoint(GeometryBase geometry, Point3d point, out Vector3d normal)
  {
    normal = Vector3d.Zero;

    try
    {
      if (geometry is Surface surface)
      {
        if (surface.ClosestPoint(point, out var u, out var v))
        {
          normal = surface.NormalAt(u, v);
          return !normal.IsTiny();
        }
      }
      else if (geometry is Brep brep)
      {
        var bestDistance = double.MaxValue;
        var found = false;

        foreach (var face in brep.Faces)
        {
          if (!face.ClosestPoint(point, out var u, out var v))
            continue;

          var candidate = face.PointAt(u, v);
          var d2 = candidate.DistanceToSquared(point);
          if (d2 >= bestDistance)
            continue;

          bestDistance = d2;
          normal = face.NormalAt(u, v);
          found = !normal.IsTiny();
        }

        if (found)
          return true;
      }
      else if (geometry is Mesh mesh)
      {
        var meshPoint = mesh.ClosestMeshPoint(point, 0.0);
        if (meshPoint != null)
        {
          normal = mesh.NormalAt(meshPoint);
          return !normal.IsTiny();
        }
      }
    }
    catch
    {
    }

    return false;
  }

  private static bool TrySetGumballFrame(RhinoViewport viewport, Plane plane)
  {
    try
    {
      var type = viewport.GetType();
      var args = new object[] { plane };

      foreach (var methodName in new[] { "SetGumballFrame", "SetGumballFrameFromPlane" })
      {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Plane) }, null);
        if (method == null)
          continue;

        var result = method.Invoke(viewport, args);
        if (result is bool b)
        {
          if (b)
            return true;
        }
        else
        {
          return true;
        }
      }

      var property = type.GetProperty("GumballFrame", BindingFlags.Instance | BindingFlags.Public);
      if (property?.CanWrite == true)
      {
        property.SetValue(viewport, plane, null);
        return true;
      }
    }
    catch
    {
    }

    return false;
  }

  private static bool RelocateGumballByCommand(Plane plane)
  {
    try
    {
      var o = plane.Origin;
      var xpt = o + plane.XAxis;
      var ypt = o + plane.YAxis;

      var command = string.Format(
        CultureInfo.InvariantCulture,
        "-_GumballRelocate {0} {1} {2} _Enter",
        WorldPointToken(o),
        WorldPointToken(xpt),
        WorldPointToken(ypt));

      return RhinoApp.RunScript(command, false);
    }
    catch
    {
      return false;
    }
  }

  private static string WorldPointToken(Point3d point)
  {
    return string.Format(
      CultureInfo.InvariantCulture,
      "w{0:G17},{1:G17},{2:G17}",
      point.X,
      point.Y,
      point.Z);
  }

  private static void ResetGumballDefault(RhinoDoc doc)
  {
    try
    {
      RhinoApp.RunScript("-_GumballReset _Enter", false);
      doc.Views.ActiveView?.Redraw();
    }
    catch
    {
    }
  }

  private static void AttachBadge(RhinoDoc doc)
  {
    if (_badge == null)
      _badge = new StatusBadgeConduit();

    _badge.Enabled = true;
    doc.Views.Redraw();
  }

  private static void DetachBadge(RhinoDoc doc)
  {
    if (_badge != null)
      _badge.Enabled = false;

    doc.Views.Redraw();
  }

  private static bool ShouldDrawBadge(DrawEventArgs e)
  {
    if (!_enabled)
      return false;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return false;

    var activeView = doc.Views.ActiveView;
    if (activeView == null)
      return false;

    var activeViewport = activeView.ActiveViewport;
    if (activeViewport == null || !IsSupportedViewport(activeViewport))
      return false;

    var drawViewport = e.Viewport;
    if (drawViewport != null && drawViewport.Id != activeViewport.Id)
      return false;

    return TryGetActiveGripContext(doc, out _);
  }

  private static bool IsSupportedViewport(RhinoViewport viewport)
  {
    if (viewport.IsPerspectiveProjection)
      return false;

    // Keep default gumball in Perspective viewport even when switched to Parallel.
    if (viewport.IsParallelProjection && IsPerspectiveNamedViewport(viewport))
      return false;

    return true;
  }

  private static bool IsPerspectiveNamedViewport(RhinoViewport viewport)
  {
    try
    {
      var name = viewport.Name ?? string.Empty;
      return name.IndexOf("perspective", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    catch
    {
      return false;
    }
  }

  private static void DrawBadge(DrawEventArgs e)
  {
    foreach (var p in BadgeOutlinePoints)
      e.Display.Draw2dText(BadgeLabel, System.Drawing.Color.Black, p, false, BadgeFontSize);

    e.Display.Draw2dText(BadgeLabel, System.Drawing.Color.White, BadgePos, false, BadgeFontSize);
  }

  private static void DrawDebugLines(DrawEventArgs e)
  {
    var debug = _lastDebugData;
    if (!debug.IsValid)
      return;

    var drawViewport = e.Viewport;
    if (drawViewport == null)
      return;

    var drawViewId = drawViewport.Id.ToString("N", CultureInfo.InvariantCulture);
    if (!string.Equals(drawViewId, debug.ViewId, StringComparison.Ordinal))
      return;

    var origin = debug.Origin;

    if (debug.HasFootPoint)
      e.Display.DrawLine(origin, debug.FootPoint, System.Drawing.Color.Gold, 2);

    if (debug.HasCurveReference)
    {
      var tangentDirection = Unit(debug.CurveReferenceTangent);
      if (!tangentDirection.IsTiny())
      {
        var tangentHalf = debug.DrawScale * 0.9;
        e.Display.DrawLine(
          debug.CurveReferencePoint - (tangentDirection * tangentHalf),
          debug.CurveReferencePoint + (tangentDirection * tangentHalf),
          System.Drawing.Color.Magenta,
          2);
      }

      if (!debug.HasFootPoint)
        DrawDebugVector(e, debug.CurveReferencePoint, debug.BasisDirection, debug.DrawScale, System.Drawing.Color.Gold);
    }

    var basisColor = debug.UsedGripToCurve ? System.Drawing.Color.Orange : System.Drawing.Color.Cyan;
    DrawDebugVector(e, origin, debug.BasisDirection, debug.DrawScale, basisColor);
    DrawDebugVector(e, origin, debug.PerpDirection, debug.DrawScale, System.Drawing.Color.YellowGreen);
    DrawDebugVector(e, origin, debug.CameraRight, debug.DrawScale * 0.8, System.Drawing.Color.IndianRed);
    DrawDebugVector(e, origin, debug.CameraUp, debug.DrawScale * 0.8, System.Drawing.Color.LightGreen);

    if (debug.ResultPlane.IsValid)
    {
      DrawDebugVector(e, origin, debug.ResultPlane.XAxis, debug.DrawScale, System.Drawing.Color.Red);
      DrawDebugVector(e, origin, debug.ResultPlane.YAxis, debug.DrawScale, System.Drawing.Color.Lime);
      DrawDebugVector(e, origin, debug.ResultPlane.ZAxis, debug.DrawScale, System.Drawing.Color.DeepSkyBlue);
    }
  }

  private static void DrawDebugVector(DrawEventArgs e, Point3d origin, Vector3d direction, double scale, System.Drawing.Color color)
  {
    if (scale <= RhinoMath.ZeroTolerance)
      return;

    var dir = Unit(direction);
    if (dir.IsTiny())
      return;

    e.Display.DrawLine(origin, origin + (dir * scale), color, 2);
  }

  private sealed class StatusBadgeConduit : DisplayConduit
  {
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (!ShouldDrawBadge(e))
        return;

      try
      {
        DrawBadge(e);
        DrawDebugLines(e);
      }
      catch
      {
      }
    }
  }

  private readonly struct OrientationDebugData
  {
    public static OrientationDebugData Empty => new(
      isValid: false,
      viewId: string.Empty,
      origin: Point3d.Unset,
      hasFootPoint: false,
      footPoint: Point3d.Unset,
      hasCurveReference: false,
      curveReferencePoint: Point3d.Unset,
      curveReferenceTangent: Vector3d.Zero,
      usedGripToCurve: false,
      basisDirection: Vector3d.Zero,
      perpDirection: Vector3d.Zero,
      cameraRight: Vector3d.Zero,
      cameraUp: Vector3d.Zero,
      resultPlane: Plane.Unset,
      drawScale: 1.0);

    public OrientationDebugData(
      bool isValid,
      string viewId,
      Point3d origin,
      bool hasFootPoint,
      Point3d footPoint,
      bool hasCurveReference,
      Point3d curveReferencePoint,
      Vector3d curveReferenceTangent,
      bool usedGripToCurve,
      Vector3d basisDirection,
      Vector3d perpDirection,
      Vector3d cameraRight,
      Vector3d cameraUp,
      Plane resultPlane,
      double drawScale)
    {
      IsValid = isValid;
      ViewId = viewId;
      Origin = origin;
      HasFootPoint = hasFootPoint;
      FootPoint = footPoint;
      HasCurveReference = hasCurveReference;
      CurveReferencePoint = curveReferencePoint;
      CurveReferenceTangent = curveReferenceTangent;
      UsedGripToCurve = usedGripToCurve;
      BasisDirection = basisDirection;
      PerpDirection = perpDirection;
      CameraRight = cameraRight;
      CameraUp = cameraUp;
      ResultPlane = resultPlane;
      DrawScale = drawScale;
    }

    public bool IsValid { get; }
    public string ViewId { get; }
    public Point3d Origin { get; }
    public bool HasFootPoint { get; }
    public Point3d FootPoint { get; }
    public bool HasCurveReference { get; }
    public Point3d CurveReferencePoint { get; }
    public Vector3d CurveReferenceTangent { get; }
    public bool UsedGripToCurve { get; }
    public Vector3d BasisDirection { get; }
    public Vector3d PerpDirection { get; }
    public Vector3d CameraRight { get; }
    public Vector3d CameraUp { get; }
    public Plane ResultPlane { get; }
    public double DrawScale { get; }
  }

  private readonly struct GripContext
  {
    public GripContext(GripObject grip, RhinoObject owner, RhinoView view, RhinoViewport viewport, string gripKey, string viewId)
    {
      Grip = grip;
      Owner = owner;
      View = view;
      Viewport = viewport;
      GripKey = gripKey;
      ViewId = viewId;
    }

    public GripObject Grip { get; }
    public RhinoObject Owner { get; }
    public RhinoView View { get; }
    public RhinoViewport Viewport { get; }
    public string GripKey { get; }
    public string ViewId { get; }
  }

  private static Vector3d Unit(Vector3d vector)
  {
    if (vector.IsTiny())
      return vector;

    vector.Unitize();
    return vector;
  }

  private static Vector3d ProjectToPlane(Vector3d vector, Vector3d zAxis)
  {
    return vector - ((vector * zAxis) * zAxis);
  }

  private static double Fold180(double angle)
  {
    if (angle > 90.0)
      return angle - 180.0;
    if (angle <= -90.0)
      return angle + 180.0;
    return angle;
  }

  private static double DegToRad(double degrees)
  {
    return degrees * (Math.PI / 180.0);
  }

  private static double RadToDeg(double radians)
  {
    return radians * (180.0 / Math.PI);
  }

  private static string PlaneKey(Plane plane)
  {
    return string.Format(
      CultureInfo.InvariantCulture,
      "{0:F4},{1:F4},{2:F4}|{3:F4},{4:F4},{5:F4}|{6:F4},{7:F4},{8:F4}",
      plane.Origin.X,
      plane.Origin.Y,
      plane.Origin.Z,
      plane.XAxis.X,
      plane.XAxis.Y,
      plane.XAxis.Z,
      plane.YAxis.X,
      plane.YAxis.Y,
      plane.YAxis.Z);
  }

  private static string PointKey(Point3d point)
  {
    return string.Format(
      CultureInfo.InvariantCulture,
      "{0:F4},{1:F4},{2:F4}",
      point.X,
      point.Y,
      point.Z);
  }

  private static string CameraKey(RhinoViewport viewport)
  {
    var framePlane = GetCameraFramePlane(viewport);

    var lens = 0.0;
    try
    {
      lens = viewport.Camera35mmLensLength;
    }
    catch
    {
    }

    return string.Format(
      CultureInfo.InvariantCulture,
      "{0:F4},{1:F4},{2:F4}|{3:F4},{4:F4},{5:F4}|{6:F4},{7:F4},{8:F4}|{9:F4},{10:F4},{11:F4}|{12:F4}|{13:F4},{14:F4},{15:F4}|{16:F4},{17:F4},{18:F4}|{19:F4},{20:F4},{21:F4}",
      viewport.CameraLocation.X,
      viewport.CameraLocation.Y,
      viewport.CameraLocation.Z,
      viewport.CameraDirection.X,
      viewport.CameraDirection.Y,
      viewport.CameraDirection.Z,
      viewport.CameraUp.X,
      viewport.CameraUp.Y,
      viewport.CameraUp.Z,
      viewport.CameraTarget.X,
      viewport.CameraTarget.Y,
      viewport.CameraTarget.Z,
      lens,
      framePlane.Origin.X,
      framePlane.Origin.Y,
      framePlane.Origin.Z,
      framePlane.XAxis.X,
      framePlane.XAxis.Y,
      framePlane.XAxis.Z,
      framePlane.YAxis.X,
      framePlane.YAxis.Y,
      framePlane.YAxis.Z);
  }

  private static Plane GetCameraFramePlane(RhinoViewport viewport)
  {
    try
    {
      var method = viewport.GetType().GetMethod("GetCameraFrame", BindingFlags.Public | BindingFlags.Instance);
      if (method != null)
      {
        var parameters = method.GetParameters();
        if (parameters.Length == 1)
        {
          var args = new object?[] { null };
          var ok = method.Invoke(viewport, args);
          if (ok is bool b && b && args[0] is Plane plane && plane.IsValid)
            return plane;
        }
      }
    }
    catch
    {
    }

    var cplane = viewport.ConstructionPlane();
    return cplane.IsValid ? cplane : Plane.WorldXY;
  }
}
