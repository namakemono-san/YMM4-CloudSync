using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using Reactive.Bindings;
using YMM4CloudSync.Core.Commons.License;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.Views;

namespace YMM4CloudSync.Core.ViewModels;

public class ToolViewModel : IDisposable
{
    public ObservableCollection<CloudServiceItem> CloudServices { get; } = [];
    public ReactiveProperty<CloudServiceItem?> SelectedCloudService { get; } = new();
    
    public string VersionText { get; private set; } = "";
    public string ChangelogText { get; private set; } = "";
    public ObservableCollection<LicenseTextViewModel> Licenses { get; } = [];
    public ReactiveProperty<LicenseTextViewModel?> CurrentLicense { get; } = new();

    public ToolViewModel()
    {
        CloudServices.Add(new CloudServiceItem(new GoogleDriveService()));
        CloudServices.Add(new CloudServiceItem(new OneDriveService()));
        
        SelectedCloudService.Value = CloudServices.FirstOrDefault();

        LoadVersionInfo();
        LoadChangelog();
        LoadLicense();
    }
    
    public async Task TryAutoConnectAsync()
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

    public void Dispose()
    {
        SelectedCloudService.Dispose();
        CurrentLicense.Dispose();
        foreach(var s in CloudServices)
        {
            if (s.Service is IDisposable d) d.Dispose();
        }
    }
}