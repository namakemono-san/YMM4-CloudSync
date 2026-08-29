using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace YMM4CloudSync.Core.Views.Tabs;

internal sealed class AssetDragAdorner : Adorner
{
    private readonly ContentPresenter _presenter;

    private double _left;
    private double _top;

    public AssetDragAdorner(UIElement adornedElement, string label) : base(adornedElement)
    {
        IsHitTestVisible = false;

        _presenter = new ContentPresenter
        {
            Content = BuildBadge(label)
        };

        AddVisualChild(_presenter);
    }

    public void UpdatePosition(Point position)
    {
        _left = position.X;
        _top = position.Y;

        (Parent as AdornerLayer)?.Update(AdornedElement);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _presenter;

    protected override Size MeasureOverride(Size constraint)
    {
        _presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        return _presenter.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _presenter.Arrange(new Rect(new Point(0, 0), _presenter.DesiredSize));

        return finalSize;
    }

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var group = new GeneralTransformGroup();

        group.Children.Add(base.GetDesiredTransform(transform));
        group.Children.Add(new TranslateTransform(_left, _top));

        return group;
    }

    private static Border BuildBadge(string label)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 170, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 4, 8, 4),
            SnapsToDevicePixels = true,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 12,
                MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        border.Effect = new DropShadowEffect
        {
            BlurRadius = 6,
            ShadowDepth = 2,
            Opacity = 0.5
        };

        return border;
    }
}
