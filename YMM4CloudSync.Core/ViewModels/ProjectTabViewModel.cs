using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Core.ViewModels;

public sealed class ProjectTabViewModel : INotifyPropertyChanged, IDisposable
{
    public const string EmptyProjectReason = "プロジェクトが空です。シーンにアイテムを追加すると保存できます。";

    private const int Idle = 0;
    private const int Busy = 1;

    private readonly IProjectDialogService _dialogs;
    private readonly SynchronizationContext? _uiContext;
    private readonly IDisposable _serviceSubscription;

    private CloudServiceItem? _observedItem;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _refresh;
    private int _processingState = Idle;
    private bool _disposed;

    public ProjectTabViewModel(ToolViewModel tool, IProjectDialogService dialogs)
    {
        Tool = tool;
        _dialogs = dialogs;
        _uiContext = SynchronizationContext.Current;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => IsConnected && !IsProcessing);
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => IsConnected && !IsProcessing && !IsProjectEmpty);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsProcessing && !IsProjectEmpty);
        OpenCommand = new AsyncRelayCommand(p => OpenAsync(p as CloudFile), p => p is CloudFile && !IsProcessing);
        DownloadCommand = new AsyncRelayCommand(p => DownloadAsync(p as CloudFile), _ => !IsProcessing);
        DeleteCommand = new AsyncRelayCommand(p => DeleteAsync(p as CloudFile), p => p is CloudFile && !IsProcessing);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);

        _serviceSubscription = Tool.SelectedCloudService.Subscribe(OnServiceChanged);
    }

    public ToolViewModel Tool { get; }

    public ObservableCollection<CloudFile> Files { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand CancelCommand { get; }

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

    public bool IsProjectEmpty
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

    public string? UploadBlockedReason => IsProjectEmpty ? EmptyProjectReason : null;

    private ICloudStorageService? Service => Tool.SelectedCloudService.Value?.Service;

    public void RefreshProjectState()
    {
        var isEmpty = YmmHelper.IsProjectEmpty() == true;

        if (isEmpty == IsProjectEmpty) return;

        IsProjectEmpty = isEmpty;
        OnPropertyChanged(nameof(UploadBlockedReason));
        RaiseCommandStates();
    }

    private void OnServiceChanged(CloudServiceItem? newItem)
    {
        if (_observedItem != null)
            _observedItem.PropertyChanged -= OnServicePropertyChanged;

        _observedItem = newItem;

        Files.Clear();
        IsConnected = newItem?.IsConnected == true;
        RaiseCommandStates();

        if (_observedItem == null) return;

        _observedItem.PropertyChanged += OnServicePropertyChanged;

        if (_observedItem.IsConnected) _ = RefreshAsync();
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CloudServiceItem.IsConnected)) return;

        void Apply()
        {
            IsConnected = sender is CloudServiceItem { IsConnected: true };
            RaiseCommandStates();

            if (IsConnected) _ = RefreshAsync();
        }

        if (_uiContext == null || _uiContext == SynchronizationContext.Current) Apply();
        else _uiContext.Post(_ => Apply(), null);
    }

    private bool TryBeginProcessing(string message)
    {
        if (Interlocked.CompareExchange(ref _processingState, Busy, Idle) != Idle) return false;

        _operation = new CancellationTokenSource();

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

    private IProgress<double> CreateProgress(string label) => new Progress<double>(p =>
    {
        ProgressValue = p;
        ProgressText = $"{label} {p:F0}%";
    });

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Service is not { } service || !service.IsAuthenticated) return;

        CancelPendingRefresh();

        var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refresh = refresh;

        try
        {
            var files = await service.ListFilesAsync(null, refresh.Token);

            Files.Clear();

            foreach (var file in files.Where(IsProjectFile))
                Files.Add(file);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Listing cancelled.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            Debug.WriteLine($"[ProjectTab] Network error: {ex.Message}");
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

    private static bool IsProjectFile(CloudFile file)
        => !file.IsFolder && file.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase);

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

    private async Task OpenAsync(CloudFile? file)
    {
        if (file == null || Service is not { } service) return;
        if (!TryBeginProcessing("ダウンロード中...")) return;

        var token = _operation!.Token;
        string? tempPath = null;

        try
        {
            var settings = Tool.Settings;

            var projectRootDir = PathHelper.ResolveProjectDirectory(settings.ProjectDirectory);

            var cacheDir = PathHelper.ResolvePath(settings.CacheDirectory, settings.ProjectDirectory);
            if (string.IsNullOrEmpty(cacheDir)) cacheDir = PathHelper.DefaultCacheDirectory;
            Directory.CreateDirectory(cacheDir);

            tempPath = PathHelper.CombineWithin(cacheDir, file.Name, "project.ymmx");

            await service.DownloadFileAsync(file.Id, tempPath, CreateProgress("ダウンロード中..."), token);

            SetStage("展開中...");

            var projectName = Path.GetFileNameWithoutExtension(PathHelper.SanitizeFileName(file.Name, "project.ymmx"));
            var outputDir = PathHelper.CombineWithin(projectRootDir, projectName, "project");

            var extractPath = tempPath;
            var result = await Task.Run(() => YmmxExtractor.Extract(
                extractPath, outputDir, _dialogs.ResolveExtractConflict, token), token);

            if (!result.Success) return;

            if (result.HashMismatch && !ConfirmOpenDespiteHashMismatch()) return;

            if (result.ExternalReferences.Count > 0)
            {
                _dialogs.ShowWarning(ExternalReferenceNotice.Build(result.ExternalReferences), "確認");
            }

            if (!string.IsNullOrEmpty(result.BackupDirectory))
            {
                await Task.Run(() => CleanupOldBackups(Path.GetDirectoryName(outputDir)!, projectName), token);
            }

            LaunchYmm(result.YmmpPath);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Open cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            if (tempPath != null) DeleteQuietly(tempPath);
            EndProcessing();
        }
    }

    private bool ConfirmOpenDespiteHashMismatch() => _dialogs.Confirm(
        "ダウンロードしたファイルのハッシュ値が一致しません。\nファイルが破損している可能性があります。\n\nこのままプロジェクトを開きますか？",
        "警告");

    private void LaunchYmm(string ymmpPath)
    {
        var ymmPath = YmmPathFinder.Find();

        if (ymmPath == null)
        {
            _dialogs.ShowInformation(
                $"展開が完了しました。\n{ymmpPath}\n\nYMM4 が見つからなかったため、手動で開いてください。", "完了");
            return;
        }

        var startInfo = new ProcessStartInfo { FileName = ymmPath, UseShellExecute = true };
        startInfo.ArgumentList.Add(ymmpPath);

        Process.Start(startInfo);
    }

    private bool EnsureProjectHasContent()
    {
        RefreshProjectState();

        if (!IsProjectEmpty) return true;

        _dialogs.ShowInformation(EmptyProjectReason, "確認");

        return false;
    }

    private string? SaveAndResolveProjectPath()
    {
        switch (YmmHelper.SaveProject())
        {
            case SaveResult.Cancelled:
                return null;
            case SaveResult.Failed:
                _dialogs.ShowError("プロジェクトの保存に失敗しました。", "エラー");
                return null;
            case SaveResult.Success:
            default:
                break;
        }

        var ymmpPath = YmmHelper.GetCurrentProjectPath();

        if (!string.IsNullOrEmpty(ymmpPath)) return ymmpPath;

        _dialogs.ShowWarning("プロジェクトパスが取得できませんでした。", "エラー");

        return null;
    }

    private async Task ExportAsync()
    {
        if (!EnsureProjectHasContent()) return;

        var ymmpPath = SaveAndResolveProjectPath();
        if (ymmpPath == null) return;

        var projectName = Path.GetFileNameWithoutExtension(ymmpPath);

        var destination = _dialogs.PickYmmxDestination("YMMX ファイルの書き出し先を選択", $"{projectName}.ymmx");
        if (destination == null) return;

        if (!TryBeginProcessing("パッケージ作成中...")) return;

        var token = _operation!.Token;
        var stagingPath = destination + ".tmp";

        try
        {
            var packProgress = CreateProgress("パッケージ作成中...");

            var packResult = await Task.Run(
                () => YmmxPacker.Pack(ymmpPath, stagingPath, projectName, packProgress, token), token);

            if (!packResult.Success)
            {
                _dialogs.ShowError("パッケージの作成に失敗しました。", "エラー");
                return;
            }

            File.Move(stagingPath, destination, overwrite: true);

            var message = $"書き出しが完了しました。\n{destination}";

            if (packResult.MissingFiles.Count > 0)
                message += $"\n\n見つからなかったファイル: {packResult.MissingFiles.Count} 件";

            if (_dialogs.AskYesNo($"{message}\n\n保存先のフォルダを開きますか？", "完了"))
                _dialogs.OpenContainingFolder(destination);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Export cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            DeleteQuietly(stagingPath);
            EndProcessing();
        }
    }

    private async Task UploadAsync()
    {
        if (Service is not { } service) return;

        if (!EnsureProjectHasContent()) return;

        var ymmpPath = SaveAndResolveProjectPath();
        if (ymmpPath == null) return;

        if (!TryBeginProcessing("保存中...")) return;

        var token = _operation!.Token;
        string? tempYmmxPath = null;

        try
        {
            var settings = Tool.Settings;
            var projectName = Path.GetFileNameWithoutExtension(ymmpPath);

            var cacheDir = PathHelper.ResolvePath(settings.CacheDirectory, settings.ProjectDirectory);
            if (string.IsNullOrEmpty(cacheDir)) cacheDir = PathHelper.DefaultCacheDirectory;
            Directory.CreateDirectory(cacheDir);

            tempYmmxPath = PathHelper.CombineWithin(cacheDir, $"{projectName}.ymmx", "project.ymmx");

            SetStage("パッケージ作成中...");

            var packPath = tempYmmxPath;
            var packProgress = CreateProgress("パッケージ作成中...");

            var packResult = await Task.Run(
                () => YmmxPacker.Pack(ymmpPath, packPath, projectName, packProgress, token), token);

            if (!packResult.Success)
            {
                _dialogs.ShowError("パッケージの作成に失敗しました。", "エラー");
                return;
            }

            SetStage("アップロード中...");

            await service.UploadFileAsync(tempYmmxPath, $"{projectName}.ymmx",
                CreateProgress("アップロード中..."), token);

            await RefreshAsync(token);

            var message = "保存が完了しました。";

            if (packResult.MissingFiles.Count > 0)
            {
                message += $"\n\n見つからなかったファイル: {packResult.MissingFiles.Count} 件";
                _dialogs.ShowWarning(message, "完了");
            }
            else
            {
                _dialogs.ShowInformation(message, "完了");
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Upload cancelled.");
        }
        catch (Exception ex)
        {
            _dialogs.ReportException(ex);
        }
        finally
        {
            if (tempYmmxPath != null) DeleteQuietly(tempYmmxPath);
            EndProcessing();
        }
    }

    private async Task DownloadAsync(CloudFile? file)
    {
        if (file == null)
        {
            _dialogs.ShowInformation("ダウンロードするファイルを選択してください。", "確認");
            return;
        }

        if (Service is not { } service) return;

        var destination = _dialogs.PickYmmxDestination(
            "保存先を選択", PathHelper.SanitizeFileName(file.Name, "project.ymmx"));
        if (destination == null) return;

        if (!TryBeginProcessing("ダウンロード中...")) return;

        var token = _operation!.Token;

        try
        {
            await service.DownloadFileAsync(file.Id, destination, CreateProgress("ダウンロード中..."), token);

            _dialogs.ShowInformation($"ダウンロードが完了しました。\n{destination}", "完了");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Download cancelled.");
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

    private async Task DeleteAsync(CloudFile? file)
    {
        if (file == null || Service is not { } service) return;

        if (!_dialogs.Confirm($"「{file.Name}」を削除しますか？\n\nこの操作は取り消せません。", "削除の確認")) return;

        if (!TryBeginProcessing("削除中...")) return;

        var token = _operation!.Token;

        try
        {
            await service.DeleteFileAsync(file.Id, token);
            await RefreshAsync(token);

            _dialogs.ShowInformation("削除が完了しました。", "完了");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Delete cancelled.");
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

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);

            var tempPath = path + ".tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProjectTab] Failed to delete {path}: {ex.Message}");
        }
    }

    private static void CleanupOldBackups(string projectsDir, string projectName)
    {
        try
        {
            var backups = Directory.GetDirectories(projectsDir, $"{projectName}_bak_*")
                .OrderByDescending(d => d)
                .ToList();

            const int keepCount = 3;

            if (backups.Count <= keepCount) return;

            for (var i = keepCount; i < backups.Count; i++)
            {
                try { Directory.Delete(backups[i], true); } catch { /* ignored */ }
            }
        }
        catch { /* ignored */ }
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
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

        CancelPendingRefresh();

        _operation?.Dispose();
        _operation = null;

        if (_observedItem != null)
        {
            _observedItem.PropertyChanged -= OnServicePropertyChanged;
            _observedItem = null;
        }

        _serviceSubscription.Dispose();
    }
}
