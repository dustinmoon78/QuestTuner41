// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using System.Threading;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTuner41.ChainHint;

/// <summary>
/// 链条文本的渲染与注入。职责边界刻意收得很窄：
/// 读任务 Conditions 抽出前置/后续/等级 → 拼成富文本行 → 覆写 locale 里的任务描述。
/// **不碰任务数据**（不动 Conditions、不动 AvailableFor*），**不碰武器块**（那是 WeaponPool 的职责）。
///
/// ★这里是本模块唯一的性能敏感点。4.1 的 LazyLoad&lt;T&gt;.Value 每次访问都会重新反序列化并重跑
/// 全部 transformer（见 SPTarkov.Server.Core/Utils/Json/LazyLoad.cs：它没有缓存字段），
/// 所以「每挂一个 transformer」＝「每次 locale 读取多跑一遍全表任务的注入」。
/// 早先的实现没有注册门控，在三个时点各 Register 一次，累计每语言挂 6 个 transformer，
/// 一次 locale 读取要白跑 6 遍注入。现在 Register 用静态门控保证每语言只挂一个。
/// </summary>
public sealed class ChainHintInjector
{
    private static readonly string[] NoIds = [];

    private static readonly ChainHintTextConfig DefaultText = new();

    /// <summary>
    /// 已挂过 transformer 的语言。刻意做成 static：LocaleTable 与它内部的 LazyLoad 是全进程单例，
    /// 换成实例字段就挡不住「初始化被调了两次」这种重复挂载，而重复挂载的代价是每次 locale
    /// 读取都多一整轮全量注入。比较器用 OrdinalIgnoreCase，与 LocaleTable.Global 保持一致。
    /// </summary>
    private static readonly HashSet<string> RegisteredLanguages = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object RegisterLock = new();

    private static readonly Regex RichTextTag = new("<[^>]{1,40}>", RegexOptions.Compiled);

    private readonly ChainHintConfig _config;
    private readonly TemplateTable _templates;
    private readonly ChainHintLog _log;
    private readonly object _dataLock = new();
    private readonly object _shapeLock = new();

    private readonly bool _isSuffix;
    private readonly bool _detailed;
    private readonly bool _showMainTag;
    private readonly bool _showLightTag;

    private readonly HashSet<string> _blackList;
    private readonly HashSet<string> _skipTraders;

    /// <summary>本模块写出的三种颜色各自的富文本开标签，<see cref="IsOwnInjectedLine"/> 按颜色配对使用。</summary>
    private readonly string[] _ownColorTags;

    /// <summary>颜色开标签之后必须紧跟的文案前缀，用来把「自己写的行」和「别人用同色写的行」区分开。</summary>
    private readonly string[] _ownLineHeads;

    private readonly string _levelTemplate;
    private readonly string _levelHead;
    private readonly string _unlockLevelSuffix;
    private readonly string? _mainTagPrefix;
    private readonly string? _lightTagPrefix;

    private readonly HashSet<string> _seenShapes = new(StringComparer.Ordinal);
    private readonly Queue<string> _shapeOrder = new();

    private const int MaxShapes = 512;
    private const int MaxSamples = 5;

    private ChainHintData? _data;
    private int _loggedQuestCount = -1;
    private int _bestInjected = -1;
    private int _rounds;
    private bool _taggedAnyName;

    public ChainHintInjector(ChainHintConfig config, TemplateTable templates, ChainHintLog log)
    {
        _config = config;
        _templates = templates;
        _log = log;

        // 诊断采样在调用点用 if 门控，所以这里先算一次开关，避免每条任务都去比枚举。
        _detailed = log.Level >= ChainHintLogLevel.Full;
        _isSuffix = string.Equals(config.InsertPosition, "suffix", StringComparison.OrdinalIgnoreCase);

        // 黑名单/跳过商人转哈希集：早先用 List.Exists 线性扫，1900 条任务每条每轮各扫一遍，
        // 用户多写几个 id 就把注入期变成 O(任务数 × 名单长)。
        _blackList = new HashSet<string>(config.QuestBlackList, StringComparer.OrdinalIgnoreCase);
        _skipTraders = new HashSet<string>(config.SkipTraderIds, StringComparer.OrdinalIgnoreCase);

        // 格式串容错：用户把 {0} 写坏时退回默认模板，而不是在注入期抛 FormatException。
        _levelTemplate = SafeFormat(config.Text.Level, DefaultText.Level);
        _levelHead = PrefixOf(_levelTemplate);
        _unlockLevelSuffix = SafeFormat(config.Text.UnlockLevelSuffix, DefaultText.UnlockLevelSuffix);

        var colorTags = new List<string>(3);
        var lineHeads = new List<string>(3);
        AddOwnMarker(colorTags, lineHeads, config.Color.QuestLevel, _levelHead);
        AddOwnMarker(colorTags, lineHeads, config.Color.PreQuest, config.Text.Prerequisite);
        AddOwnMarker(colorTags, lineHeads, config.Color.UnlockQuest, config.Text.Unlock);
        _ownColorTags = colorTags.ToArray();
        _ownLineHeads = lineHeads.ToArray();

        _showMainTag = config.ShowMainQuestTag;
        _showLightTag = config.ShowLightkeeperTag;
        _mainTagPrefix = _showMainTag ? Colorize(config.Tag.MainQuest, config.Color.MainQuest) : null;
        _lightTagPrefix = _showLightTag ? Colorize(config.Tag.LightKeeper, config.Color.LightKeeperQuest) : null;
    }

    /// <summary>
    /// 给 locale 表里每种语言挂一个 transformer，**每种语言只挂一次**，返回本次真正新挂的数量。
    /// 早先版本没有门控，三个时点各挂一遍，累计每语言 6 个；而 LazyLoad 每次读取都重跑全部
    /// transformer，于是每次 locale 读取要白跑 6 遍全表注入。补扫新增的 locale 键不再靠补挂解决 ——
    /// 它靠的是响应末段那道 Postfix 补齐（见 ChainHintLocalePostfix），那才是最后一道。
    /// </summary>
    public int Register(LocaleTable localeTable)
    {
        var added = 0;

        foreach (var (lang, lazyLoad) in localeTable.Global)
        {
            if (lazyLoad is null)
            {
                continue;
            }

            lock (RegisterLock)
            {
                if (!RegisteredLanguages.Add(lang))
                {
                    continue;
                }

                // 闭包只捕获语言代码（用于诊断日志区分「哪种语言的描述键还没到位」），不捕获 lazyLoad。
                var label = lang;
                lazyLoad.AddTransformer(locale => Inject(label, locale));
            }

            added++;
        }

        return added;
    }

    /// <summary>
    /// 响应末段补齐：由 ChainHintLocalePostfix 在 LocaleService.GetLocaleDb 返回之后调用。
    /// 该时点晚于全部 transformer，因此不受「后注册者覆盖」的影响。与 transformer 共用同一套注入逻辑，
    /// 重复调用是幂等的（每次都先剥掉上一次加进去的块）。
    /// </summary>
    public void Enhance(string lang, Dictionary<string, string> locale)
    {
        // GetLocaleDb 返回的就是 LazyLoad.Value 对应的那本字典；
        // 万一不是同一种字典（理论上不会）就静默跳过，绝不改别人的表。
        if (locale is not GlobalLocaleDictionary typed)
        {
            return;
        }

        // ★两段都在 try 里，不能把 RefreshIfQuestTableGrew 提到 try 外面：
        //   它会重建链条快照，而重建要遍历别的 mod 共享的 AvailableForStart 列表；
        //   撞上另一个 mod 正在改任务表就抛 InvalidOperationException。Harmony 不吞异常，
        //   逃出去会打在 /client/locale/<lang> 上——整个语言包请求 500，而不是少一行链文本。
        try
        {
            // 末段补齐是最后一道写入，顺带在这里对一次表：起服过程中别的 mod 会运行期注册任务，
            // 而游戏只在启动时拉一次 locale。补扫新增的链条若只在宿主的三个时点上重建就会整批漏掉
            // （实测火神重工 102 条任务全无链条即因此）。这里只读一个 Count：没变就什么都不做，
            // 变了一次才重建 —— 等于把「每轮无条件重建」换成「表真的变大才重建」。
            RefreshIfQuestTableGrew();
            InjectCore(lang, typed);
        }
        catch (Exception ex)
        {
            _log.ReportThrottled("locale 响应末段补齐失败（不影响 transformer 注入）", ex);
        }
    }

    /// <summary>任务表比当前快照大就重建。只比数量，不遍历，代价是一次 O(1) 读。</summary>
    private void RefreshIfQuestTableGrew()
    {
        var current = _templates.Quests.Count;

        lock (_dataLock)
        {
            if (_data is not null && _data.QuestIds.Length == current)
            {
                return;
            }

            _data = BuildData();
        }
    }

    /// <summary>
    /// 按**当前**任务表强制重建链条快照，不等下一次 locale 构建。
    ///
    /// ★这是本模块并入任务改写宿主之后的核心收益点，也是踩坑得来的硬约束：
    /// 快照取到的是「最新表」，但取到的时机不保证在改写之后；任务表刚被改写完（灯塔前置清空 /
    /// 收藏家前置重建）必须立刻重建，前置/后续判定才吃得到改写后的表，否则玩家会看到
    /// 「任务条件已经放开、界面还写着要完成前置任务」的矛盾。调 Harmony 的 TypePriority 没有用。
    ///
    /// 幂等：只是丢弃缓存并重建，重复调用无害。
    /// </summary>
    public ChainHintData RebuildDataNow()
    {
        lock (_dataLock)
        {
            _data = BuildData();
            return _data;
        }
    }

    /// <summary>链条数据按需构建。注入路径上不会主动作废它 —— 重建只发生在 <see cref="RebuildDataNow"/>。</summary>
    private ChainHintData Data
    {
        get
        {
            lock (_dataLock)
            {
                return _data ??= BuildData();
            }
        }
    }

    private ChainHintData BuildData()
    {
        var data = ChainHintData.Build(_templates.Quests);

        // 日志只在「任务模板数变化」时打。任务表在起服过程中会扩容（各 mod 运行期注册任务），
        // 每次重建都打就是刷屏；而这些数字只在排查链条回退时才需要，所以降为 Diagnostic。
        var count = data.QuestIds.Length;
        if (count != _loggedQuestCount)
        {
            var suffix = _loggedQuestCount >= 0 ? $"（上次 {_loggedQuestCount} 个）" : string.Empty;
            _loggedQuestCount = count;

            var rejected = data.RejectedByStatusSamples.Count;
            _log.Diagnostic(
                $"链条数据已生成/重建：任务模板 {count} 个{suffix}"
                + $" | Quest 型条件状态分布 [{FormatHistogram(data.QuestConditionStatusHistogram)}]"
                + $" | Target 形态 [{FormatHistogram(data.QuestConditionTargetShape)}]"
                // 0 条时不拼尾巴：没有信息量的「被状态筛掉 0 条」只是噪音
                + (rejected > 0 ? $" | 被状态筛掉 {rejected} 条" : string.Empty));
        }

        return data;
    }

    private GlobalLocaleDictionary? Inject(string lang, GlobalLocaleDictionary? locale)
    {
        if (locale is null)
        {
            return null;
        }

        // ★这里刻意**不**作废链条快照。早先版本每轮注入都重建一次，理由是「任务表与 locale 条目是
        // 两条独立的时间线，只按模板数变化重建会漏掉后来进表的任务」；但那是在每语言挂 6 个
        // transformer 的前提下成立的 —— 6 轮注入里每轮重建一次。现在每语言只挂 1 个，
        // 重建改由宿主三个时点的 RebuildDataNow 收口，既覆盖同样的场景又不白付 6 倍。
        try
        {
            return InjectCore(lang, locale);
        }
        catch (Exception ex)
        {
            _log.ReportThrottled("注入失败，已跳过本轮（locale 保持原样）", ex);
            return locale;
        }
    }

    private GlobalLocaleDictionary InjectCore(string lang, GlobalLocaleDictionary locale)
    {
        // 全程只取一次快照。旧写法在循环里反复访问 Data 属性，每条任务要过 4 次锁，
        // 1900 条就是 7600 次无谓的 lock/unlock。
        var data = Data;
        var questIds = data.QuestIds;

        var injected = 0;
        var noChain = 0;
        var noDescription = 0;
        List<string>? missingSamples = null;
        List<string>? noChainSamples = null;
        // ★必须初始化，不能留 null：下面两处 Tally 是在 _detailed（LogLevel=full 或旧开关 ConsoleLog=true）
        //   分支里调的，而 ConsoleLog:true 恰恰是老配置里最常见的写法。留 null 的话第一个无链条的任务
        //   就会在 Tally 里 NRE，被外层 catch 成「注入失败，已跳过本轮」——链条文本整体不出现。
        Dictionary<string, int>? noDescByPrefix = new(StringComparer.Ordinal);
        Dictionary<string, int>? noChainByPrefix = new(StringComparer.Ordinal);

        foreach (var questId in questIds)
        {
            if (IsSkipped(data, questId))
            {
                continue;
            }

            // 先拼链条文本、再查描述键：这样能把「本该注入但 locale 里还没有描述键」与
            // 「这条任务本来就没有等级/前置/后续」分开数 —— 两类问题的修法完全不同。
            var block = BuildBlock(data, questId, locale);
            if (block is null)
            {
                noChain++;

                if (_detailed)
                {
                    Tally(noChainByPrefix!, questId);
                    AddSample(ref noChainSamples, questId);
                }

                continue;
            }

            var descriptionKey = string.Concat(questId, " description");
            if (!locale.TryGetValue(descriptionKey, out var description) || string.IsNullOrEmpty(description))
            {
                noDescription++;

                if (_detailed)
                {
                    Tally(noDescByPrefix!, questId);
                    AddSample(ref missingSamples, questId);
                }

                // ★这里不能替它建键。实测建键会把运行期任务的描述压成「只有链条文本」的短串，
                // 而且仍会被排在更后面的提供者盖掉，等于既丢了原描述又不生效。
                continue;
            }

            // 先剥掉上一次注入的行再写：InsertPosition=suffix 时链条块与武器块两个截块区间会交叉，
            // 不剥离就会一轮一轮叠加出重复文本（见 StripOwnBlock）。
            var original = StripOwnBlock(description);
            locale[descriptionKey] = _isSuffix ? original + "\n" + block : block + "\n" + original;
            injected++;
        }

        // 任务名标签必须等所有链条文本拼完再上：链条文本要读**别的**任务的名字，
        // 提前打标签会让「后续任务：」里出现带标签的名字。
        if (_showMainTag || _showLightTag || _taggedAnyName)
        {
            foreach (var questId in questIds)
            {
                if (!IsSkipped(data, questId))
                {
                    ApplyNameTag(data, locale, questId);
                }
            }
        }

        // 只有形态变化才落日志。任务表扩容期间各 mod 的描述键陆续到位，这些数字几乎每轮都在抖，
        // 逐轮打出来全是没有行动价值的中间态。
        var shape = string.Concat(lang, "|", questIds.Length, "|", injected, "|", noDescription, "|", noChain);
        if (!RememberShape(shape))
        {
            return locale;
        }

        var round = Interlocked.Increment(ref _rounds);

        if (_detailed)
        {
            _log.Diagnostic(
                $"[{lang}] 链条提示注入：任务 {questIds.Length} 个，已注入 {injected}，缺描述键 {noDescription}{FormatPrefixes(noDescByPrefix)}"
                + $"，缺链条信息 {noChain}{FormatPrefixes(noChainByPrefix)}"
                + FormatSamples(missingSamples)
                + FormatSamples(noChainSamples));
        }

        // Summary 档：只在「已注入数创全局新高」时留一行。创新高保住了「注入在推进」这条主线，
        // 又不至于把扩容期间的中间态全打出来。用 CAS 而不是普通字段：并发下「刷新纪录」
        // 这个动作必须恰好发生一次，否则多客户端同时进来会各打一行。
        var best = Volatile.Read(ref _bestInjected);
        while (injected > best)
        {
            if (Interlocked.CompareExchange(ref _bestInjected, injected, best) == best)
            {
                _log.Summary(
                    $"[{lang}] 注入：任务 {questIds.Length} 个，已注入 {injected}，缺描述键 {noDescription}，缺链条信息 {noChain}（本次起服第 {round} 轮）");
                break;
            }

            best = Volatile.Read(ref _bestInjected);
        }

        return locale;
    }

    /// <summary>拼出该任务要注入的整块链条文本；返回 null 表示这条任务没有任何可显示的链条信息。</summary>
    private string? BuildBlock(ChainHintData data, string questId, GlobalLocaleDictionary locale)
    {
        List<string>? lines = null;

        if (_config.ShowLevelRequirement && data.Levels.TryGetValue(questId, out var level) && level > 0)
        {
            lines = new List<string>(3);
            lines.Add(Colorize(string.Format(_levelTemplate, level), _config.Color.QuestLevel));
        }

        if (_config.ShowPrerequisite)
        {
            var text = BuildNameList(data, data.Prerequisites, questId, _config.MaxPrerequisiteShown, locale, false);
            if (text is not null)
            {
                (lines ??= new List<string>(3)).Add(Colorize(_config.Text.Prerequisite + text + _config.Text.Ending, _config.Color.PreQuest));
            }
        }

        if (_config.ShowUnlock)
        {
            var text = BuildNameList(data, data.Unlocks, questId, _config.MaxUnlockShown, locale, _config.ShowUnlockLevel);
            if (text is not null)
            {
                (lines ??= new List<string>(3)).Add(Colorize(_config.Text.Unlock + text + _config.Text.Ending, _config.Color.UnlockQuest));
            }
        }

        return lines is null ? null : string.Join("\n", lines);
    }

    private string? BuildNameList(
        ChainHintData data,
        IReadOnlyDictionary<string, string[]> map,
        string questId,
        int max,
        GlobalLocaleDictionary locale,
        bool withLevel)
    {
        var ids = Expand(map, questId);
        if (ids.Length == 0)
        {
            return null;
        }

        var names = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (max > 0 && names.Count >= max)
            {
                break;
            }

            names.Add(FormatPeer(data, id, locale, withLevel));
        }

        return names.Count == 0 ? null : string.Join(_config.Text.Separator, names);
    }

    /// <summary>链条里的一个上下游任务：任务名（+商人名）（+等级）。</summary>
    private string FormatPeer(ChainHintData data, string id, GlobalLocaleDictionary locale, bool withLevel)
    {
        var entry = "「"
            + (locale.TryGetValue(string.Concat(id, " name"), out var raw) && !string.IsNullOrEmpty(raw) ? CleanName(raw) : id)
            + "」";

        if (_config.ShowTraderName
            && data.Traders.TryGetValue(id, out var traderId)
            && locale.TryGetValue(string.Concat(traderId, " Nickname"), out var nickname)
            && !string.IsNullOrEmpty(nickname))
        {
            entry += "(" + CleanName(nickname) + ")";
        }

        if (withLevel && data.Levels.TryGetValue(id, out var level) && level > 0)
        {
            entry += string.Format(_unlockLevelSuffix, level);
        }

        return entry;
    }

    /// <summary>ChainDepth = 0 只看直接上下游；&gt;0 再往上/下多展开相应层数。</summary>
    private string[] Expand(IReadOnlyDictionary<string, string[]> map, string questId)
    {
        var direct = GetList(map, questId);
        if (direct.Length == 0)
        {
            return NoIds;
        }

        var maxRounds = _config.ChainDepth <= 0 ? 1 : _config.ChainDepth + 1;
        if (maxRounds == 1)
        {
            // 不展开时直接回快照里的数组：它本来就是只读的，没必要复制一份。
            return direct;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { questId };
        var result = new List<string>(direct.Length);
        var frontier = direct;

        for (var round = 0; round < maxRounds && frontier.Length > 0; round++)
        {
            var isLastRound = round == maxRounds - 1;
            var next = new List<string>();

            foreach (var id in frontier)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                result.Add(id);

                if (isLastRound)
                {
                    continue;
                }

                foreach (var nested in GetList(map, id))
                {
                    if (!seen.Contains(nested))
                    {
                        next.Add(nested);
                    }
                }
            }

            frontier = next.ToArray();
        }

        return result.Count == 0 ? NoIds : result.ToArray();
    }

    private void ApplyNameTag(ChainHintData data, GlobalLocaleDictionary locale, string questId)
    {
        var nameKey = string.Concat(questId, " name");
        if (!locale.TryGetValue(nameKey, out var name) || string.IsNullOrEmpty(name))
        {
            return;
        }

        var clean = StripTagPrefix(name);
        string? prefix = null;

        if (_showMainTag && data.KappaPreQuests.Contains(questId))
        {
            prefix = _mainTagPrefix;
        }
        else if (_showLightTag && data.LightKeeperPreQuests.Contains(questId))
        {
            prefix = _lightTagPrefix;
        }

        if (prefix is not null)
        {
            locale[nameKey] = prefix + clean;
            _taggedAnyName = true;
        }
        else if (!string.Equals(clean, name, StringComparison.Ordinal))
        {
            // 这条任务上一轮带过标签、这一轮不再属于任何集合：把残留摘掉，否则标签会永久留在名字上。
            locale[nameKey] = clean;
        }
    }

    private string StripTagPrefix(string name)
    {
        if (_mainTagPrefix is not null && name.StartsWith(_mainTagPrefix, StringComparison.Ordinal))
        {
            return name[_mainTagPrefix.Length..];
        }

        if (_lightTagPrefix is not null && name.StartsWith(_lightTagPrefix, StringComparison.Ordinal))
        {
            return name[_lightTagPrefix.Length..];
        }

        return name;
    }

    /// <summary>
    /// 剥掉描述里属于本模块的行 —— **注入幂等的实现点**。
    ///
    /// transformer 与响应末段的 Postfix 会先后各写一次；InsertPosition=suffix 时链条块与武器块
    /// 两个截块区间还会交叉。不按 marker 头尾剥离自己上次注入的行，文本就会一轮轮叠加。
    /// 判据是「颜色是本模块配置的三种之一」且「颜色开标签后紧跟本模块的文案前缀」，
    /// 两个条件缺一不可，免得把别的 mod 用同色写的行误删。
    /// </summary>
    private string StripOwnBlock(string description)
    {
        // 快路径：绝大多数任务的描述首行尾行都不是本模块写的。
        // 先做两次纯比较判定，命中不了就原样返回，省掉整串 Split/Join 的复制。
        if (!IsOwnInjectedLine(FirstLine(description)) && !IsOwnInjectedLine(LastLine(description)))
        {
            return description;
        }

        var parts = description.Split('\n');
        var start = 0;
        var end = parts.Length;

        while (start < end && IsOwnInjectedLine(parts[start]))
        {
            start++;
        }

        while (end > start && IsOwnInjectedLine(parts[end - 1]))
        {
            end--;
        }

        return string.Join("\n", parts, start, end - start);
    }

    private bool IsOwnInjectedLine(string line)
    {
        for (var i = 0; i < _ownColorTags.Length; i++)
        {
            var tag = _ownColorTags[i];
            if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line[tag.Length..].StartsWith(_ownLineHeads[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSkipped(ChainHintData data, string questId)
    {
        if (_blackList.Count > 0 && _blackList.Contains(questId))
        {
            return true;
        }

        if (_skipTraders.Count == 0)
        {
            return false;
        }

        return data.Traders.TryGetValue(questId, out var traderId) && _skipTraders.Contains(traderId);
    }

    private bool RememberShape(string shape)
    {
        lock (_shapeLock)
        {
            if (!_seenShapes.Add(shape))
            {
                return false;
            }

            // 超出上限淘汰最早的，**不能** Clear()：一清空，打过的签名会被当成新的再打一轮，
            // 恰好复现这次要消灭的那种重复刷屏。
            _shapeOrder.Enqueue(shape);
            while (_shapeOrder.Count > MaxShapes)
            {
                _seenShapes.Remove(_shapeOrder.Dequeue());
            }

            return true;
        }
    }

    private static void AddOwnMarker(List<string> colorTags, List<string> lineHeads, string? color, string? head)
    {
        // 头部为空时不能收进来：StartsWith("") 恒为真，会把同色别的行全判成自己写的。
        if (string.IsNullOrEmpty(head))
        {
            return;
        }

        colorTags.Add(ColorizeOpenTag(color));
        lineHeads.Add(head!);
    }

    private static void AddSample(ref List<string>? samples, string questId)
    {
        if (samples is null)
        {
            samples = new List<string>(MaxSamples);
        }

        if (samples.Count < MaxSamples)
        {
            samples.Add(questId);
        }
    }

    private static string[] GetList(IReadOnlyDictionary<string, string[]> map, string key)
    {
        return map.TryGetValue(key, out var list) ? list : NoIds;
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    private static string LastLine(string text)
    {
        var index = text.LastIndexOf('\n');
        return index < 0 ? text : text[(index + 1)..];
    }

    private static void Tally(Dictionary<string, int> counter, string questId)
    {
        var prefix = questId.Length >= 6 ? questId[..6] : questId;
        counter[prefix] = counter.TryGetValue(prefix, out var n) ? n + 1 : 1;
    }

    /// <summary>前缀计数压成一行，只列数量最多的 4 组。分桶是因为自研与各 mod 的任务共享 id 前缀。</summary>
    private static string FormatPrefixes(Dictionary<string, int>? counts)
    {
        if (counts is null || counts.Count == 0)
        {
            return string.Empty;
        }

        var top = new List<KeyValuePair<string, int>>(4);
        foreach (var entry in counts)
        {
            var at = 0;
            while (at < top.Count && (top[at].Value > entry.Value
                                      || (top[at].Value == entry.Value && string.CompareOrdinal(top[at].Key, entry.Key) <= 0)))
            {
                at++;
            }

            if (at >= 4)
            {
                continue;
            }

            top.Insert(at, entry);
            if (top.Count > 4)
            {
                top.RemoveAt(top.Count - 1);
            }
        }

        var parts = new string[top.Count];
        for (var i = 0; i < top.Count; i++)
        {
            parts[i] = top[i].Key + "=" + top[i].Value;
        }

        return "（" + string.Join(", ", parts) + "）";
    }

    private static string FormatSamples(List<string>? samples)
    {
        return samples is { Count: > 0 } ? "；样例 [" + string.Join(", ", samples) + "]" : string.Empty;
    }

    private static string FormatHistogram(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return string.Empty;
        }

        var parts = new string[counts.Count];
        var i = 0;
        foreach (var entry in counts)
        {
            parts[i++] = entry.Key + "×" + entry.Value;
        }

        return string.Join(", ", parts);
    }

    private static string Colorize(string text, string color)
    {
        return ColorizeOpenTag(color) + text + "</b></color>";
    }

    private static string ColorizeOpenTag(string? color)
    {
        return "<color=#" + SanitizeColor(color) + "><b>";
    }

    private static string SanitizeColor(string? color)
    {
        // 容忍用户写成 #FF6666；全空时给白色而不是让正则整体失配。
        return string.IsNullOrWhiteSpace(color) ? "FFFFFF" : color.Trim().TrimStart('#');
    }

    private static string PrefixOf(string template)
    {
        var index = template.IndexOf('{');
        return index <= 0 ? template : template[..index];
    }

    private static string SafeFormat(string? template, string fallback)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return fallback;
        }

        try
        {
            _ = string.Format(template, 0);
            return template;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// 任务名/商人名在拼进链条文本前先清成纯文本：去掉富文本标签，再去掉别的 mod
    /// （如 MGMod 的【Road of 3X4】）追加的标记文字。
    /// 清干净之后，输出就不依赖各 mod transformer 的注册顺序了。
    /// </summary>
    private string CleanName(string name)
    {
        var cleaned = RichTextTag.Replace(name, string.Empty);

        foreach (var marker in _config.NameStripTexts)
        {
            if (!string.IsNullOrEmpty(marker))
            {
                cleaned = cleaned.Replace(marker, string.Empty, StringComparison.Ordinal);
            }
        }

        return cleaned.Trim();
    }
}
