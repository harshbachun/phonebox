using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace Deskmate.Agent;

/// <summary>Settings for the Valorant page (A52 fork). Secrets stay on this computer: nothing here goes to the phone.</summary>
public sealed class ValorantConfig
{
    /// <summary>Switch the phone to <see cref="Page"/> when the game starts and back home when it exits.</summary>
    public bool AutoPage { get; set; } = true;
    public string Page { get; set; } = "valorant";
    /// <summary>Medal's "save clip" hotkey (KeySender syntax, e.g. "f10" or "ctrl+f10"); pressed once per tap.</summary>
    public string ClipKey { get; set; } = "f10";
    /// <summary>Valorant's team voice key, held down while the phone's Talk button is held. One key, no modifiers.</summary>
    public string TalkKey { get; set; } = "v";
    /// <summary>Microphone the mute toggle controls: empty = the Windows default recording device, else part of its name.</summary>
    public string MicDevice { get; set; } = "";
    /// <summary>Host or IP the "ping to my server" probe sends ICMP echo requests to; empty = off.</summary>
    public string PingHost { get; set; } = "";
    /// <summary>HenrikDev API key (https://api.henrikdev.xyz/dashboard/) for post-match stats; empty = stats off.</summary>
    public string StatsApiKey { get; set; } = "";
    /// <summary>Match-history region: eu, na, ap, kr, br, latam; empty = read from the game's log.</summary>
    public string StatsRegion { get; set; } = "";
    /// <summary>Folder with callout / lineup images named after the map (Ascent.png, Bind.jpg …); empty = %APPDATA%\Deskmate\valorant-maps.</summary>
    public string MapsFolder { get; set; } = "";

    internal void Normalize()
    {
        static string Clean(string? s, int max) => (s ?? "").Trim() is var t && t.Length <= max && t.All(c => c >= ' ') ? t : "";
        Page = Clean(Page, 64) is { Length: > 0 } p ? p : "valorant";
        ClipKey = Clean(ClipKey, 40).ToLowerInvariant() is { Length: > 0 } c ? c : "f10";
        TalkKey = Clean(TalkKey, 20).ToLowerInvariant() is { Length: > 0 } k ? k : "v";
        MicDevice = Clean(MicDevice, 200);
        PingHost = Clean(PingHost, 253);
        if (PingHost.Any(ch => ch <= ' ' || ch > '~')) PingHost = "";
        StatsApiKey = Clean(StatsApiKey, 200);
        StatsRegion = Clean(StatsRegion, 8).ToLowerInvariant();
        if (StatsRegion.Length > 0 && StatsRegion is not ("eu" or "na" or "ap" or "kr" or "br" or "latam")) StatsRegion = "";
        MapsFolder = Clean(MapsFolder, 400);
    }
}

public sealed record ValorantMatch(string Id, long StartedAt, string? Map, string? Mode, string? Agent, int Kills, int Deaths, int Assists, bool? Won, int? RoundsWon, int? RoundsLost);

/// <summary>
/// The Valorant page's computer side (A52 fork): notices the game process and switches the phone's page on start / exit,
/// reads your own presence from the Riot Client's local API, presses Medal's clip hotkey, holds the team-voice key only
/// while a phone holds a lease and Valorant is in front, toggles the game microphone, probes a host for latency and fetches
/// finished matches from the HenrikDev API. It never reads game memory and never presses gameplay keys.
/// </summary>
public sealed partial class ValorantHost(ConfigStore config, PushStore push, ILogger<ValorantHost> log) : BackgroundService
{
    public const string ProcessName = "VALORANT-Win64-Shipping";
    /// <summary>A Talk hold lasts this long without a renewal from the phone (it renews every 300 ms).</summary>
    internal const long TalkLeaseMs = 1200;
    internal const int PingEverySec = 2;
    internal const int PingSamples = 90; // 3 minutes at one probe every 2 s

    private readonly Lock _gate = new();

    // ------------------------------------------------------------------ game process and page switching
    private bool _running;
    private long _runningSince;
    private int _phones;
    private (string Page, long At)? _pendingNav;

    public bool Running { get { lock (_gate) return _running; } }

    /// <summary>A phone opened its live stream (it receives events from now on).</summary>
    public void PhoneConnected()
    {
        string? page = null;
        lock (_gate)
        {
            _phones++;
            if (_pendingNav is { } p && Environment.TickCount64 - p.At < 30 * 60_000) page = p.Page;
            _pendingNav = null;
        }
        if (page is not null) EmitGoto(page, "Deskmate connected while VALORANT " + (page == HomePage() ? "was closed" : "was running"));
    }

    /// <summary>A phone's live stream closed: with no phone left, the Talk key comes up at once.</summary>
    public void PhoneDisconnected()
    {
        bool none;
        lock (_gate) { _phones = Math.Max(0, _phones - 1); none = _phones == 0; }
        if (none) EndTalk(null, "phone disconnected");
    }

    internal void SetRunning(bool running) // internal: the tests drive transitions directly
    {
        lock (_gate)
        {
            if (running == _running) return;
            _running = running;
            _runningSince = running ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
        }
        log.LogInformation("valorant: game {State}", running ? "started" : "exited");
        ClearPresence();
        if (!running) EndTalk(null, "game exited");
        if (running) Interlocked.Exchange(ref _statsDueAt, Environment.TickCount64 + 5_000);
        var cfg = config.Get().Valorant;
        if (!cfg.AutoPage) return;
        Navigate(running ? cfg.Page : HomePage(), running ? "VALORANT started" : "VALORANT closed");
    }

    private void Navigate(string page, string detail)
    {
        bool now;
        lock (_gate)
        {
            now = _phones > 0;
            _pendingNav = now ? null : (page, Environment.TickCount64);
        }
        if (now) EmitGoto(page, detail);
    }

    internal List<string> Sent { get; } = []; // the pages switched to, for the tests

    private void EmitGoto(string page, string detail)
    {
        lock (_gate) Sent.Add(page);
        push.Emit("goto", JsonSerializer.SerializeToElement(new { page, rule = "valorant", name = "VALORANT", detail, hold = false, priority = 0, countdown = 0 }));
    }

    private string HomePage()
    {
        var c = config.Get();
        if (!string.IsNullOrWhiteSpace(c.Display.HomePage) && c.Pages.Any(p => p.Id == c.Display.HomePage && p.Enabled)) return c.Display.HomePage!;
        return c.Pages.FirstOrDefault(p => p.Enabled && p.Id != c.Valorant.Page)?.Id ?? "home";
    }

    private static bool GameProcessRunning()
    {
        var list = Process.GetProcessesByName(ProcessName);
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    // ------------------------------------------------------------------ background loops
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => TalkLoop(ct), ct);
        _ = Task.Run(() => PingLoop(ct), ct);
        LoadStats();
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetRunning(GameProcessRunning());
                if (Running && tick % 2 == 0) await PollPresence(ct);
                if (StatsDue()) await FetchStats(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("valorant: {Msg}", ex.Message); }
            tick++;
            await Task.Delay(1000, ct);
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        EndTalk(null, "agent stopping");
        await base.StopAsync(ct);
    }

    // ------------------------------------------------------------------ presence (Riot Client local API, your own entry only)
    public sealed record Presence(string Status, string? Loop, string? Map, string? MapId, string? Mode, string? Queue, int? Ally, int? Enemy, long Since,
        int? PartySize = null, int? MaxPartySize = null);

    private Presence? _presence;
    private string? _presenceError;
    private string? _presenceLoop;
    private long _presenceSince;
    private string? _puuid, _riotName;
    private long _sessionAt;
    private bool _sampleSaved;
    private string? _shard;

    private static readonly HttpClient Riot = new(new HttpClientHandler
    {
        // the Riot Client serves its local API with a self-signed certificate; trust it for 127.0.0.1 only
        ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.Host == "127.0.0.1",
    }) { Timeout = TimeSpan.FromSeconds(3) };

    private void ClearPresence()
    {
        lock (_gate) { _presence = null; _presenceLoop = null; _presenceSince = 0; _sampleSaved = false; }
    }

    private static (int Port, string Password)? ReadLockfile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var parts = sr.ReadToEnd().Trim().Split(':');
            return parts.Length >= 5 && int.TryParse(parts[2], out var port) ? (port, parts[3]) : null;
        }
        catch { return null; }
    }

    private async Task<JsonNode?> RiotGet(int port, string password, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        req.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + password)));
        using var res = await Riot.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task PollPresence(CancellationToken ct)
    {
        if (ReadLockfile() is not { } lf) { SetPresenceError("Riot Client local API not found (no lockfile)"); return; }
        try
        {
            if (_puuid is null || Environment.TickCount64 - _sessionAt > 60_000) // re-read: another account may have signed in
            {
                _sessionAt = Environment.TickCount64;
                var session = await RiotGet(lf.Port, lf.Password, "/chat/v1/session", ct);
                var puuid = Str(session, "puuid");
                var name = Str(session, "game_name");
                var tag = Str(session, "game_tag");
                // RememberAccount sets the fields and saves them, so it must see the old values to notice the change
                if (puuid is not null) RememberAccount(puuid, name is null ? null : tag is null ? name : $"{name}#{tag}");
            }
            if (_puuid is null) { SetPresenceError("Riot Client has no signed-in session yet"); return; }
            var all = await RiotGet(lf.Port, lf.Password, "/chat/v4/presences", ct);
            var mine = all?["presences"]?.AsArray().FirstOrDefault(p => p?["puuid"]?.GetValue<string>() == _puuid && p?["product"]?.GetValue<string>() == "valorant");
            var b64 = mine?["private"]?.GetValue<string>();
            if (string.IsNullOrEmpty(b64)) { SetPresenceError("VALORANT has not reported a presence yet"); return; }
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            if (json is null) { SetPresenceError("VALORANT presence was empty"); return; }
            Apply(ParsePresence(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), json);
        }
        catch (HttpRequestException) { SetPresenceError("Riot Client local API is not answering"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { SetPresenceError("Riot Client local API timed out"); }
        catch (FormatException) { SetPresenceError("VALORANT presence could not be decoded"); }
        catch (JsonException) { SetPresenceError("VALORANT presence could not be read"); }
    }

    private void SetPresenceError(string error)
    {
        lock (_gate) { _presence = null; _presenceError = error; }
    }

    private void Apply(Presence fresh, JsonNode raw)
    {
        string? endedLoop = null;
        lock (_gate)
        {
            // the "since" clock restarts with every change of state; nothing of the old state carries over
            if (fresh.Loop != _presenceLoop || fresh.Status != _presence?.Status) { endedLoop = _presenceLoop; _presenceLoop = fresh.Loop; _presenceSince = fresh.Since; }
            _presence = fresh with { Since = _presenceSince };
            _presenceError = null;
        }
        // a match just ended: its result shows up in the match history after a while
        if (endedLoop == "INGAME" && fresh.Loop != "INGAME") ScheduleStatsAfterMatch();
        if (fresh.Loop == "INGAME" && !_sampleSaved)
        {
            _sampleSaved = true;
            try { File.WriteAllText(Path.Combine(AppPaths.ConfigDir, "valorant-presence-sample.json"), raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { log.LogDebug("valorant: presence sample not saved: {Msg}", ex.Message); }
        }
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static int? Int(JsonNode? n, string key) => n?[key] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : null) : null;

    /// <summary>
    /// Reads the decoded "private" presence. Riot has used a flat shape and one with matchPresenceData /
    /// partyPresenceData; both are tried. Map, mode and score are kept only for the states they belong to, so a
    /// finished match's map or score never shows in the menus.
    /// </summary>
    internal static Presence ParsePresence(JsonNode p, long now)
    {
        var match = p["matchPresenceData"] ?? p;
        var party = p["partyPresenceData"] ?? p;
        var loop = (Str(match, "sessionLoopState") ?? Str(p, "sessionLoopState"))?.ToUpperInvariant();
        var flow = Str(match, "provisioningFlow") ?? Str(p, "provisioningFlow");
        var queue = Str(match, "queueId") ?? Str(p, "queueId") ?? Str(party, "queueId");
        var mapPath = Str(match, "matchMap") ?? Str(p, "matchMap") ?? Str(party, "partyOwnerMatchMap") ?? Str(p, "partyOwnerMatchMap");
        var partyState = (Str(party, "partyState") ?? Str(p, "partyState"))?.ToUpperInvariant();
        var ally = Int(party, "partyOwnerMatchScoreAllyTeam") ?? Int(p, "partyOwnerMatchScoreAllyTeam");
        var enemy = Int(party, "partyOwnerMatchScoreEnemyTeam") ?? Int(p, "partyOwnerMatchScoreEnemyTeam");
        var size = Int(party, "partySize") ?? Int(p, "partySize");
        var capacity = Int(party, "maxPartySize") ?? Int(p, "maxPartySize");

        var status = loop switch
        {
            "INGAME" => "In game",
            "PREGAME" => "Agent select",
            "MENUS" when partyState is "MATCHMAKING" => "In queue",
            "MENUS" when partyState is "MATCHMADE_GAME_STARTING" => "Match found",
            "MENUS" => "In menus",
            _ => "In VALORANT",
        };
        var inMatch = loop is "INGAME" or "PREGAME";
        var mapId = inMatch ? MapCodename(mapPath) : null;
        var mode = flow is "CustomGame" ? "Custom game" : ModeName(queue);
        return new Presence(status, loop, mapId is null ? null : MapName(mapId), mapId, mode, queue,
            loop == "INGAME" ? ally : null, loop == "INGAME" ? enemy : null, now,
            size > 0 ? size : null, size > 0 && capacity >= size ? capacity : null);
    }

    internal static string? MapCodename(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var last = path.TrimEnd('/').Split('/').Last();
        return last.Length > 0 ? last : null;
    }

    // map asset codenames as used in /Game/Maps/<folder>/<codename>; anything unknown shows its codename
    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ascent"] = "Ascent", ["Duality"] = "Bind", ["Triad"] = "Haven", ["Bonsai"] = "Split", ["Port"] = "Icebox",
        ["Foxtrot"] = "Breeze", ["Canyon"] = "Fracture", ["Pitt"] = "Pearl", ["Jam"] = "Lotus", ["Juliett"] = "Sunset",
        ["Infinity"] = "Abyss", ["Rook"] = "Corrode", ["Range"] = "The Range", ["HURM_Alley"] = "District", ["HURM_Yard"] = "Piazza",
        ["HURM_Bowl"] = "Kasbah", ["HURM_Helix"] = "Drift", ["HURM_HighTide"] = "Glitch",
        // seen in ShooterGame.log: Summit loads as /Game/Maps/Plummet/Plummet (match of 2026-11-23), the new Range as PovegliaV2/RangeV2
        ["Plummet"] = "Summit", ["RangeV2"] = "The Range",
    };
    internal static string MapName(string codename) => Maps.TryGetValue(codename, out var n) ? n : codename;

    private static readonly Dictionary<string, string> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["competitive"] = "Competitive", ["unrated"] = "Unrated", ["swiftplay"] = "Swiftplay", ["spikerush"] = "Spike Rush",
        ["deathmatch"] = "Deathmatch", ["ggteam"] = "Escalation", ["onefa"] = "Replication", ["hurm"] = "Team Deathmatch",
        ["premier"] = "Premier", ["snowball"] = "Snowball Fight", ["newmap"] = "New Map",
    };
    internal static string? ModeName(string? queue) => string.IsNullOrWhiteSpace(queue) ? null : Modes.TryGetValue(queue, out var m) ? m : queue;

    // ------------------------------------------------------------------ clip (Medal hotkey)
    private string? _lastTap;
    private long _lastTapAt;
    internal Func<List<string>, bool> Press = KeySender.SendCombo; // the tests count presses

    /// <summary>Presses the clip hotkey once for this tap; a retried request with the same tap id is not pressed again.</summary>
    public object Clip(string? tap)
    {
        var key = config.Get().Valorant.ClipKey;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (tap is { Length: > 0 } && tap == _lastTap && now - _lastTapAt < 10_000) return new { sent = true, duplicate = true, key };
            _lastTap = tap; _lastTapAt = now;
        }
        var combos = KeySender.Parse(key);
        if (combos.Count != 1) return new { sent = false, error = $"Clip key \"{key}\" is not one key combination", key };
        var ok = Press(combos[0]);
        log.LogInformation("valorant: clip hotkey {Key} {Result}", key, ok ? "sent" : "refused by Windows");
        return new { sent = ok, key, error = ok ? null : "Windows did not accept the key press" };
    }

    // ------------------------------------------------------------------ hold to talk
    private string? _talkLease;
    private long _talkExpires;
    private string? _keyDown;         // the key currently held down by us, if any
    private string _talkReason = "Not held";
    internal Func<bool> Focused = ValorantInFront;
    internal Func<string, bool, bool> KeyEdge = SendKeyEdge;

    public string TalkBegin()
    {
        lock (_gate)
        {
            _talkLease = Guid.NewGuid().ToString("N");
            _talkExpires = Environment.TickCount64 + TalkLeaseMs;
            return _talkLease;
        }
    }

    public bool TalkRenew(string lease)
    {
        lock (_gate)
        {
            if (lease != _talkLease) return false;
            _talkExpires = Environment.TickCount64 + TalkLeaseMs;
            return true;
        }
    }

    /// <summary>Ends a hold (the given lease, or any hold when null) and lets the key up now.</summary>
    public void EndTalk(string? lease, string why)
    {
        lock (_gate)
        {
            if (lease is not null && lease != _talkLease) return;
            if (_talkLease is not null) log.LogInformation("valorant: talk released ({Why})", why);
            _talkLease = null;
        }
        TalkStep();
    }

    private async Task TalkLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { TalkStep(); } catch (Exception ex) { log.LogDebug("valorant: talk: {Msg}", ex.Message); }
            try { await Task.Delay(40, ct); } catch (OperationCanceledException) { break; }
        }
        ReleaseKey();
    }

    /// <summary>One pass: the key is down only while a lease is live, a phone is connected, Valorant is in front and the mic is not muted.</summary>
    internal void TalkStep()
    {
        bool want;
        string reason;
        lock (_gate)
        {
            if (_talkLease is not null && Environment.TickCount64 > _talkExpires)
            {
                log.LogInformation("valorant: talk released (phone stopped renewing)");
                _talkLease = null;
            }
            want = _talkLease is not null && _phones > 0;
            reason = _talkLease is null ? "Not held" : _phones == 0 ? "No phone connected" : "";
        }
        var key = config.Get().Valorant.TalkKey;
        if (want && !Focused()) { want = false; reason = "VALORANT is not in front"; }
        if (want && MicMutedCached() == true) { want = false; reason = "Microphone is muted"; }
        if (want) reason = "Talking";
        lock (_gate) _talkReason = reason;
        string? down;
        lock (_gate) down = _keyDown;
        if (down is not null && (!want || down != key)) ReleaseKey();
        if (want)
        {
            lock (_gate) down = _keyDown;
            if (down is null && KeyEdge(key, true)) lock (_gate) _keyDown = key;
        }
    }

    private void ReleaseKey()
    {
        string? down;
        lock (_gate) { down = _keyDown; _keyDown = null; }
        if (down is not null) KeyEdge(down, false);
    }

    private static bool SendKeyEdge(string key, bool down)
    {
        var combos = KeySender.Parse(key);
        return combos.Count == 1 && combos[0].Count == 1 && KeySender.KeyEdge(combos[0][0], down);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    private static (nint Hwnd, bool Valorant) _front;

    private static bool ValorantInFront()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;
        if (hwnd == _front.Hwnd) return _front.Valorant;
        GetWindowThreadProcessId(hwnd, out var pid);
        bool isGame;
        try { using var p = Process.GetProcessById((int)pid); isGame = string.Equals(p.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase); }
        catch { isGame = false; }
        _front = (hwnd, isGame);
        return isGame;
    }

    // ------------------------------------------------------------------ microphone (Windows capture endpoint)
    public sealed record MicState(bool Available, bool? Muted, string? Name, string? Error);
    private (long At, MicState State) _mic;
    internal Func<MicState>? MicOverride; // the tests

    private bool? MicMutedCached()
    {
        if (MicOverride is { } o) return o().Muted;
        if (Environment.TickCount64 - _mic.At > 250) _mic = (Environment.TickCount64, ReadMic(null));
        return _mic.State.Muted;
    }

    public MicState Mic() => MicOverride?.Invoke() ?? (_mic = (Environment.TickCount64, ReadMic(null))).State;

    public MicState ToggleMic() => ReadMic(toggle: true);

    private MicState ReadMic(bool? toggle)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var want = config.Get().Valorant.MicDevice;
            MMDevice? dev = null;
            if (want.Length > 0)
            {
                using var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var d in all)
                {
                    if (dev is null && (d.ID == want || d.FriendlyName.Contains(want, StringComparison.OrdinalIgnoreCase))) dev = d;
                    else d.Dispose();
                }
                if (dev is null) return new(false, null, null, $"No microphone matches \"{want}\"");
            }
            else dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            using (dev)
            {
                var vol = dev.AudioEndpointVolume;
                if (toggle == true)
                {
                    vol.Mute = !vol.Mute;
                    log.LogInformation("valorant: microphone {Name} {State}", dev.FriendlyName, vol.Mute ? "muted" : "unmuted");
                }
                var state = new MicState(true, vol.Mute, dev.FriendlyName, null);
                _mic = (Environment.TickCount64, state);
                return state;
            }
        }
        catch (Exception ex) { return new(false, null, null, "Microphone unavailable: " + ex.Message); }
    }

    // ------------------------------------------------------------------ ping probe
    private readonly Queue<(long At, double? Ms)> _ping = new();
    private string? _pingHost;

    private async Task PingLoop(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            var host = config.Get().Valorant.PingHost;
            lock (_gate)
            {
                if (host != _pingHost) { _pingHost = host; _ping.Clear(); } // a new host: samples of two hosts must not mix
            }
            if (host.Length > 0)
            {
                double? ms;
                try { var r = await ping.SendPingAsync(host, 1500); ms = r.Status == IPStatus.Success ? r.RoundtripTime : null; }
                catch { ms = null; }
                lock (_gate)
                {
                    if (host == _pingHost)
                    {
                        _ping.Enqueue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ms));
                        while (_ping.Count > PingSamples) _ping.Dequeue();
                    }
                }
            }
            try { await Task.Delay(PingEverySec * 1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private object PingSnapshot()
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_pingHost)) return new { host = (string?)null, samples = Array.Empty<double?>() };
            var samples = _ping.Select(s => s.Ms).ToArray();
            var lost = samples.Count(s => s is null);
            var ok = samples.Where(s => s is not null).Select(s => s!.Value).ToArray();
            return new
            {
                host = _pingHost,
                everySec = PingEverySec,
                windowSec = samples.Length * PingEverySec,
                samples,
                lastMs = samples.Length > 0 ? samples[^1] : null,
                avgMs = ok.Length > 0 ? Math.Round(ok.Average(), 1) : (double?)null,
                lossPct = samples.Length > 0 ? Math.Round(100.0 * lost / samples.Length, 1) : (double?)null,
                sent = samples.Length,
                unanswered = lost,
            };
        }
    }

    // ------------------------------------------------------------------ post-match stats (HenrikDev API, key stays here)
    private static readonly HttpClient Henrik = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, ValorantMatch> _matches = [];
    private long _statsDueAt = Environment.TickCount64 + 10_000;
    private long _statsAt;
    private string? _statsError;
    private readonly Queue<long> _afterMatch = new();
    private string StatsPath => Path.Combine(AppPaths.ConfigDir, "valorant-stats.json");

    private sealed class StatsFile
    {
        public string? Puuid { get; set; }
        public string? RiotId { get; set; }
        public List<ValorantMatch> Matches { get; set; } = [];
    }

    private void LoadStats()
    {
        try
        {
            if (!File.Exists(StatsPath)) return;
            var f = JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(StatsPath));
            if (f is null) return;
            lock (_gate)
            {
                _puuid ??= f.Puuid;
                _riotName ??= f.RiotId;
                foreach (var m in f.Matches) _matches[m.Id] = m;
            }
        }
        catch (Exception ex) { log.LogWarning("valorant: stats file unreadable: {Msg}", ex.Message); }
    }

    private void SaveStats()
    {
        StatsFile f;
        lock (_gate) f = new() { Puuid = _puuid, RiotId = _riotName, Matches = _matches.Values.OrderByDescending(m => m.StartedAt).Take(300).ToList() };
        try { File.WriteAllText(StatsPath, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { log.LogWarning("valorant: stats not saved: {Msg}", ex.Message); }
    }

    private void RememberAccount(string puuid, string? riotId)
    {
        bool changed;
        lock (_gate) changed = _puuid != puuid || (riotId is not null && _riotName != riotId);
        lock (_gate) { _puuid = puuid; _riotName = riotId ?? _riotName; }
        if (changed) SaveStats();
    }

    private void ScheduleStatsAfterMatch()
    {
        var now = Environment.TickCount64;
        lock (_gate) { _afterMatch.Clear(); foreach (var s in new[] { 45, 120, 300, 600 }) _afterMatch.Enqueue(now + s * 1000L); }
    }

    public void RefreshStats() => Interlocked.Exchange(ref _statsDueAt, Environment.TickCount64);

    private bool StatsDue()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_afterMatch.Count > 0 && _afterMatch.Peek() <= now) { _afterMatch.Dequeue(); return true; }
        }
        if (Interlocked.Read(ref _statsDueAt) <= now) { Interlocked.Exchange(ref _statsDueAt, now + (Running ? 20 : 60) * 60_000L); return true; }
        return false;
    }

    private string? StatsRegion()
    {
        var cfg = config.Get().Valorant.StatsRegion;
        if (cfg.Length > 0) return cfg;
        if (_shard is not null) return _shard;
        // the game's own log names the shard it talks to: https://glz-eu-1.eu.a.pvp.net
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            for (string? line; (line = sr.ReadLine()) is not null;)
                if (ShardRegex().Match(line) is { Success: true } m) return _shard = m.Groups[1].Value.ToLowerInvariant(); // eu, na, ap, kr, latam, br
        }
        catch { }
        return null;
    }

    [GeneratedRegex(@"https://glz-([a-z]+)-1\.[a-z]+\.a\.pvp\.net")] private static partial Regex ShardRegex();

    /// <summary>What stats need before they can be fetched (empty = ready).</summary>
    private List<string> StatsMissing()
    {
        var missing = new List<string>();
        if (config.Get().Valorant.StatsApiKey.Length == 0) missing.Add("HenrikDev API key (Settings › General › VALORANT)");
        string? puuid;
        lock (_gate) puuid = _puuid;
        if (puuid is null) missing.Add("Your Riot account: start VALORANT once with Deskmate running");
        if (StatsRegion() is null) missing.Add("Region (Settings › General › VALORANT)");
        return missing;
    }

    private async Task FetchStats(CancellationToken ct)
    {
        if (StatsMissing().Count > 0) return;
        var key = config.Get().Valorant.StatsApiKey;
        string puuid;
        lock (_gate) puuid = _puuid!;
        var url = $"https://api.henrikdev.xyz/valorant/v4/by-puuid/matches/{StatsRegion()}/pc/{puuid}?size=10";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", key);
            using var res = await Henrik.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var error = res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "HenrikDev rejected the API key",
                    HttpStatusCode.NotFound => "HenrikDev has no match history for this account and region",
                    HttpStatusCode.TooManyRequests => "HenrikDev rate limit reached; trying again later",
                    _ => $"HenrikDev answered {(int)res.StatusCode}",
                };
                lock (_gate) _statsError = error;
                log.LogInformation("valorant: stats: {Error}", error);
                return;
            }
            var added = Merge(JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)), puuid);
            lock (_gate) { _statsError = null; _statsAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); if (added > 0) _afterMatch.Clear(); }
            if (added > 0) { log.LogInformation("valorant: {N} new match(es) in the history", added); SaveStats(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            lock (_gate) _statsError = "Match history could not be loaded: " + ex.Message;
        }
    }

    /// <summary>Adds the completed matches of a v4 match list; known match ids are left as they are (no double counting).</summary>
    internal int Merge(JsonNode? body, string puuid)
    {
        var added = 0;
        foreach (var m in body?["data"]?.AsArray() ?? [])
        {
            var meta = m?["metadata"];
            var id = Str(meta, "match_id");
            if (id is null || meta?["is_completed"]?.GetValue<bool>() != true) continue;
            var me = m?["players"]?.AsArray().FirstOrDefault(p => Str(p, "puuid") == puuid);
            if (me is null) continue;
            var team = Str(me, "team_id");
            var myTeam = m?["teams"]?.AsArray().FirstOrDefault(t => Str(t, "team_id") == team);
            var started = DateTimeOffset.TryParse(Str(meta, "started_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : 0;
            var match = new ValorantMatch(id, started, Str(meta?["map"], "name"), Str(meta?["queue"], "name"), Str(me["agent"], "name"),
                Int(me["stats"], "kills") ?? 0, Int(me["stats"], "deaths") ?? 0, Int(me["stats"], "assists") ?? 0,
                myTeam?["won"] is JsonValue w && w.TryGetValue<bool>(out var won) ? won : null,
                Int(myTeam?["rounds"], "won"), Int(myTeam?["rounds"], "lost"));
            lock (_gate)
            {
                if (_matches.ContainsKey(id)) continue;
                _matches[id] = match;
            }
            added++;
        }
        return added;
    }

    internal static double KillDeath(int kills, int deaths) => deaths == 0 ? kills : Math.Round((double)kills / deaths, 2);

    private object StatsSnapshot()
    {
        var missing = StatsMissing();
        List<ValorantMatch> all;
        lock (_gate) all = [.. _matches.Values];
        var today = DateTime.Now.Date; // local timezone
        var todays = all.Where(m => m.StartedAt > 0 && DateTimeOffset.FromUnixTimeMilliseconds(m.StartedAt).ToLocalTime().Date == today).ToList();
        var last = all.OrderByDescending(m => m.StartedAt).FirstOrDefault();
        lock (_gate)
            return new
            {
                provider = "HenrikDev API (api.henrikdev.xyz)",
                ready = missing.Count == 0,
                missing,
                error = _statsError,
                updatedAt = _statsAt == 0 ? (long?)null : _statsAt,
                account = _riotName,
                last = last is null ? null : new
                {
                    last.Id, last.StartedAt, last.Map, last.Mode, last.Agent, last.Kills, last.Deaths, last.Assists, last.Won,
                    last.RoundsWon, last.RoundsLost, kd = KillDeath(last.Kills, last.Deaths), deathless = last.Deaths == 0,
                },
                today = new
                {
                    date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    wins = todays.Count(m => m.Won == true),
                    losses = todays.Count(m => m.Won == false),
                    draws = todays.Count(m => m.Won is null),
                    matches = todays.Count,
                },
            };
    }

    // ------------------------------------------------------------------ map images
    public string MapsFolder()
    {
        var f = config.Get().Valorant.MapsFolder;
        return f.Length > 0 ? f : Path.Combine(AppPaths.ConfigDir, "valorant-maps");
    }

    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>The image for a map, by display name or codename ("Bind" or "Duality"); null when there is none.</summary>
    public string? MapImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) return null;
        var folder = MapsFolder();
        foreach (var n in new[] { name, MapName(name) }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var ext in ImageTypes)
                if (Path.Combine(folder, n + ext) is var p && File.Exists(p)) return p;
        return null;
    }

    // ------------------------------------------------------------------ the phone's snapshot
    public object Snapshot()
    {
        var cfg = config.Get().Valorant;
        Presence? pres;
        string? presErr, reason;
        bool running, held, down;
        long since;
        lock (_gate)
        {
            pres = _presence; presErr = _presenceError; running = _running; since = _runningSince;
            held = _talkLease is not null; down = _keyDown is not null; reason = _talkReason;
        }
        var mic = Mic();
        return new
        {
            game = new { running, since = running ? since : (long?)null, focused = running && Focused() },
            presence = running && pres is not null ? new
            {
                pres.Status, pres.Loop, pres.Map, pres.MapId, pres.Mode, pres.Ally, pres.Enemy, pres.Since,
                pres.PartySize, pres.MaxPartySize,
                mapImage = pres.Map is not null && MapImage(pres.Map) is not null,
            } : null,
            presenceError = running ? presErr : null,
            mic = new { mic.Available, mic.Muted, mic.Name, mic.Error },
            talk = new { key = cfg.TalkKey, held, down, reason },
            clip = new { key = cfg.ClipKey },
            ping = PingSnapshot(),
            stats = StatsSnapshot(),
        };
    }
}
