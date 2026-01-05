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
            try
            {
                return processes[0].MainModule?.FileName;
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@".ymmp");
            if (key?.GetValue("") is string progId)
            {
                using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                if (cmdKey?.GetValue("") is string command)
                {
                    var exePath = ExtractExePathFromCommand(command);
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        return exePath;
                }
            }
        }
        catch
        {
            // ignored
        }

        var searchPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "YukkuriMovieMaker_v4", "YukkuriMovieMaker.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YukkuriMovieMaker4", "YukkuriMovieMaker.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YukkuriMovieMaker4", "YukkuriMovieMaker.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "YukkuriMovieMaker4", "YukkuriMovieMaker.exe"),
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }

    private static string? ExtractExePathFromCommand(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var endQuote = command.IndexOf('"', 1);
            if (endQuote > 1)
                return command[1..endQuote];
        }
        else
        {
            var spaceIndex = command.IndexOf(' ');
            return spaceIndex > 0 ? command[..spaceIndex] : command;
        }

        return null;
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
