// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace QuestTuner41.ChainHint;

/// <summary>
/// 任务链模块的配置（对应 config.json 顶层 "ChainHint" 分区）。
/// 默认值取自 4.0 时代的原版观感：描述开头、红/浅蓝/绿三行。
///
/// ★键名与更早那份独立 mod 的 config.jsonc 顶层键**逐字一致**，因为向后兼容策略是
/// 「读到旧 config.jsonc 就整段覆盖本分区」—— 键名对不上会让老用户的调参静默失效。
/// 改任何一个键名之前，先想清楚有多少老用户的 config 会因此变成孤儿。
/// </summary>
public class ChainHintConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>prefix = 塞在任务描述开头；suffix = 追加到描述结尾。</summary>
    public string InsertPosition { get; set; } = "prefix";

    public bool ShowPrerequisite { get; set; } = true;

    public bool ShowUnlock { get; set; } = true;

    public bool ShowLevelRequirement { get; set; } = true;

    public bool ShowTraderName { get; set; } = true;

    /// <summary>任务名前加「主线任务」。默认关闭：4.1 的 MGMod 已提供 3x4 指引，同时开会撞成两个标记。</summary>
    public bool ShowMainQuestTag { get; set; }

    /// <summary>任务名前加「灯塔之匙」。默认关闭：多数灯塔前置已被 MG 的 3x4 清单覆盖。</summary>
    public bool ShowLightkeeperTag { get; set; }

    /// <summary>0 = 不限；>0 = 只显示前 N 个后续任务。</summary>
    public int MaxUnlockShown { get; set; }

    /// <summary>0 = 不限；>0 = 只显示前 N 个前置任务。</summary>
    public int MaxPrerequisiteShown { get; set; }

    /// <summary>0 = 只看直接上下游；>0 = 再往上/下多展开相应层数（长链建议 1~2）。</summary>
    public int ChainDepth { get; set; }

    /// <summary>后续任务名后附带各自等级需求。</summary>
    public bool ShowUnlockLevel { get; set; }

    public ChainHintTextConfig Text { get; set; } = new();

    public ChainHintColorConfig Color { get; set; } = new();

    public ChainHintTagConfig Tag { get; set; } = new();

    public List<string> QuestBlackList { get; set; } = new();

    public List<string> SkipTraderIds { get; set; } = new();

    /// <summary>
    /// 拼进链条文本前从任务名里剔除的标记文字。
    /// 默认剔除 MGMod 的 3x4 标记：它在任务名后追加【Road of 3X4】，
    /// 不清洗的话前置/后续列表里会出现「任务名+标记」，而且结果会依赖两个 transformer 的先后顺序。
    /// </summary>
    public List<string> NameStripTexts { get; set; } = new() { "【Road of 3X4】" };

    /// <summary>
    /// 日志档位：quiet=只报异常；summary=启动摘要+注入里程碑（默认）；full=逐轮诊断明细。
    /// 写了这一项就以它为准；没写才回退到旧开关。
    /// </summary>
    public string? LogLevel { get; set; }

    /// <summary>旧版开关，仅为兼容旧 config.jsonc 保留：true=full，false=quiet。只在 LogLevel 未写时生效。</summary>
    public bool? ConsoleLog { get; set; }

    /// <summary>实际生效的档位，由 <see cref="ResolveLogLevel"/> 定档后写入。派生值，不进 config.json。</summary>
    [JsonIgnore]
    public ChainHintLogLevel EffectiveLogLevel { get; private set; } = ChainHintLogLevel.Summary;

    /// <summary>true = 配置里同时写了旧 ConsoleLog 但已被 LogLevel 盖过，需要在启动时提示用户。</summary>
    [JsonIgnore]
    public bool LegacyConsoleLogIgnored { get; private set; }

    /// <summary>
    /// 定档：显式 LogLevel 优先，其次旧 ConsoleLog，最后 summary。写错的取值一律退回 summary，不抛异常。
    /// ★优先级不能反过来——旧配置文件里普遍留着 "ConsoleLog": true，
    /// 若让它压过 LogLevel，所有老用户的降噪会静默失效、日志行数与降噪前一模一样。
    /// </summary>
    public void ResolveLogLevel()
    {
        var specified = LogLevel?.Trim();

        if (!string.IsNullOrEmpty(specified))
        {
            EffectiveLogLevel = specified!.ToLowerInvariant() switch
            {
                "quiet" => ChainHintLogLevel.Quiet,
                "full" => ChainHintLogLevel.Full,
                _ => ChainHintLogLevel.Summary,
            };

            LegacyConsoleLogIgnored = ConsoleLog.HasValue;
            return;
        }

        LegacyConsoleLogIgnored = false;
        EffectiveLogLevel = ConsoleLog switch
        {
            true => ChainHintLogLevel.Full,
            false => ChainHintLogLevel.Quiet,
            null => ChainHintLogLevel.Summary,
        };
    }

    /// <summary>
    /// 反序列化后必须调用一次：把 null / 空的段补回默认值。
    /// System.Text.Json 读到显式的 null 不抛异常，会直接把字段置成 null；
    /// 而这些字段在注入期会被无条件解引用，不补就是一轮空引用。
    /// </summary>
    public void Normalize()
    {
        Text = (Text ?? new ChainHintTextConfig()).WithDefaults();
        Color = (Color ?? new ChainHintColorConfig()).WithDefaults();
        Tag = (Tag ?? new ChainHintTagConfig()).WithDefaults();
        QuestBlackList = QuestBlackList ?? new List<string>();
        SkipTraderIds = SkipTraderIds ?? new List<string>();
        NameStripTexts = NameStripTexts ?? new List<string>();

        // 只留有意义的条目，避免黑名单里一个空串就把整张表比成「全部跳过」这种误伤。
        QuestBlackList.RemoveAll(string.IsNullOrWhiteSpace);
        SkipTraderIds.RemoveAll(string.IsNullOrWhiteSpace);
        NameStripTexts.RemoveAll(string.IsNullOrEmpty);

        // 归一成两个取值：写错、大小写不同、带空格都落到 prefix。
        // 注入期是纯字符串比较，容错放在这里做一次，比在热路径上判更便宜也更不容易漏。
        InsertPosition = string.Equals(InsertPosition?.Trim(), "suffix", StringComparison.OrdinalIgnoreCase)
            ? "suffix"
            : "prefix";

        ResolveLogLevel();
    }

    /// <summary>
    /// 用旧 config.jsonc 解析出来的实例整段覆盖本分区。
    /// 老用户从旧包升级时 config.json 里还没有 ChainHint 分区，
    /// 不导入的话他们的调参（颜色、文案、开关、黑名单）会全部退回默认值 —— 那是静默丢配置。
    /// </summary>
    public void OverrideFrom(ChainHintConfig other)
    {
        Enabled = other.Enabled;
        InsertPosition = other.InsertPosition;
        ShowPrerequisite = other.ShowPrerequisite;
        ShowUnlock = other.ShowUnlock;
        ShowLevelRequirement = other.ShowLevelRequirement;
        ShowTraderName = other.ShowTraderName;
        ShowMainQuestTag = other.ShowMainQuestTag;
        ShowLightkeeperTag = other.ShowLightkeeperTag;
        MaxUnlockShown = other.MaxUnlockShown;
        MaxPrerequisiteShown = other.MaxPrerequisiteShown;
        ChainDepth = other.ChainDepth;
        ShowUnlockLevel = other.ShowUnlockLevel;
        Text = other.Text;
        Color = other.Color;
        Tag = other.Tag;
        QuestBlackList = other.QuestBlackList;
        SkipTraderIds = other.SkipTraderIds;
        NameStripTexts = other.NameStripTexts;
        LogLevel = other.LogLevel;
        ConsoleLog = other.ConsoleLog;
    }
}

/// <summary>拼接用的文案片段。{0} 会被替换成等级。</summary>
public class ChainHintTextConfig
{
    public string Prerequisite { get; set; } = "前置任务: 完成";

    public string Unlock { get; set; } = "后续任务: ";

    /// <summary>{0} = 等级。</summary>
    public string Level { get; set; } = "等级需求: PMC等级达到{0}级，";

    public string Separator { get; set; } = "、";

    public string Ending { get; set; } = "。";

    /// <summary>ShowUnlockLevel 时的后缀，{0} = 等级。</summary>
    public string UnlockLevelSuffix { get; set; } = "[等级{0}]";

    public ChainHintTextConfig WithDefaults()
    {
        var defaults = new ChainHintTextConfig();
        Prerequisite = string.IsNullOrEmpty(Prerequisite) ? defaults.Prerequisite : Prerequisite;
        Unlock = string.IsNullOrEmpty(Unlock) ? defaults.Unlock : Unlock;
        Level = string.IsNullOrEmpty(Level) ? defaults.Level : Level;
        Separator = string.IsNullOrEmpty(Separator) ? defaults.Separator : Separator;
        Ending = string.IsNullOrEmpty(Ending) ? defaults.Ending : Ending;
        UnlockLevelSuffix = string.IsNullOrEmpty(UnlockLevelSuffix) ? defaults.UnlockLevelSuffix : UnlockLevelSuffix;
        return this;
    }
}

/// <summary>富文本颜色，写 RRGGBB（不带 #），容忍用户写成 #RRGGBB。</summary>
public class ChainHintColorConfig
{
    public string PreQuest { get; set; } = "FF6666";

    public string UnlockQuest { get; set; } = "80E0FF";

    public string QuestLevel { get; set; } = "66FF66";

    public string MainQuest { get; set; } = "FFA500";

    public string LightKeeperQuest { get; set; } = "228B22";

    public ChainHintColorConfig WithDefaults()
    {
        var defaults = new ChainHintColorConfig();
        PreQuest = string.IsNullOrWhiteSpace(PreQuest) ? defaults.PreQuest : PreQuest;
        UnlockQuest = string.IsNullOrWhiteSpace(UnlockQuest) ? defaults.UnlockQuest : UnlockQuest;
        QuestLevel = string.IsNullOrWhiteSpace(QuestLevel) ? defaults.QuestLevel : QuestLevel;
        MainQuest = string.IsNullOrWhiteSpace(MainQuest) ? defaults.MainQuest : MainQuest;
        LightKeeperQuest = string.IsNullOrWhiteSpace(LightKeeperQuest) ? defaults.LightKeeperQuest : LightKeeperQuest;
        return this;
    }
}

/// <summary>任务名前的分类标签。</summary>
public class ChainHintTagConfig
{
    public string MainQuest { get; set; } = "「主线任务」";

    public string LightKeeper { get; set; } = "「灯塔之匙」";

    public ChainHintTagConfig WithDefaults()
    {
        var defaults = new ChainHintTagConfig();
        MainQuest = string.IsNullOrEmpty(MainQuest) ? defaults.MainQuest : MainQuest;
        LightKeeper = string.IsNullOrEmpty(LightKeeper) ? defaults.LightKeeper : LightKeeper;
        return this;
    }
}
