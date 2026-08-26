using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Commons.Network;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.ViewModels;
using YMM4CloudSync.Core.Views;
using YukkuriMovieMaker.Plugin;

namespace YMM4CloudSync.Core;

public class Plugin : IToolPlugin, IDisposable
{
    public string Name => "YMM4 Cloud Sync";

    public Type ViewModelType => typeof(ToolViewModel);
    public Type ViewType => typeof(ToolView);
    
    private static readonly string PluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
    private static readonly string LauncherPath = Path.Combine(PluginDirectory, "YMM4CloudSync.YMMX.Launcher.exe");
    private static readonly string IconPath = Path.Combine(PluginDirectory, "Resources", "YMMX_logo.ico");

    private readonly YmmxFileExtension _ymmxFileExtension = new(LauncherPath, IconPath);

    public Plugin()
    {
        var sentrySettings = LoadSettings().Sentry;
        SentryReporter.Initialize(sentrySettings.Dsn, GetSentryRelease(), sentrySettings.SendDefaultPii);

        var settings = SettingsManager.Load();

        CheckFileAssociation();

        Task.Run(() =>
        {
            try
            {
                string? cacheDir = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(settings?.CacheDirectory))
                    {
                        cacheDir = PathHelper.ResolvePath(settings.CacheDirectory);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[YMM4CS][CleanUp] Failed to resolve cache directory: {ex.Message}");
                }

                CleanUpTempFiles(cacheDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YMM4CS][CleanUp] Failed to start cleanup task: {ex.Message}");
            }
        });

        if (settings.EnableUpdateCheck)
        {
            ScheduleUpdateCheck();
        }
    }

    public void Dispose()
    {
        SentryReporter.Shutdown();
    }

    private static string GetSentryRelease()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return $"ymm4-cloudsync@{version?.ToString(3) ?? "0.0.0"}";
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Settings] Failed to read appsettings.json: {ex.Message}");
        }

        return new AppSettings();
    }

    private static void CleanUpTempFiles(string? additionalDir = null)
    {
        try
        {
            var cutoffTime = DateTime.Now.AddDays(-7);

            static void ProcessBasePath(string basePath, DateTime cutoff)
            {
                try
                {
                    var directories = Directory.GetDirectories(basePath, "ymmx_*", SearchOption.TopDirectoryOnly);
                    foreach (var dir in directories)
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            if (dirInfo.CreationTime < cutoff)
                            {
                                Directory.Delete(dir, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[YMM4CS][CleanUp] Failed to delete temp directory {dir}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[YMM4CS][CleanUp] Failed to enumerate directories in {basePath}: {ex.Message}");
                }
            }

            var tempPath = Path.GetTempPath();
            ProcessBasePath(tempPath, cutoffTime);

            if (!string.IsNullOrWhiteSpace(additionalDir) && Directory.Exists(additionalDir))
            {
                ProcessBasePath(additionalDir, cutoffTime);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][CleanUp] Unexpected cleanup error: {ex.Message}");
        }
    }

    private static void ScheduleUpdateCheck()
    {
        var app = Application.Current;

        if (app == null)
        {
            Debug.WriteLine("[YMM4CS][Update] No WPF application; skipping update check.");
            return;
        }

        app.Dispatcher.InvokeAsync(() =>
        {
            var main = app.MainWindow;

            if (main is { IsLoaded: false })
            {
                void OnMainWindowLoaded(object sender, RoutedEventArgs e)
                {
                    main.Loaded -= OnMainWindowLoaded;
                    _ = RunUpdateCheckAsync();
                }

                main.Loaded += OnMainWindowLoaded;
                return;
            }

            _ = RunUpdateCheckAsync();
        }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task RunUpdateCheckAsync()
    {
        try
        {
            var checker = new UpdateChecker();
            var info = await checker.CheckForUpdatesAsync();

            if (info == null) return;

            var window = new UpdateNotificationWindow(info);

            var owner = Application.Current?.MainWindow;
            if (owner != null && !ReferenceEquals(owner, window))
                window.Owner = owner;

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            SentryReporter.Capture(ex);
            Debug.WriteLine($"[YMM4CS][Update] Update check failed: {ex.Message}");
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
}

internal class SentrySettings
{
    public string Dsn { get; set; } = "";

    public bool SendDefaultPii { get; set; } = false;
}
