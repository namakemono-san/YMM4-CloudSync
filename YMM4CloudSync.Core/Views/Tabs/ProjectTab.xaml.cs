using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.ViewModels;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class ProjectTab : UserControl
{
    private volatile bool _isProcessing;
    private IDisposable? _subscription;
    private CloudServiceItem? _observedItem;
    
    private ToolViewModel? ViewModel => DataContext as ToolViewModel;

    public ProjectTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        
        _subscription = ViewModel.SelectedCloudService.Subscribe(OnServiceChanged);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _subscription?.Dispose();
        
        if (_observedItem != null)
        {
            _observedItem.PropertyChanged -= OnServicePropertyChanged;
            _observedItem = null;
        }
    }

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
        var notProcessing = !_isProcessing;
        
        UploadButton.IsEnabled = notProcessing && isConnected;
        RefreshButton.IsEnabled = notProcessing && isConnected;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        await RefreshFileListAsync();
    }

    private async Task RefreshFileListAsync()
    {
        if (ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;
        if (!svc.IsAuthenticated) return;

        try
        {
            var files = await svc.ListFilesAsync();
            var ymmxFiles = files.Where(f => f.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase)).ToList();
            CloudFilesList.ItemsSource = ymmxFiles;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
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
        if (_isProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        _isProcessing = true;
        SetProcessingState(true, "ダウンロード中...");

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "YMM4CloudSync");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, file.Name);

            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"ダウンロード中... {p:F0}%";
            });

            await svc.DownloadFileAsync(file.Id, tempPath, progress);

            SetProcessingState(true, "展開中...");

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var projectName = Path.GetFileNameWithoutExtension(file.Name);
            var outputDir = Path.Combine(appData, "YMM4CloudSync", "Projects", projectName);

            var result = await Task.Run(() => YmmxExtractor.Extract(tempPath, outputDir));

            if (result.HashMismatch)
            {
                MessageBox.Show(
                    "ダウンロードしたファイルのハッシュ値が一致しません。\nファイルが破損している可能性があります。\n\n問題がある場合は再度ダウンロードしてください。",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.BackupDirectory))
                {
                    await Task.Run(() => CleanupOldBackups(Path.GetDirectoryName(outputDir)!, projectName));
                }

                var ymmPath = YmmPathFinder.Find();
                if (ymmPath != null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ymmPath,
                        Arguments = $"\"{result.YmmpPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show($"展開が完了しました。\n{result.YmmpPath}\n\nYMM4 が見つからなかったため、手動で開いてください。",
                        "完了", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }

            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
        }
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        _isProcessing = true;
        SetProcessingState(true, "保存中...");

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
            var tempYmmxPath = Path.Combine(Path.GetTempPath(), $"{projectName}.ymmx");

            SetProcessingState(true, "パッケージ作成中...");
            
            var packProgress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"パッケージ作成中... {p:F0}%";
            });
            
            var packResult = await Task.Run(() => YmmxPacker.Pack(ymmpPath, tempYmmxPath, projectName, packProgress));

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

            await svc.UploadFileAsync(tempYmmxPath, $"{projectName}.ymmx", progress);

            try { File.Delete(tempYmmxPath); } catch { /* ignored */ }

            await RefreshFileListAsync();

            var message = "保存が完了しました。";
            if (packResult.MissingFiles.Count > 0)
                message += $"\n\n見つからなかったファイル: {packResult.MissingFiles.Count} 件";

            MessageBox.Show(message, "完了", MessageBoxButton.OK,
                packResult.MissingFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
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

        if (_isProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "保存先を選択",
            Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
            FileName = file.Name
        };

        if (saveDialog.ShowDialog() != true) return;

        _isProcessing = true;
        SetProcessingState(true, "ダウンロード中...");

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"ダウンロード中... {p:F0}%";
            });

            await svc.DownloadFileAsync(file.Id, saveDialog.FileName, progress);

            MessageBox.Show($"ダウンロードが完了しました。\n{saveDialog.FileName}", "完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var file = GetCloudFileFromSender(sender);
        if (file == null) return;
        if (_isProcessing || ViewModel?.SelectedCloudService.Value?.Service is not { } svc) return;

        var result = MessageBox.Show(
            $"「{file.Name}」を削除しますか？\n\nこの操作は取り消せません。",
            "削除の確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _isProcessing = true;
        SetProcessingState(true, "削除中...");

        try
        {
            await svc.DeleteFileAsync(file.Id);
            await RefreshFileListAsync();
            MessageBox.Show("削除が完了しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
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
        
        UpdateUiState();
    }
}