// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using QuestTuner41.ChainHint;

namespace QuestTuner41;

/// <summary>
/// 配置。键名刻意与 sgtlaggyQuestTweaks 对齐（removeTarget / removeWeapon / eliminationCount 等），
/// 用户在两套配置之间抄写不用换脑子；不认识的键忽略。
/// 解析失败时返回"全关"配置（Safe），绝不带着半套开关去改任务。
/// </summary>
public sealed class TunerConfig
{
    // ── 条件移除（P1：target / weapon / weaponMods / distance / time / map / zone）
    public bool RemoveTarget { get; set; }
    public bool RemoveWeapon { get; set; }
    public bool RemoveWeaponMods { get; set; }
    public bool RemoveDistance { get; set; }
    public bool RemoveTime { get; set; }
    public bool RemoveMap { get; set; }
    public bool RemoveZone { get; set; }

    // ── 条件移除（P2：部位 / 自身装备 / 敌人装备 / 自身状态 / 敌人状态 / 战局内获得）
    public bool RemoveBodyPart { get; set; }
    public bool RemoveSelfGear { get; set; }
    public bool RemoveEnemyGear { get; set; }
    public bool RemoveSelfHealthEffect { get; set; }
    public bool RemoveEnemyHealthEffect { get; set; }
    public bool RemoveFindInRaid { get; set; }

    // ── QOL（键名与嵌套形状对齐 sgtlaggyQuestTweaks）
    public QualityOfLifeConfig QualityOfLife { get; set; } = new();

    // ── 特例处理（键名对齐 sgtlaggyQuestTweaks）
    public SpecialCasesConfig SpecialCases { get; set; } = new();

    // ── 修正类（v0.4.0 新增）：修数据本身的毛病，默认全开，见 FixupConfig
    public FixupConfig Fixups { get; set; } = new();

    // ── 任务链提示（v1.0.0 新增，并入自原独立 mod QuestChainHint41）
    // 单独分区放在顶层，是因为它自成一块：注入的是任务**描述文本**，
    // 与上面那些改任务**数据**的开关互不影响，关掉整块也不影响其余功能。
    // 向后兼容：老用户旧包里 mods\QuestChainHint41\config.jsonc 存在时，
    // 由 ChainHintRunner 读入并整段覆盖本分区（见 ChainHintRunner.ImportLegacyConfig）。
    public ChainHintConfig ChainHint { get; set; } = new();

    // ── 数值面板（负值 = 不改）
    public double EliminationCount { get; set; } = -1;
    public double EliminationPercent { get; set; } = -1;
    public double HandoverItemCount { get; set; } = -1;
    public double HandoverItemPercent { get; set; } = -1;

    // ── 击杀目标泛化（Scavs4All 的能力，保留为独立开关）
    public bool ReplaceScavsWithAny { get; set; } = true;

    /// <summary>
    /// 可被整族放宽成「击杀任意目标」的角色白名单**前缀**（客户端比对用的 WildSpawnType 枚举名原文，
    /// 大小写敏感；这里只做前缀判定）。默认为用户点名要放宽的两族：
    /// <c>pmcBot</c>（掠夺者 / 夜行者）、<c>sectant</c>（邪教徒：sectantPriest / sectantWarrior / sectantOni 等）。
    /// 含 boss* / follower* 等 BOSS 族角色的任务**不受影响**（保持严格）。
    /// 置空数组 = 任何带角色白名单的任务都不放宽（只放宽纯 Scav 任务，即原行为）。
    /// </summary>
    public List<string> RelaxRolePrefixes { get; set; } = new()
    {
        "pmcBot",
        "sectant",
    };

    public bool ReplacePmcWithAny { get; set; }
    public bool HarderPmc { get; set; }
    public double HarderPmcMultiplier { get; set; } = 2;

    // ── 作用范围
    public List<string> ExemptQuests { get; set; } = new();
    public List<string> OnlyQuests { get; set; } = new();
    public Dictionary<string, Dictionary<string, bool>> QuestOverrides { get; set; } = new();
    public bool HonorSgtlaggyConfig { get; set; } = true;

    /// <summary>是否把同一套改写套用到日常/周常（repeatable）模板。默认关。</summary>
    public bool AffectRepeatables { get; set; }

    // ── 诊断
    public bool DryRun { get; set; }
    public string DumpQuestTablePath { get; set; } = string.Empty;
    public string DumpRepeatablesPath { get; set; } = string.Empty;
    /// <summary>
    /// 已降级：追加到**任务目标行**尾部的标记。2026-10-11 起默认关闭，改由
    /// <see cref="QuestNameSuffix"/> 在任务名上打标（目标行是暗号、且在列表页看不到）。
    /// 键保留是为了老配置不变成孤儿；想恢复旧行为把它写回非空值即可。
    /// </summary>
    public string MarkerSuffix { get; set; } = string.Empty;

    /// <summary>
    /// 「任务优化」标记：被本 mod 改写过**条件**的任务，在任务名尾部追加的标记
    /// （如「借酒消愁（任务已优化）」）。空字符串 = 关闭。
    /// 判定口径＝该任务任一条件改写命中（<c>TotalChanges()</c> 差分，含 fixups 与配对中和）；
    /// 武器池往条件里加的枪**不算**这一族，走 <see cref="WeaponNameSuffix"/>。
    /// 落点是 locale 键 <c>"&lt;questId&gt; name"</c>，与 ChainHint 的前缀标记（主线任务等）互不干扰。
    /// </summary>
    public string QuestNameSuffix { get; set; } = "（任务已优化）";

    /// <summary>
    /// 「武器优化」标记：被武器池**真的往完成条件里 Add 过枪**的任务，在任务名尾部追加的标记
    /// （如「精准猎手（武器已优化）」）。空字符串 = 关闭。
    /// ★口径是「池确实 Add 过」（<c>WeaponPoolReport.AddedQuestIds</c>），**不是**「任务有模组枪可用」
    ///   （后者是接取清单消息的门控 AcceptLists，早已列全枪的任务也会进去，会误标一大批）。
    /// 与 <see cref="QuestNameSuffix"/> 是两个**独立维度**，同一任务可同时命中，两枚标记叠加。
    /// </summary>
    public string WeaponNameSuffix { get; set; } = "（武器已优化）";
    public bool Debug { get; set; }

    /// <summary>配置解析失败标志（只用于日志表述，此时所有开关都是关的）。</summary>
    public bool ParseFailed { get; private set; }

    /// <summary>
    /// 本次启动对**历史出厂默认值**做的一次性迁移记录（只用于日志告知，不参与行为）。
    /// 见 <see cref="MigrateHistoricalDefaults"/>。
    /// </summary>
    [JsonIgnore]
    public List<string> DefaultMigrations { get; } = new();
    public static TunerConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        TunerConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<TunerConfig>(text, ConfigJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            // 加固（2026-10-09 玩家反馈）：把出错行原文带出来再抛。
            // 最常见的是"顶层 } 后面又多一个逗号"，而 STJ 只给 0 起算的行号，
            // 玩家照抄去数行永远差一行 —— 这里换算成 1 起算并附上下文。
            throw new InvalidDataException(ConfigJson.DescribeError(text, ex), ex);
        }

        if (cfg is null)
        {
            throw new InvalidDataException("配置内容为空");
        }

        cfg.Normalize();
        cfg.MigrateHistoricalDefaults();
        return cfg;
    }

    /// <summary>
    /// 补齐显式写成 null 的集合/分段。
    /// ★必须做：System.Text.Json 读到 "exemptQuests": null 不抛异常（那不是语法错），会把属性直接置成 null，
    ///   于是后面第一次 Contains 就 NullReferenceException，整轮任务改写中止、mod 全部功能静默失效，
    ///   而用户只看到一条裸 NRE 栈。questOverrides 另有 ForQuest 的 null 判断，Fixups 另有 ?? Off()，
    ///   但 ExemptQuests / OnlyQuests / QualityOfLife / SpecialCases 四处没有兜底。
    /// </summary>
    private void Normalize()
    {
        ExemptQuests ??= new List<string>();
        OnlyQuests ??= new List<string>();
        QuestOverrides ??= new Dictionary<string, Dictionary<string, bool>>();
        QualityOfLife ??= new QualityOfLifeConfig();
        SpecialCases ??= new SpecialCasesConfig();
        Fixups ??= FixupConfig.Off();

        // 补 ChainHint：它被写成 null 时 ChainHintRunner 内部会自建实例，而宿主要往
        // 同一个实例的 NameStripTexts 里并「（已优化）」（防后缀泄漏进链条文本），
        // 实例不一致就会静默丢。这里补齐后两侧拿到的是同一个对象。
        ChainHint ??= new ChainHintConfig();
    }

    /// <summary>
    /// 把**历史出厂默认值**就地换成当前出厂默认值，让老用户升级后不用手改配置。
    ///
    /// ★为什么只认「历史出厂默认值」：改不改动由值本身决定。用户自己写过的任何值
    ///   （哪怕刚好长得像旧默认）一律原样保留——这才是"无损"，不越权替用户做主。
    /// ★为什么不回写磁盘：出厂 config.json 带 60+ 行中文注释，System.Text.Json 回写会把
    ///   注释全部抹平并重排格式，那才是真正的破坏；而且配置文件属于用户，不该由 mod 改写。
    ///   代价只是每次启动多打一行日志说明，可接受。
    /// ★缺键不用管：老配置没有 <c>weaponNameSuffix</c> 时，System.Text.Json 直接沿用属性初始值
    ///   "（武器已优化）"，这本来就是无损的；真正要补的只有"键存在但值是旧默认"的两种情形。
    /// </summary>
    private void MigrateHistoricalDefaults()
    {
        // 1.2.0 的任务名标记是单族的「（已优化）」；1.3.0 拆成任务族/武器族两枚，
        // 与「（武器已优化）」并排时前者含义就含混了，故换成对称的「（任务已优化）」。
        if (QuestNameSuffix == "（已优化）")
        {
            QuestNameSuffix = "（任务已优化）";
            DefaultMigrations.Add("questNameSuffix 由「（已优化）」自动升为「（任务已优化）」（1.3.0 起与武器族对称）");
        }

        // 1.1.1 及更早的出厂默认把目标行标记开着（原值 " (S4A)"）。1.2.0 起已由任务名标记接管，
        // 目标行那个是玩家看不见列表页的暗号、且被证实没实际覆盖面，这里顺手关掉。
        if (MarkerSuffix == " (S4A)")
        {
            MarkerSuffix = string.Empty;
            DefaultMigrations.Add("markerSuffix 由旧暗号「 (S4A)」自动关闭（标记已改到任务名上）");
        }
    }

    /// <summary>解析失败时的安全配置：任何条件都不改，只保留"什么都不做"。</summary>
    public static TunerConfig Safe()
    {
        return new TunerConfig
        {
            ReplaceScavsWithAny = false,
            ReplacePmcWithAny = false,
            HonorSgtlaggyConfig = true,
            Fixups = FixupConfig.Off(),

            // ★任务链不跟着 Safe 一起关：它不改任务数据，只往描述里加文本。
            // 配置文件解析失败时把它一起关掉，等于让用户「配置写坏 → 功能凭空消失」且无从追溯。
            ChainHint = new ChainHintConfig(),
            ParseFailed = true,
        };
    }

    /// <summary>取某个任务的有效开关：全局值再叠加 questOverrides（未列出的键沿用全局）。</summary>
    public TunerConfig ForQuest(string questId)
    {
        if (QuestOverrides is null || !QuestOverrides.TryGetValue(questId, out var ov) || ov is null || ov.Count == 0)
        {
            return this;
        }

        var c = (TunerConfig)MemberwiseClone();
        foreach (var (key, value) in ov)
        {
            switch (key.ToLowerInvariant())
            {
                case "removetarget": c.RemoveTarget = value; break;
                case "removeweapon": c.RemoveWeapon = value; break;
                case "removeweaponmods": c.RemoveWeaponMods = value; break;
                case "removedistance": c.RemoveDistance = value; break;
                case "removetime": c.RemoveTime = value; break;
                case "removemap": c.RemoveMap = value; break;
                case "removezone": c.RemoveZone = value; break;
                case "replacescavswithany": c.ReplaceScavsWithAny = value; break;
                case "replacepmcwithany": c.ReplacePmcWithAny = value; break;
                case "removebodypart": c.RemoveBodyPart = value; break;
                case "removeselfgear": c.RemoveSelfGear = value; break;
                case "removeenemygear": c.RemoveEnemyGear = value; break;
                case "removeselfhealtheffect": c.RemoveSelfHealthEffect = value; break;
                case "removeenemyhealtheffect": c.RemoveEnemyHealthEffect = value; break;
                case "removefindinraid": c.RemoveFindInRaid = value; break;

                // 修正类（v0.4.0）：改前先深拷贝 Fixups，避免把覆盖写到全局配置上
                // （配置里显式写 "Fixups": null 时按全关处理，再 Clone）
                case "widenscavroles":
                    c.Fixups = (c.Fixups ?? FixupConfig.Off()).Clone();
                    c.Fixups.WidenScavRoles = value;
                    break;
                case "widenscavlocationtargets":
                case "widenlocationtargets":
                    c.Fixups = (c.Fixups ?? FixupConfig.Off()).Clone();
                    c.Fixups.WidenLocationTargets = value;
                    break;
                case "pruneunknownweaponids":
                    c.Fixups = (c.Fixups ?? FixupConfig.Off()).Clone();
                    c.Fixups.PruneUnknownWeaponIds = value;
                    break;
                case "fixinvalidkilltargets":
                    c.Fixups = (c.Fixups ?? FixupConfig.Off()).Clone();
                    c.Fixups.FixInvalidKillTargets = value;
                    break;
            }
        }

        return c;
    }
}

/// <summary>QOL 三项（与 sgtlaggyQuestTweaks 同键名）。</summary>
public sealed class QualityOfLifeConfig
{
    /// <summary>显示所有默认隐藏的任务目标（清空条件级 visibilityConditions）。</summary>
    public bool RevealAllQuestObjectives { get; set; }

    /// <summary>把"未知奖励"标记去掉（原版基础数据里为 0 条，主要对魔改/运行时数据有意义）。</summary>
    public bool RevealUnknownRewards { get; set; }

    /// <summary>移除任务等待期。当前 SPT 4.1 的任务前置里没有时间字段，本项只记日志不实现。</summary>
    public bool RemoveTimeGates { get; set; }
}

/// <summary>特例处理（与 sgtlaggyQuestTweaks 同键名）。</summary>
public sealed class SpecialCasesConfig
{
    /// <summary>灯塔 Network Provider - Part 1 只要求等级（0 = 关闭）。</summary>
    public double LightkeeperOnlyRequireLevel { get; set; }

    /// <summary>把 Sako TRG M10 加进 The Tarkov Shooter Part 1–6 的武器列表。</summary>
    public bool TarkovShooterM10 { get; set; }

    /// <summary>按 EFT 1.1.0.0 回填收藏家（Collector）的前置表。</summary>
    public bool CollectorPrerequisiteBackport { get; set; }
}
