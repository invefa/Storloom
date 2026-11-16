using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Storloom.Wpf;

public static class ComponentVisuals {
    public static System.Windows.Shapes.Path Icon(string geometry, double size = 16) {
        var data=Geometry.Parse(geometry);var bounds=data.Bounds;double longest=Math.Max(bounds.Width,bounds.Height);
        var icon=new System.Windows.Shapes.Path { Data=data,Width=longest>0?Math.Max(1,size*bounds.Width/longest):size,Height=longest>0?Math.Max(1,size*bounds.Height/longest):size,Stretch=Stretch.Fill,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");return icon;
    }
    public static void AddSelectionColumn(DataGrid table, string property = "Selected") {
        table.IsReadOnly=false;foreach(var column in table.Columns)column.IsReadOnly=true;
        var template=new DataTemplate();var check=new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton.IsCheckedProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        check.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty,new Thickness(0));check.SetValue(FrameworkElement.MinWidthProperty,0d);check.SetValue(FrameworkElement.WidthProperty,20d);check.SetValue(Control.PaddingProperty,new Thickness(0));template.VisualTree=check;
        var style=new Style(typeof(DataGridCell),table.TryFindResource(typeof(DataGridCell)) as Style);style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
        table.Columns.Insert(0,new DataGridTemplateColumn{CellTemplate=template,CellStyle=style,Width=28,MinWidth=28,CanUserResize=false,CanUserSort=false});TableInteraction.EnableChecks(table,property);
    }
}
