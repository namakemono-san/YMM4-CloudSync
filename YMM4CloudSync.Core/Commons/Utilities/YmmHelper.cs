using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace YMM4CloudSync.Core.Commons.Utilities;

public enum SaveResult
{
    Success,
    Cancelled,
    Failed
}

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

    public static bool? IsProjectEmpty()
    {
        try
        {
            var dataContext = GetMainWindowDataContext();
            if (dataContext == null) return null;

            if (ReadBool(dataContext, "IsEmptyProject") is { } isEmpty) return isEmpty;
            if (ReadBool(dataContext, "HasAnySceneOrSceneItems") is { } hasItems) return !hasItems;

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][YmmHelper] Failed to read project state: {ex.Message}");
            return null;
        }
    }

    private static bool? ReadBool(object dataContext, string propertyName)
    {
        var property = dataContext.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        return property == null ? null : UnwrapBool(property.GetValue(dataContext), 0);
    }

    private static bool? UnwrapBool(object? value, int depth)
    {
        const int maxDepth = 4;

        switch (value)
        {
            case null:
                return null;
            case bool result:
                return result;
        }

        if (depth >= maxDepth) return null;

        var valueProperty = value.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

        return valueProperty == null ? null : UnwrapBool(valueProperty.GetValue(value), depth + 1);
    }

    public static SaveResult SaveProject(string? path = null)
    {
        try
        {
            var dataContext = GetMainWindowDataContext();
            if (dataContext == null)
                return SaveResult.Failed;

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
                    return SaveResult.Cancelled;

                currentPath = saveDialog.FileName;
            }

            var saveMethod = vmType.GetMethod("SaveProject", BindingFlags.Public | BindingFlags.Instance);
            if (saveMethod == null)
                return SaveResult.Failed;

            saveMethod.Invoke(dataContext, [currentPath]);
            return SaveResult.Success;
        }
        catch (Exception ex)
        {
            SentryReporter.Capture(ex);
            return SaveResult.Failed;
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
