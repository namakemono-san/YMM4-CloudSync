using System.Diagnostics;
using System.IO;
using System.Windows;
using YMM4CloudSync.YMMX.Launcher.Views;

namespace YMM4CloudSync.YMMX.Launcher;

public static class Program
{
    private const string YmmProcessName = "YukkuriMovieMaker";

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            MessageBox.Show("ymmxファイルを指定してください。", "YMM4 CloudSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ymmxPath = args[0];

        if (!File.Exists(ymmxPath))
        {
            MessageBox.Show($"ファイルが見つかりません:\n{ymmxPath}", "YMM4 CloudSync", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var app = new Application();
        var window = new ProgressWindow(ymmxPath);
        app.Run(window);
    }

    public static string? FindYmmPath()
    {
        var processes = Process.GetProcessesByName(YmmProcessName);
        if (processes.Length > 0)
        {
            return processes[0].MainModule?.FileName;
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "YukkuriMovieMaker_v4",
            "YukkuriMovieMaker.exe"
        );

        return File.Exists(defaultPath) ? defaultPath : null;
    }

    public static void LaunchYmm(string ymmPath, string ymmpPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ymmPath,
            Arguments = $"\"{ymmpPath}\"",
            UseShellExecute = true
        });
    }
}