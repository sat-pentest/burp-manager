using System.Text.Json.Nodes;

namespace BurpManager;

internal static class SelfTest
{
    public static int Run()
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) fail++; }

        var tmp = Path.Combine(Path.GetTempPath(), "burpmgr_selftest");
        Directory.CreateDirectory(tmp);

        var burp = new JsonObject
        {
            ["target"] = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["advanced_mode"] = true,
                    ["include"] = new JsonArray
                    {
                        new JsonObject { ["enabled"] = true, ["protocol"] = "https", ["host"] = "^kt\\.co\\.kr$", ["port"] = "", ["file"] = "", ["_x"] = "keep" },
                        new JsonObject { ["enabled"] = false, ["protocol"] = "any", ["host"] = "off\\.kt", ["port"] = "", ["file"] = "" },
                        new JsonObject { ["enabled"] = true, ["protocol"] = "any", ["host"] = "unanchored\\.kt", ["port"] = "", ["file"] = "" },
                    },
                    ["exclude"] = new JsonArray
                    {
                        new JsonObject { ["enabled"] = true, ["protocol"] = "any", ["host"] = "^www\\.kt\\.co\\.kr$", ["port"] = "", ["file"] = "" },
                    }
                }
            }
        };
        var src = Path.Combine(tmp, "KT.json");
        File.WriteAllText(src, burp.ToJsonString());

        var g = ScopeIO.ImportFile(src, "KT");
        Check("import scope", g != null);
        Check("include 3", g!.Include.Count == 3);
        Check("exclude 1", g.Exclude.Count == 1);
        Check("preserve enabled=false", g.Include[1].Enabled == false);
        Check("preserve unknown key", g.Include[0].Raw["_x"]?.GetValue<string>() == "keep");

        var doc = new MasterDocument();
        g.Meta.Roe = "1req/s";
        doc.Programs.Add(g);
        var mp = Path.Combine(tmp, "master.json");
        doc.Save(mp);
        var doc2 = MasterDocument.Load(mp);
        Check("master roundtrip count", doc2.Programs.Count == 1);
        Check("master roundtrip ROE", doc2.Programs[0].Meta.Roe == "1req/s");
        Check("master roundtrip unknown", doc2.Programs[0].Include[0].Raw["_x"]?.GetValue<string>() == "keep");

        var exp = Path.Combine(tmp, "KT.burpscope.json");
        ScopeIO.ExportProgram(doc2.Programs[0], exp);
        var scope = ScopeIO.FindScope(JsonNode.Parse(File.ReadAllText(exp)));
        Check("export scope", scope != null);
        Check("export include 3", (scope!["include"] as JsonArray)?.Count == 3);

        var lint = ScopeLinter.Run(doc2);
        Check("lint unanchored", lint.Any(f => f.Message.Contains("앵커")));

        doc2.Save(mp);
        Check("backup created", File.Exists(mp + ".bak"));

        // Proxy settings persistence round-trip.
        var pcfg = new ProxyConfig { IncludeInExport = true };
        pcfg.Listeners.Add(new ProxyListener(new JsonObject { ["running"] = true, ["listen_mode"] = "all_interfaces", ["listener_port"] = 8085 }));
        pcfg.MatchReplace.Add(new MatchReplaceRule(new JsonObject { ["enabled"] = true, ["category"] = "regex", ["rule_type"] = "request_header", ["string_match"] = "A", ["string_replace"] = "B" }));
        var pp = Path.Combine(tmp, "proxy.settings.json");
        ScopeIO.SaveProxySettings(pcfg, pp);
        var pcfg2 = new ProxyConfig();
        ScopeIO.LoadProxySettings(pcfg2, pp);
        Check("proxy settings file", File.Exists(pp));
        Check("proxy listener roundtrip", pcfg2.Listeners.Count == 1 && pcfg2.Listeners[0].Port == "8085");
        Check("proxy includeInExport roundtrip", pcfg2.IncludeInExport);
        Check("proxy match/replace roundtrip", pcfg2.MatchReplace.Count == 1 && pcfg2.MatchReplace[0].Match == "A");
        Check("proxy regex default true", pcfg2.MatchReplace[0].Regex);

        // Extension generator.
        var extCfg = new ProxyConfig { MatchReplaceScopeOnly = true };
        extCfg.MatchReplace.Add(new MatchReplaceRule(new JsonObject
        { ["enabled"] = true, ["category"] = "regex", ["rule_type"] = "response_body", ["string_match"] = "foo", ["string_replace"] = "bar" }));
        var extPath = Path.Combine(tmp, "Ext.java");
        ScopeIO.ExportMatchReplaceExtension(extCfg, extPath);
        var java = File.ReadAllText(extPath);
        Check("ext file", File.Exists(extPath));
        Check("ext implements BurpExtension", java.Contains("implements BurpExtension, HttpHandler"));
        Check("ext scope gate", java.Contains("SCOPE_ONLY = true") && java.Contains("isInScope"));
        Check("ext response replace", java.Contains("s.replaceAll(\"foo\", \"bar\")"));

        // Burp-exact export schema (the bug this fixes).
        var bcfg = new ProxyConfig();
        bcfg.Listeners.Add(new ProxyListener(new JsonObject { ["running"] = true, ["listen_mode"] = "all_interfaces", ["listener_port"] = 8080 }));
        bcfg.MatchReplace.Add(new MatchReplaceRule(new JsonObject { ["enabled"] = true, ["category"] = "literal", ["rule_type"] = "response_header", ["string_replace"] = "X: 1" }));
        var pj = bcfg.ToJson();
        var l0 = (JsonObject)((JsonArray)pj["request_listeners"]!)[0]!;
        var mr0 = (JsonObject)((JsonArray)pj["match_replace_rules"]!)[0]!;
        Check("burp listener certificate_mode", l0["certificate_mode"]?.GetValue<string>() == "per_host");
        Check("burp listener port int", l0["listener_port"]?.GetValue<int>() == 8080);
        Check("burp listener no bad keys", l0["support_invisible_proxying"] == null && l0["listen_specific_address"] == null);
        Check("burp mr rule_type", mr0["rule_type"]?.GetValue<string>() == "response_header");
        Check("burp mr category literal", mr0["category"]?.GetValue<string>() == "literal");
        Check("burp mr string_replace", mr0["string_replace"]?.GetValue<string>() == "X: 1");
        var re = new ProxyConfig();
        re.LoadFrom(pj);
        Check("reimport listener bind", re.Listeners.Count == 1 && re.Listeners[0].Bind == "all_interfaces");
        Check("reimport mr literal", re.MatchReplace.Count == 1 && re.MatchReplace[0].Replace == "X: 1" && !re.MatchReplace[0].Regex);

        log.Add(fail == 0 ? "\n=== ALL PASS ===" : $"\n=== {fail} FAILED ===");
        var report = string.Join(Environment.NewLine, log);
        File.WriteAllText(Path.Combine(tmp, "report.txt"), report);
        Console.WriteLine(report);
        return fail == 0 ? 0 : 1;
    }
}
