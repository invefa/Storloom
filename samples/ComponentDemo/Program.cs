using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Storloom.Wpf;

internal static class Program {
    [STAThread] private static int Main(string[] args) {
        var app=new Application();ComponentTheme.Apply(ComponentPalette.Dark);
        var body=new StackPanel { Margin=new Thickness(24) };
        var window=new Window { Title="Reusable WPF components",Width=850,Height=650,Content=body };
        window.SetResourceReference(Control.BackgroundProperty,"Canvas");window.SetResourceReference(Control.ForegroundProperty,"Ink");
        body.Children.Add(new TextBlock { Text="Independent component library",FontSize=24,Margin=new Thickness(0,0,0,16) });
        var theme=new ComboBox { ItemsSource=Enum.GetValues<ComponentPalette>(),SelectedItem=ComponentPalette.Dark };
        theme.SelectionChanged+=(_,_)=>ComponentTheme.Apply((ComponentPalette)theme.SelectedItem);body.Children.Add(theme);
        body.Children.Add(new TextBox { Text="Edit a value",Margin=new Thickness(0,12,0,0) });
        body.Children.Add(new PasswordBox { Password="example" });
        var table=new AdaptiveTable { AutoGenerateColumns=false,Height=210,IsReadOnly=true,Margin=new Thickness(0,16,0,8) };
        table.Columns.Add(new DataGridTextColumn { Header="Name",Binding=new Binding(nameof(Row.Name)),MinWidth=80 });
        table.Columns.Add(new DataGridTextColumn { Header="Details",Binding=new Binding(nameof(Row.Details)),MinWidth=80 });
        ComponentVisuals.AddSelectionColumn(table);
        var rows=new[]{new Row("One","A short value"),new Row("Two","A much longer value to demonstrate adaptive column sizing"),new Row("Three","Another value")};table.ItemsSource=rows;
        var selection=new TableSelection(table,"select_all");TableInteraction.Configure(table);body.Children.Add(table);
        var invert=new Button { Content="Invert selection",HorizontalAlignment=HorizontalAlignment.Left };invert.Click+=(_,_)=>selection.Invert();body.Children.Add(invert);
        var banner=new StatusBanner("Ready",StatusTone.Information) { Margin=new Thickness(0,12,0,8) };body.Children.Add(banner);
        var run=new Button { Content="Try loading feedback",HorizontalAlignment=HorizontalAlignment.Left };
        run.Click+=async(_,_)=>{run.IsEnabled=false;banner.Show("Working…",StatusTone.Busy);await Task.Delay(1800);banner.Show("Complete",StatusTone.Success);run.IsEnabled=true;};body.Children.Add(run);
        if(args.Contains("--smoke")) {
            window.ShowInTaskbar=false;
            window.Loaded+=(_,_)=>{
                try {
                    if(typeof(StatusBanner).Assembly.GetReferencedAssemblies().Any(a=>a.Name is "Storloom" or "Storloom.Core" or "Wpf.Ui"))throw new InvalidOperationException("Business dependency detected.");
                    selection.Set(true);if(selection.Header.IsChecked!=true)throw new InvalidOperationException("Select all failed.");
                    rows[0].Selected=false;if(selection.Header.IsChecked!=null)throw new InvalidOperationException("Partial state failed.");
                    banner.Show("Working",StatusTone.Busy);
                    var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(250) };
                    timer.Tick+=(_,_)=>{timer.Stop();try{var layout=(Grid)banner.Child;var row=(Grid)layout.Children[0];var icon=(System.Windows.Shapes.Path)row.Children[0];var rotation=(RotateTransform)icon.RenderTransform;if(!rotation.HasAnimatedProperties||rotation.Angle<=0)throw new InvalidOperationException("Spinner did not animate.");ComponentTheme.Apply(ComponentPalette.Light,true);if(rotation.HasAnimatedProperties)throw new InvalidOperationException("Reduced motion failed.");app.Shutdown(0);}catch(Exception ex){Fail(ex);}};timer.Start();
                }catch(Exception ex){Fail(ex);}
            };
        }
        return app.Run(window);
        void Fail(Exception ex){var directory=System.IO.Path.Combine(AppContext.BaseDirectory,"test-output");System.IO.Directory.CreateDirectory(directory);System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"smoke-error.txt"),ex.ToString());app.Shutdown(1);}
    }
    private sealed class Row(string name,string details):ISelectableRow {
        private bool selected;
        public string Name{get;}=name;public string Details{get;}=details;
        public bool Selected { get=>selected;set{if(selected==value)return;selected=value;PropertyChanged?.Invoke(this,new(nameof(Selected)));} }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
