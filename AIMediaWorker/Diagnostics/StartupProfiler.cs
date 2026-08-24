using System.Diagnostics;
using System.Globalization;

namespace AIMediaWorker.Diagnostics;

/// <summary>
/// Keeps cold-start timing on the hot path in memory and writes a single log entry
/// after the first frame. This avoids turning diagnostic file I/O into startup work.
/// </summary>
public static class StartupProfiler
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, long> Marks = new(StringComparer.Ordinal);
    private static readonly List<string> Order = [];
    private static long _origin = Stopwatch.GetTimestamp();
    private static int _completed;

    public static string? LatestSummary { get; private set; }

    public static void Start()
    {
        lock (Sync)
        {
            Marks.Clear();
            Order.Clear();
            _origin = Stopwatch.GetTimestamp();
            Marks["process-entry"] = _origin;
            Order.Add("process-entry");
            LatestSummary = null;
            Volatile.Write(ref _completed, 0);
        }
    }

    public static void Mark(string name)
    {
        var timestamp = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (Marks.ContainsKey(name)) return;
            Marks[name] = timestamp;
            Order.Add(name);
        }
    }

    public static void CompleteAtFirstFrame(string? playbackBackend = null)
    {
        Mark("first-frame");
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;

        string summary;
        lock (Sync) summary = BuildSummary(playbackBackend);
        LatestSummary = summary;
        Debug.WriteLine($"[startup] {summary}");
        _ = AppLog.WriteAsync("info", "startup-performance", "STARTUP_FIRST_FRAME_TIMING", summary);
    }

    private static string BuildSummary(string? playbackBackend)
    {
        var durations = new List<(string Name, double Milliseconds)>();
        AddDuration(durations, "settings", "settings-load-start", "settings-load-end");
        AddDuration(durations, "App XAML", "app-xaml-start", "app-xaml-end");
        AddDuration(durations, "localization apply", "localization-apply-start", "localization-apply-end");
        AddDuration(durations, "libmpv DLL", "mpv-dll-load-start", "mpv-dll-load-end");
        AddDuration(durations, "MainWindow through XAML", "main-window-create-start", "xaml-ready");
        AddDuration(durations, "XAML InitializeComponent", "xaml-start", "xaml-ready");
        AddDuration(durations, "mpv_create", "mpv-create-start", "mpv-create-end");
        AddDuration(durations, "mpv options", "mpv-options-start", "mpv-options-end");
        AddDuration(durations, "mpv_initialize", "mpv-initialize-start", "mpv-initialize-end");
        AddDuration(durations, "loadfile -> start-file", "loadfile-command", "start-file");
        AddDuration(durations, "start-file -> file-loaded", "start-file", "file-loaded");
        AddDuration(durations, "loadfile -> file-loaded", "loadfile-command", "file-loaded");
        AddDuration(durations, "loadfile -> video-reconfig", "loadfile-command", "video-reconfig");
        AddDuration(durations, "loadfile -> audio-reconfig", "loadfile-command", "audio-reconfig");
        AddDuration(durations, "file-loaded -> first frame", "file-loaded", "first-frame");
        AddDuration(durations, "video-reconfig -> first frame", "video-reconfig", "first-frame");
        AddDuration(durations, "audio-reconfig -> first frame", "audio-reconfig", "first-frame");

        var total = Elapsed("process-entry", "first-frame");
        var bottleneck = durations.Count == 0 ? default : durations.MaxBy(item => item.Milliseconds);
        var milestoneNames = new[]
        {
            "app-constructor", "settings-load-end", "mpv-dll-load-end", "xaml-ready",
            "mpv-initialize-end", "window-activated", "loadfile-command", "start-file",
            "file-loaded", "video-reconfig", "audio-reconfig", "first-frame"
        };
        var milestones = milestoneNames
            .Where(Marks.ContainsKey)
            .Select(name => $"{name}={FromOrigin(Marks[name]):0.0}ms");
        var stages = durations.Select(item => $"{item.Name}={item.Milliseconds:0.0}ms");
        var bottleneckText = bottleneck.Name is null ? "unknown" : $"{bottleneck.Name} ({bottleneck.Milliseconds:0.0}ms)";
        var backendText = string.IsNullOrWhiteSpace(playbackBackend) ? "unknown" : playbackBackend;
        return string.Create(CultureInfo.InvariantCulture,
            $"total={total:0.0}ms; backend={backendText}; longest measured stage={bottleneckText}; stages: {string.Join(", ", stages)}; milestones: {string.Join(", ", milestones)}");
    }

    private static void AddDuration(List<(string Name, double Milliseconds)> target, string name, string start, string end)
    {
        if (Marks.ContainsKey(start) && Marks.ContainsKey(end) && Marks[end] >= Marks[start])
            target.Add((name, Elapsed(start, end)));
    }

    private static double Elapsed(string start, string end) =>
        (Marks[end] - Marks[start]) * 1000d / Stopwatch.Frequency;

    private static double FromOrigin(long timestamp) =>
        (timestamp - _origin) * 1000d / Stopwatch.Frequency;
}
