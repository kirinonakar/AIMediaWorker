namespace AIMediaWorker.Settings;

public static class ShortcutActions
{
    public const string PlayPause = "PlayPause";
    public const string SeekBackward = "SeekBackward";
    public const string SeekForward = "SeekForward";
    public const string PreviousSubtitle = "PreviousSubtitle";
    public const string NextSubtitle = "NextSubtitle";
    public const string SaveSubtitle = "SaveSubtitle";
    public const string SaveSubtitleAs = "SaveSubtitleAs";
    public const string Undo = "Undo";
    public const string Redo = "Redo";
    public const string DeleteCue = "DeleteCue";
    public const string Fullscreen = "Fullscreen";
    public const string ToggleSubtitles = "ToggleSubtitles";
    public const string CloseWindow = "CloseWindow";

    public static Dictionary<string, string> CreateDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [PlayPause] = "Space",
        [SeekBackward] = "Left",
        [SeekForward] = "Right",
        [PreviousSubtitle] = "Ctrl+Left",
        [NextSubtitle] = "Ctrl+Right",
        [SaveSubtitle] = "Ctrl+S",
        [SaveSubtitleAs] = "Ctrl+Shift+S",
        [Undo] = "Ctrl+Z",
        [Redo] = "Ctrl+Y",
        [DeleteCue] = "Delete",
        [Fullscreen] = "Enter",
        [ToggleSubtitles] = "V",
        [CloseWindow] = "Ctrl+W"
    };
}

public static class ShortcutGesture
{
    public static bool Matches(string? gesture, string key, bool control, bool shift, bool alt)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var modifiers = parts[..^1];
        var expectedControl = modifiers.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase));
        var expectedShift = modifiers.Any(part => part.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var expectedAlt = modifiers.Any(part => part.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        return expectedControl == control && expectedShift == shift && expectedAlt == alt && parts[^1].Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
