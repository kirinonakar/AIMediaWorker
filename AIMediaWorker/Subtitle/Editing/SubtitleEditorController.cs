using AIMediaWorker.Subtitle.Parsing;
using AIMediaWorker.Subtitle.Writing;
using AIMediaWorker.Timeline;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace AIMediaWorker.Subtitle.Editing;

/// <summary>
/// Owns subtitle editing, selection, undo history, and timeline interaction.
/// The window supplies playback and dialog callbacks, but does not implement editor behavior.
/// </summary>
internal sealed class SubtitleEditorController
{
    private readonly ListView _subtitleList;
    private readonly Canvas _timelineCanvas;
    private readonly Func<SubtitleDocument> _document;
    private readonly Func<long> _playbackPositionMicroseconds;
    private readonly Action<TimeSpan> _seek;
    private readonly Action _contentChanged;
    private readonly Action<string> _setStatus;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object, string, Task<ContentDialogResult>> _showDialog;
    private readonly Func<string, string, Task> _showMessage;
    private readonly SubtitleCommandHistory _history = new();
    private readonly TimelineTransform _timelineTransform = new();
    private readonly Dictionary<Guid, string> _textBeforeEdit = [];
    private readonly Dictionary<Guid, (long Start, long End)> _timesBeforeEdit = [];
    private SubtitleCue? _dragCue;
    private TimelineDragMode _dragMode;
    private double _dragStartX;
    private long _dragOldStart;
    private long _dragOldEnd;
    private Rectangle? _timelinePlayhead;
    private Guid? _playbackLinkedCueId;

    public SubtitleEditorController(
        ListView subtitleList,
        Canvas timelineCanvas,
        Func<SubtitleDocument> document,
        Func<long> playbackPositionMicroseconds,
        Action<TimeSpan> seek,
        Action contentChanged,
        Action<string> setStatus,
        Func<string, string> localize,
        Func<string, object, string, Task<ContentDialogResult>> showDialog,
        Func<string, string, Task> showMessage)
    {
        _subtitleList = subtitleList;
        _timelineCanvas = timelineCanvas;
        _document = document;
        _playbackPositionMicroseconds = playbackPositionMicroseconds;
        _seek = seek;
        _contentChanged = contentChanged;
        _setStatus = setStatus;
        _localize = localize;
        _showDialog = showDialog;
        _showMessage = showMessage;
    }

    public bool HasFocus { get; private set; }

    public void BindDocument(SubtitleDocument document)
    {
        var track = document.EnsureTrack();
        _subtitleList.ItemsSource = track.Cues;
        _history.Clear();
        _playbackLinkedCueId = null;
        DrawTimeline();
    }

    public void ResetTimeline()
    {
        _timelineTransform.Reset();
        DrawTimeline();
    }

    public void Execute(IUndoableSubtitleCommand command)
    {
        _history.Execute(command);
        NotifyContentChanged();
    }

    public void AddCue()
    {
        var document = _document();
        var track = document.EnsureTrack();
        var start = _playbackPositionMicroseconds();
        var cue = new SubtitleCue
        {
            StartMicroseconds = start,
            EndMicroseconds = start + 2_000_000,
            Text = string.Empty,
            Source = SubtitleCueSource.Manual
        };
        _history.Execute(new AddSubtitleCommand(document, track.Cues, cue));
        _subtitleList.SelectedItem = cue;
        NotifyContentChanged();
    }

    public void DeleteSelectedCues()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null) return;
        var selected = _subtitleList.SelectedItems.Cast<SubtitleCue>().ToArray();
        if (selected.Length == 0) return;
        _history.Execute(new DeleteSubtitleCommand(document, track.Cues, selected));
        NotifyContentChanged();
    }

    public void SplitSelectedCue()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null || _subtitleList.SelectedItem is not SubtitleCue cue) return;
        var playhead = _playbackPositionMicroseconds();
        var split = playhead > cue.StartMicroseconds && playhead < cue.EndMicroseconds
            ? playhead
            : cue.StartMicroseconds + cue.DurationMicroseconds / 2;
        _history.Execute(new SplitSubtitleCommand(document, track.Cues, cue, split));
        NotifyContentChanged();
    }

    public void MergeSelectedCueWithNext()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null || _subtitleList.SelectedItem is not SubtitleCue first) return;
        var index = track.Cues.IndexOf(first);
        if (index < 0 || index + 1 >= track.Cues.Count) return;
        _history.Execute(new MergeSubtitleCommand(document, track.Cues, first, track.Cues[index + 1]));
        NotifyContentChanged();
    }

    public void Undo()
    {
        _history.Undo();
        NotifyContentChanged();
    }

    public void Redo()
    {
        _history.Redo();
        NotifyContentChanged();
    }

    public void CueTextGotFocus(object sender)
    {
        HasFocus = true;
        if (sender is TextBox { DataContext: SubtitleCue cue }) _textBeforeEdit[cue.Id] = cue.Text;
    }

    public void CueTextLostFocus(object sender)
    {
        HasFocus = false;
        if (sender is not TextBox { DataContext: SubtitleCue cue } box ||
            !_textBeforeEdit.Remove(cue.Id, out var before) || before == box.Text) return;
        var after = box.Text;
        cue.Text = before;
        _history.Execute(new EditSubtitleTextCommand(_document(), cue, after));
        NotifyContentChanged();
    }

    public void CueTimeGotFocus(object sender)
    {
        HasFocus = true;
        if (sender is TextBox { DataContext: SubtitleCue cue })
            _timesBeforeEdit[cue.Id] = (cue.StartMicroseconds, cue.EndMicroseconds);
    }

    public void CueTimeLostFocus(object sender)
    {
        HasFocus = false;
        if (sender is not TextBox { DataContext: SubtitleCue cue } box ||
            !_timesBeforeEdit.Remove(cue.Id, out var before)) return;
        if (!long.TryParse(box.Text, out var value))
        {
            RestoreTimeText(box, before);
            return;
        }

        var start = before.Start;
        var end = before.End;
        switch (box.Tag?.ToString())
        {
            case "Start": start = value; break;
            case "End": end = value; break;
            case "Duration": end = checked(start + value); break;
        }
        if (start < 0 || end <= start)
        {
            RestoreTimeText(box, before);
            _setStatus(_localize("StatusInvalidSubtitleTime"));
            return;
        }

        _history.Execute(new MoveSubtitleCommand(_document(), cue, start, end));
        NotifyContentChanged();
    }

    public void DuplicateSelectedCues()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null) return;
        var selected = _subtitleList.SelectedItems.Cast<SubtitleCue>().OrderBy(cue => cue.StartMicroseconds).ToArray();
        if (selected.Length == 0) return;
        var copies = selected.Select(cue =>
        {
            var copy = cue.Clone(false);
            copy.StartMicroseconds += 100_000;
            copy.EndMicroseconds += 100_000;
            return copy;
        }).ToArray();
        var commands = copies.Select(copy => (IUndoableSubtitleCommand)new AddSubtitleCommand(document, track.Cues, copy)).ToArray();
        _history.Execute(new CompositeSubtitleCommand("Duplicate subtitles", commands));
        _subtitleList.SelectedItems.Clear();
        foreach (var copy in copies) _subtitleList.SelectedItems.Add(copy);
        NotifyContentChanged();
    }

    public async Task ShiftCuesAsync()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null || track.Cues.Count == 0) return;
        var input = new NumberBox
        {
            Header = _localize("ShiftSecondsHeader"),
            Value = 0,
            SmallChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 320
        };
        if (await _showDialog(_localize("ShiftSubtitlesTitle"), input, _localize("ShiftButton")) != ContentDialogResult.Primary) return;
        var selected = _subtitleList.SelectedItems.Cast<SubtitleCue>().ToArray();
        var cues = selected.Length > 0 ? selected : track.Cues.ToArray();
        try
        {
            _history.Execute(new BatchShiftCommand(document, cues, checked((long)Math.Round(input.Value * 1_000_000))));
            NotifyContentChanged();
        }
        catch (Exception exception)
        {
            await _showMessage(_localize("InvalidShiftTitle"), exception.Message);
        }
    }

    public async Task AdjustSynchronizationAsync()
    {
        var document = _document();
        var track = document.ActiveTrack;
        if (track is null || track.Cues.Count == 0)
        {
            await _showMessage(_localize("SubtitleSyncTitle"), _localize("LoadSubtitlesFirst"));
            return;
        }

        var referenceCue = _subtitleList.SelectedItems.Cast<SubtitleCue>().OrderBy(cue => cue.StartMicroseconds).FirstOrDefault();
        var offsetInput = new NumberBox
        {
            Header = _localize("SubtitleSyncOffsetHeader"),
            Value = 0,
            SmallChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 320
        };
        var alignButton = new Button
        {
            Content = _localize("SyncToCurrentPositionButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = referenceCue is not null
        };
        alignButton.Click += (_, _) =>
        {
            if (referenceCue is not null)
                offsetInput.Value = (_playbackPositionMicroseconds() - referenceCue.StartMicroseconds) / 1_000_000d;
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = _localize("SubtitleSyncOffsetHint"), TextWrapping = TextWrapping.Wrap },
                offsetInput,
                alignButton
            }
        };

        if (await _showDialog(_localize("SubtitleSyncTitle"), content, _localize("ApplySyncButton")) != ContentDialogResult.Primary) return;
        if (!double.IsFinite(offsetInput.Value))
        {
            await _showMessage(_localize("InvalidShiftTitle"), _localize("SubtitleSyncInvalidValue"));
            return;
        }

        try
        {
            var delta = checked((long)Math.Round(offsetInput.Value * 1_000_000d, MidpointRounding.AwayFromZero));
            if (delta == 0) return;
            _history.Execute(new BatchShiftCommand(document, track.Cues.ToArray(), delta));
            NotifyContentChanged();
        }
        catch (Exception exception)
        {
            await _showMessage(_localize("InvalidShiftTitle"), exception.Message);
        }
    }

    public void SelectAll() => _subtitleList.SelectAll();

    public void CopySelectedCues()
    {
        var selected = _subtitleList.SelectedItems.Cast<SubtitleCue>().OrderBy(cue => cue.StartMicroseconds).ToArray();
        if (selected.Length == 0) return;
        var track = new SubtitleTrack();
        foreach (var cue in selected) track.Cues.Add(cue.Clone(false));
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(SrtWriter.Write(track));
        Clipboard.SetContent(package);
    }

    public async Task PasteCuesAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        try
        {
            var text = await content.GetTextAsync();
            var document = _document();
            var track = document.EnsureTrack();
            SubtitleCue[] cues;
            if (text.Contains("-->", StringComparison.Ordinal))
                cues = SrtParser.Parse(text).ActiveTrack?.Cues.Select(cue => cue.Clone(false)).ToArray() ?? [];
            else
            {
                var start = _playbackPositionMicroseconds();
                cues = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select((line, index) => new SubtitleCue
                    {
                        StartMicroseconds = start + index * 2_000_000,
                        EndMicroseconds = start + (index + 1) * 2_000_000,
                        Text = line,
                        Source = SubtitleCueSource.Manual
                    }).ToArray();
            }
            var commands = cues.Select(cue => (IUndoableSubtitleCommand)new AddSubtitleCommand(document, track.Cues, cue)).ToArray();
            _history.Execute(new CompositeSubtitleCommand("Paste subtitles", commands));
            NotifyContentChanged();
        }
        catch (Exception exception)
        {
            await _showMessage(_localize("PasteErrorTitle"), exception.Message);
        }
    }

    public void SubtitleItemClicked(ItemClickEventArgs args)
    {
        if (args.ClickedItem is SubtitleCue cue) _seek(TimeSpan.FromTicks(cue.StartMicroseconds * 10));
    }

    public void SelectRelativeCue(int delta)
    {
        var cues = _document().ActiveTrack?.Cues;
        if (cues is null || cues.Count == 0) return;
        var index = _subtitleList.SelectedItem is SubtitleCue selected ? cues.IndexOf(selected) : 0;
        index = Math.Clamp(index + delta, 0, cues.Count - 1);
        _subtitleList.SelectedItem = cues[index];
        _subtitleList.ScrollIntoView(cues[index]);
        _seek(TimeSpan.FromTicks(cues[index].StartMicroseconds * 10));
    }

    public void TimelinePointerPressed(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_timelineCanvas);
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && element != _timelineCanvas && element is not FrameworkElement { Tag: SubtitleCue })
            element = VisualTreeHelper.GetParent(element);
        if (element is FrameworkElement { Tag: SubtitleCue cue } block)
        {
            _dragCue = cue;
            _dragStartX = point.Position.X;
            _dragOldStart = cue.StartMicroseconds;
            _dragOldEnd = cue.EndMicroseconds;
            var local = args.GetCurrentPoint(block).Position.X;
            _dragMode = local <= 8 ? TimelineDragMode.ResizeStart : local >= block.ActualWidth - 8 ? TimelineDragMode.ResizeEnd : TimelineDragMode.Move;
            _subtitleList.SelectedItem = cue;
            _timelineCanvas.CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }
        _seek(TimeSpan.FromTicks(_timelineTransform.XToTime(point.Position.X) * 10));
    }

    public void TimelinePointerMoved(PointerRoutedEventArgs args)
    {
        if (_dragCue is null || !args.GetCurrentPoint(_timelineCanvas).Properties.IsLeftButtonPressed) return;
        var currentX = args.GetCurrentPoint(_timelineCanvas).Position.X;
        var delta = _timelineTransform.XToTime(currentX) - _timelineTransform.XToTime(_dragStartX);
        var trackCues = _document().ActiveTrack?.Cues;
        var candidates = (trackCues is null ? Enumerable.Empty<long>() : trackCues.Where(cue => cue != _dragCue).SelectMany(cue => new[] { cue.StartMicroseconds, cue.EndMicroseconds }))
            .Append(_playbackPositionMicroseconds());
        var tolerance = Math.Max(1L, _timelineTransform.XToTime(8) - _timelineTransform.XToTime(0));
        switch (_dragMode)
        {
            case TimelineDragMode.Move:
                var duration = _dragOldEnd - _dragOldStart;
                var start = TimelineSnapper.Snap(Math.Max(0, _dragOldStart + delta), candidates, tolerance);
                _dragCue.StartMicroseconds = start;
                _dragCue.EndMicroseconds = start + duration;
                break;
            case TimelineDragMode.ResizeStart:
                _dragCue.StartMicroseconds = Math.Min(_dragCue.EndMicroseconds - 10_000,
                    TimelineSnapper.Snap(Math.Max(0, _dragOldStart + delta), candidates, tolerance));
                break;
            case TimelineDragMode.ResizeEnd:
                _dragCue.EndMicroseconds = Math.Max(_dragCue.StartMicroseconds + 10_000,
                    TimelineSnapper.Snap(_dragOldEnd + delta, candidates, tolerance));
                break;
        }
        DrawTimeline();
        args.Handled = true;
    }

    public void TimelinePointerReleased(PointerRoutedEventArgs args)
    {
        if (_dragCue is null) return;
        _timelineCanvas.ReleasePointerCapture(args.Pointer);
        var cue = _dragCue;
        var newStart = cue.StartMicroseconds;
        var newEnd = cue.EndMicroseconds;
        cue.StartMicroseconds = _dragOldStart;
        cue.EndMicroseconds = _dragOldEnd;
        if (newStart != _dragOldStart || newEnd != _dragOldEnd)
            _history.Execute(new MoveSubtitleCommand(_document(), cue, newStart, newEnd));
        _dragCue = null;
        _dragMode = TimelineDragMode.None;
        NotifyContentChanged();
        args.Handled = true;
    }

    public void TimelinePointerWheelChanged(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_timelineCanvas);
        var delta = point.Properties.MouseWheelDelta;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) _timelineTransform.ZoomAt(delta > 0 ? 1.25 : 0.8, point.Position.X);
        else
        {
            var visible = _timelineTransform.VisibleRange(_timelineCanvas.ActualWidth);
            var shift = (visible.End - visible.Start) / 8;
            _timelineTransform.PanTo(_timelineTransform.ViewStartMicroseconds + (delta > 0 ? -shift : shift));
        }
        DrawTimeline();
        args.Handled = true;
    }

    public void DrawTimeline(long? positionMicroseconds = null)
    {
        _timelineCanvas.Children.Clear();
        _timelinePlayhead = null;
        if (_document().ActiveTrack?.Cues is { } cues)
        {
            foreach (var cue in cues)
            {
                var left = _timelineTransform.TimeToX(cue.StartMicroseconds);
                var right = _timelineTransform.TimeToX(cue.EndMicroseconds);
                if (right < 0) continue;
                if (left > _timelineCanvas.ActualWidth) break;
                var border = new Border
                {
                    Width = Math.Max(3, right - left),
                    Height = Math.Max(20, _timelineCanvas.ActualHeight - 8),
                    Background = ThemeBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 40, 130, 220)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2, 4, 2),
                    Child = new TextBlock
                    {
                        Text = cue.GetDisplayText(SubtitleDisplayMode.OriginalAndTranslation),
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 4,
                        FontSize = 13,
                        LineHeight = 17,
                        Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255))
                    },
                    Tag = cue
                };
                Canvas.SetLeft(border, left);
                Canvas.SetTop(border, 4);
                _timelineCanvas.Children.Add(border);
            }
        }
        _timelinePlayhead = new Rectangle
        {
            Width = 2,
            Height = _timelineCanvas.ActualHeight,
            Fill = ThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 255, 69, 0)),
            IsHitTestVisible = false
        };
        Canvas.SetTop(_timelinePlayhead, 0);
        _timelineCanvas.Children.Add(_timelinePlayhead);
        UpdateTimelinePlayhead(positionMicroseconds ?? _playbackPositionMicroseconds());
    }

    public void UpdatePlaybackPosition(long positionMicroseconds)
    {
        var viewportChanged = _timelineCanvas.ActualWidth > 0 &&
            _timelineTransform.EnsureVisible(positionMicroseconds, _timelineCanvas.ActualWidth);
        var cue = _document().FindActiveCue(positionMicroseconds);
        if (!HasFocus)
        {
            var cueChanged = cue?.Id != _playbackLinkedCueId;
            _playbackLinkedCueId = cue?.Id;
            if (cue is not null)
            {
                if (!_subtitleList.SelectedItems.Contains(cue)) _subtitleList.SelectedItem = cue;
                if (cueChanged) _subtitleList.ScrollIntoView(cue, ScrollIntoViewAlignment.Leading);
            }
        }
        if (viewportChanged) DrawTimeline(positionMicroseconds);
        else UpdateTimelinePlayhead(positionMicroseconds);
    }

    private void UpdateTimelinePlayhead(long positionMicroseconds)
    {
        if (_timelinePlayhead is null) return;
        var playheadX = _timelineTransform.TimeToX(positionMicroseconds);
        _timelinePlayhead.Height = _timelineCanvas.ActualHeight;
        _timelinePlayhead.Visibility = playheadX >= 0 && playheadX <= _timelineCanvas.ActualWidth
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_timelinePlayhead.Visibility == Visibility.Visible) Canvas.SetLeft(_timelinePlayhead, playheadX);
    }

    private void NotifyContentChanged()
    {
        DrawTimeline();
        _contentChanged();
    }

    private static void RestoreTimeText(TextBox box, (long Start, long End) before) =>
        box.Text = box.Tag?.ToString() switch
        {
            "Start" => before.Start.ToString(),
            "End" => before.End.ToString(),
            _ => (before.End - before.Start).ToString()
        };

    private static Brush ThemeBrush(string resourceKey, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(resourceKey, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private enum TimelineDragMode { None, Move, ResizeStart, ResizeEnd }
}
