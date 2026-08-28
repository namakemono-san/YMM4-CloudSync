using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using YMM4CloudSync.Core.ViewModels;

namespace YMM4CloudSync.Core.Views.Tabs;

public partial class AssetTab : UserControl
{
    private AssetTabViewModel? _viewModel;
    private Point _dragOrigin;
    private AssetItemViewModel? _dragCandidate;
    private AssetItemViewModel? _selectionOnRelease;
    private AssetDragAdorner? _adorner;
    private AdornerLayer? _adornerLayer;
    private UIElement? _adornerRoot;
    private bool _isDraggingOut;

    public AssetTab()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.TimelineCommandSource = this;
            return;
        }

        if (DataContext is not ToolViewModel tool) return;

        _viewModel = new AssetTabViewModel(tool, new ProjectDialogService(this))
        {
            TimelineCommandSource = this
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        tool.AttachDisposable(_viewModel);
        DataContext = _viewModel;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssetTabViewModel.IsCreatingFolder)) return;
        if (_viewModel is not { IsCreatingFolder: true }) return;

        Dispatcher.BeginInvoke(() =>
        {
            NewFolderNameBox.Focus();
            NewFolderNameBox.SelectAll();
        });
    }

    private void OnListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (_viewModel is not { } viewModel) return;

        e.Handled = true;

        if (e.Delta > 0) viewModel.Layout.Increase();
        else if (e.Delta < 0) viewModel.Layout.Decrease();
    }

    private void OnListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindItem(e.OriginalSource as DependencyObject) is not { } item) return;

        e.Handled = true;

        _ = _viewModel?.ActivateAsync(item);
    }

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);
        _selectionOnRelease = null;

        var hit = FindItem(e.OriginalSource as DependencyObject);

        _dragCandidate = hit is { CanDrag: true } ? hit : null;

        if (hit == null || e.ClickCount > 1 || Keyboard.Modifiers != ModifierKeys.None) return;
        if (AssetList.SelectedItems.Count <= 1 || !AssetList.SelectedItems.Contains(hit)) return;

        _selectionOnRelease = hit;
        e.Handled = true;
    }

    private void OnListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionOnRelease is not { } item) return;

        _selectionOnRelease = null;

        AssetList.SelectedItems.Clear();
        AssetList.SelectedItem = item;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(null);

        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var candidate = _dragCandidate;

        _dragCandidate = null;
        _selectionOnRelease = null;

        var items = CollectDraggableItems(candidate);

        if (items.Count == 0) return;

        var paths = new StringCollection();

        foreach (var item in items) paths.Add(item.LocalPath);

        var data = new DataObject();
        data.SetFileDropList(paths);

        _isDraggingOut = true;

        ShowGhost(items);

        foreach (var item in items) item.IsDragging = true;

        try
        {
            DragDrop.DoDragDrop(AssetList, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (COMException)
        {
        }
        finally
        {
            foreach (var item in items) item.IsDragging = false;

            HideGhost();
            _isDraggingOut = false;
        }
    }

    private void OnListGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = true;
        e.Handled = true;

        if (_adorner == null || _adornerRoot == null) return;
        if (!GetCursorPos(out var point)) return;

        try
        {
            var local = _adornerRoot.PointFromScreen(new Point(point.X, point.Y));

            _adorner.UpdatePosition(new Point(local.X + 14, local.Y + 10));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ShowGhost(IReadOnlyList<AssetItemViewModel> items)
    {
        var label = items.Count == 1
            ? items[0].Name
            : $"{items[0].Name} ほか {items.Count - 1} 件";

        var window = Window.GetWindow(this);

        _adornerRoot = window?.Content as UIElement ?? this;
        _adornerLayer = AdornerLayer.GetAdornerLayer(_adornerRoot);

        if (_adornerLayer == null)
        {
            _adornerRoot = this;
            _adornerLayer = AdornerLayer.GetAdornerLayer(this);
        }

        if (_adornerLayer == null || _adornerRoot == null) return;

        _adorner = new AssetDragAdorner(_adornerRoot, label);
        _adornerLayer.Add(_adorner);

        AssetList.GiveFeedback += OnListGiveFeedback;
    }

    private void HideGhost()
    {
        AssetList.GiveFeedback -= OnListGiveFeedback;

        if (_adornerLayer != null && _adorner != null) _adornerLayer.Remove(_adorner);

        _adorner = null;
        _adornerLayer = null;
        _adornerRoot = null;
    }

    private List<AssetItemViewModel> CollectDraggableItems(AssetItemViewModel candidate)
    {
        var items = AssetList.SelectedItems
            .OfType<AssetItemViewModel>()
            .Where(i => i is { CanDrag: true, HasLocalFile: true })
            .ToList();

        if (candidate.HasLocalFile && !items.Contains(candidate)) items.Insert(0, candidate);

        return items;
    }

    private AssetItemViewModel? FindItem(DependencyObject? source)
    {
        if (source == null) return null;

        return ItemsControl.ContainerFromElement(AssetList, source) is ListBoxItem container
            ? container.DataContext as AssetItemViewModel
            : null;
    }

    private void OnAssetsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAssetsDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!CanAcceptDrop(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = paths.Where(File.Exists).ToArray();

        if (files.Length == 0) return;

        _ = _viewModel?.UploadFilesAsync(files);
    }

    private bool CanAcceptDrop(DragEventArgs e)
    {
        if (_isDraggingOut) return false;
        if (_viewModel is not { IsConnected: true, IsProcessing: false }) return false;

        return e.Data.GetDataPresent(DataFormats.FileDrop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}
