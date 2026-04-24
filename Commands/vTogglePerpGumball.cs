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
      return;

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

    var perspectiveMode = IsPerspectiveMode(ctx.Viewport);
    if (!perspectiveMode && !cameraChanged && !gripOrViewChanged && !gripPointChanged && _idleSettle <= 0)
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
      return;

    UpdateGumballForActiveGrip(doc, force, ctx, allowCommandFallback);
  }

  private static void UpdateGumballForActiveGrip(RhinoDoc doc, bool force, GripContext ctx, bool allowCommandFallback)
  {
    if (!_enabled)
      return;

    var plane = ComputePlaneForGrip(ctx.Viewport, ctx.Grip, ctx.Owner.Geometry, doc.ModelAbsoluteTolerance);
    if (!plane.IsValid)
      return;

    var planeKey = PlaneKey(plane);
    var isPerspectiveMode = IsPerspectiveMode(ctx.Viewport);

    if (!force &&
        string.Equals(_lastGripKey, ctx.GripKey, StringComparison.Ordinal) &&
        string.Equals(_lastViewId, ctx.ViewId, StringComparison.Ordinal) &&
        string.Equals(_lastPlaneKey, planeKey, StringComparison.Ordinal))
    {
      if (!isPerspectiveMode || !NeedsPerspectiveReapply(doc, plane))
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

  private static bool NeedsPerspectiveReapply(RhinoDoc doc, Plane expectedPlane)
  {
    try
    {
      if (doc.GetGumballPlane(out var current) && current.IsValid)
      {
        return !string.Equals(PlaneKey(current), PlaneKey(expectedPlane), StringComparison.Ordinal);
      }
    }
    catch
    {
    }

    // If we cannot read current state, keep perspective resilient by retrying.
    return true;
  }

  private static Plane ComputePlaneForGrip(RhinoViewport viewport, GripObject grip, GeometryBase geometry, double tolerance)
  {
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
      var tangent = CurveTangentAtGrip(curve, grip, tolerance);
      if (tangent.IsTiny())
        return ConstrainPerspectiveRotationToWorldZ(viewport, new Plane(origin, cameraRight, cameraUp));

      var z = viewDirection;
      var tangent2d = ProjectToPlane(tangent, z);
      if (tangent2d.IsTiny())
        tangent2d = cameraRight;
      tangent2d = Unit(tangent2d);

      var perp2d = Vector3d.CrossProduct(tangent2d, z);
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

      return ConstrainPerspectiveRotationToWorldZ(viewport, new Plane(origin, x, y));
    }

    var hasNormal = TrySurfaceNormalAtPoint(geometry, origin, out var normal);
    var yAxis = hasNormal && !normal.IsTiny() ? Unit(normal) : cameraUp;

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
    return ConstrainPerspectiveRotationToWorldZ(viewport, new Plane(origin, xAxis, yAxis));
  }

  private static Plane ConstrainPerspectiveRotationToWorldZ(RhinoViewport viewport, Plane plane)
  {
    if (!IsPerspectiveMode(viewport))
      return plane;

    var viewDirection = Unit(viewport.CameraDirection);
    if (viewDirection.IsTiny())
      viewDirection = Unit(viewport.ConstructionPlane().ZAxis);
    if (viewDirection.IsTiny())
      viewDirection = Vector3d.ZAxis;

    var worldZ = Vector3d.ZAxis;
    var worldPlanX = ProjectToPlane(plane.XAxis, worldZ);
    if (worldPlanX.IsTiny())
    {
      worldPlanX = ProjectToPlane(Unit(viewport.CameraX), worldZ);
      if (worldPlanX.IsTiny())
        worldPlanX = Vector3d.XAxis;
    }

    worldPlanX = Unit(worldPlanX);
    var azimuth = Math.Atan2(worldPlanX.Y, worldPlanX.X);

    var basisX = ProjectToPlane(Vector3d.XAxis, viewDirection);
    if (basisX.IsTiny())
      basisX = ProjectToPlane(Unit(viewport.CameraX), viewDirection);
    if (basisX.IsTiny())
      basisX = Vector3d.XAxis;
    basisX = Unit(basisX);

    var basisY = ProjectToPlane(Vector3d.YAxis, viewDirection);
    if (basisY.IsTiny())
      basisY = Unit(Vector3d.CrossProduct(viewDirection, basisX));
    if (basisY.IsTiny())
      basisY = Unit(viewport.CameraY);
    if (basisY.IsTiny())
      basisY = Vector3d.YAxis;
    basisY = Unit(basisY);

    var xAxis = Unit((Math.Cos(azimuth) * basisX) + (Math.Sin(azimuth) * basisY));
    if (xAxis.IsTiny())
      xAxis = basisX;

    var yAxis = Unit(Vector3d.CrossProduct(viewDirection, xAxis));
    if (yAxis.IsTiny())
      yAxis = Unit(viewport.CameraY);
    if (yAxis.IsTiny())
      yAxis = Vector3d.YAxis;

    var cameraUp = Unit(viewport.CameraY);
    if (!cameraUp.IsTiny() && (yAxis * cameraUp) < 0)
    {
      xAxis = -xAxis;
      yAxis = -yAxis;
    }

    return new Plane(plane.Origin, xAxis, yAxis);
  }

  private static bool IsPerspectiveMode(RhinoViewport viewport)
  {
    return viewport.IsPerspectiveProjection || IsPerspectiveNamedViewport(viewport);
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

  private sealed class StatusBadgeConduit : DisplayConduit
  {
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (!ShouldDrawBadge(e))
        return;

      try
      {
        DrawBadge(e);
      }
      catch
      {
      }
    }
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
