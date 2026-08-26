using System.Diagnostics;
using System.IO;
using System.Windows;
using YMM4CloudSync.YMMX.Launcher.Views;

namespace YMM4CloudSync.YMMX.Launcher;

public static class Program
{
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

    public static void LaunchYmm(string ymmPath, string ymmpPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ymmPath,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(ymmpPath);

        Process.Start(startInfo);
    }
}
