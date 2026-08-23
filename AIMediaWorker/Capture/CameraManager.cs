using Windows.Devices.Enumeration;

namespace AIMediaWorker.Capture;

public sealed record CaptureDevice(string Id, string Name);

public sealed class CameraManager
{
    public async Task<IReadOnlyList<CaptureDevice>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken).ConfigureAwait(false);
        return devices.Select(device => new CaptureDevice(device.Id, device.Name)).ToArray();
    }

    public async Task<IReadOnlyList<CaptureDevice>> GetMicrophonesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask(cancellationToken).ConfigureAwait(false);
        return devices.Select(device => new CaptureDevice(device.Id, device.Name)).ToArray();
    }
}
