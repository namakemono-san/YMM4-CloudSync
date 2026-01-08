using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Views;
using YukkuriMovieMaker.Plugin;

namespace YMM4CloudSync.Core;

public class Plugin : IToolPlugin, IDisposable
{
    public string Name => "YMM4 Cloud Sync";

    public Type ViewModelType => typeof(ToolView);
    public Type ViewType => typeof(ToolView);
    
    private static readonly string PluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
    private static readonly string LauncherPath = Path.Combine(PluginDirectory, "YMM4CloudSync.YMMX.Launcher.exe");
    private static readonly string IconPath = Path.Combine(PluginDirectory, "Resources", "YMMX_logo.ico");

    private readonly YmmxFileExtension _ymmxFileExtension = new(LauncherPath, IconPath);
    private readonly IDisposable _sentryGuard;

    public Plugin()
    {
        var settings = LoadSettings();
        var sentrySettings = settings.Sentry;
    
        _sentryGuard = SentrySdk.Init(o =>
        {
            o.Dsn = sentrySettings.Dsn;
            o.Release = sentrySettings.Release;
            o.SendDefaultPii = sentrySettings.SendDefaultPii;
        });

        CheckFileAssociation();
        Task.Run(CleanUpTempFiles);
        
        if (settings.Update.EnableUpdateCheck)
        {
            Task.Run(CheckUpdateAsync);
        }
    }
    
    public void Dispose()
    {
        _sentryGuard.Dispose();
    }
    
    private static AppSettings LoadSettings()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
            var configPath = Path.Combine(pluginDir, "appsettings.json");
        
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppSettings>(json);
                return config ?? new AppSettings();
            }
        }
        catch
        {
            // ignored
        }

        return new AppSettings();
    }

    
    private static void CleanUpTempFiles()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var directories = Directory.GetDirectories(tempPath, "ymmx_*");

            foreach (var dir in directories)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[YMM4CS][CleanUp] Failed to delete temp directory {dir}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][CleanUp] Failed to enumerate temp directories: {ex.Message}");
        }
    }

    private static async Task CheckUpdateAsync()
    {
        Console.WriteLine("[YMM4CS][Update] Checking for updates...");;
        var checker = new UpdateChecker();
        var info = await checker.CheckForUpdatesAsync();
        
        if (info != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = new UpdateNotificationWindow(info);
                window.ShowDialog();
            });
        }
    }
    
    private void CheckFileAssociation()
    {
        if (_ymmxFileExtension.IsRegistered()) return;

        var result = MessageBox.Show(
            "YMM4 Cloud Sync用の拡張子がゆっくりMovieMaker4に関連付けられていません。\n以下の拡張子を関連付けしますか？\n\n- .ymmx: YMM4 Cloud Sync用拡張プロジェクトファイル\n\n関連付けると、各ファイルをダブルクリックでYMM4を起動できるようになります。",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.None
        );

        if (result != MessageBoxResult.Yes) return;

        try
        {
            _ymmxFileExtension.Register();
            MessageBox.Show("関連付けが完了しました。", "YMM4 CloudSync", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "ファイル関連付けの登録に失敗しました。\n\n管理者権限が必要な場合があります。\nYMM4を管理者として実行するか、手動でレジストリを設定してください。",
                "権限エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"ファイル関連付けの登録に失敗しました。\n\n{ex.Message}",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

internal class AppSettings
{
    public SentrySettings Sentry { get; init; } = new();
    public UpdateSettings Update { get; init; } = new();
}

internal class SentrySettings
{
    public string Dsn { get; set; } = "";
    public string Release { get; set; } = "ymm4-cloudsync@1.0.0";
    public bool SendDefaultPii { get; set; } = false;
}

internal class UpdateSettings
{
    public bool EnableUpdateCheck { get; set; } = true;
}