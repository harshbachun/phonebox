using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneBox.Agent;

static void Check(bool ok, string description)
{
    if (!ok) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}

var dir = Directory.CreateTempSubdirectory("PhoneBox-comparison-");
try
{
    var path = Path.Combine(dir.FullName, "config.json");
    File.WriteAllText(path, """
        { "pages": [
          {"id":"discord","title":"My Discord","widgets":[{"id":"dc","type":"discord","w":12,"h":6,"options":{"layout":"page","control":true,"favorites":["77"],"dimFrom":4}}]},
          {"id":"valorant","title":"VALORANT","widgets":[{"id":"game","type":"valorant","w":12,"h":6}]},
          {"id":"ai","widgets":[{"id":"q","type":"quotas","w":12,"h":6,"options":{"style":"bars","pageSize":0}}]}],
          "phones":[{"id":"a52","pages":["discord","valorant"]},{"id":"other","pages":["ai"]}],
          "valorant":{"clipKey":"F10","talkKey":"V"}, "updates":{"auto":false} }
        """);
    var store = new ConfigStore(path);
    string Json(object value) => JsonSerializer.Serialize(value, ConfigStore.WriteOpts);
    var before = store.Get();
    var pages = before.Pages.Select(Json).ToArray();
    var game = Json(before.Valorant);
    store.PersistGenerated();
    var migrated = new ConfigStore(path).Get();
    Check(migrated.Pages.Select(p => p.Id).SequenceEqual(["discord", "discord-official", "valorant", "ai"]), "adjacent official page and previous page order");
    Check(migrated.Pages.Where(p => p.Id != "discord-official").Select(Json).SequenceEqual(pages), "every original page and option preserved");
    var official = migrated.Pages[1].Widgets!.Single();
    Check(official.Type == "discord" && official.Str("layout") == null && official.Bool("control") == true && official.W == 12 && official.H == 6, "full-size official Discord renderer");
    Check(migrated.PagesFor("a52").Count() == 3 && migrated.PagesFor("other").Count() == 1, "per-phone page selections");
    Check(Json(migrated.Valorant) == game && !migrated.Updates.Auto && migrated.CodexBar.CodexLiveQuota, "VALORANT settings, automatic updates, live quotas");
    migrated.Pages.RemoveAt(1);
    migrated.CodexBar.CodexLiveQuota = false;
    Check(store.Save(Json(migrated)) == null, "save later comparison preferences");
    var restarted = new ConfigStore(path);
    restarted.PersistGenerated();
    Check(restarted.Get().Pages.Count == 3 && !restarted.Get().CodexBar.CodexLiveQuota, "restart respects later page removal and quota opt-out");

    foreach (var route in new[] { "/api/valorant", "/api/valorant/map", "/api/valorant/talk/down", "/api/valorant/talk/renew", "/api/valorant/talk/up", "/api/valorant/clip", "/api/valorant/mic/toggle", "/api/valorant/stats/refresh" })
        Check(Lan.AllowedOverLan(route), "phone route " + route);

    var states = JsonNode.Parse("""[{"user":{"id":"1","username":"Sam"},"voice_state":{"mute":true}},{"user":{"id":"2","username":"Sam"},"voice_state":{"self_deaf":true}}]""");
    var parse = typeof(DiscordMonitor).GetMethod("MembersOf", BindingFlags.NonPublic | BindingFlags.Static)!;
    var members = (List<DiscordMember>)parse.Invoke(null, [states, "2"])!;
    Check(!members[0].IsSelf && members[0].Muted && members[0].ServerMuted && members[1].IsSelf && members[1].Deafened && !members[1].ServerDeafened, "Discord self ID, duplicate names and server-enforced voice flags");
    Check((members[1] with { Speaking = true }).IsSelf, "official speaking updates retain the A52 self badge");
}
finally
{
    dir.Delete(true);
}
