using Microsoft.Win32;

namespace AIMediaWorker.Diagnostics;

public sealed record GraphicsAdapterInfo(string Name, string? DriverVersion, string? Provider);
public sealed record RtxVideoSuperResolutionCapability(bool IsSupported, string Status, IReadOnlyList<GraphicsAdapterInfo> Adapters);

public sealed class GraphicsCapabilityService
{
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public RtxVideoSuperResolutionCapability DetectRtxVideoSuperResolution()
    {
        var adapters = new List<GraphicsAdapterInfo>();
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (displayClass is not null)
            {
                foreach (var name in displayClass.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsDigit)))
                {
                    using var key = displayClass.OpenSubKey(name);
                    var description = key?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(description)) continue;
                    adapters.Add(new GraphicsAdapterInfo(description, key?.GetValue("DriverVersion") as string, key?.GetValue("ProviderName") as string));
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        var supported = adapters.Any(adapter => IsRtxAdapterName(adapter.Name, adapter.Provider));
        var status = supported
            ? "Supported RTX hardware detected. AIMediaWorker requests NVIDIA RTX Video Super Resolution through mpv's D3D11 video filter; enable RTX Video Super Resolution in NVIDIA App. Per-frame activation remains driver controlled."
            : "No NVIDIA RTX 20-series-or-newer adapter was detected. Playback uses the normal mpv scaler and hardware/software decode fallback.";
        return new RtxVideoSuperResolutionCapability(supported, status, adapters);
    }

    public static bool IsRtxAdapterName(string name, string? provider = null)
    {
        var combined = $"{provider} {name}";
        return combined.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
               (name.Contains("RTX", StringComparison.OrdinalIgnoreCase) || name.Contains("NVIDIA RTX", StringComparison.OrdinalIgnoreCase));
    }
}
