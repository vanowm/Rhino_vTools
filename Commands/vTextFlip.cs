using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vTextFlip command bridged from TextFlip.py.
/// </summary>
public sealed class vTextFlip : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTextFlip";

  /// <summary>
  /// Runs TextFlip script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("TextFlip.py", EnglishName);
  }
}
