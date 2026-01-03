using System.IO;
using System.Windows;
using Microsoft.Win32;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.YMMX.Core;

namespace YMM4CloudSync.Core.Views;

public partial class ToolView
{
    private readonly GoogleDriveService _driveService = new();
    private bool _isProcessing;

    public ToolView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await TryAutoConnectAsync();
    }

    private async Task TryAutoConnectAsync()
    {
        try
        {
            var credentialPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YMM4CloudSync", "google_credentials");

            if (Directory.Exists(credentialPath) && Directory.GetFiles(credentialPath).Length > 0)
            {
                await ConnectAsync();
            }
        }
        catch
        {
            // ignored
        }
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_isProcessing) return;

        _isProcessing = true;
        ConnectButton.IsEnabled = false;
        ConnectionStatus.Text = "接続中...";

        try
        {
            var success = await _driveService.AuthenticateAsync();

            if (success)
            {
                ConnectionStatus.Text = "接続済み";
                ConnectionStatus.Foreground = System.Windows.Media.Brushes.Green;
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                UploadButton.IsEnabled = true;
                DownloadButton.IsEnabled = true;

                await RefreshFileListAsync();
            }
            else
            {
                ConnectionStatus.Text = "接続失敗";
                ConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
                ConnectButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus.Text = "エラー";
            ConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            ConnectButton.IsEnabled = true;
            MessageBox.Show($"接続に失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Google Drive との接続を解除しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        await _driveService.LogoutAsync();

        ConnectionStatus.Text = "未接続";
        ConnectionStatus.Foreground = System.Windows.Media.Brushes.Gray;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        UploadButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        CloudFilesList.ItemsSource = null;
    }

    private async Task RefreshFileListAsync()
    {
        try
        {
            var files = await _driveService.ListFilesAsync();
            var ymmxFiles = files.Where(f => f.Name.EndsWith(".ymmx", StringComparison.OrdinalIgnoreCase)).ToList();
            CloudFilesList.ItemsSource = ymmxFiles;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイル一覧の取得に失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || !_driveService.IsAuthenticated) return;

        _isProcessing = true;
        SetProcessingState(true, "保存中...");

        try
        {
            var saved = YmmHelper.SaveProject();
            if (!saved)
            {
                MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ymmpPath = YmmHelper.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(ymmpPath))
            {
                MessageBox.Show("プロジェクトが保存されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var projectName = Path.GetFileNameWithoutExtension(ymmpPath);
            var tempYmmxPath = Path.Combine(Path.GetTempPath(), $"{projectName}.ymmx");

            SetProcessingState(true, "パッケージ作成中...");
            var packResult = await Task.Run(() => YmmxPacker.Pack(ymmpPath, tempYmmxPath, projectName));

            if (!packResult.Success)
            {
                MessageBox.Show("パッケージの作成に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetProcessingState(true, "アップロード中...");
            var progress = new Progress<double>(p =>
            {
                ProgressBar.Value = p;
                ProgressText.Text = $"アップロード中... {p:F0}%";
            });

            await _driveService.UploadFileAsync(tempYmmxPath, $"{projectName}.ymmx", progress);

            try { File.Delete(tempYmmxPath); }
            catch
            {
                // ignored
            }

            await RefreshFileListAsync();

            var message = "アップロードが完了しました。";
            if (packResult.MissingFiles.Count > 0)
            {
                message += $"\n\n⚠️ 見つからなかったファイル: {packResult.MissingFiles.Count} 件";
            }

            MessageBox.Show(message, "完了", MessageBoxButton.OK, 
                packResult.MissingFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"アップロードに失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
        }
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || !_driveService.IsAuthenticated) return;

        var selectedFile = CloudFilesList.SelectedItem as CloudFile;
        if (selectedFile == null)
        {
            MessageBox.Show("ダウンロードするファイルを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "保存先を選択",
            Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
            FileName = selectedFile.Name
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

            await _driveService.DownloadFileAsync(selectedFile.Id, saveDialog.FileName, progress);

            MessageBox.Show($"ダウンロードが完了しました。\n{saveDialog.FileName}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ダウンロードに失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isProcessing = false;
            SetProcessingState(false);
        }
    }

    // ReSharper disable once AsyncVoidMethod
    private async void OnExportLocalClick(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        _isProcessing = true;
        SetProcessingState(true, "保存中...");

        try
        {
            var saved = YmmHelper.SaveProject();
            if (!saved)
            {
                MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ymmpPath = YmmHelper.GetCurrentProjectPath();
            if (string.IsNullOrEmpty(ymmpPath))
            {
                MessageBox.Show("プロジェクトが保存されていません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "ymmx ファイルの保存先",
                Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
                FileName = Path.GetFileNameWithoutExtension(ymmpPath) + ".ymmx",
                InitialDirectory = Path.GetDirectoryName(ymmpPath)
            };

            if (saveDialog.ShowDialog() != true) return;

            var projectName = Path.GetFileNameWithoutExtension(ymmpPath);

            SetProcessingState(true, "パッケージ作成中...");
            var result = await Task.Run(() => YmmxPacker.Pack(ymmpPath, saveDialog.FileName, projectName));

            if (!result.Success) return;
            
            var message = $"エクスポートが完了しました。\n{result.OutputPath}";
            if (result.MissingFiles.Count > 0)
            {
                message += $"\n\n⚠️ 見つからなかったファイル: {result.MissingFiles.Count} 件";
            }
            MessageBox.Show(message, "完了", MessageBoxButton.OK,
                result.MissingFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エクスポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

        UploadButton.IsEnabled = !isProcessing && _driveService.IsAuthenticated;
        DownloadButton.IsEnabled = !isProcessing && _driveService.IsAuthenticated;
        ExportLocalButton.IsEnabled = !isProcessing;
    }
}