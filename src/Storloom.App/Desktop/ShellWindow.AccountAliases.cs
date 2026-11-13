using Storloom.Core;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record AccountAliases(Dictionary<string,string> Accounts,Dictionary<string,string> Sources);
    private string AccountAliasesPath=>Path.Combine(dataRoot,"account-aliases.json");
    private AccountAliases LoadAccountAliases(){
        if(!File.Exists(AccountAliasesPath))return new(new(StringComparer.Ordinal),new(StringComparer.Ordinal));
        PathPolicy.AssertNoLinks(AccountAliasesPath);if(new FileInfo(AccountAliasesPath).Length>1024*1024)throw new InvalidDataException("Invalid account alias settings.");
        var result=JsonFiles.Read<AccountAliases>(AccountAliasesPath);if(result.Accounts==null||result.Sources==null||result.Accounts.Values.Concat(result.Sources.Values).Any(v=>v==null||v.Length>80||v.Any(char.IsControl)))throw new InvalidDataException("Invalid account alias settings.");return result;
    }
    private void EditAccountAliases(){
        if(selectedHomeSource is not {} item)return;
        var aliases=LoadAccountAliases();var window=new AccountAliasWindow(item.Account,item.SourceLabel,aliases.Accounts.GetValueOrDefault(item.AccountKey)??"",aliases.Sources.GetValueOrDefault(item.Id)??""){Owner=this};
        if(window.ShowDialog()!=true)return;
        static void Set(Dictionary<string,string> entries,string key,string value){if(value.Length==0)entries.Remove(key);else entries[key]=value;}
        Set(aliases.Accounts,item.AccountKey,window.AccountAlias);Set(aliases.Sources,item.Id,window.SourceAlias);JsonFiles.Write(AccountAliasesPath,aliases);selectedArchiveId=item.Id;RenderChatStage();
    }
}
