using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Reactive.Bindings;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Commons.License;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Commons;

namespace YMM4CloudSync.Core.Views;

[SuppressMessage("ReSharper", "AsyncVoidMethod")]
public partial class ToolView
{
    // ReSharper disable MemberCanBePrivate.Global
    public ObservableCollection<LicenseTextViewModel> Licenses { get; } = [];
    public ReactiveProperty<LicenseTextViewModel?> CurrentLicense { get; } = new();

    public ObservableCollection<CloudServiceItem> CloudServices { get; } = [];
    public ReactiveProperty<CloudServiceItem?> SelectedCloudService { get; } = new();
    // ReSharper restore MemberCanBePrivate.Global

    private bool _isProcessing;

    private ICloudStorageService? CurrentService => SelectedCloudService.Value?.Service;
    private bool IsConnected => SelectedCloudService.Value?.IsConnected == true;

    public ToolView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;

        CloudServices.Add(new CloudServiceItem(new GoogleDriveService()));
        CloudServices.Add(new CloudServiceItem(new OneDriveService()));

        SelectedCloudService.Value = CloudServices.FirstOrDefault();

        SelectedCloudService.Subscribe(async void (_) =>
        {
            CloudFilesList.ItemsSource = null;

            var ok = IsConnected;
            UploadButton.IsEnabled = ok;
            RefreshButton.IsEnabled = ok;

            if (ok)
                await RefreshFileListAsync();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadVersionInfo();
        LoadChangelog();
        LoadLicense();

        await TryAutoConnectAsync();
    }

    private void LoadVersionInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "1.0.0"}";
    }

    private void LoadChangelog()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var changelogPath = Path.Combine(pluginDir, "Resources", "ChangeLog.txt");

            ChangelogText.Text = File.Exists(changelogPath)
                ? File.ReadAllText(changelogPath)
                : "更新履歴ファイルが見つかりません。";
        }
        catch (Exception ex)
        {
            ChangelogText.Text = $"読み込みに失敗しました: {ex.Message}";
        }
    }

    private void LoadLicense()
    {
        var licenses = LicenseLoader.Load()
            .Select(x => new LicenseTextViewModel(x))
            .OrderBy(x => x.Name)
            .ToList();

        Licenses.Clear();
        foreach (var l in licenses)
            Licenses.Add(l);

        CurrentLicense.Value = Licenses.FirstOrDefault();
    }

    private async Task TryAutoConnectAsync()
    {
        foreach (var item in CloudServices)
        {
            try
            {
                var ok = await item.Service.AuthenticateAsync();
                item.IsConnected = ok && item.Service.IsAuthenticated;
            }
            catch
            {
                item.IsConnected = false;
            }
        }

        var connected = IsConnected;
        UploadButton.IsEnabled = connected;
        RefreshButton.IsEnabled = connected;
        
        if (IsConnected)
            await RefreshFileListAsync();
    }

    private async void OnServiceToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        if (sender is not Button b) return;
        if (b.Tag is not CloudServiceItem item) return;

        _isProcessing = true;
        try
        {
            if (item.IsConnected)
            {
                var r = MessageBox.Show($"{item.Name} との接続を解除しますか？", "確認",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;

                await item.Service.LogoutAsync();
                item.IsConnected = false;

                if (SelectedCloudService.Value == item)
                    CloudFilesList.ItemsSource = null;
            }
            else
            {
                bool ok;
                
                if (item.Service is OneDriveService one)
                    ok = await one.AuthenticateInteractiveAsync();
                else
                    ok = await item.Service.AuthenticateAsync();
                
                item.IsConnected = ok && item.Service.IsAuthenticated;

                if (SelectedCloudService.Value == item && item.IsConnected)
                    await RefreshFileListAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
            throw;
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || !IsConnected) return;
        await RefreshFileListAsync();
    }

    private async Task RefreshFileListAsync()
    {
        var svc = CurrentService;
        if (svc == null || !IsConnected) return;

        try
        {
            var files = await svc.ListFilesAsync();
            var ymmxFiles = files.Where(f => f.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase)).ToList();
            CloudFilesList.ItemsSource = ymmxFiles;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
            throw;
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
        var svc = CurrentService;
        if (_isProcessing || svc == null || !IsConnected) return;

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
                    "警告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
            throw;
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
        catch
        {
            // ignored
        }
    }
    
    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var svc = CurrentService;
        var file = GetCloudFileFromSender(sender);

        if (file == null)
        {
            MessageBox.Show("ダウンロードするファイルを選択してください。", "確認",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isProcessing || svc == null || !IsConnected) return;

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
            MessageBox.Show($"ダウンロードに失敗しました。\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var svc = CurrentService;
        var file = GetCloudFileFromSender(sender);

        if (file == null)
        {
            MessageBox.Show("削除するファイルを選択してください。", "確認",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isProcessing || svc == null || !IsConnected) return;

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

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var svc = CurrentService;
        if (_isProcessing || svc == null || !IsConnected) return;

        _isProcessing = true;
        SetProcessingState(true, "保存中...");

        try
        {
            var saved = YmmHelper.SaveProject();
            if (!saved)
            {
                MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ymmpPath = YmmHelper.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(ymmpPath))
            {
                MessageBox.Show("プロジェクトが保存されていません。", "エラー",
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

    private void SetProcessingState(bool isProcessing, string? message = null)
    {
        ProgressPanel.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = message ?? "処理中...";
        ProgressBar.Value = 0;

        UploadButton.IsEnabled = !isProcessing && IsConnected;
        RefreshButton.IsEnabled = !isProcessing && IsConnected;
    }
}
