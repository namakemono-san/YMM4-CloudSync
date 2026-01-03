using System.IO;
using System.Windows;
using YMM4CloudSync.YMMX.Core;
using YMM4CloudSync.YMMX.Core.Models;

namespace YMM4CloudSync.YMMX.Launcher.Views;

public partial class ProgressWindow : Window
{
    private readonly string _ymmxPath;

    public ProgressWindow(string ymmxPath)
    {
        InitializeComponent();
        _ymmxPath = ymmxPath;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = $"展開中: {Path.GetFileName(_ymmxPath)}";
        Progress.IsIndeterminate = true;

        try
        {
            var outputDir = GetOutputDirectory();

            var result = await Task.Run(() => YmmxExtractor.Extract(
                _ymmxPath, 
                outputDir,
                ConflictResolver));

            if (!result.Success)
            {
                Close();
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            PercentText.Text = "100%";
            StatusText.Text = "起動中...";

            LaunchYmm(result.YmmpPath);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "YMM4 Cloud Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"展開に失敗しました:\n{ex.Message}", "YMM4 Cloud Sync", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private ExtractConflictAction ConflictResolver(YmmxMeta? existing, YmmxMeta? incoming)
    {
        if (existing == null) return ExtractConflictAction.Overwrite;

        var existingName = existing.Name;
        var existingDate = existing.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");

        var message = $"同じ場所に既存のプロジェクトがあります。\n\n" +
                      $"既存: {existingName}\n" +
                      $"更新日時: {existingDate}\n\n" +
                      $"上書きしますか？";

        var result = MessageBox.Show(
            message,
            "確認",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => ExtractConflictAction.Overwrite,
            MessageBoxResult.No => ExtractConflictAction.CreateNew,
            _ => ExtractConflictAction.Cancel
        };
    }

    private string GetOutputDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fileName = Path.GetFileNameWithoutExtension(_ymmxPath);
        return Path.Combine(appData, "YMM4CloudSync", "Projects", fileName);
    }

    private void LaunchYmm(string ymmpPath)
    {
        var ymmPath = Program.FindYmmPath();

        if (ymmPath == null)
        {
            MessageBox.Show(
                "YukkuriMovieMaker.exe が見つかりません。",
                "YMM4 CloudSync",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Close();
            return;
        }

        Program.LaunchYmm(ymmPath, ymmpPath);
        Close();
    }
}