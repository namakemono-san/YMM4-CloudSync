using System.ComponentModel;
using System.Runtime.CompilerServices;
using YMM4CloudSync.Core.Services;

namespace YMM4CloudSync.Core.Views;

public sealed class CloudServiceItem(ICloudStorageService service) : INotifyPropertyChanged
{
    public ICloudStorageService Service { get; } = service;
    public string Name => Service.ServiceName;

    bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}