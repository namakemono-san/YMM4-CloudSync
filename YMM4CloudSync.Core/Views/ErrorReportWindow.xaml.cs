using System.Windows;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Core.Views;

public partial class ErrorReportWindow : Window
{
    private readonly SentryId _sentryId;
    private readonly Exception _exception;

    public ErrorReportWindow(Exception ex, SentryId sentryId)
    {
        InitializeComponent();
        _sentryId = sentryId;
        _exception = ex;

        ErrorMessageBox.Text = ex.Message;

        StackTraceBox.Text = ex.ToString();
    }
    
    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var comment = CommentTextBox.Text;

        if (!string.IsNullOrWhiteSpace(comment))
        {
            SentryReporter.CaptureFeedback(new SentryFeedback(comment, associatedEventId: _sentryId));
            
            MessageBox.Show("詳細レポートを送信しました。\nご協力ありがとうございます。", "送信完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Close();
    }

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_exception.ToString());
            MessageBox.Show("クリップボードにコピーしました。", "コピー完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("コピーに失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}