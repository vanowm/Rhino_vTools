using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// vScallop command bridged from Scallop.py.
/// </summary>
public sealed class vScallop : Command
{
  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vScallop";

  /// <summary>
  /// Runs Scallop script behavior.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    return vPythonScriptCommandRunner.Run("Scallop.py", EnglishName);
  }
}
