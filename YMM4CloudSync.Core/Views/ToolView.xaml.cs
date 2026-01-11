using System.Windows;
using System.Windows.Controls;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Core.Views;

public partial class ToolView : UserControl, IDisposable
{
    private readonly ToolViewModel _viewModel;

    public ToolView()
    {
        InitializeComponent();
        
        _viewModel = new ToolViewModel();
        DataContext = _viewModel;
        
        Loaded += OnLoaded;
    }
    
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.TryAutoConnectAsync();
    }

    public void Dispose()
    {
        _viewModel.Dispose();
    }
}