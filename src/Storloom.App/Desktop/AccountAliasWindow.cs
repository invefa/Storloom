namespace Storloom.App;

internal sealed class AccountAliasWindow:ThemeWindow {
    internal string AccountAlias{get;private set;}="";
    internal string SourceAlias{get;private set;}="";
    internal AccountAliasWindow(string account,string source,string accountAlias,string sourceAlias):base(L.T("AccountAliases"),600,360){
        MinWidth=480;MinHeight=0;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height-40;Caption.ShowMaximize=Caption.ShowMinimize=false;
        var accountInput=View.Input(accountAlias);accountInput.Name="account_alias";accountInput.MaxLength=80;
        var sourceInput=View.Input(sourceAlias);sourceInput.Name="source_alias";sourceInput.MaxLength=80;
        var error=View.Text("",color:"Muted");
        var save=View.Button("Apply",()=>{
            string a=accountInput.Text.Trim(),s=sourceInput.Text.Trim();if(a.Any(char.IsControl)||s.Any(char.IsControl)){error.Text=L.T("AliasInvalid");return;}
            AccountAlias=a;SourceAlias=s;DialogResult=true;
        },true);save.IsDefault=true;save.Name="aliases_save";var cancel=View.Button("Cancel",Close);cancel.IsCancel=true;
        var actions=View.Row(cancel,save);actions.HorizontalAlignment=HorizontalAlignment.Right;actions.Margin=new Thickness(0,14,0,0);
        var sourceText=View.Text(source,12,"Muted");sourceText.Margin=new Thickness(0,0,0,10);sourceText.TextWrapping=TextWrapping.Wrap;
        var form=View.Stack(View.Text(account,14),sourceText,View.Text(L.T("AccountAliasesHelp"),color:"Muted"),View.Field("AccountAlias",accountInput),View.Field("SourceAlias",sourceInput),error);
        var layout=View.Grid(View.Fill,View.Auto);layout.Margin=new Thickness(22);var scroll=View.FormScroll(form);scroll.MaxHeight=SystemParameters.WorkArea.Height-180;View.Add(layout,scroll,0);View.Add(layout,actions,1);Body.Children.Add(layout);Loaded+=(_,_)=>accountInput.Focus();
    }
}
