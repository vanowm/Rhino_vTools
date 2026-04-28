using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vLineLength command bridged from LineLength.py.
/// </summary>
public sealed class vLineLength : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLineLength";

  /// <summary>
  /// Runs LineLength script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("LineLength.py", EnglishName);
  }
}
