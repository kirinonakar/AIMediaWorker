using System.Collections.ObjectModel;

namespace AIMediaWorker.Subtitle.Editing;

public sealed class EditSubtitleTextCommand(SubtitleDocument document, SubtitleCue cue, string newText) : IUndoableSubtitleCommand
{
    private readonly string _oldText = cue.Text;
    public string Description => "Edit subtitle text";
    public void Execute() { cue.Text = newText; document.MarkDirty(); }
    public void Undo() { cue.Text = _oldText; document.MarkDirty(); }
}

public sealed class SetSubtitleTranslationCommand(SubtitleDocument document, SubtitleCue cue, string newTranslation) : IUndoableSubtitleCommand
{
    private readonly string? _oldTranslation = cue.TranslatedText;
    public string Description => "Translate subtitle";
    public void Execute() { cue.TranslatedText = newTranslation; document.MarkDirty(); }
    public void Undo() { cue.TranslatedText = _oldTranslation; document.MarkDirty(); }
}

public sealed class AddSubtitleCommand(SubtitleDocument document, ObservableCollection<SubtitleCue> cues, SubtitleCue cue, int index = -1) : IUndoableSubtitleCommand
{
    private int _actualIndex;
    public string Description => "Add subtitle";
    public void Execute()
    {
        _actualIndex = index < 0 ? cues.Count : Math.Min(index, cues.Count);
        cues.Insert(_actualIndex, cue);
        document.Sort();
        _actualIndex = cues.IndexOf(cue);
        document.MarkDirty();
    }
    public void Undo() { cues.Remove(cue); document.MarkDirty(); }
}

public sealed class DeleteSubtitleCommand(SubtitleDocument document, ObservableCollection<SubtitleCue> cues, IReadOnlyCollection<SubtitleCue> selected) : IUndoableSubtitleCommand
{
    private readonly (int Index, SubtitleCue Cue)[] _items = selected.Select(c => (cues.IndexOf(c), c)).Where(x => x.Item1 >= 0).OrderBy(x => x.Item1).ToArray();
    public string Description => _items.Length == 1 ? "Delete subtitle" : $"Delete {_items.Length} subtitles";
    public void Execute()
    {
        foreach (var item in _items.Reverse()) cues.RemoveAt(item.Index);
        document.MarkDirty();
    }
    public void Undo()
    {
        foreach (var item in _items) cues.Insert(Math.Min(item.Index, cues.Count), item.Cue);
        document.MarkDirty();
    }
}

public sealed class MoveSubtitleCommand(SubtitleDocument document, SubtitleCue cue, long newStartMicroseconds, long newEndMicroseconds) : IUndoableSubtitleCommand
{
    private readonly long _oldStart = cue.StartMicroseconds;
    private readonly long _oldEnd = cue.EndMicroseconds;
    public string Description => "Move subtitle";
    public void Execute() { Set(newStartMicroseconds, newEndMicroseconds); }
    public void Undo() { Set(_oldStart, _oldEnd); }
    private void Set(long start, long end)
    {
        if (start < 0 || end <= start) throw new ArgumentOutOfRangeException(nameof(start));
        cue.StartMicroseconds = start;
        cue.EndMicroseconds = end;
        document.Sort();
        document.MarkDirty();
    }
}

public sealed class BatchShiftCommand(SubtitleDocument document, IReadOnlyCollection<SubtitleCue> cues, long deltaMicroseconds) : IUndoableSubtitleCommand
{
    public string Description => "Shift subtitles";
    public void Execute() => Shift(deltaMicroseconds);
    public void Undo() => Shift(-deltaMicroseconds);
    private void Shift(long delta)
    {
        var updates = new List<(SubtitleCue Cue, long Start, long End)>(cues.Count);
        foreach (var cue in cues)
        {
            long start;
            long end;
            try
            {
                start = checked(cue.StartMicroseconds + delta);
                end = checked(cue.EndMicroseconds + delta);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Shift exceeds the supported subtitle time range.");
            }
            if (start < 0 || end <= start) throw new InvalidOperationException("Shift would move a subtitle before zero.");
            updates.Add((cue, start, end));
        }
        foreach (var update in updates) { update.Cue.StartMicroseconds = update.Start; update.Cue.EndMicroseconds = update.End; }
        document.Sort();
        document.MarkDirty();
    }
}

public sealed class SplitSubtitleCommand : IUndoableSubtitleCommand
{
    private readonly SubtitleDocument _document;
    private readonly ObservableCollection<SubtitleCue> _cues;
    private readonly SubtitleCue _original;
    private readonly long _splitAt;
    private readonly string _firstText;
    private readonly string _secondText;
    private SubtitleCue? _second;

    public SplitSubtitleCommand(SubtitleDocument document, ObservableCollection<SubtitleCue> cues, SubtitleCue original, long splitAt, int? textIndex = null)
    {
        if (splitAt <= original.StartMicroseconds || splitAt >= original.EndMicroseconds) throw new ArgumentOutOfRangeException(nameof(splitAt));
        _document = document;
        _cues = cues;
        _original = original;
        _splitAt = splitAt;
        var index = Math.Clamp(textIndex ?? FindSplit(original.Text), 0, original.Text.Length);
        _firstText = original.Text[..index].TrimEnd();
        _secondText = original.Text[index..].TrimStart();
    }

    public string Description => "Split subtitle";
    public void Execute()
    {
        _second ??= new SubtitleCue { StartMicroseconds = _splitAt, EndMicroseconds = _original.EndMicroseconds, Text = _secondText, Style = _original.Style, Speaker = _original.Speaker, Confidence = _original.Confidence, Source = _original.Source };
        _original.EndMicroseconds = _splitAt;
        _original.Text = _firstText;
        _cues.Insert(_cues.IndexOf(_original) + 1, _second);
        _document.MarkDirty();
    }
    public void Undo()
    {
        if (_second is null) return;
        _cues.Remove(_second);
        _original.EndMicroseconds = _second.EndMicroseconds;
        _original.Text = string.IsNullOrEmpty(_firstText) ? _secondText : string.IsNullOrEmpty(_secondText) ? _firstText : $"{_firstText} {_secondText}";
        _document.MarkDirty();
    }
    private static int FindSplit(string text)
    {
        var center = text.Length / 2;
        for (var distance = 0; distance < center; distance++)
        {
            if (center + distance < text.Length && char.IsWhiteSpace(text[center + distance])) return center + distance;
            if (center - distance >= 0 && char.IsWhiteSpace(text[center - distance])) return center - distance;
        }
        return center;
    }
}

public sealed class MergeSubtitleCommand : IUndoableSubtitleCommand
{
    private readonly SubtitleDocument _document;
    private readonly ObservableCollection<SubtitleCue> _cues;
    private readonly SubtitleCue _first;
    private readonly SubtitleCue _second;
    private readonly string _oldText;
    private readonly long _oldEnd;
    private int _secondIndex;

    public MergeSubtitleCommand(SubtitleDocument document, ObservableCollection<SubtitleCue> cues, SubtitleCue first, SubtitleCue second)
    {
        _document = document;
        _cues = cues;
        _first = first;
        _second = second;
        _oldText = first.Text;
        _oldEnd = first.EndMicroseconds;
    }
    public string Description => "Merge subtitles";
    public void Execute()
    {
        _secondIndex = _cues.IndexOf(_second);
        if (_secondIndex < 0) throw new InvalidOperationException("Subtitle to merge is not in the track.");
        _first.EndMicroseconds = Math.Max(_first.EndMicroseconds, _second.EndMicroseconds);
        _first.Text = string.IsNullOrWhiteSpace(_first.Text) ? _second.Text : $"{_first.Text.TrimEnd()} {_second.Text.TrimStart()}";
        _cues.Remove(_second);
        _document.MarkDirty();
    }
    public void Undo()
    {
        _first.Text = _oldText;
        _first.EndMicroseconds = _oldEnd;
        _cues.Insert(Math.Min(_secondIndex, _cues.Count), _second);
        _document.MarkDirty();
    }
}
