using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace YMM4CloudSync.Core.Commons.Utilities;

public class YmmxFileExtension(string launcherPath, string iconPath)
{
    private const string Extension = ".ymmx";
    private const string ProgId = "YMM4CloudSync.YMMX";
    private const string ProdName = "YMM4 Cloud Sync プロジェクトファイル";

    public bool IsRegistered()
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{ProgId}\shell\open\command", false);
        var command = key?.GetValue("") as string;
        return command == GetCommand();
    }

    public void Register()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var classes = hkcu.CreateSubKey(@"Software\Classes", true);
        
        using (var extKey = classes.CreateSubKey(Extension, true))
        {
            extKey.SetValue("", ProgId, RegistryValueKind.String);
        }

        using (var progIdKey = classes.CreateSubKey(ProgId, true))
        {
            progIdKey.SetValue("", ProdName, RegistryValueKind.String);
        }
        
        using (var iconKey = classes.CreateSubKey($@"{ProgId}\DefaultIcon", true))
        {
            iconKey.SetValue("", iconPath, RegistryValueKind.String);
        }

        using (var cmdKey = classes.CreateSubKey($@"{ProgId}\shell\open\command", true))
        {
            cmdKey.SetValue("", GetCommand(), RegistryValueKind.String);
        }
    }
    
    // ReSharper disable once UnusedMember.Global
    [SuppressMessage("Performance", "CA1822:メンバーを static に設定します")]
    public void Unregister()
    {
        using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
        using (var classes = hkcu.OpenSubKey(@"Software\Classes", true))
        {
            classes?.DeleteSubKeyTree(Extension, false);
            classes?.DeleteSubKeyTree(ProgId, false);
        }

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
        using (var classes = hklm.OpenSubKey(@"SOFTWARE\Classes", true))
        {
            classes?.DeleteSubKeyTree(ProgId, false);
        }
    }

    private string GetCommand() => $"\"{launcherPath}\" \"%1\"";
}
