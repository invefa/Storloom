namespace Storloom.App;

internal enum StatusTone { Information, Busy, Success, Error }

internal sealed class StatusBanner : Border {
    private readonly TextBlock message;
    private readonly System.Windows.Shapes.Path icon;
    private readonly ProgressBar progress;
    public StatusTone Tone { get; private set; }
    public StatusBanner(string text, StatusTone tone = StatusTone.Information) {
        Padding = new Thickness(12); CornerRadius = new CornerRadius(4); BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(BorderBrushProperty, "Line");
        var layout = View.Grid(View.Auto, View.Auto); var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        icon = View.Icon("M 8,1 A 7,7 0 1 1 8,15 A 7,7 0 1 1 8,1", 14);
        message = View.Text(""); message.Margin = new Thickness(0); message.VerticalAlignment = VerticalAlignment.Center;
        View.Add(row, icon, 0); View.Add(row, message, 0, 1); View.Add(layout, row, 0);
        progress = new ProgressBar { Height = 3, Margin = new Thickness(26, 10, 0, 0), IsIndeterminate = true };
        View.Add(layout, progress, 1); Child = layout;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Polite);
        Show(text, tone);
    }
    public void Show(string text, StatusTone tone = StatusTone.Information) {
        Tone = tone; message.Text = text;
        icon.Data = Geometry.Parse(tone switch {
            StatusTone.Success => "M 2,8 L 6,12 L 14,3",
            StatusTone.Error => "M 3,3 L 13,13 M 13,3 L 3,13",
            StatusTone.Busy => "M 3,8 A 5,5 0 1 1 8,13",
            _ => "M 8,1 A 7,7 0 1 1 8,15 A 7,7 0 1 1 8,1 M 8,7 L 8,12 M 8,4 L 8,4.5" });
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tone == StatusTone.Error ? "Danger" : "Muted");
        progress.Visibility = tone == StatusTone.Busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
