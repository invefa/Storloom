using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.App;
using Xunit;
namespace Storloom.App.Tests;
public sealed class PreferencePolicyTests {
    private static Type Type(string name)=>typeof(ChatReaderWindow).Assembly.GetType("Storloom.App."+name)!;
    [Fact]public void AutomaticWorkersRespectTaskDiskCpuAndMemoryLimits(){
        var choose=Type("WorkerRecommendation").GetMethod("Choose",BindingFlags.NonPublic|BindingFlags.Static)!;
        int Count(string task,bool? disk,int cpu=16,long memory=8L<<30)=>(int)choose.Invoke(null,[task,disk,cpu,memory])!;
        Assert.Equal(4,Count("backup",false));Assert.Equal(5,Count("backup",true));Assert.Equal(6,Count("export",false));Assert.Equal(4,Count("export",true));Assert.Equal(5,Count("encryption",true));Assert.Equal(2,Count("encryption",false,2));Assert.Equal(2,Count("export",false,16,1L<<30));Assert.InRange(Count("backup",null),1,8);
    }
    [Fact]public void ReaderAppearancePersistsOnlyPresentationFields(){
        using var input=JsonDocument.Parse("""{"palette":"wechat","dark":true,"view":{"pageSize":500,"autoBottom":true,"minimap":"collapsed","text":"private message","previewHeight":-1},"password":"secret"}""");
        var value=(JsonObject)Type("ReaderPreferences").GetMethod("Sanitize",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[input.RootElement])!;
        Assert.Equal("wechat",value["palette"]!.GetValue<string>());Assert.True(value["dark"]!.GetValue<bool>());Assert.Equal(500,value["view"]!["pageSize"]!.GetValue<double>());Assert.Null(value["password"]);Assert.Null(value["view"]!["text"]);Assert.Null(value["view"]!["previewHeight"]);
    }
    [Fact]public void PickerHistorySeparatesPurposesAndKeepsStableIdentity(){
        var method=Type("PickerHistory").GetMethod("Identity",BindingFlags.Static|BindingFlags.NonPublic)!;
        object Id(string purpose)=>method.Invoke(null,[purpose])!;
        Assert.Equal(Id("backup-open"),Id("backup-open"));Assert.NotEqual(Id("backup-open"),Id("database-key-import"));Assert.NotEqual(Id("reader-save-image"),Id("reader-save-file"));
    }
}
