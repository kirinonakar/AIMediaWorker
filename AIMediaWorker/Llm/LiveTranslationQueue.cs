using System.Threading.Channels;

namespace AIMediaWorker.Llm;

internal sealed record LiveTranslationRequest(
    string Text, string SourceContext, long Generation, long Sequence,
    long DisplayRevision, bool CompletesSentence);

/// <summary>
/// Bounded pending work for a live display. Only pending text is coalesced;
/// an in-flight request and translations already shown are never removed.
/// </summary>
internal sealed class LiveTranslationQueue
{
    internal const int MaximumRequests = 8;
    internal const int MaximumTextCharacters = 600;
    internal static readonly TimeSpan MaximumPendingAge = TimeSpan.FromSeconds(8);
    private readonly object _gate = new();
    private readonly List<(LiveTranslationRequest Request, DateTimeOffset Enqueued)> _pending = [];
    private readonly Func<DateTimeOffset> _now;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });

    internal LiveTranslationQueue(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    internal int Count { get { lock (_gate) return _pending.Count; } }

    internal void Clear()
    {
        lock (_gate) _pending.Clear();
    }

    internal void Enqueue(LiveTranslationRequest request)
    {
        lock (_gate)
        {
            if (_pending.Count > 0 && _pending[^1].Request.Generation != request.Generation) _pending.Clear();
            var now = _now();
            if (_pending.Count >= MaximumRequests || IsStale(now))
            {
                // Include the newest context/revision, and retain the untranslated
                // pending deltas together instead of asking for each old fragment.
                request = Merge(_pending.Select(item => item.Request).Append(request));
                _pending.Clear();
            }
            _pending.Add((LimitText(request), now));
            _signal.Writer.TryWrite(true);
        }
    }

    internal async ValueTask<LiveTranslationRequest> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                if (IsStale(_now()))
                {
                    var merged = Merge(_pending.Select(item => item.Request));
                    _pending.Clear();
                    return merged;
                }
                var request = _pending[0].Request;
                _pending.RemoveAt(0);
                if (_pending.Count > 0) _signal.Writer.TryWrite(true);
                return request;
            }
        }
    }

    private bool IsStale(DateTimeOffset now) =>
        _pending.Count > 0 && now - _pending[0].Enqueued >= MaximumPendingAge;

    private static LiveTranslationRequest Merge(IEnumerable<LiveTranslationRequest> requests)
    {
        var items = requests.ToArray();
        return LimitText(items[^1] with
        {
            Text = string.Join(' ', items.Select(item => item.Text)),
            SourceContext = items[0].SourceContext
        });
    }

    private static LiveTranslationRequest LimitText(LiveTranslationRequest request)
    {
        var text = request.Text;
        var context = request.SourceContext;
        if (text.Length > MaximumTextCharacters)
        {
            var offset = text.Length - MaximumTextCharacters;
            // Avoid splitting UTF-16 surrogate pairs when dropping an old prefix.
            if (char.IsLowSurrogate(text[offset])) offset++;
            context = $"{context} {text[..offset]}";
            text = text[offset..].TrimStart();
        }
        if (context.Length > 480)
        {
            var offset = context.Length - 480;
            if (char.IsLowSurrogate(context[offset])) offset++;
            context = context[offset..].TrimStart();
        }
        return request with { Text = text, SourceContext = context };
    }
}
