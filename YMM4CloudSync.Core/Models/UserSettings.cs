using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace YMM4CloudSync.Core.Models;

public class UserSettings : INotifyPropertyChanged
{
    public UserSettings()
    {
        CacheDirectory = Path.Combine(Path.GetTempPath(), "YMM4CloudSync");
        ProjectDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4CloudSync", "Projects");
        AssetDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMM4CloudSync", "Assets");
        EnableUpdateCheck = true;
        PromptForFileAssociation = true;
    }

    public string CacheDirectory
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string ProjectDirectory
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string AssetDirectory
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool EnableUpdateCheck
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool PromptForFileAssociation
    {
        get;
        set => SetProperty(ref field, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}