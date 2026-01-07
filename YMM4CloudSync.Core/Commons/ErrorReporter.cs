using System.Windows;
using YMM4CloudSync.Core.Views;

namespace YMM4CloudSync.Core.Commons;

public static class ErrorReporter
{
    public static void ReportAndShowDialog(Exception ex)
    {
        var sentryId = SentrySdk.CaptureException(ex);

        Application.Current.Dispatcher.Invoke(() =>
        {
            var window = new ErrorReportWindow(ex, sentryId);
            window.ShowDialog();
        });
    }
}