using Xunit;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Tests;

internal sealed class FakeCloudStorageService : ICloudStorageService
{
    private const string RootKey = "";

    private readonly Dictionary<string, List<CloudFile>> _tree =
        new(StringComparer.Ordinal) { [RootKey] = [] };

    public string ServiceName => "Fake";

    public string ConnectionKey => "fake";

    public bool IsAuthenticated => true;

    public int CreateFolderCalls { get; private set; }

    public List<CloudFile> Root => _tree[RootKey];

    public void AddToRoot(CloudFile file) => Root.Add(file);

    public Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<CloudFile>> ListFilesAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var list = _tree.TryGetValue(folderId ?? RootKey, out var found) ? found : [];

        return Task.FromResult(new List<CloudFile>(list));
    }

    public Task DownloadFileAsync(string remoteFileId, string localPath, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        foreach (var list in _tree.Values) list.RemoveAll(f => f.Id == fileId);

        return Task.CompletedTask;
    }

    public Task<CloudFile> CreateFolderAsync(string? parentId, string name,
        CancellationToken cancellationToken = default)
    {
        CreateFolderCalls++;

        var key = parentId ?? RootKey;
        var folder = new CloudFile($"{key}/{name}", name, CloudMimeTypes.DropboxFolder, null, null, parentId);

        if (!_tree.TryGetValue(key, out var list)) _tree[key] = list = [];

        list.Add(folder);
        _tree[folder.Id] = [];

        return Task.FromResult(folder);
    }

    public Task<string> UploadFileToFolderAsync(string localPath, string? parentFolderId, string fileName,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var key = parentFolderId ?? RootKey;
        var file = new CloudFile($"{key}/{fileName}", fileName, "application/octet-stream", 1,
            DateTime.UnixEpoch, parentFolderId);

        if (!_tree.TryGetValue(key, out var list)) _tree[key] = list = [];

        list.Add(file);
        progress?.Report(100.0);

        return Task.FromResult(file.Id);
    }
}

public class CloudAssetRootTests
{
    [Fact]
    public async Task EnsureAsync_CreatesTheAssetsFolder_WhenTheRootIsEmpty()
    {
        var service = new FakeCloudStorageService();

        var id = await CloudAssetRoot.EnsureAsync(service);

        Assert.Equal(1, service.CreateFolderCalls);
        Assert.Single(service.Root);
        Assert.Equal(CloudAssetRoot.FolderName, service.Root[0].Name);
        Assert.Equal(service.Root[0].Id, id);
    }

    [Fact]
    public async Task EnsureAsync_ReusesAnExistingFolder()
    {
        var service = new FakeCloudStorageService();

        var first = await CloudAssetRoot.EnsureAsync(service);
        var second = await CloudAssetRoot.EnsureAsync(service);

        Assert.Equal(first, second);
        Assert.Equal(1, service.CreateFolderCalls);
    }

    [Fact]
    public async Task EnsureAsync_MatchesTheFolderNameCaseInsensitively()
    {
        var service = new FakeCloudStorageService();
        service.AddToRoot(new CloudFile("existing", "assets", CloudMimeTypes.DropboxFolder, null, null));

        var id = await CloudAssetRoot.EnsureAsync(service);

        Assert.Equal("existing", id);
        Assert.Equal(0, service.CreateFolderCalls);
    }

    [Fact]
    public async Task EnsureAsync_ThrowsWhenAFileTakesTheName()
    {
        var service = new FakeCloudStorageService();
        service.AddToRoot(new CloudFile("f", CloudAssetRoot.FolderName, "application/octet-stream", 1, null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CloudAssetRoot.EnsureAsync(service));

        Assert.Contains(CloudAssetRoot.FolderName, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, service.CreateFolderCalls);
    }

    [Fact]
    public async Task EnsureAsync_IgnoresProjectFilesInTheRoot()
    {
        var service = new FakeCloudStorageService();
        service.AddToRoot(new CloudFile("p", "project.ymmx", "application/octet-stream", 1, null));

        await CloudAssetRoot.EnsureAsync(service);

        Assert.Equal(1, service.CreateFolderCalls);
    }
}
