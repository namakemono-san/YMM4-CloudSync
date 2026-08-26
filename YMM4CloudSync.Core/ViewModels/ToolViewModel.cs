using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using System.Windows.Forms;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using YMM4CloudSync.Core.Commons.Configuration;
using YMM4CloudSync.Core.Commons.License;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.ViewModels;

public class ToolViewModel : IDisposable
{
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

    public UserSettings Settings { get; }
    
    public ObservableCollection<CloudServiceItem> CloudServices { get; } = [];
    public ReactiveProperty<CloudServiceItem?> SelectedCloudService { get; } = new();
    
    public string VersionText { get; private set; } = "";
    public string ChangelogText { get; private set; } = "";
    public ObservableCollection<LicenseTextViewModel> Licenses { get; } = [];
    public ReactiveProperty<LicenseTextViewModel?> CurrentLicense { get; } = new();
    
    public ReactiveProperty<string> ProjectDirectory { get; }
    public ReactiveProperty<string> CacheDirectory { get; }
    
    public ReadOnlyReactivePropertySlim<string?> ProjectDirectoryPreview { get; }
    public ReadOnlyReactivePropertySlim<string?> CacheDirectoryPreview { get; }

    public ReactiveCommand BrowseProjectDirCommand { get; }
    public ReactiveCommand BrowseCacheDirCommand { get; }
    public ReactiveCommand ResetProjectDirCommand { get; }
    public ReactiveCommand ResetCacheDirCommand { get; }
    
    private static string DefaultProjectDir => PathHelper.DefaultProjectDirectory;

    private static string DefaultCacheDir => PathHelper.DefaultCacheDirectory;

    public ToolViewModel()
    {
        Settings = SettingsManager.Load();
        Settings.PropertyChanged += (_, _) => SettingsManager.Save(Settings);
        
        CloudServices.Add(new CloudServiceItem(new GoogleDriveService()));
        CloudServices.Add(new CloudServiceItem(new OneDriveService()));
        CloudServices.Add(new CloudServiceItem(new DropboxService()));
        
        SelectedCloudService.Value = CloudServices.FirstOrDefault();
        SelectedCloudService.AddTo(_disposables);

        LoadVersionInfo();
        LoadChangelog();
        LoadLicense();
        CurrentLicense.AddTo(_disposables);

        ProjectDirectory = new ReactiveProperty<string>(Settings.ProjectDirectory)
            .AddTo(_disposables);
        
        CacheDirectory = new ReactiveProperty<string>(Settings.CacheDirectory)
            .AddTo(_disposables);

        ProjectDirectory.Subscribe(x => Settings.ProjectDirectory = x).AddTo(_disposables);

        CacheDirectory.Subscribe(x => Settings.CacheDirectory = x).AddTo(_disposables);

        ProjectDirectoryPreview = ProjectDirectory
            .Select(PathHelper.ResolveProjectDirectory)
            .ToReadOnlyReactivePropertySlim()
            .AddTo(_disposables);

        CacheDirectoryPreview = CacheDirectory
            .CombineLatest(ProjectDirectory, (cache, project) => PathHelper.ResolvePath(cache, project))
            .ToReadOnlyReactivePropertySlim()
            .AddTo(_disposables);

        BrowseProjectDirCommand = new ReactiveCommand()
            .WithSubscribe(() => 
            {
                var path = SelectFolder("プロジェクト保存先を選択", ProjectDirectory.Value);
                if (!string.IsNullOrEmpty(path)) ProjectDirectory.Value = path;
            })
            .AddTo(_disposables);

        BrowseCacheDirCommand = new ReactiveCommand()
            .WithSubscribe(() => 
            {
                var path = SelectFolder("キャッシュ保存先を選択", CacheDirectory.Value);
                if (!string.IsNullOrEmpty(path)) CacheDirectory.Value = path;
            })
            .AddTo(_disposables);

        ResetProjectDirCommand = new ReactiveCommand()
            .WithSubscribe(() => ProjectDirectory.Value = DefaultProjectDir)
            .AddTo(_disposables);

        ResetCacheDirCommand = new ReactiveCommand()
            .WithSubscribe(() => CacheDirectory.Value = DefaultCacheDir)
            .AddTo(_disposables);
    }
    
    private int _autoConnectState;

    public async Task TryAutoConnectAsync()
    {
        if (Interlocked.CompareExchange(ref _autoConnectState, 1, 0) != 0) return;

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
    }

    private void LoadVersionInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = $"Version {version?.ToString(3) ?? "1.0.0"}";
    }

    private void LoadChangelog()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var changelogPath = Path.Combine(pluginDir, "Resources", "ChangeLog.txt");

            ChangelogText = File.Exists(changelogPath)
                ? File.ReadAllText(changelogPath)
                : "更新履歴ファイルが見つかりません。";
        }
        catch (Exception ex)
        {
            ChangelogText = $"読み込みに失敗しました: {ex.Message}";
        }
    }

    private void LoadLicense()
    {
        var licenses = LicenseLoader.Load()
            .Select(x => new LicenseTextViewModel(x))
            .OrderBy(x => x.Name);

        Licenses.Clear();
        foreach (var l in licenses)
            Licenses.Add(l);

        CurrentLicense.Value = Licenses.FirstOrDefault();
    }

    private string? SelectFolder(string description, string? initialPath)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = description;
        dialog.UseDescriptionForTitle = true;
        dialog.ShowNewFolderButton = true;

        if (string.IsNullOrEmpty(initialPath))
            return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;

        var resolvedPath = PathHelper.ResolvePath(initialPath, ProjectDirectory.Value);
        if (Directory.Exists(resolvedPath))
        {
            dialog.SelectedPath = resolvedPath;
        }

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public void Dispose()
    {
        _disposables.Dispose();

        foreach(var s in CloudServices)
        {
            if (s.Service is IDisposable d) d.Dispose();
        }
    }
}