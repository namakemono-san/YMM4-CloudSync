using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YMM4CloudSync.YMMX.Core;

namespace YMM4CloudSync.Core.Views;

public partial class ToolView : UserControl
{
    public ToolView()
    {
        InitializeComponent();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var saved = SaveCurrentProject();
        if (!saved)
        {
            MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var ymmpPath = GetCurrentProjectPath();

        if (string.IsNullOrEmpty(ymmpPath))
        {
            MessageBox.Show("プロジェクトが保存されていません。\n先にプロジェクトを保存してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "ymmx ファイルの保存先",
            Filter = "YMMX ファイル (*.ymmx)|*.ymmx",
            FileName = Path.GetFileNameWithoutExtension(ymmpPath) + ".ymmx",
            InitialDirectory = Path.GetDirectoryName(ymmpPath)
        };

        if (saveDialog.ShowDialog() != true) return;

        try
        {
            var projectName = Path.GetFileNameWithoutExtension(ymmpPath);
            YmmxPacker.Pack(ymmpPath, saveDialog.FileName, projectName);
            MessageBox.Show($"エクスポートが完了しました。\n{saveDialog.FileName}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エクスポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool SaveCurrentProject()
    {
        try
        {
            var mainWindow = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w =>
                    string.Equals(
                        w.GetType().FullName,
                        "YukkuriMovieMaker.Views.MainView",
                        StringComparison.Ordinal));

            if (mainWindow?.DataContext == null) return false;

            var dataContext = mainWindow.DataContext;
            var vmType = dataContext.GetType();

            var filePathProp = vmType.GetProperty("ProjectFilePath", BindingFlags.Public | BindingFlags.Instance);
            var currentPath = ExtractValue<string>(filePathProp?.GetValue(dataContext));

            if (string.IsNullOrEmpty(currentPath))
            {
                var saveDialog = new SaveFileDialog
                {
                    Title = "プロジェクトを保存",
                    Filter = "YMM プロジェクト (*.ymmp)|*.ymmp",
                    FileName = "新規プロジェクト.ymmp"
                };

                if (saveDialog.ShowDialog() != true) return false;
                currentPath = saveDialog.FileName;
            }

            var saveMethod = vmType.GetMethod("SaveProject", BindingFlags.Public | BindingFlags.Instance);
            if (saveMethod == null) return false;

            saveMethod.Invoke(dataContext, [currentPath]);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YMM4CloudSync] Save error: {ex.Message}");
            return false;
        }
    }

    private static string? GetCurrentProjectPath()
    {
        try
        {
            var mainWindow = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w =>
                    string.Equals(
                        w.GetType().FullName,
                        "YukkuriMovieMaker.Views.MainView",
                        StringComparison.Ordinal));

            if (mainWindow?.DataContext == null) return null;

            var vmType = mainWindow.DataContext.GetType();
            var filePathProp = vmType.GetProperty("ProjectFilePath", BindingFlags.Public | BindingFlags.Instance);

            if (filePathProp == null) return null;

            var filePathValue = filePathProp.GetValue(mainWindow.DataContext);
            return ExtractValue<string>(filePathValue);
        }
        catch
        {
            return null;
        }
    }

    private static T? ExtractValue<T>(object? obj)
    {
        switch (obj)
        {
            case null:
                return default;
            case T directValue:
                return directValue;
        }

        var valueProperty = obj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty == null) return default;

        var innerValue = valueProperty.GetValue(obj);
        if (innerValue is T typedValue) return typedValue;
        return innerValue != null ? ExtractValue<T>(innerValue) : default;
    }
}