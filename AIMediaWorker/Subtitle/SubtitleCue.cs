using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIMediaWorker.Subtitle;

public enum SubtitleCueSource
{
    Imported,
    Embedded,
    AutomaticSpeechRecognition,
    Live,
    Manual
}

public sealed class SubtitleCue : INotifyPropertyChanged
{
    private long _startMicroseconds;
    private long _endMicroseconds;
    private string _text = string.Empty;
    private string? _translatedText;
    private string? _style;
    private string? _speaker;
    private double? _confidence;

    public Guid Id { get; init; } = Guid.NewGuid();
    public long StartMicroseconds { get => _startMicroseconds; set => SetTime(ref _startMicroseconds, value); }
    public long EndMicroseconds { get => _endMicroseconds; set => SetTime(ref _endMicroseconds, value); }
    public string Text
    {
        get => _text;
        set
        {
            var normalized = value ?? string.Empty;
            if (_text == normalized) return;
            _text = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDistinctTranslation));
        }
    }

    public string? TranslatedText
    {
        get => _translatedText;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(_translatedText, normalized, StringComparison.Ordinal)) return;
            _translatedText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDistinctTranslation));
        }
    }

    public bool HasDistinctTranslation => !string.IsNullOrWhiteSpace(TranslatedText) &&
        !string.Equals(Text, TranslatedText, StringComparison.Ordinal);
    public string? Style { get => _style; set => Set(ref _style, value); }
    public string? Speaker { get => _speaker; set => Set(ref _speaker, value); }
    public double? Confidence { get => _confidence; set => Set(ref _confidence, value); }
    public SubtitleCueSource Source { get; set; } = SubtitleCueSource.Imported;
    public long DurationMicroseconds { get => EndMicroseconds - StartMicroseconds; set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); EndMicroseconds = checked(StartMicroseconds + value); } }

    public string GetDisplayText(SubtitleDisplayMode mode) => mode switch
    {
        SubtitleDisplayMode.Original => Text,
        SubtitleDisplayMode.Translation => string.IsNullOrWhiteSpace(TranslatedText) ? Text : TranslatedText,
        SubtitleDisplayMode.OriginalAndTranslation => string.IsNullOrWhiteSpace(TranslatedText) || string.Equals(Text, TranslatedText, StringComparison.Ordinal)
            ? Text
            : $"{Text}\n{TranslatedText}",
        _ => Text
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public SubtitleCue Clone(bool preserveId = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        StartMicroseconds = StartMicroseconds,
        EndMicroseconds = EndMicroseconds,
        Text = Text,
        TranslatedText = TranslatedText,
        Style = Style,
        Speaker = Speaker,
        Confidence = Confidence,
        Source = Source
    };

    public void Validate()
    {
        if (StartMicroseconds < 0) throw new InvalidDataException("Subtitle start time cannot be negative.");
        if (EndMicroseconds <= StartMicroseconds) throw new InvalidDataException("Subtitle end time must be after its start time.");
    }

    private void SetTime(ref long field, long value)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged();
        OnPropertyChanged(nameof(DurationMicroseconds));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
