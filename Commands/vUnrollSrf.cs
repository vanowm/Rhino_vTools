using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Runs the built-in UnrollSrf command and selects all newly created objects
/// on completion (flat surfaces, curves, and any labels).
/// </summary>
public sealed class vUnrollSrf : Command
{
  private static bool _restartingAfterDelegate;
  private static EventHandler? _pendingIdleHandler;
  private static HashSet<Guid>? _snapshot;

  public override string EnglishName => "vUnrollSrf";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating — registers vUnrollSrf as the
    // repeatable last command without running anything.
    if (_restartingAfterDelegate)
    {
      _restartingAfterDelegate = false;
      return Result.Success;
    }

    // Snapshot before UnrollSrf adds anything.
    _snapshot = new HashSet<Guid>(
      doc.Objects.GetObjectList(new ObjectEnumeratorSettings
      {
        IncludeGrips   = false,
        DeletedObjects = false
      }).Select(o => o.Id));

    CancelPending();
    _pendingIdleHandler = OnIdleLaunch;
    RhinoApp.Idle += _pendingIdleHandler;
    return Result.Success;
  }

  /// <summary>
  /// Returns true if <paramref name="pt"/> is within <paramref name="tolerance"/>
  /// of any geometry in <paramref name="objects"/>.
  /// </summary>
  private static bool IsTouchingAny(Point3d pt, IList<RhinoObject> objects, double tolerance)
  {
    foreach (var obj in objects)
    {
      switch (obj.Geometry)
      {
        case Brep brep:
          if (pt.DistanceTo(brep.ClosestPoint(pt)) <= tolerance) return true;
          break;
        case Curve curve:
          if (curve.ClosestPoint(pt, out double t) &&
              pt.DistanceTo(curve.PointAt(t)) <= tolerance) return true;
          break;
        case Surface srf:
          if (srf.ClosestPoint(pt, out double u, out double v) &&
              pt.DistanceTo(srf.PointAt(u, v)) <= tolerance) return true;
          break;
      }
    }
    return false;
  }

  private static bool IsUnrollableSurfaceObject(RhinoObject obj)
    => obj.Geometry is Brep or Surface;

  private static bool IsPointTouchingSurfaceObject(Point3d pt, RhinoObject surfaceObject, double tolerance)
  {
    switch (surfaceObject.Geometry)
    {
      case Brep brep:
        return pt.DistanceTo(brep.ClosestPoint(pt)) <= tolerance;

      case Surface srf:
        return srf.ClosestPoint(pt, out double u, out double v) &&
               pt.DistanceTo(srf.PointAt(u, v)) <= tolerance;

      default:
        return false;
    }
  }

  private static bool BoundingBoxesOverlap(BoundingBox a, BoundingBox b)
    => a.IsValid && b.IsValid &&
       a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
       a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
       a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

  private static bool IsObjectTouchingSurfaceObject(RhinoObject obj, RhinoObject surfaceObject, double tolerance)
  {
    if (obj.Id == surfaceObject.Id)
      return false;

    var objBox = obj.Geometry.GetBoundingBox(true);
    var srfBox = surfaceObject.Geometry.GetBoundingBox(true);
    objBox.Inflate(tolerance);
    srfBox.Inflate(tolerance);
    if (!BoundingBoxesOverlap(objBox, srfBox))
      return false;

    switch (obj.Geometry)
    {
      case Rhino.Geometry.Point point:
        return IsPointTouchingSurfaceObject(point.Location, surfaceObject, tolerance);

      case Curve curve:
      {
        if (IsPointTouchingSurfaceObject(curve.PointAtStart, surfaceObject, tolerance) ||
            IsPointTouchingSurfaceObject(curve.PointAtEnd, surfaceObject, tolerance))
          return true;

        const int sampleCount = 16;
        var domain = curve.Domain;
        for (var i = 1; i < sampleCount; i++)
        {
          var t = domain.ParameterAt(i / (double)sampleCount);
          if (IsPointTouchingSurfaceObject(curve.PointAt(t), surfaceObject, tolerance))
            return true;
        }

        return false;
      }

      case Brep brep:
        return brep.Vertices.Any(v => IsPointTouchingSurfaceObject(v.Location, surfaceObject, tolerance));

      case Surface srf:
      {
        var u = srf.Domain(0);
        var v = srf.Domain(1);
        var corners = new[]
        {
          srf.PointAt(u.Min, v.Min),
          srf.PointAt(u.Max, v.Min),
          srf.PointAt(u.Min, v.Max),
          srf.PointAt(u.Max, v.Max)
        };
        return corners.Any(pt => IsPointTouchingSurfaceObject(pt, surfaceObject, tolerance));
      }

      default:
        return false;
    }
  }

  private static bool TryRunPreselectedSurfaceUnrolls(RhinoDoc doc, double tolerance, out bool handled)
  {
    handled = false;

    var selected = doc.Objects
      .GetSelectedObjects(false, false)
      .Where(o => o != null)
      .ToList();

    var surfaces = selected
      .Where(IsUnrollableSurfaceObject)
      .ToList();

    if (surfaces.Count <= 1)
      return false;

    handled = true;
    var anySucceeded = false;

    foreach (var surface in surfaces)
    {
      doc.Objects.UnselectAll();
      surface.Select(true);

      foreach (var obj in selected)
      {
        if (obj.Id == surface.Id)
          continue;

        if (IsUnrollableSurfaceObject(obj))
          continue;

        if (IsObjectTouchingSurfaceObject(obj, surface, tolerance))
          obj.Select(true);
      }

      doc.Views.Redraw();

      if (!RhinoApp.RunScript("_UnrollSrf", false))
        break;

      anySucceeded = true;
    }

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    return anySucceeded;
  }

  private static void CancelPending()  {
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }
  }

  private static void OnIdleLaunch(object? sender, EventArgs e)
  {
    CancelPending();

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null) return;

    var snapshot = _snapshot ?? new HashSet<Guid>();
    _snapshot = null;

    var snapTol = doc.ModelAbsoluteTolerance * 10.0;
    var ok = TryRunPreselectedSurfaceUnrolls(doc, snapTol, out var handledPreselectedSurfaces);
    if (!handledPreselectedSurfaces)
      ok = RhinoApp.RunScript("_UnrollSrf", false);

    var startOrient2pt = false;

    if (ok)
    {
      var allNew = doc.Objects
        .GetObjectList(new ObjectEnumeratorSettings
        {
          IncludeGrips   = false,
          DeletedObjects = false,
          VisibleFilter  = true
        })
        .Where(o => !snapshot.Contains(o.Id))
        .ToList();

      // Flat geometry (surfaces, curves, etc.) — always selected.
      var flatGeom = allNew.Where(o => o.Geometry is not TextDot).ToList();

      // TextDots: UnrollSrf places them on both the flat result AND the original
      // 3D surface.  Only include dots whose position touches a newly created
      // flat object — that filters out the 3D-surface correspondence labels.
      var flatDots = allNew
        .Where(o => o.Geometry is TextDot td && IsTouchingAny(td.Point, flatGeom, snapTol))
        .ToList();

      var toSelect = flatGeom.Concat(flatDots).ToList();
      if (toSelect.Count > 0)
      {
        if (toSelect.Count > 1)
        {
          var groupIndex = doc.Groups.Add($"vUnrollSrf {DateTime.Now:yyyyMMdd HHmmss}");
          if (groupIndex >= 0)
          {
            foreach (var obj in toSelect)
              doc.Groups.AddToGroup(groupIndex, obj.Id);
          }
        }

        doc.Objects.UnselectAll();
        foreach (var obj in toSelect)
          obj.Select(true);
        doc.Views.Redraw();

        startOrient2pt = true;
      }
    }

    if (startOrient2pt)
      _ = RhinoApp.RunScript("_vOrient2pt", false);

    // Silently re-run vUnrollSrf so pressing Enter repeats it, not _UnrollSrf or vOrient2pt.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vUnrollSrf", false);
    _restartingAfterDelegate = false;
  }
}
