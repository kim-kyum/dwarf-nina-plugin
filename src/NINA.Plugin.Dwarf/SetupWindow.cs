using System.Windows;
using System.Windows.Controls;
using Dwarf.Core;

namespace DwarfNina.Plugin;

/// <summary>Tiny settings dialog built in code (no XAML): model, IP address, default filter, default gain.</summary>
internal static class SetupWindow {
    public static bool Show(DwarfSettings settings) {
        var models = DeviceProfile.All.ToList();
        var model = new ComboBox { ItemsSource = models.Select(m => m.DisplayName).ToList(), SelectedIndex = Math.Max(0, models.FindIndex(m => m.ModelId == settings.ModelId)) };
        var host = new TextBox { Text = settings.Host, Width = 180 };
        var filter = new ComboBox { Width = 180 };
        var gain = new TextBox { Text = settings.DefaultGain.ToString(), Width = 80 };

        void RefreshFilters() {
            var p = models[Math.Max(0, model.SelectedIndex)];
            filter.ItemsSource = p.FilterLabels.Count > 0 ? p.FilterLabels : new[] { "(none)" };
            filter.SelectedIndex = Math.Clamp(settings.DefaultFilter, 0, Math.Max(0, p.FilterLabels.Count - 1));
        }
        model.SelectionChanged += (_, _) => RefreshFilters();
        RefreshFilters();

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int r, string label, UIElement control, string? hint = null) {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r); grid.Children.Add(l);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(control);
            if (hint != null) panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.6, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(panel, r); Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        }
        Row(0, "기종", model);
        Row(1, "DWARF IP", host, "비워 두면 자동 검색");
        Row(2, "기본 필터", filter, "필터휠 미연결 시 사용");
        Row(3, "기본 게인", gain, $"NINA가 게인을 지정하지 않을 때");

        var ok = new Button { Content = "저장", Width = 90, IsDefault = true, Margin = new Thickness(0, 12, 8, 0) };
        var cancel = new Button { Content = "취소", Width = 90, IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, 4); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);

        var window = new Window {
            Title = "DWARF 설정",
            Content = grid,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true) return false;

        settings.ModelId = models[Math.Max(0, model.SelectedIndex)].ModelId;
        settings.Host = host.Text.Trim();
        settings.DefaultFilter = Math.Max(0, filter.SelectedIndex);
        if (int.TryParse(gain.Text.Trim(), out var g)) settings.DefaultGain = g;
        return true;
    }
}
