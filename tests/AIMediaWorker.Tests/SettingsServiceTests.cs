using System.Text.Json;
using AIMediaWorker.Asr;
using AIMediaWorker.Settings;

namespace AIMediaWorker.Tests;

public sealed class SettingsServiceTests
{
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
