using Google.Apis.Util.Store;
using System.IO;
using System.Text.Json;

namespace YMM4CloudSync.Core.Commons;

public class EncryptedFileDataStore : IDataStore
{
    private readonly string _folderPath;

    public EncryptedFileDataStore(string folderPath)
    {
        _folderPath = folderPath;
        Directory.CreateDirectory(_folderPath);
    }

    public Task ClearAsync()
    {
        if (!Directory.Exists(_folderPath)) return Task.CompletedTask;
        
        Directory.Delete(_folderPath, true);
        Directory.CreateDirectory(_folderPath);
        
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var filePath = Path.Combine(_folderPath, key);
        SecureStorageHelper.Delete(filePath);
        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        var filePath = Path.Combine(_folderPath, key);
        var data = SecureStorageHelper.Load(filePath);

        if (data == null)
        {
            return Task.FromResult<T?>(default);
        }

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(data);
            var result = JsonSerializer.Deserialize<T>(json);
            return Task.FromResult(result);
        }
        catch
        {
            return Task.FromResult<T?>(default);
        }
    }

    public Task StoreAsync<T>(string key, T value)
    {
        var filePath = Path.Combine(_folderPath, key);
        var json = JsonSerializer.Serialize(value);
        var data = System.Text.Encoding.UTF8.GetBytes(json);

        SecureStorageHelper.Save(filePath, data);
        return Task.CompletedTask;
    }
}