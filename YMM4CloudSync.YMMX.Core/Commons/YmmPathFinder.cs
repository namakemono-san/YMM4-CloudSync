using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace YMM4CloudSync.YMMX.Core.Commons;

public static class YmmPathFinder
{
    private const string ProcessName = "YukkuriMovieMaker";
    private const string ExeName = "YukkuriMovieMaker.exe";

    public static string? Find()
    {
        var processes = Process.GetProcessesByName(ProcessName);
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
            using var key = Registry.ClassesRoot.OpenSubKey(@".ymmp");
            if (key?.GetValue("") is string progId)
            {
                using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
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

        var baseDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        var folderNames = new[]
        {
            "YukkuriMovieMaker_v4",
            "YukkuriMovieMaker_v4_Lite",
        };

        return (from baseDir in baseDirs from folder in folderNames select Path.Combine(baseDir, folder, ExeName)).FirstOrDefault(File.Exists);
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
}