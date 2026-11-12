namespace Storloom.App;

internal sealed class KeyPasswordWindow : ThemeWindow {
    internal string? Password { get; private set; }
    internal KeyPasswordWindow(bool creating) : base(L.T(creating?"ProtectedKeySave":"ProtectedKeyUnlock"),480,260) {
        MinWidth=400;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.Height;MinHeight=0;Caption.ShowMaximize=Caption.ShowMinimize=false;
        var stack=View.Stack(View.Text(L.T(creating?"ProtectedKeyHelp":"ProtectedKeyUnlockHelp"),color:"Muted"));stack.Margin=new Thickness(22);Body.Children.Add(stack);
        var password=View.SecretInput();var confirmation=View.SecretInput();stack.Children.Add(View.Field("VaultPassword",password));if(creating)stack.Children.Add(View.Field("VaultConfirmPassword",confirmation));
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var buttons=View.Row(View.Button("Cancel",()=>Close()),View.Button(creating?"ProtectedKeySave":"ProtectedKeyUnlock",()=>{
            if(password.Password.Length<8||(creating&&password.Password!=confirmation.Password)){error.Text=L.T("VaultPasswordMismatch");return;}
            Password=password.Password;password.Clear();confirmation.Clear();DialogResult=true;
        },true));buttons.HorizontalAlignment=HorizontalAlignment.Right;buttons.Margin=new Thickness(0,12,0,0);stack.Children.Add(buttons);
        Closed+=(_,_)=>{password.Clear();confirmation.Clear();};Loaded+=(_,_)=>password.Focus();
    }
    internal void ClearSecret()=>Password=null;
}
