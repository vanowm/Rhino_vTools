using System;
using System.Globalization;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using Rhino.Runtime.InteropWrappers;

namespace vTools.Commands;

internal static class HideSetState
{
  private const string Tag = "HideSetState";
  private const string TrackingKey = "vTools.HideSet";
  private const string TrackingOrderKey = "vTools.HideSetOrder";

  private static readonly MethodInfo? ObjectPointerMethod =
    typeof(RhinoObject).GetMethod(
      "ConstPointer",
      BindingFlags.Instance | BindingFlags.NonPublic);

  private static readonly Type? UnsafeNativeMethodsType =
    typeof(RhinoApp).Assembly.GetType("UnsafeNativeMethods", false);

  private static readonly MethodInfo? GetNameMethod =
    UnsafeNativeMethodsType?.GetMethod(
      "CRhinoObject_AttachHideGetName",
      BindingFlags.Static | BindingFlags.NonPublic);

  private static readonly MethodInfo? RemoveNameMethod =
    UnsafeNativeMethodsType?.GetMethod(
      "CRhinoObject_RemoveHideName",
      BindingFlags.Static | BindingFlags.NonPublic);

  public static bool NativeAccessAvailable =>
    ObjectPointerMethod != null &&
    GetNameMethod != null &&
    RemoveNameMethod != null;

  public static string GetTrackedName(RhinoObject obj) =>
    (obj.Attributes.GetUserString(TrackingKey) ?? string.Empty).Trim();

  public static long GetTrackedOrder(RhinoObject obj) =>
    long.TryParse(
      obj.Attributes.GetUserString(TrackingOrderKey),
      NumberStyles.Integer,
      CultureInfo.InvariantCulture,
      out var order)
      ? order
      : obj.RuntimeSerialNumber;

  public static bool SetTrackedName(
    RhinoDoc doc,
    Guid objectId,
    string hideSetName,
    long order = 0)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj == null)
      return false;

    var attributes = obj.Attributes.Duplicate();
    bool changed;
    if (string.IsNullOrEmpty(hideSetName))
    {
      changed = attributes.DeleteUserString(TrackingKey);
      changed = attributes.DeleteUserString(TrackingOrderKey) || changed;
    }
    else
    {
      var orderText = order.ToString(CultureInfo.InvariantCulture);
      changed = !string.Equals(
          attributes.GetUserString(TrackingKey),
          hideSetName,
          StringComparison.Ordinal) ||
        !string.Equals(
          attributes.GetUserString(TrackingOrderKey),
          orderText,
          StringComparison.Ordinal);
      attributes.SetUserString(TrackingKey, hideSetName);
      attributes.SetUserString(TrackingOrderKey, orderText);
    }

    return !changed || doc.Objects.ModifyAttributes(objectId, attributes, true);
  }

  public static bool TryGetNativeName(
    RhinoObject obj,
    out string hideSetName)
  {
    hideSetName = string.Empty;
    if (!TryGetPointer(obj, out var objectPointer) || GetNameMethod == null)
      return false;

    try
    {
      using var value = new StringHolder();
      var found = (bool)(GetNameMethod.Invoke(
        null,
        new object[] { objectPointer, value.NonConstPointer() }) ?? false);
      if (!found)
        return false;

      hideSetName = value.ToStringSafe() ?? string.Empty;
      return !string.IsNullOrEmpty(hideSetName);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  hide-set lookup failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }

  public static bool RemoveNativeName(RhinoObject obj)
  {
    if (!TryGetPointer(obj, out var objectPointer) || RemoveNameMethod == null)
      return false;

    try
    {
      return (bool)(RemoveNameMethod.Invoke(
        null,
        new object[] { objectPointer }) ?? false);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  hide-set removal failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }

  private static bool TryGetPointer(
    RhinoObject obj,
    out IntPtr objectPointer)
  {
    objectPointer = IntPtr.Zero;
    if (ObjectPointerMethod == null)
      return false;

    try
    {
      objectPointer =
        (IntPtr)(ObjectPointerMethod.Invoke(obj, null) ?? IntPtr.Zero);
      return objectPointer != IntPtr.Zero;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"  pointer lookup failed object={obj.Id}: {ex.Message}");
      return false;
    }
  }
}
