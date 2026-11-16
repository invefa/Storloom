namespace Storloom.Wpf;

public enum StatusTone { Information, Busy, Success, Error }

public sealed class StatusBanner : Border {
    private readonly TextBlock message;
    private readonly System.Windows.Shapes.Path icon;
    private readonly ProgressBar progress;
    private readonly RotateTransform rotation = new();
    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(nameof(ReduceMotion), typeof(bool), typeof(StatusBanner), new PropertyMetadata(false, (d, _) => ((StatusBanner)d).UpdateAnimation()));
    public bool ReduceMotion { get => (bool)GetValue(ReduceMotionProperty); set => SetValue(ReduceMotionProperty, value); }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBanner), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(StatusTone), typeof(StatusBanner), new PropertyMetadata(StatusTone.Information, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StatusTone Tone { get => (StatusTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) { var banner=(StatusBanner)target; if(banner.message!=null)banner.UpdateState(); }
    public StatusBanner() : this("") { }
    public StatusBanner(string text, StatusTone tone = StatusTone.Information) {
        Padding = new Thickness(12); CornerRadius = new CornerRadius(4); BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(BorderBrushProperty, "Line");
        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } } }; var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        icon = ComponentVisuals.Icon("M 8,1 A 7,7 0 1 1 8,15 A 7,7 0 1 1 8,1", 14);
        icon.RenderTransformOrigin = new Point(.5, .5); icon.RenderTransform = rotation;
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        IsVisibleChanged += (_, _) => UpdateAnimation();
        message = new TextBlock(); message.Margin = new Thickness(0); message.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(message, 1); row.Children.Add(icon); row.Children.Add(message); layout.Children.Add(row);
        progress = new ProgressBar { Height = 3, Margin = new Thickness(26, 10, 0, 0), IsIndeterminate = true };
        Grid.SetRow(progress, 1); layout.Children.Add(progress); Child = layout;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Polite);
        SetResourceReference(ReduceMotionProperty, "ReduceMotion");
        Show(text, tone);
    }
    public void Show(string text, StatusTone tone = StatusTone.Information) {
        SetCurrentValue(TextProperty, text); SetCurrentValue(ToneProperty, tone); UpdateState();
    }
    private void UpdateState() {
        message.Text = Text;
        icon.Data = Geometry.Parse(Tone switch {
            StatusTone.Success => "M 2,8 L 6,12 L 14,3",
            StatusTone.Error => "M 3,3 L 13,13 M 13,3 L 3,13",
            StatusTone.Busy => "M 3,8 A 5,5 0 1 1 8,13",
            _ => "M 8,1 A 7,7 0 1 1 8,15 A 7,7 0 1 1 8,1 M 8,7 L 8,12 M 8,4 L 8,4.5" });
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, Tone == StatusTone.Error ? "Danger" : "Muted");
        progress.Visibility = Tone == StatusTone.Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnimation();
    }
    private void UpdateAnimation() {
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        rotation.Angle = 0;
        if (Tone != StatusTone.Busy || !IsLoaded || !IsVisible || ReduceMotion) return;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1))) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }
}
