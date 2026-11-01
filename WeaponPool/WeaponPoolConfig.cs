// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace QuestTuner41.WeaponPool;

/// <summary>
/// 武器池模块配置。与 AddNewWeaponsToQuest41 的 config.json 逐字段同源
/// （工作区 <c>src/QuestTuner41/weaponPool.json</c> = 主环境 ANWQ config.json 逐字节同源），
/// 只是换了文件名与加载入口：<see cref="Load"/> 读 <c>&lt;modDir&gt;/weaponPool.json</c>。
///
/// 缺省语义与 ANWQ 的 <c>LoadConfig()</c> 一致：
/// <list type="bullet">
///   <item>autoScanMods / autoScanPlatformMatch / sendWeaponListOnQuestAccept / outputTaskWeapons 缺省 true；</item>
///   <item>outputAllWeapons / logMissingWeapons / consoleLogEnabled 缺省 false；</item>
///   <item>maxWeaponNames / acceptMessageMaxWeapons 缺省 0（=不截断）；</item>
///   <item>lang 缺省 "ch"；</item>
///   <item>解析任何失败都不抛异常，只写 <see cref="LoadError"/>（调用方决定怎么报）。</item>
/// </list>
/// </summary>
public sealed class WeaponPoolConfig
{
    /// <summary>逐项明细日志开关（★明细一律走 ILogger.Debug，不落盘；生产汇总行由宿主负责）。</summary>
    public bool ConsoleLogEnabled { get; set; }

    /// <summary>输出「config 缺失的任务武器」清单。</summary>
    public bool OutputTaskWeapons { get; set; } = true;

    /// <summary>输出全部武器清单。</summary>
    public bool OutputAllWeapons { get; set; }

    /// <summary>把缺失项作为可复制的 config 片段打出来（ANWQ: logMissingWeapons）。</summary>
    public bool LogMissingWeapons { get; set; }

    /// <summary>运行时自动扫描 user/mods 提取克隆映射（缺省开）。</summary>
    public bool AutoScanMods { get; set; } = true;

    /// <summary>刀/雷的自动映射也走平台匹配（缺省开；关闭后刀/雷不自动建组）。</summary>
    public bool AutoScanPlatformMatch { get; set; } = true;

    /// <summary>
    /// 同型号兜底（缺省开，v1.4.0）：克隆映射的键永远是「基底」，任务若直接点名一个「变体」
    /// （mod 自建枪、或自称克隆的枪），池子就永远匹配不到它、扩不动。开启后按物品模板的内部名
    /// <c>Item.Name</c>（items.json 的 <c>_name</c>，如 <c>weapon_colt_m4a1_556x45</c>）把同名武器
    /// 归为一组，组内**双向**互相放行。实测覆盖 Katarina Black 的 5 把自制枪
    /// （M4A1 / AXMC / AK103 / AKM / Rhino），它们是独立重定义（origin 不可信），
    /// 但内部名与原版枪一一同名。
    /// ★为什么不用 origin / _proto：实测这批枪的 origin 全空、_proto 指向错位
    ///   （AKM 指向 M4A1、AXMC 指向一个配件 id），拿它建映射会把任务放宽到完全无关的枪。
    /// </summary>
    public bool SameModelFallback { get; set; } = true;

    /// <summary>描述块里武器名上限（0=不截断）。</summary>
    public int MaxWeaponNames { get; set; }

    /// <summary>按物品 ID 排除（不参与武器池与自动映射）。</summary>
    public List<string> ExcludeWeapons { get; set; } = new();

    /// <summary>按来源模组目录名排除整包物品。</summary>
    public List<string> ExcludeMods { get; set; } = new();

    /// <summary>locale 语言键（物品名 <c>{id} Name</c>、任务名 <c>{id} name</c>）。</summary>
    public string Lang { get; set; } = "ch";

    /// <summary>接取任务后额外发一条「本任务可用武器」清单消息（缺省开）。</summary>
    public bool SendWeaponListOnQuestAccept { get; set; } = true;

    /// <summary>清单消息条数上限（0=全列）。</summary>
    public int AcceptMessageMaxWeapons { get; set; }

    /// <summary>手工映射：任务要求武器 ID → 要追加的可用武器 ID 列表（大小写不敏感）。</summary>
    public Dictionary<string, List<string>> NewWeapons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>加载/解析失败的原因（成功时为 null）。★宿主接线点：非 null 时应报一次告警，不要静默。</summary>
    public string? LoadError { get; private set; }

    // 解析选项已提到 ConfigJson.DocumentOptions：主配置、武器池、第三方配置共用一套语义，
    // 免得出现"主配置能读、武器池不能读"这种只在某一份文件上复现的怪毛病。

    /// <summary>
    /// 加载武器池配置，两个来源按优先级依次尝试：
    /// <list type="number">
    /// <item>主环境：<c>config.json</c> 的 <c>weaponPool</c> 分区 —— 正式版只让用户改这一个文件。</item>
    /// <item>回落：独立的 <c>weaponPool.json</c> —— 老用户升级后已调好的参数不丢。</item>
    /// </list>
    /// 两个来源都没有（或都解析失败）才记 <see cref="LoadError"/>；返回的对象一定是可用的（缺省值语义与 ANWQ 相同）。
    /// </summary>
    public static WeaponPoolConfig Load(string modDir)
    {
        var config = new WeaponPoolConfig();
        var primaryPath = Path.Combine(modDir, "config.json");
        var legacyPath = Path.Combine(modDir, "weaponPool.json");
        string? primaryError = null;

        // ① 主环境：config.json 的 weaponPool 分区。分区整个缺失不算错（老包本来就没这个分区），继续往下回落。
        string? text = null;
        if (File.Exists(primaryPath))
        {
            try
            {
                text = File.ReadAllText(primaryPath);
                using var doc = JsonDocument.Parse(text, ConfigJson.DocumentOptions);
                if (doc.RootElement.TryGetProperty("weaponPool", out var pool))
                {
                    ParseInto(pool, config);
                    return config;
                }
            }
            catch (Exception ex)
            {
                // 主配置坏了不能直接吞掉：记下来，但仍给回落文件一次机会。
                primaryError = $"主配置 weaponPool 分区解析失败：{ConfigJson.DescribeError(text, ex)}";
            }
        }

        // ② 回落：独立 weaponPool.json（老用户）。
        if (File.Exists(legacyPath))
        {
            try
            {
                text = File.ReadAllText(legacyPath);
                using var doc = JsonDocument.Parse(text, ConfigJson.DocumentOptions);
                ParseInto(doc.RootElement, config);
                return config;
            }
            catch (Exception ex)
            {
                config.LoadError = $"武器池配置解析失败（weaponPool.json）：{ConfigJson.DescribeError(text, ex)}";
                return config;
            }
        }

        config.LoadError = primaryError
            ?? $"未找到武器池配置：{primaryPath} 里没有 weaponPool 分区，且 {legacyPath} 也不存在";
        return config;
    }

    /// <summary>
    /// 逐键解析：单个键类型不对只回落缺省值，不让整份配置作废
    /// （ANWQ 是整块 try/catch，会丢掉已解析的部分）。
    /// </summary>
    private static void ParseInto(JsonElement root, WeaponPoolConfig config)
    {
        config.ConsoleLogEnabled = ReadBool(root, "consoleLogEnabled", config.ConsoleLogEnabled);
        config.OutputTaskWeapons = ReadBool(root, "outputTaskWeapons", config.OutputTaskWeapons);
        config.OutputAllWeapons = ReadBool(root, "outputAllWeapons", config.OutputAllWeapons);
        config.LogMissingWeapons = ReadBool(root, "logMissingWeapons", config.LogMissingWeapons);
        config.AutoScanMods = ReadBool(root, "autoScanMods", config.AutoScanMods);
        config.AutoScanPlatformMatch = ReadBool(root, "autoScanPlatformMatch", config.AutoScanPlatformMatch);
        config.SameModelFallback = ReadBool(root, "sameModelFallback", config.SameModelFallback);
        config.MaxWeaponNames = ReadInt(root, "maxWeaponNames", config.MaxWeaponNames);
        config.ExcludeWeapons = ReadStringList(root, "excludeWeapons");
        config.ExcludeMods = ReadStringList(root, "excludeMods");
        config.Lang = ReadString(root, "lang", config.Lang);
        config.SendWeaponListOnQuestAccept = ReadBool(root, "sendWeaponListOnQuestAccept", config.SendWeaponListOnQuestAccept);
        config.AcceptMessageMaxWeapons = ReadInt(root, "acceptMessageMaxWeapons", config.AcceptMessageMaxWeapons);
        config.NewWeapons = ReadWeapons(root);
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)
            ? v
            : fallback;

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        var s = e.GetString();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    private static List<string> ReadStringList(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }

    private static Dictionary<string, List<string>> ReadWeapons(JsonElement root)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("weapons", out var weapons) || weapons.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var prop in weapons.EnumerateObject())
        {
            var ids = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        ids.Add(item.GetString() ?? string.Empty);
                    }
                }
            }

            map[prop.Name] = ids;
        }

        return map;
    }
}
