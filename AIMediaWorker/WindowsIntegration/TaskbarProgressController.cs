using System.Runtime.InteropServices;
using AIMediaWorker.Diagnostics;
using AIMediaWorker.Playback;

namespace AIMediaWorker.WindowsIntegration;

/// <summary>
/// Mirrors playback progress on the Windows taskbar button through the shell
/// ITaskbarList3 interface (progress fill, pause overlay, indeterminate spinner).
/// </summary>
internal sealed class TaskbarProgressController : IDisposable
{
    private const uint TbpfNoProgress = 0x0;
    private const uint TbpfIndeterminate = 0x1;
    private const uint TbpfNormal = 0x2;
    private const uint TbpfPaused = 0x8;

    // CLSID_TaskbarList (shobjidl_core.h).
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    private readonly nint _windowHandle;
    private ITaskbarList3? _taskbarList;
    private bool _unavailable;

    private uint _appliedState = uint.MaxValue;
    private ulong _appliedCompleted = ulong.MaxValue;
    private ulong _appliedTotal = ulong.MaxValue;

    public TaskbarProgressController(nint windowHandle)
    {
        _windowHandle = windowHandle;
        if (windowHandle == nint.Zero) { _unavailable = true; return; }
        try
        {
            var taskbarType = Type.GetTypeFromCLSID(TaskbarListClsid);
            if (taskbarType is null ||
                Activator.CreateInstance(taskbarType) is not ITaskbarList3 taskbarList || taskbarList.HrInit() != 0)
            {
                _unavailable = true;
                return;
            }

            _taskbarList = taskbarList;
        }
        catch (Exception exception)
        {
            _unavailable = true;
            _ = AppLog.WriteAsync("warning", "playback", "TASKBAR_PROGRESS_INIT_ERROR", exception.Message, exception);
        }
    }

    /// <summary>Applies the playback state and position to the taskbar button.</summary>
    public void Update(PlaybackState state, TimeSpan position, TimeSpan duration)
    {
        if (_unavailable) return;
        switch (state)
        {
            case PlaybackState.Loading:
                Apply(TbpfIndeterminate, 0, 1);
                break;
            case PlaybackState.Paused:
                Apply(TbpfPaused, ClampProgress(position, duration), ProgressTotal(duration));
                break;
            case PlaybackState.Playing when duration > TimeSpan.Zero:
                Apply(TbpfNormal, ClampProgress(position, duration), ProgressTotal(duration));
                break;
            case PlaybackState.Playing:
                Apply(TbpfIndeterminate, 0, 1); // Live stream or unknown duration.
                break;
            default:
                Clear();
                break;
        }
    }

    /// <summary>Removes any progress indication from the taskbar button.</summary>
    public void Clear() => Apply(TbpfNoProgress, 0, 1);

    public void Dispose()
    {
        var taskbarList = _taskbarList;
        _taskbarList = null;
        _unavailable = true;
        if (taskbarList is null) return;
        try { taskbarList.SetProgressState(_windowHandle, TbpfNoProgress); }
        catch { /* The window is going away; the shell clears the state itself. */ }
    }

    private static ulong ClampProgress(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0;
        return (ulong)Math.Clamp(position.TotalMilliseconds, 0, duration.TotalMilliseconds);
    }

    private static ulong ProgressTotal(TimeSpan duration) => duration > TimeSpan.Zero ? Math.Max(1UL, (ulong)duration.TotalMilliseconds) : 1UL;

    private void Apply(uint state, ulong completed, ulong total)
    {
        if (_unavailable || _taskbarList is null) return;
        if (state == _appliedState && completed == _appliedCompleted && total == _appliedTotal) return;
        try
        {
            if (state != _appliedState) _taskbarList.SetProgressState(_windowHandle, state);
            if (state is TbpfNormal or TbpfPaused &&
                (completed != _appliedCompleted || total != _appliedTotal))
                _taskbarList.SetProgressValue(_windowHandle, completed, total);
            _appliedState = state;
            _appliedCompleted = completed;
            _appliedTotal = total;
        }
        catch (Exception exception)
        {
            _unavailable = true;
            _taskbarList = null;
            _ = AppLog.WriteAsync("warning", "playback", "TASKBAR_PROGRESS_ERROR", exception.Message, exception);
        }
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig]
        int HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);

        void SetProgressState(nint hwnd, uint tbpFlags);
    }
}
