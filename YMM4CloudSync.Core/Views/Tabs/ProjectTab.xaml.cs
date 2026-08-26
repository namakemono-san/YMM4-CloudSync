using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.ViewModels;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class ProjectTab : UserControl
{
    private const int Idle = 0;
    private const int Busy = 1;

    private const string EmptyProjectTooltip = "プロジェクトが空です。シーンにアイテムを追加すると保存できます。";

    private int _processingState = Idle;
    private CancellationTokenSource? _cancellation;
    private IDisposable? _subscription;
    private CloudServiceItem? _observedItem;
    private DispatcherTimer? _projectStateTimer;
    private CancellationTokenSource? _refreshCancellation;

    private bool IsProcessing => Volatile.Read(ref _processingState) == Busy;

    private ToolViewModel? ViewModel => DataContext as ToolViewModel;

    public ProjectTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartProjectStateWatch();

        if (ViewModel == null) return;

        _subscription?.Dispose();
        _subscription = ViewModel.SelectedCloudService.Subscribe(OnServiceChanged);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopProjectStateWatch();
        CancelPendingRefresh();

        _subscription?.Dispose();
        _subscription = null;

        if (_observedItem != null)
        {
            _observedItem.PropertyChanged -= OnServicePropertyChanged;
            _observedItem = null;
        }
    }

    private void CancelPendingRefresh()
    {
        if (_refreshCancellation == null) return;

        try
        {
            _refreshCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        _refreshCancellation.Dispose();
        _refreshCancellation = null;
    }

    private void StartProjectStateWatch()
    {
        if (_projectStateTimer != null) return;

        _projectStateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _projectStateTimer.Tick += OnProjectStateTick;
        _projectStateTimer.Start();
    }

    private void StopProjectStateWatch()
    {
        if (_projectStateTimer == null) return;

        _projectStateTimer.Stop();
        _projectStateTimer.Tick -= OnProjectStateTick;
        _projectStateTimer = null;
    }

    private void OnProjectStateTick(object? sender, EventArgs e) => UpdateUiState();

    private void OnServiceChanged(CloudServiceItem? newItem)
    {
        if (_observedItem != null)
        {
            _observedItem.PropertyChanged -= OnServicePropertyChanged;
        }

        _observedItem = newItem;
        CloudFilesList.ItemsSource = null;
        UpdateUiState();

        if (_observedItem == null) return;

        _observedItem.PropertyChanged += OnServicePropertyChanged;

        if (_observedItem.IsConnected)
        {
            _ = RefreshFileListAsync();
        }
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudServiceItem.IsConnected))
        {
            Dispatcher.Invoke(() =>
            {
                UpdateUiState();
                if (sender is CloudServiceItem { IsConnected: true })
                {
                    _ = RefreshFileListAsync();
                }
            });
        }
    }

    private void UpdateUiState()
    {
        var isConnected = ViewModel?.SelectedCloudService.Value?.IsConnected == true;
        var notProcessing = !IsProcessing;
        var hasContent = YmmHelper.IsProjectEmpty() != true;

        UploadButton.IsEnabled = notProcessing && isConnected && hasContent;
        UploadButton.ToolTip = hasContent ? null : EmptyProjectTooltip;
        RefreshButton.IsEnabled = notProcessing && isConnected;
    }

    private bool TryBeginProcessing(string message)
    {
        if (Interlocked.CompareExchange(ref _processingState, Busy, Idle) != Idle)
            return false;

        _cancellation = new CancellationTokenSource();
        SetProcessingState(true, message);
        return true;
    }

    private void EndProcessing()
    {
        _cancellation?.Dispose();
        _cancellation = null;

        Volatile.Write(ref _processingState, Idle);
        SetProcessingState(false);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not { IsCancellationRequested: false }) return;

        CancelButton.IsEnabled = false;
        ProgressText.Text = "中止しています...";
        _cancellation.Cancel();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (IsProcessing) return;
        await RefreshFileListAsync();
    }

    private async Task RefreshFileListAsync(CancellationToken cancellationToken = default)
    {
        if (ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;
        if (!svc.IsAuthenticated) return;

        CancelPendingRefresh();

        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refreshCancellation = refreshCancellation;

        try
        {
            var files = await svc.ListFilesAsync(null, refreshCancellation.Token);
            var ymmxFiles = files
                .Where(f => !f.IsFolder && f.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase))
                .ToList();
            CloudFilesList.ItemsSource = ymmxFiles;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("通信がキャンセルまたはタイムアウトしました。");
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException)
        {
            Debug.WriteLine($"通信エラー: {ex.Message}");
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refreshCancellation))
            {
                _refreshCancellation = null;
            }

            refreshCancellation.Dispose();
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CloudFilesList.SelectedItem is CloudFile f)
            _ = OpenProjectAsync(f);
    }

    private CloudFile? GetCloudFileFromSender(object sender)
    {
        return sender switch
        {
            Button { Tag: CloudFile file } => file,
            MenuItem => CloudFilesList.SelectedItem as CloudFile,
            _ => null
        };
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var file = GetCloudFileFromSender(sender);
        if (file != null) await OpenProjectAsync(file);
    }

    private async Task OpenProjectAsync(CloudFile file)
    {
        if (ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;
        if (ViewModel.Settings == null) return;
        if (!TryBeginProcessing("ダウンロード中...")) return;

        var token = _cancellation!.Token;
        string? tempPath = null;

        try
        {
            var projectRootDir = PathHelper.ResolveProjectDirectory(ViewModel.Settings.ProjectDirectory);

            var cacheDir = PathHelper.ResolvePath(ViewModel.Settings.CacheDirectory, ViewModel.Settings.ProjectDirectory);
            if (string.IsNullOrEmpty(cacheDir)) cacheDir = PathHelper.DefaultCacheDirectory;
            Directory.CreateDirectory(cacheDir);

            tempPath = PathHelper.CombineWithin(cacheDir, file.Name, "project.ymmx");

            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"ダウンロード中... {p:F0}%";
            });

            await svc.DownloadFileAsync(file.Id, tempPath, progress, token);

            SetProcessingState(true, "展開中...");

            var projectName = Path.GetFileNameWithoutExtension(PathHelper.SanitizeFileName(file.Name, "project.ymmx"));
            var outputDir = PathHelper.CombineWithin(projectRootDir, projectName, "project");

            var extractPath = tempPath;
            var result = await Task.Run(() => YmmxExtractor.Extract(
                extractPath,
                outputDir,
                (existing, incoming) => Dispatcher.Invoke(() => ResolveExtractConflict(existing, incoming)),
                token), token);

            if (!result.Success) return;

            if (result.HashMismatch && !ConfirmOpenDespiteHashMismatch()) return;

            NotifyExternalReferences(result.ExternalReferences);

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
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            if (tempPath != null) DeleteQuietly(tempPath);
            EndProcessing();
        }
    }

    private static ExtractConflictAction ResolveExtractConflict(YmmxMeta? existing, YmmxMeta? incoming)
    {
        _ = incoming;

        if (existing == null) return ExtractConflictAction.Overwrite;

        var existingDate = existing.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

        var message = "同じ場所に既存のプロジェクトがあります。\n\n" +
                      $"既存: {existing.Name}\n" +
                      $"更新日時: {existingDate}\n\n" +
                      "上書きしますか？";

        var result = MessageBox.Show(
            message,
            "確認",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => ExtractConflictAction.Overwrite,
            MessageBoxResult.No => ExtractConflictAction.CreateNew,
            _ => ExtractConflictAction.Cancel
        };
    }

    private static void NotifyExternalReferences(List<string> externalReferences)
    {
        if (externalReferences.Count == 0) return;

        MessageBox.Show(
            ExternalReferenceNotice.Build(externalReferences),
            "確認",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static bool ConfirmOpenDespiteHashMismatch()
    {
        var result = MessageBox.Show(
            "ダウンロードしたファイルのハッシュ値が一致しません。\nファイルが破損している可能性があります。\n\nこのままプロジェクトを開きますか？",
            "警告",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private static void LaunchYmm(string ymmpPath)
    {
        var ymmPath = YmmPathFinder.Find();

        if (ymmPath == null)
        {
            MessageBox.Show($"展開が完了しました。\n{ymmpPath}\n\nYMM4 が見つからなかったため、手動で開いてください。",
                "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ymmPath,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ymmpPath);

        Process.Start(startInfo);
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        if (YmmHelper.IsProjectEmpty() == true)
        {
            MessageBox.Show(EmptyProjectTooltip, "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateUiState();
            return;
        }

        if (!TryBeginProcessing("保存中...")) return;

        var token = _cancellation!.Token;
        string? tempYmmxPath = null;

        try
        {
            var saveResult = YmmHelper.SaveProject();

            switch (saveResult)
            {
                case SaveResult.Cancelled:
                    return;
                case SaveResult.Failed:
                    MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                case SaveResult.Success:
                default:
                    break;
            }

            var ymmpPath = YmmHelper.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(ymmpPath))
            {
                MessageBox.Show("プロジェクトパスが取得できませんでした。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var projectName = Path.GetFileNameWithoutExtension(ymmpPath);
            var cacheDir = PathHelper.ResolvePath(ViewModel.Settings.CacheDirectory, ViewModel.Settings.ProjectDirectory);
            if (string.IsNullOrEmpty(cacheDir)) cacheDir = PathHelper.DefaultCacheDirectory;
            Directory.CreateDirectory(cacheDir);

            tempYmmxPath = PathHelper.CombineWithin(cacheDir, $"{projectName}.ymmx", "project.ymmx");

            SetProcessingState(true, "パッケージ作成中...");

            var packProgress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"パッケージ作成中... {p:F0}%";
            });

            var packPath = tempYmmxPath;
            var packResult = await Task.Run(
                () => YmmxPacker.Pack(ymmpPath, packPath, projectName, packProgress, token), token);

            if (!packResult.Success)
            {
                MessageBox.Show("パッケージの作成に失敗しました。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetProcessingState(true, "アップロード中...");
            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"アップロード中... {p:F0}%";
            });

            await svc.UploadFileAsync(tempYmmxPath, $"{projectName}.ymmx", progress, token);

            await RefreshFileListAsync(token);

            var message = "保存が完了しました。";
            if (packResult.MissingFiles.Count > 0)
                message += $"\n\n見つからなかったファイル: {packResult.MissingFiles.Count} 件";

            MessageBox.Show(message, "完了", MessageBoxButton.OK,
                packResult.MissingFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Upload cancelled.");
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            if (tempYmmxPath != null) DeleteQuietly(tempYmmxPath);
            EndProcessing();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var file = GetCloudFileFromSender(sender);
        if (file == null)
        {
            MessageBox.Show("ダウンロードするファイルを選択してください。", "確認",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "保存先を選択",
            Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
            FileName = PathHelper.SanitizeFileName(file.Name, "project.ymmx")
        };

        if (saveDialog.ShowDialog() != true) return;

        if (!TryBeginProcessing("ダウンロード中...")) return;

        var token = _cancellation!.Token;

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"ダウンロード中... {p:F0}%";
            });

            await svc.DownloadFileAsync(file.Id, saveDialog.FileName, progress, token);

            MessageBox.Show($"ダウンロードが完了しました。\n{saveDialog.FileName}", "完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Download cancelled.");
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            EndProcessing();
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var file = GetCloudFileFromSender(sender);
        if (file == null) return;
        if (IsProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        var confirm = MessageBox.Show(
            $"「{file.Name}」を削除しますか？\n\nこの操作は取り消せません。",
            "削除の確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        if (!TryBeginProcessing("削除中...")) return;

        var token = _cancellation!.Token;

        try
        {
            await svc.DeleteFileAsync(file.Id, token);
            await RefreshFileListAsync(token);
            MessageBox.Show("削除が完了しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ProjectTab] Delete cancelled.");
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
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

    private void CleanupOldBackups(string projectsDir, string projectName)
    {
        try
        {
            var pattern = $"{projectName}_bak_*";
            var backups = Directory.GetDirectories(projectsDir, pattern)
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

    private void SetProcessingState(bool isProcessing, string? message = null)
    {
        ProgressPanel.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = message ?? "処理中...";
        ProgressBar.Value = 0;
        CancelButton.IsEnabled = isProcessing;

        UpdateUiState();
    }
}
