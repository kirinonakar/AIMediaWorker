using System.Text.Json;
using AIMediaWorker.Asr;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Tests;

public sealed class SettingsServiceTests
{
    [Theory]
    [InlineData("Cuda", "Float16", 180, AsrDevice.Auto, 30)]
    [InlineData("Cpu", "Int8", 1, AsrDevice.Cpu, 5)]
    public async Task LoadNormalizesLegacyAsrOptions(string device, string precision, int seconds, AsrDevice expected, int expectedSeconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aimedia-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Asr = new { Device = device, Precision = precision, ChunkDurationSeconds = seconds } }));
            var settings = await new SettingsService(path).LoadAsync();
            Assert.Equal(expected, settings.Asr.Device);
            Assert.Equal(AsrPrecision.Auto, settings.Asr.Precision);
            Assert.Equal(expectedSeconds, settings.Asr.ChunkDurationSeconds);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadKeepsCrispAsrRuntimeDirectoryInSyncWithWorkerFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "custom-asr");
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, $"{{\"SchemaVersion\":7,\"Asr\":{{\"WorkerDirectory\":{JsonSerializer.Serialize(worker)}}}}}");
        try
        {
            var settings = await new SettingsService(path).LoadAsync();
            Assert.Equal(worker, settings.Asr.WorkerDirectory);
            Assert.Equal(Path.Combine(worker, "crispasr"), settings.Asr.CrispAsrRuntimeDirectory);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoadFallsBackToTheDefaultWorkerFolderBesideTheExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), "AIMediaWorker.Tests", Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":7,\"Asr\":{}}");
        try
        {
            var settings = await new SettingsService(path).LoadAsync();
            Assert.Null(settings.Asr.WorkerDirectory);
            Assert.Equal(Path.Combine(AsrRuntimePaths.DefaultWorkerDirectory, "crispasr"), settings.Asr.CrispAsrRuntimeDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
