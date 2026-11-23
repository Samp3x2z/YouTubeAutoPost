using System.Text.Json;
using YouTubeAutoPost.Models;

namespace YouTubeAutoPost.Services;

public sealed class SettingsStore
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YouTubeAutoPost", "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_filePath)) return new AppSettings();
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var folder = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(folder);
            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
