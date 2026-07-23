using System;
using System.IO;
using System.Windows.Media;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Configures and toggles an audible notification when a Rhino command fails.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vCommandFailSound : Command
{
  public override string EnglishName => "vCommandFailSound";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    CommandFailSoundMonitor.EnsureSettingsLoaded();

    while (true)
    {
      using var getter = new GetOption();
      getter.EnableTransparentCommands(true);
      getter.AcceptNothing(true);
      getter.SetCommandPrompt(CommandFailSoundMonitor.GetPrompt());

      var soundOptionIndex = getter.AddOptionList(
        "Sound",
        CommandFailSoundMonitor.SoundNames,
        CommandFailSoundMonitor.SoundIndex);
      var audioFileOptionIndex = getter.AddOption("AudioFile");
      var previewOptionIndex = getter.AddOption("Preview");

      var getResult = getter.Get();
      if (getResult == GetResult.Cancel)
        return Result.Cancel;

      if (getResult == GetResult.Nothing)
      {
        var enabled = CommandFailSoundMonitor.Toggle();
        RhinoApp.WriteLine(
          enabled
            ? "Command failure sound: ON"
            : "Command failure sound: OFF");
        return Result.Success;
      }

      if (getResult != GetResult.Option || getter.Option() == null)
        return getter.CommandResult();

      var option = getter.Option()!;
      if (option.Index == soundOptionIndex)
      {
        CommandFailSoundMonitor.SetSound(option.CurrentListOptionIndex);
      }
      else if (option.Index == audioFileOptionIndex)
      {
        if (TryChooseAudioFile(CommandFailSoundMonitor.AudioFile, out var audioFile))
          CommandFailSoundMonitor.SetAudioFile(audioFile);
      }
      else if (option.Index == previewOptionIndex)
      {
        CommandFailSoundMonitor.Preview();
      }
    }
  }

  private static bool TryChooseAudioFile(string currentPath, out string audioFile)
  {
    audioFile = string.Empty;

    var dialog = new OpenFileDialog
    {
      Title = "Select command failure sound",
      Filter =
        "Audio files (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a|" +
        "All files (*.*)|*.*",
      DefaultExt = "wav",
      MultiSelect = false
    };

    if (!string.IsNullOrWhiteSpace(currentPath))
    {
      try
      {
        var fullPath = Path.GetFullPath(currentPath);
        dialog.FileName = fullPath;
        dialog.InitialDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
      }
      catch
      {
      }
    }

    if (!dialog.ShowOpenDialog() || string.IsNullOrWhiteSpace(dialog.FileName))
      return false;

    audioFile = Path.GetFullPath(dialog.FileName);
    return true;
  }
}

internal static class CommandFailSoundMonitor
{
  private const string Tag = "vCommandFailSound";
  private const string SettingsSection = "vCommandFailSound";
  private const string SoundKey = "sound";
  private const string AudioFileKey = "audioFile";

  internal static readonly string[] SoundNames =
  {
    "Default",
    "Asterisk",
    "Exclamation",
    "Hand",
    "Question",
    "Custom"
  };

  private static readonly object Sync = new();
  private static bool _enabled;
  private static bool _settingsLoaded;
  private static int _soundIndex;
  private static string _audioFile = string.Empty;
  private static MediaPlayer? _mediaPlayer;

  internal static int SoundIndex
  {
    get
    {
      lock (Sync)
        return _soundIndex;
    }
  }

  internal static string AudioFile
  {
    get
    {
      lock (Sync)
        return _audioFile;
    }
  }

  internal static void EnsureSettingsLoaded()
  {
    lock (Sync)
    {
      if (_settingsLoaded)
        return;

      var loaded = ToolsOptionStore.Read(
        SettingsSection,
        section =>
        {
          var soundIndex = 0;
          var audioFile = string.Empty;

          if (ToolsOptionStore.TryGetString(section, SoundKey, out var soundName))
          {
            var persistedIndex = Array.FindIndex(
              SoundNames,
              candidate => string.Equals(candidate, soundName, StringComparison.OrdinalIgnoreCase));
            if (persistedIndex >= 0)
              soundIndex = persistedIndex;
          }

          if (ToolsOptionStore.TryGetString(section, AudioFileKey, out var persistedFile))
            audioFile = persistedFile;

          return (soundIndex, audioFile);
        });

      _soundIndex = loaded.soundIndex;
      _audioFile = loaded.audioFile;
      _settingsLoaded = true;
    }
  }

  internal static string GetPrompt()
  {
    EnsureSettingsLoaded();

    lock (Sync)
    {
      var state = _enabled ? "ON" : "OFF";
      var sound = SoundNames[_soundIndex];
      if (_soundIndex == SoundNames.Length - 1 && !string.IsNullOrWhiteSpace(_audioFile))
        sound += $": {Path.GetFileName(_audioFile)}";

      return $"Command failure sound is {state} ({sound}). Press Enter to toggle";
    }
  }

  internal static bool Toggle()
  {
    EnsureSettingsLoaded();

    lock (Sync)
    {
      if (_enabled)
        Disable();
      else
        Enable();

      return _enabled;
    }
  }

  internal static void SetSound(int soundIndex)
  {
    EnsureSettingsLoaded();

    lock (Sync)
      _soundIndex = Math.Clamp(soundIndex, 0, SoundNames.Length - 1);

    SaveSettings();
    Log.Write(Tag, $"sound -> {SoundNames[SoundIndex]}");
  }

  internal static void SetAudioFile(string audioFile)
  {
    EnsureSettingsLoaded();

    lock (Sync)
    {
      _audioFile = audioFile;
      _soundIndex = SoundNames.Length - 1;
    }

    SaveSettings();
    Log.Write(Tag, $"audio file -> {audioFile}");
  }

  internal static void Preview()
  {
    EnsureSettingsLoaded();
    _ = PlaySelectedSound(true);
  }

  internal static void Stop()
  {
    lock (Sync)
    {
      Disable();
      CloseMediaPlayer();
    }
  }

  private static void Enable()
  {
    if (_enabled)
      return;

    Command.EndCommand -= OnCommandEnded;
    Command.EndCommand += OnCommandEnded;
    _enabled = true;
    Log.Write(Tag, "watcher enabled");
  }

  private static void Disable()
  {
    Command.EndCommand -= OnCommandEnded;
    if (_enabled)
      Log.Write(Tag, "watcher disabled");
    _enabled = false;
  }

  private static void OnCommandEnded(object? sender, CommandEventArgs e)
  {
    if (!_enabled || !IsFailure(e.CommandResult))
      return;

    Log.Write(
      Tag,
      $"failure command={e.CommandEnglishName} result={e.CommandResult}");
    _ = PlaySelectedSound(false);
  }

  private static bool IsFailure(Result result) =>
    result != Result.Success &&
    result != Result.Cancel;

  private static bool PlaySelectedSound(bool preview)
  {
    if (!OperatingSystem.IsWindows())
      return false;

    int soundIndex;
    string audioFile;
    lock (Sync)
    {
      soundIndex = _soundIndex;
      audioFile = _audioFile;
    }

    if (soundIndex == SoundNames.Length - 1)
    {
      if (TryPlayAudioFile(audioFile))
        return true;

      if (preview)
        RhinoApp.WriteLine("Command failure sound: select an existing audio file.");

      PlaySystemSound(0);
      return false;
    }

    return PlaySystemSound(soundIndex);
  }

  private static bool TryPlayAudioFile(string audioFile)
  {
    if (string.IsNullOrWhiteSpace(audioFile) || !File.Exists(audioFile))
    {
      Log.Write(Tag, $"audio file unavailable: {audioFile}");
      return false;
    }

    try
    {
      CloseMediaPlayer();

      var player = new MediaPlayer();
      player.MediaEnded += (_, _) => ReleaseMediaPlayer(player);
      player.MediaFailed += (_, args) =>
      {
        Log.Write(Tag, $"audio playback failed: {args.ErrorException.Message}");
        ReleaseMediaPlayer(player);
        _ = PlaySystemSound(0);
      };

      lock (Sync)
        _mediaPlayer = player;

      player.Open(new Uri(Path.GetFullPath(audioFile), UriKind.Absolute));
      player.Play();
      return true;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"audio playback failed: {ex.Message}");
      CloseMediaPlayer();
      return false;
    }
  }

  private static bool PlaySystemSound(int soundIndex)
  {
    if (!OperatingSystem.IsWindows())
      return false;

    CloseMediaPlayer();

    try
    {
      var sound = soundIndex switch
      {
        1 => System.Media.SystemSounds.Asterisk,
        2 => System.Media.SystemSounds.Exclamation,
        3 => System.Media.SystemSounds.Hand,
        4 => System.Media.SystemSounds.Question,
        _ => System.Media.SystemSounds.Beep
      };

      sound.Play();
      return true;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"system sound failed: {ex.Message}");
    }

    try
    {
      Console.Beep(1200, 120);
      return true;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"console beep failed: {ex.Message}");
      return false;
    }
  }

  private static void ReleaseMediaPlayer(MediaPlayer player)
  {
    lock (Sync)
    {
      if (!ReferenceEquals(_mediaPlayer, player))
        return;

      _mediaPlayer = null;
    }

    player.Close();
  }

  private static void CloseMediaPlayer()
  {
    MediaPlayer? player;
    lock (Sync)
    {
      player = _mediaPlayer;
      _mediaPlayer = null;
    }

    if (player == null)
      return;

    player.Stop();
    player.Close();
  }

  private static void SaveSettings()
  {
    int soundIndex;
    string audioFile;
    lock (Sync)
    {
      soundIndex = _soundIndex;
      audioFile = _audioFile;
    }

    var saved = ToolsOptionStore.Update(SettingsSection, section =>
    {
      section[SoundKey] = SoundNames[soundIndex];
      section[AudioFileKey] = audioFile;
    });

    if (!saved)
      RhinoApp.WriteLine($"vCommandFailSound: failed to save options: {ToolsOptionStore.LastError}");
  }
}
