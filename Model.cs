using System.Text.Json;
using System.Text.Json.Nodes;

namespace BurpManager;

/// <summary>
/// A single scope rule. Keeps the raw JsonObject so any Burp keys we do not
/// explicitly understand survive an import -> edit -> export round-trip.
/// </summary>
public sealed class ScopeRule
{
    public JsonObject Raw { get; }

    public ScopeRule(JsonObject raw) => Raw = raw;

    private string GetStr(string key) => Raw[key]?.GetValue<string>() ?? "";
    private void SetStr(string key, string val) => Raw[key] = val;

    public bool Enabled
    {
        get => Raw["enabled"]?.GetValueKind() != JsonValueKind.False
               && (Raw["enabled"]?.GetValue<bool>() ?? true);
        set => Raw["enabled"] = value;
    }

    public string Protocol { get => GetStr("protocol"); set => SetStr("protocol", value); }
    public string Host     { get => GetStr("host");     set => SetStr("host", value); }
    public string Port     { get => GetStr("port");     set => SetStr("port", value); }
    public string File     { get => GetStr("file");     set => SetStr("file", value); }

    public override string ToString()
    {
        var proto = string.IsNullOrEmpty(Protocol) ? "any" : Protocol;
        var host  = string.IsNullOrEmpty(Host) ? "(no host)" : Host;
        return $"{proto}  {host}";
    }
}

public sealed class ProgramMeta
{
    public string Roe { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string RateLimit { get; set; } = "";
}

/// <summary>A named program group (KT, GS, ...) holding its own scope block.</summary>
public sealed class ProgramGroup
{
    public string Name { get; set; }
    public ProgramMeta Meta { get; } = new();
    public bool AdvancedMode { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public List<ScopeRule> Include { get; } = new();
    public List<ScopeRule> Exclude { get; } = new();

    public ProgramGroup(string name) => Name = name;
}

// ===================================================================== proxy

/// <summary>Base wrapper preserving unknown Burp keys around a JsonObject.</summary>
public abstract class JsonRow
{
    public JsonObject Raw { get; }
    protected JsonRow(JsonObject raw) => Raw = raw;
    protected string S(string k) => Raw[k]?.GetValue<string>() ?? "";
    protected void S(string k, string v) => Raw[k] = v;
    protected bool GB(string k, bool def) =>
        Raw[k] is null ? def : Raw[k]!.GetValueKind() != JsonValueKind.False;
    protected void SB(string k, bool v) => Raw[k] = v;
}

public sealed class ProxyListener : JsonRow
{
    public ProxyListener(JsonObject r) : base(r) { }
    public bool Running { get => GB("running", true); set => SB("running", value); }
    public string Bind { get => S("listen_mode"); set => S("listen_mode", value); }          // loopback_only|all_interfaces|specific_address
    public string Port
    {
        get => Raw["listener_port"]?.ToString() ?? "";
        set => Raw["listener_port"] = int.TryParse(value, out var n) ? n : 8080;
    }

    /// <summary>Emit the exact key set Burp expects for a proxy listener.</summary>
    public JsonObject ToBurpJson() => new()
    {
        ["certificate_mode"] = "per_host",
        ["custom_tls_protocols"] = new JsonArray("TLSv1", "TLSv1.1", "TLSv1.2", "TLSv1.3"),
        ["enable_http2"] = true,
        ["listen_mode"] = string.IsNullOrEmpty(Bind) ? "loopback_only" : Bind,
        ["listener_port"] = int.TryParse(Port, out var n) ? n : 8080,
        ["running"] = Running,
        ["use_custom_tls_protocols"] = false,
    };
}

public sealed class InterceptRule : JsonRow
{
    public InterceptRule(JsonObject r) : base(r) { }
    public bool Enabled { get => GB("enabled", true); set => SB("enabled", value); }
    public string Operator { get => S("boolean_operator"); set => S("boolean_operator", value); }        // and|or
    public string MatchType { get => S("match_type"); set => S("match_type", value); }
    public string Relationship { get => S("match_relationship"); set => S("match_relationship", value); }
    public string Condition { get => S("match_condition"); set => S("match_condition", value); }

    public JsonObject ToBurpJson()
    {
        var o = new JsonObject
        {
            ["boolean_operator"] = string.IsNullOrEmpty(Operator) ? "or" : Operator,
            ["enabled"] = Enabled,
            ["match_relationship"] = string.IsNullOrEmpty(Relationship) ? "matches" : Relationship,
            ["match_type"] = string.IsNullOrEmpty(MatchType) ? "url" : MatchType,
        };
        if (!string.IsNullOrEmpty(Condition)) o["match_condition"] = Condition;
        return o;
    }
}

public sealed class MatchReplaceRule : JsonRow
{
    public MatchReplaceRule(JsonObject r) : base(r) { }
    public bool Enabled { get => GB("enabled", true); set => SB("enabled", value); }
    public bool Regex { get => S("category") != "literal"; set => S("category", value ? "regex" : "literal"); }
    public string RuleType { get => S("rule_type"); set => S("rule_type", value); }          // request_header, response_body, ...
    public string Match { get => S("string_match"); set => S("string_match", value); }
    public string Replace { get => S("string_replace"); set => S("string_replace", value); }
    public string Comment { get => S("comment"); set => S("comment", value); }

    public JsonObject ToBurpJson()
    {
        var o = new JsonObject
        {
            ["category"] = Regex ? "regex" : "literal",
            ["comment"] = Comment,
            ["enabled"] = Enabled,
            ["rule_type"] = string.IsNullOrEmpty(RuleType) ? "request_header" : RuleType,
        };
        if (!string.IsNullOrEmpty(Match)) o["string_match"] = Match;
        if (!string.IsNullOrEmpty(Replace)) o["string_replace"] = Replace;
        return o;
    }
}

/// <summary>Common (shared) Burp proxy configuration.</summary>
public sealed class ProxyConfig
{
    public List<ProxyListener> Listeners { get; } = new();
    public bool InterceptRequests { get; set; } = true;
    public List<InterceptRule> RequestRules { get; } = new();
    public bool InterceptResponses { get; set; }
    public List<InterceptRule> ResponseRules { get; } = new();
    public List<MatchReplaceRule> MatchReplace { get; } = new();
    public bool IncludeInExport { get; set; }
    public bool MatchReplaceScopeOnly { get; set; }   // apply all match/replace only to in-scope traffic (enforced by exported extension)

    public bool HasContent =>
        Listeners.Count > 0 || RequestRules.Count > 0 || ResponseRules.Count > 0 || MatchReplace.Count > 0;

    public JsonObject ToJson()
    {
        var listeners = new JsonArray();
        foreach (var l in Listeners) listeners.Add(l.ToBurpJson());
        var mr = new JsonArray();
        foreach (var r in MatchReplace) mr.Add(r.ToBurpJson());
        JsonArray Rules(List<InterceptRule> rs)
        {
            var a = new JsonArray();
            foreach (var r in rs) a.Add(r.ToBurpJson());
            return a;
        }
        return new JsonObject
        {
            ["request_listeners"] = listeners,
            ["intercept_client_requests"] = new JsonObject { ["do_intercept"] = InterceptRequests, ["rules"] = Rules(RequestRules) },
            ["intercept_server_responses"] = new JsonObject { ["do_intercept"] = InterceptResponses, ["rules"] = Rules(ResponseRules) },
            ["match_replace_rules"] = mr,
        };
    }

    public void LoadFrom(JsonObject px)
    {
        Listeners.Clear(); RequestRules.Clear(); ResponseRules.Clear(); MatchReplace.Clear();

        if (px["request_listeners"] is JsonArray la)
            foreach (var i in la) if (i is JsonObject o) Listeners.Add(new ProxyListener(o.DeepClone().AsObject()));

        if (px["intercept_client_requests"] is JsonObject cr)
        {
            InterceptRequests = cr["do_intercept"]?.GetValue<bool>() ?? true;
            if (cr["rules"] is JsonArray ra)
                foreach (var i in ra) if (i is JsonObject o) RequestRules.Add(new InterceptRule(o.DeepClone().AsObject()));
        }
        if (px["intercept_server_responses"] is JsonObject sr)
        {
            InterceptResponses = sr["do_intercept"]?.GetValue<bool>() ?? false;
            if (sr["rules"] is JsonArray ra)
                foreach (var i in ra) if (i is JsonObject o) ResponseRules.Add(new InterceptRule(o.DeepClone().AsObject()));
        }
        if (px["match_replace_rules"] is JsonArray ma)
            foreach (var i in ma) if (i is JsonObject o) MatchReplace.Add(new MatchReplaceRule(o.DeepClone().AsObject()));
    }
}

/// <summary>The unified master document (single source of truth).</summary>
public sealed class MasterDocument
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public List<ProgramGroup> Programs { get; } = new();
    public ProxyConfig Proxy { get; } = new();

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static MasterDocument Load(string path)
    {
        var text = System.IO.File.ReadAllText(path);
        var root = JsonNode.Parse(text)?.AsObject()
                   ?? throw new InvalidDataException("Master JSON root is not an object.");

        var doc = new MasterDocument
        {
            SchemaVersion = root["schemaVersion"]?.GetValue<int>() ?? CurrentSchema
        };

        if (root["programs"] is JsonObject progs)
        {
            foreach (var kv in progs)
            {
                if (kv.Value is not JsonObject po) continue;
                var g = new ProgramGroup(kv.Key)
                {
                    AdvancedMode = po["advanced_mode"]?.GetValue<bool>() ?? true,
                    Enabled = po["enabled"]?.GetValue<bool>() ?? true,
                };
                if (po["meta"] is JsonObject m)
                {
                    g.Meta.Roe = m["roe"]?.GetValue<string>() ?? "";
                    g.Meta.SourceFile = m["sourceFile"]?.GetValue<string>() ?? "";
                    g.Meta.RateLimit = m["rateLimit"]?.GetValue<string>() ?? "";
                }
                LoadRules(po["include"], g.Include);
                LoadRules(po["exclude"], g.Exclude);
                doc.Programs.Add(g);
            }
        }

        if (root["proxy"] is JsonObject px) doc.Proxy.LoadFrom(px);
        doc.Proxy.IncludeInExport = root["proxyIncludeInExport"]?.GetValue<bool>() ?? false;
        doc.Proxy.MatchReplaceScopeOnly = root["proxyMatchReplaceScopeOnly"]?.GetValue<bool>() ?? false;
        return doc;
    }

    private static void LoadRules(JsonNode? arr, List<ScopeRule> target)
    {
        if (arr is not JsonArray ja) return;
        foreach (var item in ja)
            if (item is JsonObject o)
                target.Add(new ScopeRule(o.DeepClone().AsObject()));
    }

    public void Save(string path)
    {
        if (System.IO.File.Exists(path))
            System.IO.File.Copy(path, path + ".bak", overwrite: true);

        var progs = new JsonObject();
        foreach (var g in Programs)
        {
            var include = new JsonArray();
            foreach (var r in g.Include) include.Add(r.Raw.DeepClone());
            var exclude = new JsonArray();
            foreach (var r in g.Exclude) exclude.Add(r.Raw.DeepClone());

            progs[g.Name] = new JsonObject
            {
                ["meta"] = new JsonObject
                {
                    ["roe"] = g.Meta.Roe,
                    ["sourceFile"] = g.Meta.SourceFile,
                    ["rateLimit"] = g.Meta.RateLimit,
                },
                ["advanced_mode"] = g.AdvancedMode,
                ["enabled"] = g.Enabled,
                ["include"] = include,
                ["exclude"] = exclude,
            };
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["proxy"] = Proxy.ToJson(),
            ["proxyIncludeInExport"] = Proxy.IncludeInExport,
            ["proxyMatchReplaceScopeOnly"] = Proxy.MatchReplaceScopeOnly,
            ["programs"] = progs,
        };

        System.IO.File.WriteAllText(path, root.ToJsonString(WriteOpts));
    }
}
