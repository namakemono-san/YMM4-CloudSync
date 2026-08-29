using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using YMM4CloudSync.Core.Commons.Utilities;
using YMM4CloudSync.Core.ViewModels;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.Core.Views.Tabs;

public sealed class ProjectDialogService : IProjectDialogService
{
    private readonly DependencyObject _owner;

    public ProjectDialogService(DependencyObject owner)
    {
        _owner = owner;
    }

    private Dispatcher Dispatcher => _owner.Dispatcher;

    private Window? OwnerWindow => Window.GetWindow(_owner);

    public void ShowInformation(string message, string caption)
        => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string caption)
        => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string caption)
        => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string caption)
    {
        return Dispatcher.Invoke(() =>
        {
            var owner = OwnerWindow;

            var result = owner == null
                ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            return result == MessageBoxResult.Yes;
        });
    }

    public bool AskYesNo(string message, string caption)
    {
        return Dispatcher.Invoke(() =>
        {
            var owner = OwnerWindow;

            var result = owner == null
                ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Information)
                : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Information);

            return result == MessageBoxResult.Yes;
        });
    }

    public ExtractConflictAction ResolveExtractConflict(YmmxMeta? existing, YmmxMeta? incoming)
    {
        return Dispatcher.Invoke(() =>
        {
            if (existing == null) return ExtractConflictAction.Overwrite;

            var existingDate = existing.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

            var message = "同じ場所に既存のプロジェクトがあります。\n\n" +
                          $"既存: {existing.Name}\n" +
                          $"更新日時: {existingDate}\n\n" +
                          "上書きしますか？";

            var owner = OwnerWindow;

            var result = owner == null
                ? MessageBox.Show(message, "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
                : MessageBox.Show(owner, message, "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            return result switch
            {
                MessageBoxResult.Yes => ExtractConflictAction.Overwrite,
                MessageBoxResult.No => ExtractConflictAction.CreateNew,
                _ => ExtractConflictAction.Cancel
            };
        });
    }

    public string? PickYmmxDestination(string title, string suggestedFileName)
    {
        return Dispatcher.Invoke(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
                DefaultExt = ".ymmx",
                AddExtension = true,
                FileName = suggestedFileName
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });
    }

    public string[]? PickFilesToUpload(string title)
    {
        return Dispatcher.Invoke(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "すべてのファイル (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            };

            return dialog.ShowDialog() == true ? dialog.FileNames : null;
        });
    }

    public void OpenContainingFolder(string filePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(filePath);

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProjectTab] Failed to open folder: {ex.Message}");
        }
    }

    public void ReportException(Exception exception) => ErrorReporter.ReportAndShowDialog(exception);

    private void Show(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        Dispatcher.Invoke(() =>
        {
            var owner = OwnerWindow;

            if (owner == null) MessageBox.Show(message, caption, button, icon);
            else MessageBox.Show(owner, message, caption, button, icon);
        });
    }
}
