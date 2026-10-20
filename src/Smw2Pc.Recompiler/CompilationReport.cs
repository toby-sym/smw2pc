using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Smw2Pc.Core.Cpu;

namespace Smw2Pc.Recompiler;

public static class CompilationReport
{
    internal static object[] AddressCoverage(IReadOnlyList<DecodedCpuBlock> blocks)
    {
        var addresses = new HashSet<uint>();
        foreach (var block in blocks)
        {
            var address = block.StartAddress;
            foreach (var instruction in block.Instructions)
            {
                var length = (instruction.NextProgramCounter - (int)(address & 0xFFFF)) & 0xFFFF;
                for (var i = 0; i < length; i++)
                    addresses.Add((address & 0xFF0000) | (ushort)(address + i));
                address = (address & 0xFF0000) | instruction.NextProgramCounter;
            }
        }
        return addresses.GroupBy(a => a >> 16).OrderBy(g => g.Key).Select(g => (object)new
        {
            Bank = $"{g.Key:X2}",
            DiscoveredBytes = g.Count(),
            FirstAddress = $"{g.Min():X6}",
            LastAddress = $"{g.Max():X6}",
        }).ToArray();
    }

    public static void SetBuildStatus(string directory, string status, string? error = null)
    {
        var path = Path.Combine(directory, "manifest.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["BuildStatus"] = status;
        node["BuildError"] = error;
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        WriteHtml(directory);
    }

    public static void WriteHtml(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var data = document.RootElement;
        string Get(string key) => data.TryGetProperty(key, out var value) ? value.ToString() : "";
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>SMW2PC compilation report</title><style>
            body{font:16px/1.6 system-ui;margin:40px auto;padding:0 24px;max-width:950px;background:#101318;color:#edf1f8}
            h1,h2{line-height:1.2}code{overflow-wrap:anywhere;color:#99ceff}table{border-collapse:collapse;width:100%}
            td,th{text-align:left;padding:8px;border-bottom:1px solid #364253}p{color:#c3cedd}li{overflow-wrap:anywhere}
            </style><h1>Compilation report</h1>
            """);
        html.Append($"<h2>{E(Get("Title"))}</h2><p>ROM SHA-256: <code>{E(Get("Sha256"))}</code></p>");
        html.Append($"<p>Build status: <strong>{E(Get("BuildStatus"))}</strong></p>");
        if (!string.IsNullOrEmpty(Get("BuildError"))) html.Append($"<p>{E(Get("BuildError"))}</p>");
        html.Append($"<p>{E(Get("MainCpuBlocks"))} main CPU blocks; {E(Get("AudioEntries"))} audio entries; {E(Get("WritableCodeVariants"))} writable code variants.</p>");
        html.Append("<p>These counts describe discovered code. They do not measure whole-game compatibility. ROM data also contains graphics, music and tables, so a percentage of ROM bytes would be misleading.</p>");
        html.Append($"<p>{E(Get("CoverageNote"))}</p><h2>Discovered instruction bytes by bank</h2><table><tr><th>Bank</th><th>Bytes</th><th>First address</th><th>Last address</th></tr>");
        if (data.TryGetProperty("AddressCoverage", out var banks))
            foreach (var bank in banks.EnumerateArray())
                html.Append($"<tr><td>{E(bank.GetProperty("Bank").ToString())}</td><td>{bank.GetProperty("DiscoveredBytes")}</td><td><code>{bank.GetProperty("FirstAddress")}</code></td><td><code>{bank.GetProperty("LastAddress")}</code></td></tr>");
        html.Append("</table><h2>Unresolved static transfers</h2><ul>");
        if (data.TryGetProperty("UnresolvedStaticTransfers", out var warnings))
            foreach (var warning in warnings.EnumerateArray()) html.Append($"<li>{E(warning.ToString())}</li>");
        html.Append("</ul><p>An unresolved transfer may be covered by profiling. An uncompiled path encountered during play stops execution and produces a compatibility report in the save folder.</p></html>");
        File.WriteAllText(Path.Combine(directory, "compatibility.html"), html.ToString());
    }
}
