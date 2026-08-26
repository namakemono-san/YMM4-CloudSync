using System.Windows;
using System.Windows.Controls;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Core.Views;

public partial class ToolView : UserControl, IDisposable
{
    private ToolViewModel? _ownedViewModel;
    private bool _disposed;

    public ToolView()
    {
        InitializeComponent();

        _ownedViewModel = new ToolViewModel();
        DataContext = _ownedViewModel;

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_ownedViewModel == null) return;
        if (ReferenceEquals(e.NewValue, _ownedViewModel)) return;

        _ownedViewModel.Dispose();
        _ownedViewModel = null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is ToolViewModel viewModel)
        {
            await viewModel.TryAutoConnectAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Loaded -= OnLoaded;
        DataContextChanged -= OnDataContextChanged;

        _ownedViewModel?.Dispose();
        _ownedViewModel = null;
    }
}
