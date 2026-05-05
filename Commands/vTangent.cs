using System;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Moves a curve rigidly so that one (or both) of its endpoints aligns tangentially
/// to selected driver curves. Ported from Tangent.py.
///
/// Workflow:
///   S1 — pick near the end of the subject curve you want aligned to D1.
///   D1 — pick the driver curve; the tangent at the pick point is used.
///   S2 — pick the OTHER end of the same subject curve (optional second alignment).
///   D2 — pick a second driver curve (optional; press Enter to skip).
///
/// With only D1: subject is translated+rotated so S1-end tangent matches D1 tangent.
/// With D1+D2:   a twist is also applied to minimise the rotation needed to also
///               match the S2-end tangent to D2 (or its reverse).
/// </summary>
public sealed class vTangent : Command
{
  public override string EnglishName => "vTangent";

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static (ObjRef? Ref, CurveEnd End) PickSubjectEnd(string prompt, Guid? requiredId = null)
  {
    var go = new GetObject();
    go.SetCommandPrompt(prompt);
    go.GeometryFilter  = ObjectType.Curve;
    go.EnablePreSelect(false, true);
    go.SubObjectSelect = false;
    go.AcceptNothing(false);

    var res = go.Get();
    if (res != GetResult.Object || go.CommandResult() != Result.Success)
      return (null, CurveEnd.None);

    var objRef = go.Object(0);
    var curve  = objRef.Curve();
    if (curve == null)
    {
      RhinoApp.WriteLine("vTangent: invalid curve selection.");
      return (null, CurveEnd.None);
    }
    if (curve.IsClosed)
    {
      RhinoApp.WriteLine("vTangent: closed curves are not supported.");
      return (null, CurveEnd.None);
    }
    if (requiredId.HasValue && objRef.ObjectId != requiredId.Value)
    {
      RhinoApp.WriteLine("vTangent: S2 must be picked on the same subject curve as S1.");
      return (null, CurveEnd.None);
    }

    var pick = objRef.SelectionPoint();
    if (!pick.IsValid) pick = curve.PointAtStart;

    double dStart = pick.DistanceTo(curve.PointAtStart);
    double dEnd   = pick.DistanceTo(curve.PointAtEnd);
    var end = dStart <= dEnd ? CurveEnd.Start : CurveEnd.End;

    return (objRef, end);
  }

  private static (Curve? Curve, Point3d Pick) PickDriverCurve(string prompt, bool allowNone)
  {
    var go = new GetObject();
    go.SetCommandPrompt(prompt);
    go.GeometryFilter  = ObjectType.Curve;
    go.EnablePreSelect(false, true);
    go.SubObjectSelect = false;
    go.AcceptNothing(allowNone);

    var res = go.Get();
    if (go.CommandResult() != Result.Success)
      return (null, Point3d.Unset);
    if (allowNone && res == GetResult.Nothing)
      return (null, Point3d.Unset); // skipped — not a cancel

    if (res != GetResult.Object)
      return (null, Point3d.Unset);

    var objRef = go.Object(0);
    var curve  = objRef.Curve();
    if (curve == null)
    {
      RhinoApp.WriteLine("vTangent: invalid driver curve.");
      return (null, Point3d.Unset);
    }

    var pick = objRef.SelectionPoint();
    if (!pick.IsValid) pick = curve.PointAt(curve.Domain.Mid);

    return (curve, pick);
  }

  private static (Point3d Pt, Vector3d Tan)? DriverTangentAtPick(Curve curve, Point3d pickPt)
  {
    if (!curve.ClosestPoint(pickPt, out double t)) return null;
    var tan = curve.TangentAt(t);
    if (!tan.IsValid) return null;
    if (!tan.Unitize()) return null;
    return (curve.PointAt(t), tan);
  }

  private static (Point3d Pt, Vector3d Tan)? CurveEndData(Curve curve, CurveEnd end)
  {
    var pt  = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
    var tan = end == CurveEnd.Start ? curve.TangentAtStart : curve.TangentAtEnd;
    if (!tan.IsValid) return null;
    if (!tan.Unitize()) return null;
    return (pt, tan);
  }

  private static Vector3d ProjectToPlane(Vector3d v, Vector3d axisUnit)
  {
    var proj = new Vector3d(v);
    proj -= axisUnit * (proj * axisUnit);
    return proj;
  }

  private static double? SignedAngleAboutAxis(Vector3d from, Vector3d to, Vector3d axisUnit)
  {
    var p1 = ProjectToPlane(from, axisUnit);
    var p2 = ProjectToPlane(to,   axisUnit);
    if (p1.IsTiny() || p2.IsTiny()) return null;
    p1.Unitize(); p2.Unitize();
    double angle = Vector3d.VectorAngle(p1, p2);
    var cross    = Vector3d.CrossProduct(p1, p2);
    if (cross * axisUnit < 0.0) angle = -angle;
    return angle;
  }

  /// <summary>
  /// Build the rigid transform that maps the subject curve so that:
  ///   • s1End tangent aligns to d1Tangent at d1Point
  ///   • (optionally) s2End tangent aligns as closely as possible to d2Tangent
  /// </summary>
  private static Transform? ComposeTransform(
    Curve subjectCurve,
    CurveEnd s1End, Point3d d1Point, Vector3d d1Tangent,
    CurveEnd s2End = CurveEnd.None, Vector3d d2Tangent = default)
  {
    bool hasD2 = s2End != CurveEnd.None && d2Tangent != default;

    var s1Data = CurveEndData(subjectCurve, s1End);
    if (s1Data == null) return null;
    var (s1Pt, s1Tan) = s1Data.Value;

    // Flip driver tangent if it points "away" from the subject tangent direction.
    if (s1Tan * d1Tangent < 0.0)
      d1Tangent = -d1Tangent;

    var rot1  = Transform.Rotation(s1Tan, d1Tangent, s1Pt);
    var xform = rot1;

    if (hasD2)
    {
      var s2Data = CurveEndData(subjectCurve, s2End);
      if (s2Data == null) return null;
      var (_, s2Tan) = s2Data.Value;

      var s2After = s2Tan;
      s2After.Transform(rot1);

      var axis = d1Tangent;
      if (!axis.Unitize()) return null;

      double? angleA = SignedAngleAboutAxis(s2After,  d2Tangent, axis);
      double? angleB = SignedAngleAboutAxis(s2After, -d2Tangent, axis);

      double? angle = (angleA, angleB) switch
      {
        (null, null)  => null,
        (null, _)     => angleB,
        (_, null)     => angleA,
        _             => Math.Abs(angleA!.Value) <= Math.Abs(angleB!.Value) ? angleA : angleB,
      };

      if (angle.HasValue)
      {
        var rot2 = Transform.Rotation(angle.Value, axis, s1Pt);
        xform = rot2 * xform;
      }
    }

    var trans = Transform.Translation(d1Point - s1Pt);
    return trans * xform;
  }

  // ── RunCommand ────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Step 1: pick S1 (end of subject curve aligned to D1).
    var (s1Ref, endForD1) = PickSubjectEnd("Select S1 (subject curve near end for D1)");
    if (s1Ref == null) return Result.Cancel;

    // Step 2: pick D1 (required driver curve).
    var (d1Curve, d1Pick) = PickDriverCurve("Select D1 curve (required)", allowNone: false);
    if (d1Curve == null) return Result.Cancel;

    // Step 3: pick S2 (other end of same subject curve for optional second alignment).
    var (s2Ref, endForD2) = PickSubjectEnd(
      "Select S2 on the same subject curve (near end for D2)",
      requiredId: s1Ref.ObjectId);
    if (s2Ref == null) return Result.Cancel;

    // Step 4: pick D2 (optional second driver; Enter to skip).
    var (d2Curve, d2Pick) = PickDriverCurve("Select D2 curve (optional) or press Enter", allowNone: true);
    // d2Curve == null and d2Pick == Unset means user pressed Enter — that's fine, not a cancel.
    // But if D2 selection was cancelled (Esc), CommandResult != Success — handled inside PickDriverCurve.

    // Evaluate tangents.
    var subjectCurve = s1Ref.Curve();
    if (subjectCurve == null)
    {
      RhinoApp.WriteLine("vTangent: failed to read subject curve.");
      return Result.Failure;
    }

    var d1Result = DriverTangentAtPick(d1Curve, d1Pick);
    if (d1Result == null)
    {
      RhinoApp.WriteLine("vTangent: could not evaluate tangent on D1 curve.");
      return Result.Failure;
    }
    var (d1Point, d1Tan) = d1Result.Value;

    Vector3d d2Tan = default;
    if (d2Curve != null)
    {
      var d2Result = DriverTangentAtPick(d2Curve, d2Pick);
      if (d2Result == null)
      {
        RhinoApp.WriteLine("vTangent: could not evaluate tangent on D2 curve.");
        return Result.Failure;
      }
      d2Tan = d2Result.Value.Tan;
    }

    if (d2Curve != null && endForD2 == endForD1)
    {
      RhinoApp.WriteLine("vTangent: S2 was picked on the same end as S1. Pick the opposite end for D2.");
      return Result.Failure;
    }

    // Compose transform.
    var xform = ComposeTransform(
      subjectCurve,
      endForD1, d1Point, d1Tan,
      d2Curve != null ? endForD2 : CurveEnd.None,
      d2Curve != null ? d2Tan    : default);

    if (xform == null)
    {
      RhinoApp.WriteLine("vTangent: failed to compute transform.");
      return Result.Failure;
    }

    // Apply.
    var outCurve = subjectCurve.DuplicateCurve();
    if (outCurve == null)
    {
      RhinoApp.WriteLine("vTangent: failed to duplicate subject curve.");
      return Result.Failure;
    }
    if (!outCurve.Transform(xform.Value))
    {
      RhinoApp.WriteLine("vTangent: failed to transform subject curve.");
      return Result.Failure;
    }
    if (!doc.Objects.Replace(s1Ref.ObjectId, outCurve))
    {
      RhinoApp.WriteLine("vTangent: failed to update subject curve in document.");
      return Result.Failure;
    }

    doc.Views.Redraw();

    RhinoApp.WriteLine(d2Curve == null
      ? "vTangent: curve moved rigidly — S1 tangent aligned to D1."
      : "vTangent: curve moved rigidly — S1 and S2 tangents aligned.");

    return Result.Success;
  }
}
