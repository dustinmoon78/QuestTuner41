// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuestTuner41.WeaponPool;

/// <summary>
/// 运行时自动扫描 user/mods 下的模组数据文件，提取「自定义物品 → 克隆基底」映射。
/// 覆盖五种字段写法：itemTplToClone（WTT 系）/ cloneId（MGMod）/ origin（Katarina）/
/// _proto（4.1 模板原型，EternalCycle 等）/ _targetid（超级物品包）。
/// 注意：export*.json 是部分模组的运行时全表导出（含原版与其它 mod 物品），
/// 只作补缺使用（真实定义优先，先扫所有常规文件、最后填充 export 未覆盖的 ID）。
/// v2.2.0：增加物品归属 mod 记录（IdToMod），供 excludeMods 按整包排除。
///
/// 并入 QuestTuner41 时唯一的行为差异：跳过目录由硬编码的 "AddNewWeaponsToQuest41"
/// 改为调用方传入的 <c>selfModDirName</c>（单 DLL 合并后自己的目录名变了）。
/// </summary>
public static class AutoScan
{
    public sealed record ScanResult(
        Dictionary<string, string> CloneMap,
        HashSet<string> ExcludedIds,
        int SkippedFiles,
        Dictionary<string, string> IdToMod);

    private static readonly System.Text.RegularExpressions.Regex Hex24 =
        new("^[0-9a-f]{24}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <param name="modsDir">user/mods 绝对路径。</param>
    /// <param name="selfModDirName">本 mod 自己的目录名（跳过自身数据文件，避免把自己写的导出件当模组定义）。</param>
    public static ScanResult Scan(string modsDir, string selfModDirName)
    {
        var clone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idToMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dumpPending = new List<(string Id, string Clone)>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        if (!Directory.Exists(modsDir))
        {
            return new ScanResult(clone, excluded, skipped, idToMod);
        }

        foreach (var modDir in Directory.EnumerateDirectories(modsDir).OrderBy(Path.GetFileName))
        {
            var mod = Path.GetFileName(modDir);
            if (mod is "00MLOM" || string.Equals(mod, selfModDirName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(modDir, "*.json", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
            {
                var low = f.Replace('\\', '/').ToLowerInvariant();
                if (low.Contains("/backups/") || low.Contains("/locale"))
                {
                    continue;
                }

                try
                {
                    var root = ParseJson(f);
                    if (root == null)
                    {
                        skipped++;
                        continue;
                    }

                    if (Path.GetFileName(f).StartsWith("export", StringComparison.OrdinalIgnoreCase))
                    {
                        Walk(root, mod, clone, idToMod, dumpPending);
                    }
                    else
                    {
                        Walk(root, mod, clone, idToMod, null);
                    }
                }
                catch
                {
                    // 重复键/畸形结构等单文件异常：只跳过该文件，绝不让一个坏文件拖垮全扫描
                    skipped++;
                }
            }
        }

        // export 全表导出只补缺：真实定义（常规文件）已登记的 ID 不覆盖
        foreach (var (id, c) in dumpPending)
        {
            if (clone.TryAdd(id, c))
            {
                idToMod.TryAdd(id, "export-dump");
            }
        }

        // DWV 运行时生成 ID：99_Ids 文件的「值」才是生成 ID（键是「名字:ID」）
        var dwvDir = Path.Combine(modsDir, "DefinitiveWeaponVariants", "db", "99_Ids");
        if (Directory.Exists(dwvDir))
        {
            foreach (var f in Directory.GetFiles(dwvDir, "*.json"))
            {
                if (ParseJson(f) is JsonObject o)
                {
                    foreach (var kv in o)
                    {
                        if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && Is24(s))
                        {
                            excluded.Add(s);
                        }
                    }
                }
            }
        }

        return new ScanResult(clone, excluded, skipped, idToMod);
    }

    private static void Walk(JsonNode? node, string mod, Dictionary<string, string> clone,
        Dictionary<string, string> idToMod, List<(string, string)>? dumpPending)
    {
        switch (node)
        {
            case JsonObject o:
            {
                // WTT 系定义：{ newId: { itemTplToClone: ... } }
                foreach (var kv in o)
                {
                    if (kv.Value is not JsonObject def)
                    {
                        continue;
                    }

                    if (def["itemTplToClone"] is JsonValue tv && tv.TryGetValue<string>(out var tcs) && Is24(tcs))
                    {
                        Reg(First24(def["_id"], def["id"], kv.Key), tcs, mod, clone, idToMod, dumpPending);
                    }
                }

                // MGMod 单件：{ items: { newId, cloneId } }
                if (o["newId"] is JsonValue ni && ni.TryGetValue<string>(out var nid) &&
                    o["cloneId"] is JsonValue ci && ci.TryGetValue<string>(out var ccs))
                {
                    Reg(nid, ccs, mod, clone, idToMod, dumpPending);
                }

                // Katarina：{ item: { _id, _proto }, origin }
                if (o["item"] is JsonObject it && it["_id"] is JsonValue ii && ii.TryGetValue<string>(out var kid))
                {
                    Reg(kid, Str24(o["origin"]) ?? Str24(it["_proto"]), mod, clone, idToMod, dumpPending);
                }

                // 整模板 / 超级物品包：{ _id, _props | _targetid, ... }
                if (o["_id"] is JsonValue ti && ti.TryGetValue<string>(out var tid) &&
                    (o["_props"] != null || o["_targetid"] != null))
                {
                    Reg(tid, Str24(o["_targetid"]) ?? Str24(o["origin"]) ?? Str24(o["_proto"]), mod, clone, idToMod, dumpPending);
                }

                foreach (var kv in o)
                {
                    Walk(kv.Value, mod, clone, idToMod, dumpPending);
                }

                break;
            }
            case JsonArray arr:
                foreach (var x in arr)
                {
                    Walk(x, mod, clone, idToMod, dumpPending);
                }

                break;
        }
    }

    private static void Reg(string? id, string? cloneBase, string mod, Dictionary<string, string> cloneMap,
        Dictionary<string, string> idToMod, List<(string, string)>? dumpPending)
    {
        if (!Is24(id) || !Is24(cloneBase))
        {
            return;
        }

        if (dumpPending != null)
        {
            dumpPending.Add((id!, cloneBase!));
        }
        else
        {
            if (cloneMap.TryAdd(id!, cloneBase!))
            {
                idToMod[id!] = mod;
            }
        }
    }

    private static JsonNode? ParseJson(string path)
    {
        try
        {
            // 选项统一走 ConfigJson（注释 + 尾逗号容忍），别在本文件再留第四份字面量，免得日后漂移。
            return JsonNode.Parse(File.ReadAllBytes(path), null, ConfigJson.DocumentOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? Str24(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && Is24(s) ? s : null;

    private static string? First24(JsonNode? a, JsonNode? b, string? c) => Str24(a) ?? Str24(b) ?? (Is24(c) ? c : null);

    private static bool Is24(string? s) => s != null && Hex24.IsMatch(s);
}
