using System.Diagnostics;
using System.Reflection;
using System.Windows;
using YMM4CloudSync.Core.Commons.Network;

namespace YMM4CloudSync.Core.Views;

public partial class UpdateNotificationWindow : Window
{
    private const string ReleasesUrl = "https://github.com/namakemono-san/YMM4-CloudSync/releases";

    private readonly string _downloadUrl;

    public UpdateNotificationWindow(ReleaseInfo info)
    {
        InitializeComponent();

        _downloadUrl = ToSafeWebUrl(info.HtmlUrl) ?? ReleasesUrl;

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

    private static string? ToSafeWebUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;

        var isWebScheme = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

        return isWebScheme && !uri.IsFile && !uri.IsUnc ? uri.AbsoluteUri : null;
    }
}