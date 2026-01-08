using System.Diagnostics;
using System.Reflection;
using System.Windows;
using YMM4CloudSync.Core.Commons;

namespace YMM4CloudSync.Core.Views;

public partial class UpdateNotificationWindow : Window
{
    private readonly string _downloadUrl;

    public UpdateNotificationWindow(ReleaseInfo info)
    {
        InitializeComponent();

        _downloadUrl = info.HtmlUrl ?? "https://github.com/namakemono-san/YMM4-CloudSync/releases";

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersionText.Text = currentVersion?.ToString() ?? "Unknown";
        LatestVersionText.Text = info.Version.ToString();
        ReleaseNotesText.Text = info.Body ?? "リリースノートはありません。";
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _downloadUrl,
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ブラウザを開けませんでした。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}