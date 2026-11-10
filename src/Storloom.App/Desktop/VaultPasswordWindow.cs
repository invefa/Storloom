namespace Storloom.App;

internal sealed class VaultPasswordWindow : ThemeWindow {
    private readonly PasswordBox password=View.SecretInput(),confirmation=View.SecretInput();
    internal string? Password { get; private set; }
    internal void ClearSecret() => Password=null;
    internal bool IncludeMedia { get; private set; }=true;
    internal int Workers { get; private set; }=2;
    public VaultPasswordWindow(bool creating,bool encrypting) : base(L.T(creating?"VaultCreate":"VaultUnlock"),520,creating?480:encrypting?390:260) {
        MinWidth=500;MinHeight=240;Height=creating?550:encrypting?430:280;ResizeMode=ResizeMode.NoResize;
        var stack=new StackPanel{Margin=new Thickness(22)};Body.Children.Add(stack);
        stack.Children.Add(View.Text(L.T(creating?"VaultCreateHelp":"VaultUnlockHelp"),color:"Muted"));
        stack.Children.Add(View.Text(L.T("VaultPassword")));stack.Children.Add(password);
        if(creating){stack.Children.Add(View.Text(L.T("VaultConfirmPassword")));stack.Children.Add(confirmation);}
        var media=new CheckBox{Content=L.T("VaultIncludeMedia"),IsChecked=true,Margin=new Thickness(0,12,0,0)};
        var workers=new ComboBox{ItemsSource=new[]{1,2,4},SelectedItem=2,MinWidth=75,HorizontalAlignment=HorizontalAlignment.Left};
        if(encrypting){stack.Children.Add(media);stack.Children.Add(View.Text(L.T("VaultPartialHelp"),color:"Muted"));stack.Children.Add(View.Text(L.T("VaultWorkers")));stack.Children.Add(workers);}
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var row=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var cancel=new Button{Content=L.T("Cancel"),IsCancel=true};cancel.Click+=(_,_)=>Close();row.Children.Add(cancel);
        var okay=new Button{Content=L.T(encrypting?"VaultEncrypt":"VaultUnlock"),IsDefault=true};okay.Click+=(_,_)=>{
            if(creating&&(password.Password.Length<10||password.Password!=confirmation.Password)){error.Text=L.T("VaultPasswordMismatch");return;}
            if(password.Password.Length==0)return;
            Password=password.Password;IncludeMedia=media.IsChecked==true;Workers=workers.SelectedItem is int count?count:2;password.Clear();confirmation.Clear();DialogResult=true;
        };row.Children.Add(okay);stack.Children.Add(row);Loaded+=(_,_)=>password.Focus();
    }
}
