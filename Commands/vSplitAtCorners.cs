using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vSplitAtCorners command bridged from SplitAtCorners.py.
/// </summary>
public sealed class vSplitAtCorners : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vSplitAtCorners";

  /// <summary>
  /// Runs SplitAtCorners script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("SplitAtCorners.py", EnglishName);
  }
}
