using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NEXCORE;

/// <summary>Fixed-size game cards: realizes only the viewport and one extra row.</summary>
public sealed class VirtualizingGamePanel : VirtualizingPanel, IScrollInfo
{
    internal const double CardWidth = 194, CardHeight = 345;
    private Size _extent, _viewport;
    private double _offset;
    private int _columns = 1;
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new Size();
        double width = double.IsInfinity(available.Width) ? Math.Max(CardWidth, ActualWidth) : Math.Max(0, available.Width);
        double height = double.IsInfinity(available.Height) ? CardHeight * 3 : Math.Max(0, available.Height);
        _columns = Math.Max(1, (int)(width / CardWidth));
        _viewport = new Size(width, height);
        _extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)_columns) * CardHeight);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();
        int first = Math.Min(owner.Items.Count, Math.Max(0, (int)(_offset / CardHeight) * _columns));
        int last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((_offset + height) / CardHeight) + 1) * _columns - 1);
        // Accessing InternalChildren connects the panel to its items generator.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < first || itemIndex > last)
            { generator.Remove(position, 1); RemoveInternalChildRange(i, 1); }
        }
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool fresh);
                    if (fresh)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(CardWidth, CardHeight));
                }
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % _columns * CardWidth, index / _columns * CardHeight - _offset, CardWidth, CardHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The generator has already updated its map; discard stale visuals.
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or
            System.Collections.Specialized.NotifyCollectionChangedAction.Replace or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
        {
            int index = args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move ? args.OldPosition.Index : args.Position.Index;
            if (index >= 0 && index + args.ItemUICount <= InternalChildren.Count) RemoveInternalChildRange(index, args.ItemUICount);
        }
        base.OnItemsChanged(sender, args); InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        double top = index / _columns * CardHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + CardHeight > _offset + ViewportHeight) SetVerticalOffset(top + CardHeight - ViewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (DependencyObject? node = visual; node != null && node != this; node = VisualTreeHelper.GetParent(node))
            if (VisualTreeHelper.GetParent(node) == this && node is UIElement child)
            {
                int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(child), 0));
                BringIndexIntoView(index); break;
            }
        return rectangle;
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        _offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset - 40);
    public void LineDown() => SetVerticalOffset(_offset + 40);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 120);
    public void MouseWheelDown() => SetVerticalOffset(_offset + 120);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
