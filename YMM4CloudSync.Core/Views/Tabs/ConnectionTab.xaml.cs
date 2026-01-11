using System.Windows;
using System.Windows.Controls;
using YMM4CloudSync.Core.Commons;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class ConnectionTab : UserControl
{
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
                var r = MessageBox.Show($"{item.Name} との接続を解除しますか？", "確認",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;

                await item.Service.LogoutAsync();
                item.IsConnected = false;
            }
            else
            {
                var ok = item.Service switch
                {
                    OneDriveService one => await one.AuthenticateInteractiveAsync(),
                    GoogleDriveService gDrive => await gDrive.AuthenticateInteractiveAsync(),
                    _ => await item.Service.AuthenticateAsync()
                };

                item.IsConnected = ok && item.Service.IsAuthenticated;
            }
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
}