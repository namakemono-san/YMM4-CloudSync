using System.Windows;
using System.Windows.Controls;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.Services;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class ConnectionTab : UserControl
{
    private ToolViewModel? ViewModel => DataContext as ToolViewModel;

    public ConnectionTab()
    {
        InitializeComponent();
    }

    private async void OnServiceToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.Tag is not CloudServiceItem item) return;

        b.IsEnabled = false;

        try
        {
            if (item.IsConnected)
            {
                await DisconnectAsync(item);
                return;
            }

            var ok = item.Service switch
            {
                OneDriveService one => await one.AuthenticateInteractiveAsync(),
                DropboxService dropbox => await dropbox.AuthenticateInteractiveAsync(),
                GoogleDriveService gDrive => await gDrive.AuthenticateInteractiveAsync(),
                WebDavService webDav => ConfigureWebDav(webDav),
                _ => await item.Service.AuthenticateAsync()
            };

            item.IsConnected = ok && item.Service.IsAuthenticated;
        }
        catch (Exception ex)
        {
            ErrorReporter.ReportAndShowDialog(ex);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    private async Task DisconnectAsync(CloudServiceItem item)
    {
        var isWebDav = item.Service is WebDavService;

        var message = isWebDav
            ? $"{item.Name} の接続設定を削除しますか？"
            : $"{item.Name} との接続を解除しますか？";

        var confirmation = MessageBox.Show(message, "確認",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes) return;

        await item.Service.LogoutAsync();
        item.IsConnected = false;

        if (isWebDav) ViewModel?.RemoveWebDavConnection(item);
    }

    private void OnAddWebDavClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        var item = viewModel.AddWebDavConnection();

        if (item.Service is not WebDavService service) return;

        if (ConfigureWebDav(service))
        {
            item.IsConnected = service.IsAuthenticated;
            return;
        }

        viewModel.RemoveWebDavConnection(item);
    }

    private bool ConfigureWebDav(WebDavService service)
    {
        var window = new WebDavSettingsWindow(service)
        {
            Owner = Window.GetWindow(this)
        };

        return window.ShowDialog() == true && service.IsAuthenticated;
    }
}
