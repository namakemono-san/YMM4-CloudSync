using System.Windows;
using YMM4CloudSync.Core.Models;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Views;

public partial class WebDavSettingsWindow : Window
{
    private readonly WebDavService _service;

    public WebDavSettingsWindow(WebDavService service)
    {
        InitializeComponent();

        _service = service;

        var settings = service.Settings;

        DisplayNameBox.Text = settings.DisplayName;
        ServerUrlBox.Text = settings.ServerUrl;
        UserNameBox.Text = settings.UserName;
        PasswordBox.Password = settings.Password;
        BasePathBox.Text = settings.BasePath;
        AuthModeBox.SelectedIndex = (int)settings.AuthMode;
        ChunkedUploadCheckBox.IsChecked = settings.EnableChunkedUpload;
        AllowInsecureCheckBox.IsChecked = settings.AllowInsecureConnection;
        AllowUntrustedCertificateCheckBox.IsChecked = settings.AllowUntrustedCertificate;

        UpdateRiskWarning();
    }

    private void OnRiskyOptionChanged(object sender, RoutedEventArgs e) => UpdateRiskWarning();

    private void UpdateRiskWarning()
    {
        var warnings = new List<string>();

        if (AllowInsecureCheckBox.IsChecked == true)
        {
            warnings.Add("http:// では認証情報が実質平文で送信されます。信頼できるネットワーク以外では使用しないでください。");
        }

        if (AllowUntrustedCertificateCheckBox.IsChecked == true)
        {
            warnings.Add("証明書の検証を無効にすると、接続先が本物のサーバーか確認できなくなります。通信内容を第三者に読み取られる可能性があるため、自宅サーバーなど接続先を完全に把握している場合に限って使用してください。");
        }

        RiskWarningText.Text = string.Join("\n\n", warnings);
        RiskWarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private WebDavSettings BuildSettings()
    {
        var settings = _service.Settings.Clone();

        settings.DisplayName = DisplayNameBox.Text.Trim();
        settings.ServerUrl = ServerUrlBox.Text.Trim();
        settings.UserName = UserNameBox.Text.Trim();
        settings.Password = PasswordBox.Password;
        settings.BasePath = string.IsNullOrWhiteSpace(BasePathBox.Text) ? "YMM4CloudSync" : BasePathBox.Text.Trim();
        settings.AuthMode = (WebDavAuthMode)Math.Max(0, AuthModeBox.SelectedIndex);
        settings.EnableChunkedUpload = ChunkedUploadCheckBox.IsChecked == true;
        settings.AllowInsecureConnection = AllowInsecureCheckBox.IsChecked == true;
        settings.AllowUntrustedCertificate = AllowUntrustedCertificateCheckBox.IsChecked == true;

        return settings;
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings();

        try
        {
            WebDavService.ValidateAndBuildUri(settings);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "設定の確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.UserName))
        {
            MessageBox.Show(this, "ユーザー名を入力してください。", "設定の確認",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (settings.AllowUntrustedCertificate && !ConfirmUntrustedCertificate()) return;

        SetBusy(true);

        try
        {
            var connected = await _service.ConnectAsync(settings);

            if (!connected)
            {
                SetBusy(false);
                StatusText.Text = "接続できませんでした。";
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, ex.Message, "接続エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ConfirmUntrustedCertificate()
    {
        var result = MessageBox.Show(
            this,
            "証明書の検証を無効にしようとしています。\n\n" +
            "この設定では、接続先が本物のサーバーであることを確認できません。\n" +
            "同じネットワーク上の第三者が通信を傍受・改ざんできる状態になります。\n\n" +
            "続行しますか？",
            "確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private void SetBusy(bool isBusy)
    {
        ConnectButton.IsEnabled = !isBusy;
        DisplayNameBox.IsEnabled = !isBusy;
        ServerUrlBox.IsEnabled = !isBusy;
        UserNameBox.IsEnabled = !isBusy;
        PasswordBox.IsEnabled = !isBusy;
        BasePathBox.IsEnabled = !isBusy;
        AuthModeBox.IsEnabled = !isBusy;
        ChunkedUploadCheckBox.IsEnabled = !isBusy;
        AllowInsecureCheckBox.IsEnabled = !isBusy;
        AllowUntrustedCertificateCheckBox.IsEnabled = !isBusy;

        StatusText.Text = isBusy ? "接続しています..." : "";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
