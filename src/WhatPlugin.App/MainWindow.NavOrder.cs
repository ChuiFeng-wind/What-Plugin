using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WhatPlugin.Core;

namespace WhatPlugin.App;

/// <summary>左侧导航的自定义顺序：拖动、右键菜单（上移、下移）或 Alt+上下键。“设置”不在其中，固定在最下面。</summary>
public partial class MainWindow
{
    private List<string>? _navDefaults;
    private Point _navDragStart;
    private RadioButton? _navDragItem;
    private NavInsertAdorner? _navAdorner;

    private static string NavKey(RadioButton item) => item.Uid is { Length: > 0 } uid ? uid : item.Tag as string ?? "";

    private List<RadioButton> NavItems => NavList.Children.OfType<RadioButton>().ToList();

    private List<string> NavKeys => NavItems.Select(NavKey).ToList();

    /// <summary>启动时（扩展加载完以后）按保存的顺序排列。</summary>
    private void ApplyNavOrder()
    {
        _navDefaults ??= NavKeys;
        var order = NavOrder.Apply(_navDefaults, NavOrder.Parse(_database.GetSetting(NavOrder.Setting)));
        Arrange(order);
    }

    private void Arrange(IReadOnlyList<string> order)
    {
        var byKey = NavItems.ToDictionary(NavKey);
        if (order.SequenceEqual(NavKeys))
            return;
        NavList.Children.Clear();
        foreach (var key in order)
        {
            if (byKey.Remove(key, out var item))
                NavList.Children.Add(item);
        }
        foreach (var rest in byKey.Values)
            NavList.Children.Add(rest);
    }

    private void SaveNavOrder(List<string> order)
    {
        Arrange(order);
        var saved = NavOrder.Merge(NavOrder.Parse(_database.GetSetting(NavOrder.Setting)), order);
        _database.SetSetting(NavOrder.Setting, NavOrder.Serialize(saved));
    }

    private void MoveNav(RadioButton item, int delta)
    {
        if (NavOrder.Move(NavKeys, NavKey(item), delta) is { } order)
        {
            SaveNavOrder(order);
            item.Focus();
        }
    }

    private void MoveNavTo(RadioButton item, int index)
    {
        if (NavOrder.MoveTo(NavKeys, NavKey(item), index) is { } order)
            SaveNavOrder(order);
    }

    private void ResetNavOrder()
    {
        _database.SetSetting(NavOrder.Setting, "");
        if (_navDefaults != null)
            Arrange(NavOrder.Apply(_navDefaults, []));
        StatusText.Text = "左侧导航已恢复默认顺序";
    }

    // ---------- 右键菜单和键盘 ----------

    private void NavList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (AncestorOf<RadioButton>(e.OriginalSource) is not { } item || item.Parent != NavList)
        {
            e.Handled = true;
            return;
        }
        var index = NavList.Children.IndexOf(item);
        var last = NavList.Children.Count - 1;
        var up = MenuEntry("上移", "", () => MoveNav(item, -1), "Alt+↑");
        up.IsEnabled = index > 0;
        var down = MenuEntry("下移", "", () => MoveNav(item, 1), "Alt+↓");
        down.IsEnabled = index < last;
        var top = MenuEntry("移到最上", "", () => MoveNavTo(item, 0));
        top.IsEnabled = index > 0;
        var bottom = MenuEntry("移到最下", "", () => MoveNavTo(item, last));
        bottom.IsEnabled = index < last;
        var reset = MenuEntry("恢复默认顺序", "", ResetNavOrder);
        reset.IsEnabled = _navDefaults != null && !NavKeys.SequenceEqual(_navDefaults);
        OpenMenu(item, e, up, down, top, bottom, new Separator(), reset);
    }

    private void NavList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down))
            return;
        if (AncestorOf<RadioButton>(e.OriginalSource) is not { } item || item.Parent != NavList)
            return;
        MoveNav(item, e.SystemKey == Key.Up ? -1 : 1);
        e.Handled = true;
    }

    // ---------- 拖动 ----------

    private void NavList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _navDragStart = e.GetPosition(NavList);
        _navDragItem = AncestorOf<RadioButton>(e.OriginalSource) is { } item && item.Parent == NavList ? item : null;
    }

    private void NavList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _navDragItem == null)
            return;
        var delta = e.GetPosition(NavList) - _navDragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        var item = _navDragItem;
        _navDragItem = null;
        // 放开按钮的鼠标捕获：拖动结束后不会被当成一次点击去切换页面。
        Mouse.Capture(null);
        try
        {
            DragDrop.DoDragDrop(item, new DataObject(typeof(RadioButton), item), DragDropEffects.Move);
        }
        finally
        {
            HideNavAdorner();
        }
    }

    /// <summary>指针位置对应的插入位置（0 到项数），按每项的中线判断。</summary>
    private int NavInsertIndex(DragEventArgs e, out double lineY)
    {
        var y = e.GetPosition(NavList).Y;
        var items = NavItems;
        for (var i = 0; i < items.Count; i++)
        {
            var top = items[i].TranslatePoint(new Point(0, 0), NavList).Y;
            if (y < top + items[i].ActualHeight / 2)
            {
                lineY = top - 1;
                return i;
            }
        }
        lineY = items.Count == 0 ? 0 : items[^1].TranslatePoint(new Point(0, items[^1].ActualHeight), NavList).Y + 1;
        return items.Count;
    }

    private void NavList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(RadioButton)) is not RadioButton dragged || dragged.Parent != NavList)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        NavInsertIndex(e, out var lineY);
        if (_navAdorner == null && AdornerLayer.GetAdornerLayer(NavList) is { } layer)
        {
            _navAdorner = new NavInsertAdorner(NavList);
            layer.Add(_navAdorner);
        }
        _navAdorner?.MoveTo(lineY);
    }

    private void NavList_DragLeave(object sender, DragEventArgs e)
    {
        // 移到子项上也会触发，只有真正离开列表才隐藏。
        var point = e.GetPosition(NavList);
        if (point.X < 0 || point.Y < 0 || point.X > NavList.ActualWidth || point.Y > NavList.ActualHeight)
            HideNavAdorner();
    }

    private void NavList_Drop(object sender, DragEventArgs e)
    {
        HideNavAdorner();
        if (e.Data.GetData(typeof(RadioButton)) is not RadioButton dragged || dragged.Parent != NavList)
            return;
        var index = NavInsertIndex(e, out _);
        var from = NavList.Children.IndexOf(dragged);
        // 插入位置按移走之后的列表算。
        MoveNavTo(dragged, index > from ? index - 1 : index);
        e.Handled = true;
    }

    private void HideNavAdorner()
    {
        if (_navAdorner != null)
            AdornerLayer.GetAdornerLayer(NavList)?.Remove(_navAdorner);
        _navAdorner = null;
    }

    /// <summary>拖动时显示插入位置的横线。</summary>
    private sealed class NavInsertAdorner : Adorner
    {
        private double _y;

        public NavInsertAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        public void MoveTo(double y)
        {
            if (Math.Abs(_y - y) < 0.5)
                return;
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var brush = (Application.Current.TryFindResource("Accent") as Brush) ?? Brushes.DodgerBlue;
            var width = ((FrameworkElement)AdornedElement).ActualWidth;
            var pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawing.DrawLine(pen, new Point(6, _y), new Point(width - 6, _y));
            drawing.DrawEllipse(brush, null, new Point(6, _y), 3, 3);
        }
    }
}
