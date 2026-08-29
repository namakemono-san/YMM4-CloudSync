using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.ViewModels;

public sealed class AssetTabViewModel : INotifyPropertyChanged, IDisposable
{
    private const int Idle = 0;
    private const int Busy = 1;
    private const int MaxConcurrentDownloads = 3;

    private readonly IProjectDialogService _dialogs;
    private readonly SynchronizationContext? _uiContext;
    private readonly IDisposable _serviceSubscription;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly ConcurrentDictionary<string, AssetItemViewModel> _activeDownloads = new(StringComparer.Ordinal);
    private readonly List<BreadcrumbNode> _stack = [];
    private readonly List<List<BreadcrumbNode>> _history = [];

    private int _historyIndex = -1;
    private bool _restoringHistory;

    private CloudServiceItem? _observedItem;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _refresh;
    private int _processingState = Idle;
    private bool _disposed;

    public AssetTabViewModel(ToolViewModel tool, IProjectDialogService dialogs)
    {
        Tool = tool;
        _dialogs = dialogs;
        _uiContext = SynchronizationContext.Current;

        AssetsView = CollectionViewSource.GetDefaultView(Items) as ListCollectionView
                     ?? new ListCollectionView(Items);
        AssetsView.Filter = PassesFilter;
        AssetsView.CustomSort = new AssetItemComparer(SortKey, SortDescending);

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => IsConnected && !IsProcessing);
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => IsConnected && !IsProcessing && _stack.Count > 1);
        BackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack && !IsProcessing);
        ForwardCommand = new AsyncRelayCommand(GoForwardAsync, () => CanGoForward && !IsProcessing);
        NavigateCommand = new AsyncRelayCommand(p => NavigateToAsync(p as BreadcrumbNode), _ => !IsProcessing);
        OpenCommand = new AsyncRelayCommand(p => ActivateAsync(p as AssetItemViewModel), _ => !IsProcessing);
        DownloadCommand = new AsyncRelayCommand(p => DownloadAsync(p as AssetItemViewModel), _ => IsConnected);
        CancelDownloadCommand = new RelayCommand(p => CancelDownload(p as AssetItemViewModel));
        OpenFolderCommand = new RelayCommand(p => OpenContainingFolder(p as AssetItemViewModel));
        AddToTimelineCommand = new AsyncRelayCommand(p => ActivateAsync(p as AssetItemViewModel),
            p => p is AssetItemViewModel { IsFolder: false });
        DownloadAndOpenFolderCommand = new AsyncRelayCommand(
            p => DownloadAndOpenFolderAsync(p as AssetItemViewModel), _ => IsConnected);

        BeginCreateFolderCommand = new RelayCommand(_ => BeginCreateFolder(), _ => IsConnected && !IsProcessing);
        ConfirmCreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync, () => IsCreatingFolder && !IsProcessing);
        CancelCreateFolderCommand = new RelayCommand(_ => IsCreatingFolder = false);

        UploadCommand = new AsyncRelayCommand(PickAndUploadAsync, () => IsConnected && !IsProcessing);
        DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p as AssetItemViewModel),
            p => p is AssetItemViewModel && !IsProcessing);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);

        SwitchSortKeyCommand = new RelayCommand(p =>
        {
            if (p is AssetSortKey key) ApplySort(key, SortDescending);
            else if (p is string text && Enum.TryParse<AssetSortKey>(text, out var parsed)) ApplySort(parsed, SortDescending);
        });

        SwitchSortOrderCommand = new RelayCommand(p =>
        {
            var descending = p is bool flag ? flag : p as string == "Descending";

            ApplySort(SortKey, descending);
        });

        SwitchViewCommand = new RelayCommand(p =>
        {
            if (p is AssetViewMode mode) Layout.SwitchTo(mode);
            else if (p is string text && Enum.TryParse<AssetViewMode>(text, out var parsed)) Layout.SwitchTo(parsed);
        });

        IncreaseLayoutSizeCommand = new RelayCommand(_ => Layout.Increase(), _ => Layout.CanIncrease);
        DecreaseLayoutSizeCommand = new RelayCommand(_ => Layout.Decrease(), _ => Layout.CanDecrease);

        Layout.PropertyChanged += (_, _) =>
        {
            IncreaseLayoutSizeCommand.RaiseCanExecuteChanged();
            DecreaseLayoutSizeCommand.RaiseCanExecuteChanged();

            foreach (var item in Items) item.IconPixels = Layout.IconSize;
        };

        _serviceSubscription = Tool.SelectedCloudService.Subscribe(OnServiceChanged);
    }

    public ToolViewModel Tool { get; }

    public ObservableCollection<AssetItemViewModel> Items { get; } = [];

    public ListCollectionView AssetsView { get; }

    public AssetSortKey SortKey
    {
        get;
        private set => SetProperty(ref field, value);
    } = AssetSortKey.Name;

    public bool SortDescending
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsSortByName => SortKey == AssetSortKey.Name;
    public bool IsSortByType => SortKey == AssetSortKey.Type;
    public bool IsSortBySize => SortKey == AssetSortKey.Size;
    public bool IsSortByModifiedTime => SortKey == AssetSortKey.ModifiedTime;
    public bool IsSortAscending => !SortDescending;

    public void ApplySort(AssetSortKey key, bool descending)
    {
        SortKey = key;
        SortDescending = descending;

        AssetsView.CustomSort = new AssetItemComparer(key, descending);

        OnPropertyChanged(nameof(IsSortByName));
        OnPropertyChanged(nameof(IsSortByType));
        OnPropertyChanged(nameof(IsSortBySize));
        OnPropertyChanged(nameof(IsSortByModifiedTime));
        OnPropertyChanged(nameof(IsSortAscending));
    }

    public ObservableCollection<BreadcrumbNode> Breadcrumbs { get; } = [];

    public AssetLayout Layout { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand GoUpCommand { get; }
    public AsyncRelayCommand BackCommand { get; }
    public AsyncRelayCommand ForwardCommand { get; }
    public AsyncRelayCommand NavigateCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand AddToTimelineCommand { get; }
    public AsyncRelayCommand DownloadAndOpenFolderCommand { get; }
    public RelayCommand BeginCreateFolderCommand { get; }
    public AsyncRelayCommand ConfirmCreateFolderCommand { get; }
    public RelayCommand CancelCreateFolderCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SwitchSortKeyCommand { get; }
    public RelayCommand SwitchSortOrderCommand { get; }
    public RelayCommand SwitchViewCommand { get; }
    public RelayCommand IncreaseLayoutSizeCommand { get; }
    public RelayCommand DecreaseLayoutSizeCommand { get; }

    public bool IsConnected
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool IsProcessing
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public bool CanCancel
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public double ProgressValue
    {
        get;
        private set => SetProperty(ref field, value);
    }

    public string ProgressText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "処理中...";

    public bool IsCreatingFolder
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;

            RaiseCommandStates();
        }
    }

    public string NewFolderName
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    public AssetItemViewModel? SelectedItem
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            RaiseCommandStates();
        }
    }

    public string? SearchQuery
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            AssetsView.Refresh();
        }
    }

    public bool ShowVideo
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool ShowAudio
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool ShowImage
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool ShowText
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool ShowOther
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool ShowFolder
    {
        get;
        set => SetFilter(ref field, value);
    } = true;

    public bool DownloadedOnly
    {
        get;
        set => SetFilter(ref field, value);
    }

    public bool IsFilteredByType => Criteria.IsFilteredByType;

    private void SetFilter<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;

        OnPropertyChanged(nameof(IsFilteredByType));
        AssetsView.Refresh();
    }

    private ICloudStorageService? Service => Tool.SelectedCloudService.Value?.Service;

    private string? CurrentFolderId => _stack.Count > 0 ? _stack[^1].Id : null;

    private List<string> CurrentSegments => _stack.Skip(1).Select(n => n.Name).ToList();

    private string AssetDirectory
    {
        get
        {
            var settings = Tool.Settings;
            var resolved = PathHelper.ResolvePath(settings.AssetDirectory, settings.ProjectDirectory);

            return string.IsNullOrEmpty(resolved) ? PathHelper.DefaultAssetDirectory : resolved;
        }
    }

    private AssetFilterCriteria Criteria => new(
        SearchQuery, ShowVideo, ShowAudio, ShowImage, ShowText, ShowOther, ShowFolder, DownloadedOnly);

    private bool PassesFilter(object candidate)
        => candidate is AssetItemViewModel item
           && AssetFilter.Matches(Criteria, item.Name, item.IsFolder, item.Category, item.State);

    private void OnServiceChanged(CloudServiceItem? newItem)
    {
        if (_observedItem != null)
            _observedItem.PropertyChanged -= OnServicePropertyChanged;

        _observedItem = newItem;

        ResetTree();

        IsConnected = newItem?.IsConnected == true;
        RaiseCommandStates();

        if (_observedItem == null) return;

        _observedItem.PropertyChanged += OnServicePropertyChanged;

        if (_observedItem.IsConnected) _ = EnterRootAsync();
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CloudServiceItem.IsConnected)) return;

        PostToUi(() =>
        {
            IsConnected = sender is CloudServiceItem { IsConnected: true };
            RaiseCommandStates();

            if (IsConnected) _ = EnterRootAsync();
            else ResetTree();
        });
    }

    private void ResetTree()
    {
        _stack.Clear();
        Breadcrumbs.Clear();
        Items.Clear();
        _history.Clear();
        _historyIndex = -1;
        IsCreatingFolder = false;
    }

    public async Task EnterRootAsync()
    {
        if (Service is not { } service || !service.IsAuthenticated) return;

        try
        {
            var rootId = await CloudAssetRoot.EnsureAsync(service, _lifetime.Token);

            _stack.Clear();
            _stack.Add(new BreadcrumbNode(rootId, CloudAssetRoot.FolderName));

            _history.Clear();
            _historyIndex = -1;

            SyncBreadcrumbs();
            RecordHistory();

            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[AssetTab] Root resolution cancelled.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            Debug.WriteLine($"[AssetTab] Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
    }

    private void SyncBreadcrumbs()
    {
        Breadcrumbs.Clear();

        foreach (var node in _stack) Breadcrumbs.Add(node);

        RaiseCommandStates();
    }

    public bool CanGoBack => _historyIndex > 0;

    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    private void RecordHistory()
    {
        if (_restoringHistory) return;

        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add([.. _stack]);
        _historyIndex = _history.Count - 1;

        RaiseCommandStates();
    }

    private Task GoBackAsync()
    {
        if (!CanGoBack) return Task.CompletedTask;

        _historyIndex--;

        return RestoreHistoryAsync();
    }

    private Task GoForwardAsync()
    {
        if (!CanGoForward) return Task.CompletedTask;

        _historyIndex++;

        return RestoreHistoryAsync();
    }

    private async Task RestoreHistoryAsync()
    {
        _restoringHistory = true;

        try
        {
            _stack.Clear();
            _stack.AddRange(_history[_historyIndex]);

            IsCreatingFolder = false;
            SyncBreadcrumbs();

            await RefreshAsync();
        }
        finally
        {
            _restoringHistory = false;
            RaiseCommandStates();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Service is not { } service || !service.IsAuthenticated) return;
        if (CurrentFolderId is not { } folderId) return;

        CancelPendingRefresh();

        var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _refresh = refresh;

        try
        {
            var entries = await service.ListFilesAsync(folderId, refresh.Token);

            var assetDirectory = AssetDirectory;
            var segments = CurrentSegments;

            var built = entries
                .Select(f => Build(service, assetDirectory, segments, f))
                .ToList();

            Items.Clear();

            foreach (var item in built) Items.Add(item);

            AssetsView.Refresh();
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[AssetTab] Listing cancelled.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            Debug.WriteLine($"[AssetTab] Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            if (ReferenceEquals(_refresh, refresh)) _refresh = null;
            refresh.Dispose();
        }
    }

    private AssetItemViewModel Build(ICloudStorageService service, string assetDirectory,
        List<string> segments, CloudFile file)
    {
        if (file.IsFolder) return NewItem(file, "", AssetState.NotDownloaded);

        if (_activeDownloads.TryGetValue(file.Id, out var running)) return running;

        string localPath;

        try
        {
            localPath = AssetPathMapper.GetLocalPath(assetDirectory, service.ConnectionKey, segments, file.Name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AssetTab] Failed to map {file.Name}: {ex.Message}");
            return NewItem(file, "", AssetState.NotDownloaded);
        }

        var entry = AssetStateStore.Find(service.ConnectionKey, file.Id);
        var exists = File.Exists(localPath);

        var state = AssetStateResolver.Resolve(file.ModifiedTime, file.Size, entry, exists);

        return NewItem(file, localPath, state);
    }

    private AssetItemViewModel NewItem(CloudFile file, string localPath, AssetState state)
        => new(file, localPath, state) { IconPixels = Layout.IconSize };

    private void CancelPendingRefresh()
    {
        if (_refresh == null) return;

        try
        {
            _refresh.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _refresh.Dispose();
        _refresh = null;
    }

    public async Task ActivateAsync(AssetItemViewModel? item)
    {
        if (item == null) return;

        if (item.IsFolder)
        {
            await EnterFolderAsync(item);
            return;
        }

        if (item.State is AssetState.Downloading) return;

        if (item.State is not (AssetState.Downloaded or AssetState.Stale) || !item.HasLocalFile)
        {
            await DownloadAsync(item);
        }

        if (!item.HasLocalFile) return;

        AddToTimeline([item.LocalPath]);
    }

    public void AddToTimeline(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        if (YmmTimeline.TryAddFiles(paths, TimelineCommandSource)) return;

        if (_timelinePlacementReported) return;

        _timelinePlacementReported = true;

        _dialogs.ShowInformation(
            "ダウンロードは完了しましたが、タイムラインへの自動配置ができませんでした。\n\n" +
            "プロジェクトを開いてから、もう一度お試しください。\n" +
            "一覧からタイムラインへドラッグして配置することもできます。",
            "確認");
    }

    public IInputElement? TimelineCommandSource { get; set; }

    private bool _timelinePlacementReported;

    private async Task EnterFolderAsync(AssetItemViewModel item)
    {
        _stack.Add(new BreadcrumbNode(item.Id, item.Name));
        IsCreatingFolder = false;
        SyncBreadcrumbs();
        RecordHistory();

        await RefreshAsync();
    }

    private async Task GoUpAsync()
    {
        if (_stack.Count <= 1) return;

        _stack.RemoveAt(_stack.Count - 1);
        IsCreatingFolder = false;
        SyncBreadcrumbs();
        RecordHistory();

        await RefreshAsync();
    }

    private async Task NavigateToAsync(BreadcrumbNode? node)
    {
        if (node == null) return;

        var index = _stack.FindIndex(n => ReferenceEquals(n, node));
        if (index < 0 || index == _stack.Count - 1) return;

        _stack.RemoveRange(index + 1, _stack.Count - index - 1);
        IsCreatingFolder = false;
        SyncBreadcrumbs();
        RecordHistory();

        await RefreshAsync();
    }

    private async Task DownloadAsync(AssetItemViewModel? item)
    {
        if (item == null || item.IsFolder) return;
        if (Service is not { } service) return;
        if (string.IsNullOrEmpty(item.LocalPath)) return;
        if (!_activeDownloads.TryAdd(item.Id, item)) return;

        var parentId = CurrentFolderId;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        item.DownloadCts = cts;

        item.State = AssetState.Downloading;
        item.Progress = 0;
        item.ErrorMessage = null;

        var progress = new ThrottledProgress(new Progress<double>(p => item.Progress = p));

        try
        {
            await _downloadSlots.WaitAsync(cts.Token);

            try
            {
                var directory = Path.GetDirectoryName(item.LocalPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await service.DownloadFileAsync(item.Id, item.LocalPath, progress, cts.Token);

                AssetStateStore.Save(new AssetStateEntry
                {
                    ConnectionKey = service.ConnectionKey,
                    FileId = item.Id,
                    RemoteModifiedTime = item.File.ModifiedTime,
                    RemoteSize = item.File.Size,
                    LocalPath = item.LocalPath,
                    RemoteParentId = parentId
                });

                PostToUi(() =>
                {
                    item.Progress = 100;
                    item.State = AssetState.Downloaded;

                    if (DownloadedOnly) AssetsView.Refresh();
                });
            }
            finally
            {
                _downloadSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            PostToUi(() =>
            {
                item.Progress = 0;
                item.State = AssetState.NotDownloaded;
            });
        }
        catch (PathTooLongException)
        {
            PostToUi(() => Fail(item,
                "保存先のパスが長すぎます。設定タブでアセット保存先を短いパスに変更するか、フォルダ階層を浅くしてください。"));
        }
        catch (Exception ex)
        {
            PostToUi(() => Fail(item, ex.Message));
        }
        finally
        {
            _activeDownloads.TryRemove(item.Id, out _);
            item.DownloadCts = null;
            cts.Dispose();
        }
    }

    private static void Fail(AssetItemViewModel item, string message)
    {
        item.Progress = 0;
        item.ErrorMessage = message;
        item.State = AssetState.Failed;
    }

    private void CancelDownload(AssetItemViewModel? item)
    {
        if (item?.DownloadCts is not { IsCancellationRequested: false } cts) return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DownloadAndOpenFolderAsync(AssetItemViewModel? item)
    {
        if (item == null || item.IsFolder) return;

        if (!item.HasLocalFile || item.State == AssetState.Stale) await DownloadAsync(item);

        if (!item.HasLocalFile)
        {
            if (item.State == AssetState.Failed)
            {
                _dialogs.ShowWarning(
                    item.ErrorMessage ?? "ダウンロードに失敗したため、フォルダーを開けませんでした。", "確認");
            }

            return;
        }

        OpenContainingFolder(item);
    }

    private void OpenContainingFolder(AssetItemViewModel? item)
    {
        if (item is not { HasLocalFile: true }) return;

        _dialogs.OpenContainingFolder(item.LocalPath);
    }

    private void BeginCreateFolder()
    {
        NewFolderName = "";
        IsCreatingFolder = true;
    }

    private async Task CreateFolderAsync()
    {
        if (Service is not { } service) return;
        if (CurrentFolderId is not { } parentId) return;

        var name = PathHelper.SanitizeFileName(NewFolderName, "");

        if (string.IsNullOrWhiteSpace(name))
        {
            _dialogs.ShowWarning("フォルダ名を入力してください。ファイル名に使えない文字は使用できません。", "確認");
            return;
        }

        if (!TryBeginProcessing("フォルダを作成中...")) return;

        var token = _operation!.Token;

        try
        {
            await service.CreateFolderAsync(parentId, name, token);

            IsCreatingFolder = false;
            NewFolderName = "";

            await RefreshAsync(token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[AssetTab] Create folder cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            EndProcessing();
        }
    }

    private async Task PickAndUploadAsync()
    {
        var paths = _dialogs.PickFilesToUpload("アップロードする素材を選択");

        if (paths is not { Length: > 0 }) return;

        await UploadFilesAsync(paths);
    }

    public async Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (Service is not { } service) return;
        if (CurrentFolderId is not { } parentId) return;

        var files = paths.Where(File.Exists).ToList();

        if (files.Count == 0)
        {
            _dialogs.ShowInformation("アップロードできるファイルがありませんでした。フォルダはまとめてアップロードできません。", "確認");
            return;
        }

        if (!TryBeginProcessing("アップロード中...")) return;

        var token = _operation!.Token;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var name = PathHelper.SanitizeFileName(Path.GetFileName(path), "asset");

                SetStage($"アップロード中... ({i + 1}/{files.Count}) {name}");

                var label = $"アップロード中... ({i + 1}/{files.Count})";

                await service.UploadFileToFolderAsync(path, parentId, name, CreateProgress(label), token);
            }

            await RefreshAsync(token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[AssetTab] Upload cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            EndProcessing();
        }
    }

    private async Task DeleteAsync(AssetItemViewModel? item)
    {
        if (item == null || Service is not { } service) return;

        if (!TryBeginProcessing("削除中...")) return;

        var token = _operation!.Token;

        try
        {
            if (item.IsFolder)
            {
                var children = await service.ListFilesAsync(item.Id, token);

                if (children.Count > 0)
                {
                    _dialogs.ShowWarning(
                        $"「{item.Name}」は空ではありません。\n\n" +
                        "中身のあるフォルダの削除は取り消せないため、先に中のファイルを削除してください。",
                        "削除できません");
                    return;
                }
            }

            var message = item.IsFolder
                ? $"フォルダ「{item.Name}」を削除しますか？\n\nこの操作は取り消せません。"
                : $"「{item.Name}」をクラウドから削除しますか？\n\n" +
                  "この操作は取り消せません。ダウンロード済みのローカルファイルは削除されません。";

            if (!_dialogs.Confirm(message, "削除の確認")) return;

            await service.DeleteFileAsync(item.Id, token);

            AssetStateStore.Remove(service.ConnectionKey, item.Id);

            await RefreshAsync(token);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[AssetTab] Delete cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            EndProcessing();
        }
    }

    private bool TryBeginProcessing(string message)
    {
        if (Interlocked.CompareExchange(ref _processingState, Busy, Idle) != Idle) return false;

        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        IsProcessing = true;
        CanCancel = true;
        ProgressValue = 0;
        ProgressText = message;
        RaiseCommandStates();

        return true;
    }

    private void EndProcessing()
    {
        _operation?.Dispose();
        _operation = null;

        Volatile.Write(ref _processingState, Idle);

        IsProcessing = false;
        CanCancel = false;
        ProgressValue = 0;
        ProgressText = "処理中...";
        RaiseCommandStates();
    }

    private void SetStage(string message)
    {
        ProgressValue = 0;
        ProgressText = message;
    }

    private void Cancel()
    {
        if (_operation is not { IsCancellationRequested: false }) return;

        CanCancel = false;
        ProgressText = "中止しています...";
        CancelCommand.RaiseCanExecuteChanged();

        _operation.Cancel();
    }

    private IProgress<double> CreateProgress(string label) => new ThrottledProgress(new Progress<double>(p =>
    {
        ProgressValue = p;
        ProgressText = $"{label} {p:F0}%";
    }));

    private void PostToUi(Action action)
    {
        if (_uiContext == null || _uiContext == SynchronizationContext.Current) action();
        else _uiContext.Post(_ => action(), null);
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        GoUpCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        NavigateCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        DownloadAndOpenFolderCommand.RaiseCanExecuteChanged();
        AddToTimelineCommand.RaiseCanExecuteChanged();
        BeginCreateFolderCommand.RaiseCanExecuteChanged();
        ConfirmCreateFolderCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CancelPendingRefresh();

        _operation?.Dispose();
        _operation = null;

        if (_observedItem != null)
        {
            _observedItem.PropertyChanged -= OnServicePropertyChanged;
            _observedItem = null;
        }

        _serviceSubscription.Dispose();
        _downloadSlots.Dispose();
        _lifetime.Dispose();
    }
}
