using System.ComponentModel;
using System.Runtime.CompilerServices;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Views;

public sealed class CloudServiceItem(ICloudStorageService service) : INotifyPropertyChanged
{
    public ICloudStorageService Service { get; } = service;
    public string Name => Service.ServiceName;

    public bool IsConnected
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
