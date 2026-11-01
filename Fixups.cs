// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json;

namespace QuestTuner41;

/// <summary>
/// 任务条件「修正类」开关（与调节类开关分开，默认全开）——修的都是数据本身的毛病，
/// 不改任务设计意图以外的东西：
///
/// 1. <b>角色白名单放宽</b>：`Kills.savageRole` 属「消灭任意 Scav」族写法时，追加环境里确实会刷的
///    角色名（尸潮 infected*、事件变体等）。原版这类白名单只列普通 Scav，装了敌人模组后
///    玩家看着像 Scav 的敌人击杀一律不计。
/// 2. <b>地图白名单补漏</b>：任务文案「工厂除外」但数据只列若干张图时，补上漏掉的地图。
///    只补「大范围（条目数 ≥ LocationWidenMinMaps）」且**确实是地图 Id** 的清单，
///    1~2 张图的刻意限制不动。
/// 3. <b>死引用武器 ID 剔除</b>：`Kills.weapon` 里指向库中不存在物品的 24 位 ID
///    （多为把商人报价 ID 误写成武器模板 ID），剔除后白名单才真实反映可用武器。
///    只在启动末段/首请求两层执行（启动期物品还没注册完）。
/// 4. <b>非法击杀目标修正</b>：`Kills.target` 写成客户端不会传的值（如 `"Scav"`，客户端只传
///    Savage/Bot/Any/阵营名）时按别名表映射为合法值；这类条件原本永远不成立。
/// 5. <b>Location 的 target 补空</b>（防崩不变量，**无开关、始终执行**）：`Location` 条件的
///    `target` 为空时补 `["Any"]`。客户端对该字段无 null 保护（直接调 LINQ），缺失会在击杀瞬间
///    抛 ArgumentNullException 并打断 `Player.OnDead`（尸体不生成 / 击杀不计数 / 死亡善后不执行）。
///
/// 覆盖范围说明：本类只作用于 `AvailableForFinish` 里 `CounterCreator` 的子条件
/// （见 `Entry.ProcessCondition`）。`AvailableForStart` / `Fail` / 日常周常模板不在其内
/// —— 原版 `Fail` 组另有 5 处 Location，实测全部自带 target。
/// </summary>
public sealed class FixupConfig
{
    /// <summary>放宽「消灭任意 Scav」族的角色白名单。</summary>
    public bool WidenScavRoles { get; set; } = true;

    /// <summary>要追加的角色名（客户端比对的是 WildSpawnType 枚举名原文，大小写敏感）。</summary>
    public List<string> AddRoles { get; set; } = new()
    {
        "infectedAssault",
        "infectedCivil",
        "infectedPmc",
        "infectedLaborant",
        "infectedTagilla",
        "cursedAssault",
        "crazyAssaultEvent",
        "arenaFighterEvent",
        "assaultGroup",
    };

    /// <summary>是否把掠夺者/夜行者（pmcBot）也算作 Scav（默认否，与原版语义一致）。</summary>
    public bool IncludePmcBot { get; set; }

    /// <summary>凡含 assault 的组合都算族写法（默认否：只认白名单 ⊆ {assault, marksman, savage}）。</summary>
    public bool IncludeNonIdiomRoleSets { get; set; }

    /// <summary>给大范围地图白名单补漏。</summary>
    public bool WidenLocationTargets { get; set; } = true;

    /// <summary>要补进地图白名单的 location Id（客户端上报的是 location 的 Id 字段原文）。</summary>
    public List<string> AddLocationTargets { get; set; } = new() { "Labyrinth" };

    /// <summary>地图白名单条目数下限：低于此值视为「刻意限定某几张图」，不补。</summary>
    public int LocationWidenMinMaps { get; set; } = 5;

    /// <summary>剔除指向不存在物品的武器 ID。</summary>
    public bool PruneUnknownWeaponIds { get; set; } = true;
    /// <summary>修正非法击杀目标。</summary>
    public bool FixInvalidKillTargets { get; set; } = true;

    /// <summary>非法 target 别名表（客户端只会传 Savage/Bot/Any/AnyPmc/Usec/Bear 等）。</summary>
    public Dictionary<string, string> KillTargetAliases { get; set; } = new(StringComparer.Ordinal)
    {
        ["Scav"] = "Savage",
    };

    /// <summary>全关配置（配置解析失败时用，保证"什么都不做"）。</summary>
    public static FixupConfig Off() => new()
    {
        WidenScavRoles = false,
        WidenLocationTargets = false,
        PruneUnknownWeaponIds = false,
        FixInvalidKillTargets = false,
        AddRoles = new(),
        AddLocationTargets = new(),
        KillTargetAliases = new(StringComparer.Ordinal),
    };

    /// <summary>深拷贝：questOverrides 覆盖修正类开关时用，避免改到全局配置。</summary>
    public FixupConfig Clone() => new()
    {
        WidenScavRoles = WidenScavRoles,
        AddRoles = new List<string>(AddRoles ?? new()),
        IncludePmcBot = IncludePmcBot,
        IncludeNonIdiomRoleSets = IncludeNonIdiomRoleSets,
        WidenLocationTargets = WidenLocationTargets,
        AddLocationTargets = new List<string>(AddLocationTargets ?? new()),
        LocationWidenMinMaps = LocationWidenMinMaps,
        PruneUnknownWeaponIds = PruneUnknownWeaponIds,
        FixInvalidKillTargets = FixInvalidKillTargets,
        KillTargetAliases = new Dictionary<string, string>(KillTargetAliases ?? new(), StringComparer.Ordinal),
    };
}

/// <summary>
/// 修正类改写的执行器。逐 CounterCreator 调用，只碰该计数器的子条件；
/// 全部是「构造新对象 + 整体替换引用」（补扫在请求线程上跑，不能就地改共享集合），
/// 且幂等（三个时点各跑一遍结果一致）。
/// </summary>
internal sealed class FixupRunner
{
    private const string KillsConditionType = "Kills";
    private const string LocationConditionType = "Location";

    /// <summary>Location 条件 target 缺失时补的值：「不限地图」。</summary>
    private const string AnyLocationTarget = "Any";
    private const string AssaultRole = "assault";

    // 用 \z 而不是 $：.NET 的 $ 允许匹配结尾前的换行，会让「24 位 + \n」这种串漏进 MongoId 构造并抛异常
    private static readonly Regex MongoIdPattern = new(@"^[0-9a-fA-F]{24}\z", RegexOptions.Compiled);

    /// <summary>「消灭任意 Scav」族白名单允许出现的角色名（原版三种写法：["assault"]、["assault","marksman"]、["assault","savage","marksman"]）。</summary>
    private static readonly HashSet<string> ScavFamilyRoles = new(StringComparer.Ordinal)
    {
        "assault",
        "marksman",
        "savage",
    };

    /// <summary>已知地图 Id（客户端上报的 location Id 字段原文）。用于识别「这确实是一张地图清单」——
    /// 与 `removeZone` 把 InZone 改写成 Location 的产物区分开：那种条件的 Target 是区域 Id，不是地图 Id。</summary>
    private static readonly HashSet<string> KnownMapIds = new(StringComparer.Ordinal)
    {
        "bigmap", "Woods", "Shoreline", "Interchange", "RezervBase", "laboratory",
        "Lighthouse", "TarkovStreets", "Sandbox", "Sandbox_high", "factory4_day",
        "factory4_night", "Labyrinth", "Terminal", "Town", "Suburbs",
    };

    /// <summary>本进程已放宽过的击杀条件 ID：放宽后白名单不再属族写法，靠它保证改配置后仍能再追加。</summary>
    private readonly HashSet<string> _widenedConditionIds = new(StringComparer.Ordinal);

    public int RolesAdded { get; private set; }
    public int MapsAdded { get; private set; }
    public int WeaponIdsPruned { get; private set; }
    public int TargetsFixed { get; private set; }

    /// <summary>给 Location 条件补回 ["Any"] 的次数。</summary>
    public int LocationTargetsFixed { get; private set; }

    public int TotalApplied => RolesAdded + MapsAdded + WeaponIdsPruned + TargetsFixed + LocationTargetsFixed;

    /// <summary>
    /// 对一条 CounterCreator 的子条件列表做四类修正。调用方负责任务级作用域
    /// （exemptQuests / onlyQuests / questOverrides）与 DryRun 判定。
    /// </summary>
    public void Apply(
        IReadOnlyList<QuestConditionCounterCondition> subs,
        FixupConfig fixups,
        bool dryRun,
        bool itemTableFinal,
        Dictionary<MongoId, TemplateItem> items,
        bool targetRestrictionRemoved)
    {
        if (subs.Count == 0)
        {
            return;
        }

        // 兜底体检（放在最前、且不受下面 killSubs 早退影响）：Location 条件的硬性不变量是
        // target 非空——客户端 ConditionLocationProgressChecker.Test 直接对 target 调 LINQ，
        // target 为 null 会在击杀瞬间抛 ArgumentNullException 并打断 Player.OnDead。
        // ★刻意不做成配置开关：这是防崩不变量（只在畸形数据上写，正常数据零改动），
        //   与玩法无关；做成开关会让 Off()/Clone()/questOverrides 多一处可能漏拷的状态。
        foreach (var sub in subs.Where(s => s is not null && s.ConditionType == LocationConditionType))
        {
            EnsureLocationTarget(sub!, dryRun);
        }

        var killSubs = subs.Where(s => s is not null && s.ConditionType == KillsConditionType).ToList();
        if (killSubs.Count == 0)
        {
            return;
        }

        var anyScavFamilyKill = false;

        foreach (var sub in killSubs)
        {
            if (sub is null)
            {
                continue;
            }

            if (fixups.FixInvalidKillTargets)
            {
                FixInvalidTarget(sub, fixups, dryRun);
            }

            if (fixups.PruneUnknownWeaponIds && itemTableFinal)
            {
                PruneUnknownWeaponIds(sub, items, dryRun);
            }

            var conditionId = sub.Id?.ToString() ?? string.Empty;
            if (!IsScavFamilyIdiom(sub.SavageRole, conditionId, fixups))
            {
                continue;
            }

            anyScavFamilyKill = true;

            // 目标限定已被整体移除（target=Any）时，角色白名单会被清掉，再放宽没有意义
            if (fixups.WidenScavRoles && !targetRestrictionRemoved)
            {
                WidenRoles(sub, conditionId, fixups, dryRun);
            }
        }

        if (fixups.WidenLocationTargets && anyScavFamilyKill && (fixups.AddLocationTargets?.Count ?? 0) > 0)
        {
            foreach (var sub in subs.Where(s => s is not null && s.ConditionType == LocationConditionType))
            {
                WidenLocationTargets(sub!, fixups, dryRun);
            }
        }
    }

    private bool IsScavFamilyIdiom(List<string>? roles, string conditionId, FixupConfig fixups)
    {
        if (roles is null || roles.Count == 0)
        {
            return false;
        }

        if (!roles.Contains(AssaultRole, StringComparer.Ordinal))
        {
            return false;
        }

        if (roles.All(role => ScavFamilyRoles.Contains(role)))
        {
            return true;
        }

        if (conditionId.Length > 0 && _widenedConditionIds.Contains(conditionId))
        {
            return true;
        }

        return fixups.IncludeNonIdiomRoleSets;
    }

    private void WidenRoles(QuestConditionCounterCondition sub, string conditionId, FixupConfig fixups, bool dryRun)
    {
        var current = sub.SavageRole;
        if (current is null || current.Count == 0)
        {
            return;
        }

        var wanted = BuildRoleList(fixups);
        var pending = wanted.Where(role => !current.Contains(role, StringComparer.Ordinal)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var widened = new List<string>(current);
        widened.AddRange(pending);
        if (!dryRun)
        {
            sub.SavageRole = widened;
            if (conditionId.Length > 0)
            {
                _widenedConditionIds.Add(conditionId);
            }
        }

        RolesAdded += pending.Count;
    }

    private static List<string> BuildRoleList(FixupConfig fixups)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in fixups.AddRoles ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                result.Add(role.Trim());
            }
        }

        if (fixups.IncludePmcBot)
        {
            result.Add("pmcBot");
        }

        return result.ToList();
    }

    /// <summary>
    /// 保证 Location 条件的 target 非空：为空（null / 空数组）时补 ["Any"]。
    /// 客户端的硬性前提——`ConditionLocationProgressChecker.Test` 直接对 target 调 LINQ，
    /// 缺失时抛 ArgumentNullException；该异常发生在击杀瞬间的 OnEnemyKill 链（Player.OnDead 内），
    /// 会打断 OnDead，造成「尸体不生成 / 击杀不计数 / 死亡善后不执行」。
    /// </summary>
    private void EnsureLocationTarget(QuestConditionCounterCondition sub, bool dryRun)
    {
        var target = sub.Target;

        // 已是非空清单 → 无事可做
        if (target is not null && target.IsList && target.List is not null
            && target.List.Any(v => !string.IsNullOrEmpty(v)))
        {
            return;
        }

        // 单值形态（IsItem）：客户端字段是 string[]，裸标量无法反序列化 → 包成单元素数组归一化。
        // （当前环境 0 实例；这里是防将来有别的来源造出该形态。）
        if (target is not null && target.IsItem && !string.IsNullOrEmpty(target.Item))
        {
            if (!dryRun)
            {
                sub.Target = new ListOrT<string>(new List<string> { target.Item }, null);
            }

            LocationTargetsFixed++;
            return;
        }

        if (!dryRun)
        {
            // 给 List（数组）而非 Item：原版 208 处 Location 的 target 都是数组形态，
            // ListOrTConverter 也把 IsList 序列化成数组。
            sub.Target = new ListOrT<string>(new List<string> { AnyLocationTarget }, null);
        }

        LocationTargetsFixed++;
    }

    private void WidenLocationTargets(QuestConditionCounterCondition sub, FixupConfig fixups, bool dryRun)
    {
        var target = sub.Target;
        if (target is null || !target.IsList || target.List is null)
        {
            return;
        }

        if (target.List.Count < fixups.LocationWidenMinMaps)
        {
            return;
        }

        // 必须确实是地图清单：Target 全是地图 Id 才行（removeZone 会把 InZone 改写成
        // Location，那种条件的 Target 是区域 Id，不能往里塞地图）
        if (!target.List.Any(v => v is not null && KnownMapIds.Contains(v)))
        {
            return;
        }

        var pending = (fixups.AddLocationTargets ?? new List<string>())
            .Where(map => !string.IsNullOrWhiteSpace(map) && !target.List.Contains(map, StringComparer.Ordinal))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var widened = new List<string>(target.List);
        widened.AddRange(pending);
        if (!dryRun)
        {
            sub.Target = new ListOrT<string>(widened, null);
        }

        MapsAdded += pending.Count;
    }

    /// <summary>
    /// ★顺序契约（2026-09-19 并入 AddNewWeaponsToQuest41 时确立）：
    /// 「加武器」（武器池模块）必须在本类 prune 之前跑，并把它**本遍写入任务条件的全部武器 ID、
    /// 以及它依赖的克隆基底 ID**登记进 <see cref="KeptWeaponIds"/> 保留集；
    /// prune 只删「既不在 item 表、也不在保留集」的死引用。
    /// 这样即便某个 mod 的武器 ID 在本层可见的 item 表里还没落地（晚注册/运行时生成），也不会被误删。
    /// </summary>
    private readonly HashSet<string> _keptWeaponIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>武器池登记入口：本遍新增/依赖的武器 ID（幂等，可跨三层时点累计）。</summary>
    public void KeepWeaponIds(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _keptWeaponIds.Add(id);
            }
        }
    }

    /// <summary>保留集条数（供汇总行展示）。</summary>
    public int KeptWeaponIdsCount => _keptWeaponIds.Count;

    private void PruneUnknownWeaponIds(QuestConditionCounterCondition sub, Dictionary<MongoId, TemplateItem> items, bool dryRun)
    {
        var weapons = sub.Weapon;
        if (weapons is null || weapons.Count == 0)
        {
            return;
        }

        var dead = weapons
            // ★顺序契约：不在 item 表 **且** 不在武器池保留集，才判定为死引用（见 KeptWeaponIds 注释）
            .Where(w => !string.IsNullOrEmpty(w) && MongoIdPattern.IsMatch(w)
                        && !items.ContainsKey(new MongoId(w))
                        && !_keptWeaponIds.Contains(w))
            .ToList();
        if (dead.Count == 0)
        {
            return;
        }

        if (!dryRun)
        {
            // 整表替换：原集合减去证明不存在的条目，其余原样保留
            sub.Weapon = new HashSet<string>(weapons.Where(w => !dead.Contains(w, StringComparer.Ordinal)));
        }

        WeaponIdsPruned += dead.Count;
    }

    private void FixInvalidTarget(QuestConditionCounterCondition sub, FixupConfig fixups, bool dryRun)
    {
        var aliases = fixups.KillTargetAliases;
        if (aliases is null || aliases.Count == 0)
        {
            return;
        }

        var target = sub.Target;
        if (target is null)
        {
            return;
        }

        if (target.IsItem)
        {
            var value = target.Item;
            if (value is null || !aliases.TryGetValue(value, out var mapped))
            {
                return;
            }

            if (!dryRun)
            {
                sub.Target = new ListOrT<string>(null, mapped);
            }

            TargetsFixed++;
            return;
        }

        if (!target.IsList || target.List is null)
        {
            return;
        }

        var changed = false;
        var mappedList = new List<string>(target.List.Count);
        foreach (var value in target.List)
        {
            if (value is not null && aliases.TryGetValue(value, out var mapped))
            {
                mappedList.Add(mapped);
                TargetsFixed++;
                changed = true;
            }
            else
            {
                mappedList.Add(value!);
            }
        }

        if (!changed)
        {
            return;
        }

        if (!dryRun)
        {
            sub.Target = new ListOrT<string>(mappedList, null);
        }
    }
}
