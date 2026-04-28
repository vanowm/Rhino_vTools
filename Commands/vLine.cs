using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vLine command bridged from LinePlus.py.
/// </summary>
public sealed class vLine : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLine";

  /// <summary>
  /// Runs LinePlus script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("LinePlus.py", EnglishName);
  }
}
