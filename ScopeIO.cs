using System.Text.Json;
using System.Text.Json.Nodes;

namespace BurpManager;

/// <summary>Import scope from raw Burp JSON and export a Burp-loadable file.</summary>
public static class ScopeIO
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject? FindScope(JsonNode? root)
    {
        if (root is not JsonObject o) return null;
        if (o["target"]?["scope"] is JsonObject s1) return s1;
        if (o["scope"] is JsonObject s2) return s2;
        if (o["include"] is JsonArray || o["exclude"] is JsonArray) return o;
        return null;
    }

    public static ProgramGroup? ImportFile(string path, string programName)
    {
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text);
        var scope = FindScope(root);
        if (scope is null) return null;

        var g = new ProgramGroup(programName)
        {
            AdvancedMode = scope["advanced_mode"]?.GetValue<bool>() ?? true
        };
        g.Meta.SourceFile = path;

        CopyRules(scope["include"], g.Include);
        CopyRules(scope["exclude"], g.Exclude);
        return g;
    }

    private static void CopyRules(JsonNode? arr, List<ScopeRule> target)
    {
        if (arr is not JsonArray ja) return;
        foreach (var item in ja)
            if (item is JsonObject o)
                target.Add(new ScopeRule(o.DeepClone().AsObject()));
    }

    public static void ExportProgram(ProgramGroup g, string path, ProxyConfig? proxy = null)
    {
        var include = new JsonArray();
        foreach (var r in g.Include) include.Add(r.Raw.DeepClone());
        var exclude = new JsonArray();
        foreach (var r in g.Exclude) exclude.Add(r.Raw.DeepClone());

        var root = new JsonObject
        {
            ["target"] = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["advanced_mode"] = g.AdvancedMode,
                    ["exclude"] = exclude,
                    ["include"] = include,
                }
            }
        };
        if (proxy is { HasContent: true }) root["proxy"] = proxy.ToJson();
        File.WriteAllText(path, root.ToJsonString(WriteOpts));
    }

    /// <summary>Write a Burp config file containing only the common proxy block.</summary>
    public static void ExportProxy(ProxyConfig proxy, string path)
    {
        var root = new JsonObject { ["proxy"] = proxy.ToJson() };
        File.WriteAllText(path, root.ToJsonString(WriteOpts));
    }

    /// <summary>Persist the common proxy config to the app-level settings file.</summary>
    public static void SaveProxySettings(ProxyConfig p, string path)
    {
        var root = new JsonObject
        {
            ["proxy"] = p.ToJson(),
            ["includeInExport"] = p.IncludeInExport,
            ["matchReplaceScopeOnly"] = p.MatchReplaceScopeOnly,
        };
        File.WriteAllText(path, root.ToJsonString(WriteOpts));
    }

    /// <summary>Load the app-level proxy settings into a config (no-op if the file is absent).</summary>
    public static void LoadProxySettings(ProxyConfig p, string path)
    {
        if (!File.Exists(path)) return;
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;
        if (root["proxy"] is JsonObject px) p.LoadFrom(px);
        p.IncludeInExport = root["includeInExport"]?.GetValue<bool>() ?? p.IncludeInExport;
        p.MatchReplaceScopeOnly = root["matchReplaceScopeOnly"]?.GetValue<bool>() ?? p.MatchReplaceScopeOnly;
    }

    /// <summary>
    /// Generate a Montoya (Burp) extension .java that applies the match/replace rules,
    /// gated on in-scope traffic when MatchReplaceScopeOnly is set.
    /// </summary>
    public static void ExportMatchReplaceExtension(ProxyConfig p, string path)
    {
        static string J(string s) => (s ?? "")
            .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        static string Repl(MatchReplaceRule r) =>
            (r.Regex ? "s = s.replaceAll(" : "s = s.replace(") + $"\"{J(r.Match)}\", \"{J(r.Replace)}\");";

        var rules = p.MatchReplace.Where(r => r.Enabled).ToList();
        var reqRules = rules.Where(r => r.RuleType.StartsWith("request")).ToList();
        var respRules = rules.Where(r => r.RuleType.StartsWith("response")).ToList();
        bool scopeOnly = p.MatchReplaceScopeOnly;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("package burpmanager;");
        sb.AppendLine();
        sb.AppendLine("import burp.api.montoya.BurpExtension;");
        sb.AppendLine("import burp.api.montoya.MontoyaApi;");
        sb.AppendLine("import burp.api.montoya.core.ByteArray;");
        sb.AppendLine("import burp.api.montoya.http.handler.*;");
        sb.AppendLine("import burp.api.montoya.http.message.requests.HttpRequest;");
        sb.AppendLine("import burp.api.montoya.http.message.responses.HttpResponse;");
        sb.AppendLine();
        sb.AppendLine("// Auto-generated by BurpManager.");
        sb.AppendLine("// Applies match/replace over the full HTTP message text" +
                      (scopeOnly ? ", limited to IN-SCOPE traffic." : "."));
        sb.AppendLine("// Build against montoya-api and load the compiled jar in Burp (Extensions > Add).");
        sb.AppendLine("public class BurpManagerMatchReplace implements BurpExtension, HttpHandler {");
        sb.AppendLine("    private MontoyaApi api;");
        sb.AppendLine($"    private static final boolean SCOPE_ONLY = {(scopeOnly ? "true" : "false")};");
        sb.AppendLine();
        sb.AppendLine("    @Override public void initialize(MontoyaApi api) {");
        sb.AppendLine("        this.api = api;");
        sb.AppendLine("        api.extension().setName(\"BurpManager Match/Replace\");");
        sb.AppendLine("        api.http().registerHttpHandler(this);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // request handler
        sb.AppendLine("    @Override public RequestToBeSentAction handleHttpRequestToBeSent(HttpRequestToBeSent req) {");
        if (reqRules.Count == 0)
            sb.AppendLine("        return RequestToBeSentAction.continueWith(req);");
        else
        {
            sb.AppendLine("        if (SCOPE_ONLY && !api.scope().isInScope(req.url())) return RequestToBeSentAction.continueWith(req);");
            sb.AppendLine("        String s = req.toString();");
            foreach (var r in reqRules) sb.AppendLine($"        {Repl(r)}   // {r.RuleType}");
            sb.AppendLine("        return RequestToBeSentAction.continueWith(HttpRequest.httpRequest(req.httpService(), ByteArray.byteArray(s)));");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // response handler
        sb.AppendLine("    @Override public ResponseReceivedAction handleHttpResponseReceived(HttpResponseReceived resp) {");
        if (respRules.Count == 0)
            sb.AppendLine("        return ResponseReceivedAction.continueWith(resp);");
        else
        {
            sb.AppendLine("        if (SCOPE_ONLY && !api.scope().isInScope(resp.initiatingRequest().url())) return ResponseReceivedAction.continueWith(resp);");
            sb.AppendLine("        String s = resp.toString();");
            foreach (var r in respRules) sb.AppendLine($"        {Repl(r)}   // {r.RuleType}");
            sb.AppendLine("        return ResponseReceivedAction.continueWith(HttpResponse.httpResponse(ByteArray.byteArray(s)));");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Load proxy settings from a Burp JSON into an existing config. Returns false if none found.</summary>
    public static bool ImportProxy(string path, ProxyConfig proxy)
    {
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
        var px = root["proxy"] as JsonObject;
        if (px is null && root["request_listeners"] is JsonArray) px = root;
        if (px is null) return false;
        proxy.LoadFrom(px);
        return true;
    }
}

public sealed record LintFinding(string Severity, string Program, string Message);

public static class ScopeLinter
{
    public static List<LintFinding> Run(MasterDocument doc)
    {
        var findings = new List<LintFinding>();
        var hostOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in doc.Programs)
        {
            if (g.Include.Count(r => r.Enabled) == 0)
                findings.Add(new("HIGH", g.Name, "활성화된 include 규칙이 없습니다 — Burp에서 in-scope 가 비어 있습니다."));
            if (g.Exclude.Count == 0)
                findings.Add(new("INFO", g.Name, "exclude 규칙이 없습니다 — ROE 제외 항목이 반영됐는지 확인하세요."));

            foreach (var r in g.Include.Where(r => r.Enabled))
            {
                var host = r.Host?.Trim() ?? "";
                if (string.IsNullOrEmpty(host))
                {
                    findings.Add(new("HIGH", g.Name, "host 가 비어 있는 활성 include 규칙 — 전 도메인 대상이 될 수 있습니다."));
                    continue;
                }
                if (host is ".*" or "^.*$" or ".*.*")
                    findings.Add(new("HIGH", g.Name, $"과광범위 host 패턴: '{host}' — out-of-scope 히트 위험."));
                if (g.AdvancedMode && (!host.StartsWith("^") || !host.EndsWith("$")))
                    findings.Add(new("MEDIUM", g.Name, $"앵커(^…$) 없는 host: '{host}' — 부분 매칭 위험."));

                if (!hostOwners.TryGetValue(host, out var owners)) hostOwners[host] = owners = new();
                if (!owners.Contains(g.Name)) owners.Add(g.Name);
            }
        }

        foreach (var kv in hostOwners.Where(kv => kv.Value.Count > 1))
            findings.Add(new("MEDIUM", string.Join(", ", kv.Value),
                $"동일 host 가 여러 프로그램에 존재: '{kv.Key}'."));

        return findings;
    }
}
