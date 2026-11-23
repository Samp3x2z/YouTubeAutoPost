using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;

namespace YouTubeAutoPost.Services;

// Google OAuth token data is encrypted for the current Windows user using DPAPI.
public sealed class DpapiDataStore : IDataStore
{
    private readonly string _folder;

    public DpapiDataStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(PathFor(key), encrypted);
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return default!;
        var encrypted = await File.ReadAllBytesAsync(path);
        var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return System.Text.Json.JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(clear))!;
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_folder, safe + ".bin");
    }
}
