using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class ProjectTab : UserControl
{
    private ProjectTabViewModel? _viewModel;
    private DispatcherTimer? _projectStateTimer;

    public ProjectTab()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null && DataContext is ToolViewModel tool)
        {
            _viewModel = new ProjectTabViewModel(tool, new ProjectDialogService(this));
            tool.AttachDisposable(_viewModel);
            DataContext = _viewModel;
        }

        _viewModel?.RefreshProjectState();

        StartProjectStateWatch();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopProjectStateWatch();
    }

    private void StartProjectStateWatch()
    {
        if (_projectStateTimer != null) return;

        _projectStateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _projectStateTimer.Tick += OnProjectStateTick;
        _projectStateTimer.Start();
    }

    private void StopProjectStateWatch()
    {
        if (_projectStateTimer == null) return;

        _projectStateTimer.Stop();
        _projectStateTimer.Tick -= OnProjectStateTick;
        _projectStateTimer = null;
    }

    private void OnProjectStateTick(object? sender, EventArgs e) => _viewModel?.RefreshProjectState();
}
