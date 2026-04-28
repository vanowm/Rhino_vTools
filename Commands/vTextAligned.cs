using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vTextAligned command bridged from TextAligned.py.
/// </summary>
public sealed class vTextAligned : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTextAligned";

  /// <summary>
  /// Runs TextAligned script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("TextAligned.py", EnglishName);
  }
}
