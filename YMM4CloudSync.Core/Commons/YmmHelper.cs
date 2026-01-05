using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace YMM4CloudSync.Core.Commons;

public static class YmmHelper
{
    private const string MainViewTypeName = "YukkuriMovieMaker.Views.MainView";

    private static object? GetMainWindowDataContext()
    {
        var mainWindow = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w =>
                string.Equals(
                    w.GetType().FullName,
                    MainViewTypeName,
                    StringComparison.Ordinal));

        return mainWindow?.DataContext;
    }

    public static string? GetCurrentProjectPath()
    {
        var dataContext = GetMainWindowDataContext();
        if (dataContext == null) return null;

        var vmType = dataContext.GetType();
        var filePathProp = vmType.GetProperty("ProjectFilePath", BindingFlags.Public | BindingFlags.Instance);

        if (filePathProp == null) return null;

        var filePathValue = filePathProp.GetValue(dataContext);
        return ExtractValue<string>(filePathValue);
    }

    public static bool SaveProject(string? path = null)
    {
        try
        {
            var dataContext = GetMainWindowDataContext();
            if (dataContext == null)
                return false;

            var vmType = dataContext.GetType();
            var currentPath = path ?? GetCurrentProjectPath();

            if (string.IsNullOrEmpty(currentPath))
            {
                var saveDialog = new SaveFileDialog
                {
                    Title = "プロジェクトを保存",
                    Filter = "YMM プロジェクト (*.ymmp)|*.ymmp",
                    FileName = "新規プロジェクト.ymmp"
                };

                if (saveDialog.ShowDialog() != true)
                    return false;

                currentPath = saveDialog.FileName;
            }

            var saveMethod = vmType.GetMethod("SaveProject", BindingFlags.Public | BindingFlags.Instance);
            if (saveMethod == null)
                return false;

            saveMethod.Invoke(dataContext, [currentPath]);
            return true;
        }
        catch
        {
            return false;
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