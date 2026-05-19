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

    var ok = RhinoApp.RunScript("_UnrollSrf", false);

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
      var snapTol = doc.ModelAbsoluteTolerance * 10.0;
      var flatDots = allNew
        .Where(o => o.Geometry is TextDot td && IsTouchingAny(td.Point, flatGeom, snapTol))
        .ToList();

      var toSelect = flatGeom.Concat(flatDots).ToList();
      if (toSelect.Count > 0)
      {
        doc.Objects.UnselectAll();
        foreach (var obj in toSelect)
          obj.Select(true);
        doc.Views.Redraw();
      }
    }

    // Silently re-run vUnrollSrf so pressing Enter repeats it, not _UnrollSrf.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vUnrollSrf", false);
    _restartingAfterDelegate = false;
  }
}
