namespace AIMediaWorker.Settings;

public static class ShortcutActions
{
    public const string PlayPause = "PlayPause";
    public const string PlayPauseAlternate = "PlayPauseAlternate";
    public const string PlayFromBeginning = "PlayFromBeginning";
    public const string PreviousMedia = "PreviousMedia";
    public const string NextMedia = "NextMedia";
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
    public const string ToggleTimelinePanel = "ToggleTimelinePanel";
    public const string ToggleSidePanel = "ToggleSidePanel";
    public const string CloseWindow = "CloseWindow";

    public static Dictionary<string, string> CreateDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [PlayPause] = "Space",
        [PlayPauseAlternate] = "Ctrl+P",
        [PlayFromBeginning] = "Ctrl+Shift+N",
        [PreviousMedia] = "Ctrl+B",
        [NextMedia] = "Ctrl+F",
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
        [ToggleTimelinePanel] = "Ctrl+1",
        [ToggleSidePanel] = "Ctrl+2",
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
        return expectedControl == control &&
               expectedShift == shift &&
               expectedAlt == alt &&
               NormalizeKey(parts[^1]).Equals(NormalizeKey(key), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length == 2 && normalized[0] is 'D' or 'd' && char.IsDigit(normalized[1])) return normalized[1].ToString();
        if (normalized.StartsWith("NumberPad", StringComparison.OrdinalIgnoreCase) &&
            normalized.Length == 10 && char.IsDigit(normalized[^1])) return normalized[^1].ToString();
        if (normalized.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) &&
            normalized.Length == 7 && char.IsDigit(normalized[^1])) return normalized[^1].ToString();
        if (normalized.StartsWith("Number", StringComparison.OrdinalIgnoreCase) &&
            normalized.Length == 7 && char.IsDigit(normalized[^1])) return normalized[^1].ToString();
        return normalized;
    }
}
