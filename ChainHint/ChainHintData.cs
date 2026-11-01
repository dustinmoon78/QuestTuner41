// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Threading;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace QuestTuner41.ChainHint;

/// <summary>
/// 任务链快照：把任务模板里「谁能接 / 接了能解锁谁 / 要几级」抽成一张可反复查询的表。
///
/// 判据只读 Conditions.AvailableForStart，**不碰任何任务数据**：
///   · ConditionType = Level 且 value > 0 → 等级需求；
///   · ConditionType = Quest 且 status 含 Success/Fail → 前置任务；反查它即得后续任务。
/// status 只认 Success/Fail，是因为「处于某状态才能接」并不等于「必须先做完」，
/// 混进来会把可选支线说成硬前置。
///
/// ★Build 是一次性快照，读的是调用那一刻的任务表。任务表被改写后必须强制重建，
/// 否则会出现「任务条件已经放开、界面还写着要完成前置任务」的矛盾（见 ChainHintInjector.RebuildDataNow）。
/// </summary>
public sealed class ChainHintData
{
    /// <summary>收藏家，主线任务链的源头。</summary>
    public const string KappaQuestId = "5c51aac186f77432ea65c552";

    /// <summary>灯塔之匙任务。</summary>
    public const string LightKeeperQuestId = "625d6ff5ddc94657c21a1625";

    /// <summary>全表任务 id，顺序即遍历顺序。</summary>
    public string[] QuestIds { get; private init; } = [];

    /// <summary>任务 id → 该任务的前置任务 id。</summary>
    public IReadOnlyDictionary<string, string[]> Prerequisites { get; private init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>任务 id → 把它当前置的那些任务（后续任务）。</summary>
    public IReadOnlyDictionary<string, string[]> Unlocks { get; private init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>任务 id → 等级需求。</summary>
    public IReadOnlyDictionary<string, int> Levels { get; private init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>任务 id → 商人 id，用于在链条后面挂商人名。</summary>
    public IReadOnlyDictionary<string, string> Traders { get; private init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>收藏家的前置任务集合，用于「主线任务」标签。</summary>
    public HashSet<string> KappaPreQuests { get; private init; } = new(StringComparer.Ordinal);

    /// <summary>灯塔之匙的前置任务集合，用于「灯塔之匙」标签。</summary>
    public HashSet<string> LightKeeperPreQuests { get; private init; } = new(StringComparer.Ordinal);

    /// <summary>诊断：Quest 型条件的状态直方图（键 = 状态值组合或 "null"）。</summary>
    public IReadOnlyDictionary<string, int> QuestConditionStatusHistogram { get; private init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>诊断：Quest 型条件的 target 形态计数（single / list / empty / null）。</summary>
    public IReadOnlyDictionary<string, int> QuestConditionTargetShape { get; private init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>诊断：因状态不含 Success/Fail 被跳过、且该任务没有任何链条信息的样例（最多 5 条）。</summary>
    public IReadOnlyList<string> RejectedByStatusSamples { get; private init; } = [];

    /// <summary>
    /// 遍历任务表构建快照。
    ///
    /// ★先复制任务表再遍历：那张字典是所有 mod 共享的可变集合，别的 mod 可以在请求线程上往里加任务，
    /// 直接 foreach 会在枚举器被改动的瞬间抛 InvalidOperationException。复制撞上并发改动就重试，
    /// 冲突窗口只有几条指令；重试耗尽才把异常抛给调用方，由注入器按限次规则报出来，绝不静默吞掉。
    /// </summary>
    public static ChainHintData Build(Dictionary<MongoId, Quest> source)
    {
        var snapshot = CopyQuestTable(source);

        var questIds = new List<string>(snapshot.Length);
        var prerequisites = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var traders = new Dictionary<string, string>(StringComparer.Ordinal);
        var statusHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetShapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectedSamples = new List<string>(5);

        foreach (var (id, quest) in snapshot)
        {
            if (quest is null)
            {
                continue;
            }

            var questId = id.ToString();
            questIds.Add(questId);
            traders[questId] = quest.TraderId.ToString();

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions is null)
            {
                continue;
            }

            foreach (var condition in conditions)
            {
                if (condition is null)
                {
                    continue;
                }

                if (string.Equals(condition.ConditionType, "Level", StringComparison.OrdinalIgnoreCase))
                {
                    var value = condition.Value;
                    if (value.HasValue && value.Value > 0)
                    {
                        levels[questId] = (int)value.Value;
                    }

                    continue;
                }

                if (!string.Equals(condition.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Tally(statusHistogram, DescribeStatus(condition));
                Tally(targetShapes, DescribeTargetShape(condition));

                if (!IsCompletionCondition(condition))
                {
                    if (rejectedSamples.Count < 5)
                    {
                        rejectedSamples.Add($"{questId}({SafeName(quest)}) status={DescribeStatus(condition)}");
                    }

                    continue;
                }

                foreach (var target in QuestTargets(condition))
                {
                    AddUnique(prerequisites, questId, target);
                }
            }
        }

        // 反向索引：谁把 X 当前置，X 的后续任务里就有谁。
        var unlocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (questId, list) in prerequisites)
        {
            foreach (var prerequisite in list)
            {
                AddUnique(unlocks, prerequisite, questId);
            }
        }

        return new ChainHintData
        {
            QuestIds = questIds.ToArray(),
            Prerequisites = Freeze(prerequisites),
            Unlocks = Freeze(unlocks),
            Levels = levels,
            Traders = traders,
            KappaPreQuests = CollectPreQuests(snapshot, KappaQuestId),
            LightKeeperPreQuests = CollectPreQuests(snapshot, LightKeeperQuestId),
            QuestConditionStatusHistogram = statusHistogram,
            QuestConditionTargetShape = targetShapes,
            RejectedByStatusSamples = rejectedSamples,
        };
    }

    private static KeyValuePair<MongoId, Quest>[] CopyQuestTable(Dictionary<MongoId, Quest> source)
    {
        const int MaxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var copy = new List<KeyValuePair<MongoId, Quest>>(source.Count);
                foreach (var entry in source)
                {
                    copy.Add(entry);
                }

                return copy.ToArray();
            }
            catch (InvalidOperationException) when (attempt < MaxAttempts)
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>取某个任务（按 id 定位）的全部前置任务 id，不做状态过滤 —— 标签只看「是不是它的前置」。</summary>
    private static HashSet<string> CollectPreQuests(KeyValuePair<MongoId, Quest>[] snapshot, string questId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, quest) in snapshot)
        {
            if (id.ToString() != questId || quest?.Conditions?.AvailableForStart is null)
            {
                continue;
            }

            foreach (var condition in quest.Conditions.AvailableForStart)
            {
                if (condition is null || !string.Equals(condition.ConditionType, "Quest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var target in QuestTargets(condition))
                {
                    result.Add(target);
                }
            }

            break;
        }

        return result;
    }

    private static bool IsCompletionCondition(QuestCondition condition)
    {
        var status = condition.Status;
        return status is not null && (status.Contains(QuestStatusEnum.Success) || status.Contains(QuestStatusEnum.Fail));
    }

    /// <summary>Quest 条件的 target 字段既可能是单个字符串也可能是数组（ListOrT），两种都要读。</summary>
    private static IEnumerable<string> QuestTargets(QuestCondition condition)
    {
        var target = condition.Target;
        if (target is null)
        {
            yield break;
        }

        if (target.IsItem)
        {
            if (!string.IsNullOrEmpty(target.Item))
            {
                yield return target.Item!;
            }

            yield break;
        }

        var list = target.List;
        if (list is null)
        {
            yield break;
        }

        foreach (var item in list)
        {
            if (!string.IsNullOrEmpty(item))
            {
                yield return item!;
            }
        }
    }

    private static Dictionary<string, string[]> Freeze(Dictionary<string, List<string>> source)
    {
        var result = new Dictionary<string, string[]>(source.Count, StringComparer.Ordinal);
        foreach (var (key, list) in source)
        {
            result[key] = list.ToArray();
        }

        return result;
    }

    private static void AddUnique(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>(2);
            map[key] = list;
        }

        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }

    private static void Tally(Dictionary<string, int> counter, string key)
    {
        counter[key] = counter.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    private static string DescribeStatus(QuestCondition condition)
    {
        var status = condition.Status;
        if (status is null)
        {
            return "null";
        }

        // 不用 LINQ：这是构建期每个 Quest 条件都要走一次的路径，StringBuilder 足够且没有委托开销。
        var builder = new StringBuilder(16);
        foreach (var item in status)
        {
            if (builder.Length > 0)
            {
                builder.Append('+');
            }

            builder.Append(((int)item).ToString());
        }

        return builder.ToString();
    }

    private static string DescribeTargetShape(QuestCondition condition)
    {
        var target = condition.Target;
        if (target is null)
        {
            return "null";
        }

        if (target.IsItem)
        {
            return "single";
        }

        var list = target.List;
        return list is null ? "empty" : $"list:{list.Count}";
    }

    private static string SafeName(Quest quest)
    {
        return string.IsNullOrEmpty(quest.Name) ? quest.Id.ToString() : quest.Name!;
    }
}
