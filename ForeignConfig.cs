// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace QuestTuner41;

/// <summary>
/// 读取 sgtlaggyQuestTweaks 的配置（纯文件读取，不引用它的 DLL）。
/// 目的只有一个：它排除了哪些任务，我们也跳过，避免"用户在那边排除了、这边照样改"。
/// 全程 try/catch，读不到就当没装，绝不影响本 mod 的加载；告警交给调用方输出。
/// </summary>
public sealed class ForeignConfig
{
    private const string SgtlaggyPath = "user/mods/sgtlaggyQuestTweaks/config.json";

    /// <summary>已知也会动任务的 mod：只探测目录是否存在并在日志里提示"可能叠加"，不做自动适配。</summary>
    private static readonly (string Dir, string Note)[] KnownQuestMods =
    {
        ("[SVM] Server Value Modifier", "可能改任务/物品数值"),
        ("acidphantasm-reffriendlyquests", "Fence 相关任务"),
        ("StartCollectorEarly", "收藏家前置"),
        ("LacyPvETweaks", "PvE 任务与数值调整"),
    };

    public HashSet<string> ExemptQuests { get; } = new(StringComparer.Ordinal);
    public List<string> OnlyQuests { get; } = new();
    public List<string> DetectedOthers { get; } = new();
    public bool? RemoveTarget { get; private set; }
    public bool? AffectsRepeatables { get; private set; }
    public bool Loaded { get; private set; }
    public List<string> Warnings { get; } = new();

    public static ForeignConfig Load()
    {
        var f = new ForeignConfig();

        foreach (var (dir, note) in KnownQuestMods)
        {
            try
            {
                if (Directory.Exists(Path.Combine("user", "mods", dir)))
                {
                    f.DetectedOthers.Add($"{dir}（{note}）");
                }
            }
            catch
            {
                // 探测失败忽略
            }
        }

        try
        {
            if (!File.Exists(SgtlaggyPath))
            {
                return f;
            }

            // 统一用宽松选项读：它那边若是 JSONC（带注释/尾逗号），也要能把排除清单读出来，
            // 否则会去改用户已经排除掉的任务。消息保持一句话，别把第三方文件原文抄进我们日志。
            using var doc = JsonDocument.Parse(File.ReadAllText(SgtlaggyPath), ConfigJson.DocumentOptions);
            var root = doc.RootElement;

            if (root.TryGetProperty("exemptQuests", out var ex) && ex.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ex.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } id)
                    {
                        f.ExemptQuests.Add(id);
                    }
                }
            }

            if (root.TryGetProperty("onlyQuests", out var only) && only.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in only.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } id)
                    {
                        f.OnlyQuests.Add(id);
                    }
                }
            }

            if (root.TryGetProperty("GlobalConditions", out var gc) && gc.ValueKind == JsonValueKind.Object)
            {
                if (gc.TryGetProperty("removeTarget", out var rt) && rt.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    f.RemoveTarget = rt.GetBoolean();
                }

                if (gc.TryGetProperty("affectRepeatables", out var ar) && ar.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    f.AffectsRepeatables = ar.GetBoolean();
                }
            }

            f.Loaded = true;
        }
        catch (Exception ex)
        {
            f.Warnings.Add($"读取 sgtlaggyQuestTweaks 配置失败（忽略其排除清单）：{ex.Message}");
        }

        return f;
    }
}
