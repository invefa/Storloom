using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class ArchiveIdentityWindow : ThemeWindow {
    public ChatPlatform? Platform { get; private set; }
    public string? Account { get; private set; }
    public ArchiveIdentityWindow(ArchiveReference reference) : base(L.T("ArchiveIdentity"), 660, 390) {
        ShowInTaskbar = false;
        var layout = View.Grid(View.Auto, View.Auto, View.Auto, View.Auto, View.Fill, View.Auto); layout.Margin = new Thickness(24); Body.Children.Add(layout);
        View.Add(layout, View.Text(L.T("ArchiveIdentityHelp"), color: "Muted"), 0);
        var path = View.Text(reference.Directory, 12, "Muted"); path.ToolTip = reference.Directory; View.Add(layout, path, 1);
        var platform = new ComboBox { Name = "archive_identity_platform", ItemsSource = new[] { L.T("ArchiveIdentityUnassigned"), "QQ", "WeChat" },
            SelectedIndex = reference.Metadata.Platform == ChatPlatform.QQ ? 1 : reference.Metadata.Platform == ChatPlatform.WeChat ? 2 : 0 };
        var account = View.Input(reference.Metadata.Account ?? ""); account.Name = "archive_identity_account";
        View.Add(layout, View.Field("ArchiveIdentityPlatform", platform), 2); View.Add(layout, View.Field("Account", account), 3);
        var notice = View.Text("", 12, "Muted"); View.Add(layout, notice, 4);
        var save = View.Button("ArchiveIdentitySave", () => {
            string value = account.Text.Trim();
            if (value.Length > 128 || value.Any(char.IsControl) || value.Length > 0 && platform.SelectedIndex == 0) { notice.Text = L.T("ArchiveIdentityRequired"); account.Focus(); return; }
            Platform = platform.SelectedIndex == 1 ? ChatPlatform.QQ : platform.SelectedIndex == 2 ? ChatPlatform.WeChat : null;
            Account = value.Length == 0 ? null : value; DialogResult = true;
        }, true); save.Name = "archive_identity_save"; save.IsDefault = true;
        var cancel = View.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true;
        var buttons = View.Row(cancel, save); buttons.HorizontalAlignment = HorizontalAlignment.Right; View.Add(layout, buttons, 5);
    }
}
