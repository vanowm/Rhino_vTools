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
/// Rotates curve A about its endpoint nearest to an endpoint of curve B so that
/// curve A is perpendicular to curve B at that B endpoint, measured in the active
/// CPlane. Ported from PerpTo.py.
///
/// Workflow:
///   Pick curve A — the curve to rotate.
///   Pick curve B — the reference curve (not moved).
///   The nearest endpoint pair is found automatically.
///   A is rotated about its near endpoint in the active CPlane until it is
///   perpendicular to B's tangent at B's near endpoint.
/// </summary>
public sealed class vPerpendicularTo : Command
{
  public override string EnglishName => "vPerpendicularTo";

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static (Guid Id, Curve? Curve, ObjRef? Ref) PickCurve(string prompt)
  {
    var go = new GetObject();
    go.SetCommandPrompt(prompt);
    go.GeometryFilter  = ObjectType.Curve;
    go.SubObjectSelect = true;
    go.EnablePreSelect(false, true);
    go.DeselectAllBeforePostSelect = false;

    var res = go.Get();
    if (res != GetResult.Object || go.ObjectCount < 1)
      return (Guid.Empty, null, null);

    var objRef = go.Object(0);
    if (objRef == null) return (Guid.Empty, null, null);

    var rObj = objRef.Object();
    if (rObj == null) return (Guid.Empty, null, null);

    var crv = objRef.Curve() ?? objRef.Geometry() as Curve;
    if (crv == null) return (Guid.Empty, null, null);

    return (rObj.Id, crv.DuplicateCurve(), objRef);
  }

  private static Vector3d TangentAtEndpoint(Curve curve, bool atStart)
  {
    var tan = atStart ? curve.TangentAtStart : curve.TangentAtEnd;
    if (tan.IsTiny()) return Vector3d.Unset;
    tan.Unitize();
    return tan;
  }

  /// <summary>
  /// Returns (aAtStart, bAtStart) for the endpoint pair that is geometrically closest.
  /// </summary>
  private static (bool AAtStart, bool BAtStart) ClosestEndpointPair(Curve a, Curve b)
  {
    var (aS, aE) = (a.PointAtStart, a.PointAtEnd);
    var (bS, bE) = (b.PointAtStart, b.PointAtEnd);
    var pairs = new List<(bool AStart, bool BStart, double Dist)>
    {
      (true,  true,  aS.DistanceTo(bS)),
      (true,  false, aS.DistanceTo(bE)),
      (false, true,  aE.DistanceTo(bS)),
      (false, false, aE.DistanceTo(bE)),
    };
    pairs.Sort((x, y) => x.Dist.CompareTo(y.Dist));
    return (pairs[0].AStart, pairs[0].BStart);
  }

  private static Vector3d ProjectToPlane(Vector3d v, Vector3d normal)
    => v - normal * (v * normal);

  private static Vector3d Unitized(Vector3d v)
  {
    if (v.IsTiny()) return Vector3d.Unset;
    var u = new Vector3d(v);
    u.Unitize();
    return u;
  }

  /// <summary>
  /// Returns the perpendicular to bTangentInPlane (rotated 90° about cplaneZ) that
  /// is closest in angle to aTangentInPlane — i.e., picks the rotation direction
  /// that minimises the required sweep.
  /// </summary>
  private static Vector3d ChooseTargetPerpendicular(
    Vector3d bTanInPlane, Vector3d cplaneZ, Vector3d aTanInPlane)
  {
    var perp = Vector3d.CrossProduct(cplaneZ, bTanInPlane);
    if (perp.IsTiny()) return Vector3d.Unset;
    perp.Unitize();

    var perpAlt   = -perp;
    double angle1 = Vector3d.VectorAngle(aTanInPlane, perp);
    double angle2 = Vector3d.VectorAngle(aTanInPlane, perpAlt);
    return angle1 <= angle2 ? perp : perpAlt;
  }

  // ── RunCommand ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Pick A (curve to rotate).
    var (aId, curveA, _) = PickCurve("Select curve A (to rotate)");
    if (aId == Guid.Empty || curveA == null) return Result.Cancel;

    // Pick B (reference).
    var (bId, curveB, _) = PickCurve("Select curve B (reference, not moved)");
    if (bId == Guid.Empty || curveB == null) return Result.Cancel;

    // Find closest endpoint pair.
    var (aAtStart, bAtStart) = ClosestEndpointPair(curveA, curveB);
    var pivot    = aAtStart ? curveA.PointAtStart : curveA.PointAtEnd;
    var aTangent = TangentAtEndpoint(curveA, aAtStart);
    var bTangent = TangentAtEndpoint(curveB, bAtStart);

    if (aTangent == Vector3d.Unset || bTangent == Vector3d.Unset)
    {
      RhinoApp.WriteLine("vPerpendicularTo: could not evaluate endpoint tangent(s).");
      return Result.Failure;
    }

    // Active CPlane.
    var view    = doc.Views.ActiveView;
    var cplane  = view?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    var cplaneZ = cplane.ZAxis;
    if (cplaneZ.IsTiny()) cplaneZ = Vector3d.ZAxis;
    cplaneZ.Unitize();

    // Project tangents to CPlane for 2-D angle logic.
    var aInPlane = Unitized(ProjectToPlane(aTangent, cplaneZ));
    var bInPlane = Unitized(ProjectToPlane(bTangent, cplaneZ));

    if (aInPlane == Vector3d.Unset || bInPlane == Vector3d.Unset)
    {
      RhinoApp.WriteLine("vPerpendicularTo: tangents are parallel to the CPlane normal; use a different view/CPlane.");
      return Result.Failure;
    }

    var targetDir = ChooseTargetPerpendicular(bInPlane, cplaneZ, aInPlane);
    if (targetDir == Vector3d.Unset)
    {
      RhinoApp.WriteLine("vPerpendicularTo: could not build a stable perpendicular direction.");
      return Result.Failure;
    }

    double angle = Vector3d.VectorAngle(aInPlane, targetDir, cplane);
    var xform    = Transform.Rotation(angle, cplaneZ, pivot);

    var newId = doc.Objects.Transform(aId, xform, deleteOriginal: true);
    if (newId == Guid.Empty)
    {
      RhinoApp.WriteLine("vPerpendicularTo: failed to transform curve A.");
      return Result.Failure;
    }

    doc.Views.Redraw();
    return Result.Success;
  }
}
