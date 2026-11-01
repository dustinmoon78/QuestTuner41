// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTuner41.WeaponPool;

/// <summary>
/// 武器池一遍执行的结果（★宿主接线点：本工作区日志铁律是「默认只留 1 行汇总」，
/// 所以本模块不逐项打日志，所有要展示/对账的数字都从本对象取）。
/// 本遍新增的武器 ID 由 <see cref="KeptWeaponIds"/> 交给
/// <c>FixupRunner.KeepWeaponIds</c>，这是「先加武器、后清死引用」顺序契约的一半。
/// </summary>
public sealed class WeaponPoolReport
{
    /// <summary>本遍写进任务条件 <c>Kills.weapon</c> 的武器条数（幂等：重复跑不重复计；dryRun 时=「本遍本会写入」的条数）。</summary>
    public int Added { get; set; }

    /// <summary>config 手工映射组数（weaponPool.json 的 <c>weapons</c> 段条数）。</summary>
    public int Groups { get; set; }

    /// <summary>本遍自动新建的映射组数（任务里缺映射的基础武器 → 新建组）。</summary>
    public int AutoGroups { get; set; }

    /// <summary>自动新建组覆盖的目标数（候选武器条数）。</summary>
    public int AutoTargets { get; set; }

    /// <summary>自动扫描到的克隆映射条数（含 export 补缺；未扫描时为 0）。</summary>
    public int CloneMappings { get; set; }

    /// <summary>自动扫描跳过的坏文件数。</summary>
    public int SkippedFiles { get; set; }

    /// <summary>DWV 运行时生成 ID（不参与池与自动映射）的条数。</summary>
    public int ExcludedDwvIds { get; set; }

    /// <summary>同型号兜底（v1.4.0）新建的映射组数：按 <c>Item.Name</c> 同名分组的枪械组数（组内 ≥2 才计）。</summary>
    public int SameModelGroups { get; set; }

    /// <summary>同型号兜底写进映射的目标条数（各组「组内其它成员」累计）。</summary>
    public int SameModelTargets { get; set; }

    /// <summary>本进程是否真的执行过扫描（false=复用上一次扫描结果）。</summary>
    public bool ScanPerformed { get; set; }

    /// <summary>本次扫描耗时（毫秒；复用缓存时为 0）。</summary>
    public long ScanElapsedMs { get; set; }

    /// <summary>自动扫描给「既有手工组」追加的组数（后装 mod 的武器克隆自已覆盖基础）。</summary>
    public int AppendedGroups { get; set; }

    /// <summary>自动扫描给「既有手工组」追加的目标数。</summary>
    public int AppendedTargets { get; set; }

    /// <summary>
    /// config 里缺映射的「任务武器 ID」（24 位 hex，= ANWQ 的 <c>needAddWeapons</c> 语义，
    /// 而不是显示名；导出的配置片段以 ID 作键，名只是 <c>Name:</c> 占位符）。
    /// </summary>
    public List<string> Missing { get; } = new();

    /// <summary>
    /// ★顺序契约产物：本遍写入任务条件的全部武器 ID + 它们依赖的克隆基底 ID。
    /// 宿主必须在 <c>FixupRunner</c> 的 prune 之前调用 <c>FixupRunner.KeepWeaponIds(KeptWeaponIds)</c>；
    /// 否则 prune 会把「item 表里还没落地/运行时生成」的新增武器当死引用静默删掉。
    /// </summary>
    public HashSet<string> KeptWeaponIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>questId → 该任务可用的「模组武器名」（已排序；接任务清单消息用）。</summary>
    public Dictionary<string, List<string>> AcceptLists { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>questId → 去富文本的任务名（清单消息标题用；查不到时是「本任务」）。</summary>
    public Dictionary<string, string> AcceptQuestNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★宿主接线点（2026-10-11 任务名「武器已优化」标记）：本遍**确实往该任务的完成条件里
    /// <c>Weapon.Add</c> 了至少一把武器**的任务 ID 集合。
    /// ★与 <see cref="AcceptLists"/> 的口径不同，不要混用：后者门控是「任务有模组武器可用」
    ///   （<c>modWeaponNames.Count &gt; 0</c>），一个早已列全模组枪、池里啥也没加的任务同样会进去；
    ///   本集合只收真正被 Add 过的任务 —— 这正是「武器优化」标记要的口径。
    /// 幂等：第二遍遍历与后续轮次的 <c>isNew</c> 恒 false，故只增不减、跨轮次累积（与 KeptWeaponIds 同语义）。
    /// </summary>
    public HashSet<string> AddedQuestIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// localeKey（<c>{questId} description</c>）→ 可用武器名列表（同一任务多次 Run 会累积/覆盖为最新一遍的结果）。
    ///
    /// ★宿主接线点（重要）：把这段文本写进 locale **不能**直接写
    /// <c>LocaleTable.Global[lang].Value[key]</c> —— 那个字典每次访问都会重新反序列化，写完就丢。
    /// 必须走 <c>LazyLoad.AddTransformer</c>，在 transformer 里对每个 key 调
    /// <see cref="WeaponPoolRunner.BuildDescriptionText"/>（它自带锚点幂等：先截旧块再重拼）；
    /// key 在 locale 里不存在或为空时跳过（ANWQ 同语义）。
    /// </summary>
    public Dictionary<string, List<string>> DescriptionMods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 任务<b>原始</b>要求的武器名（扩充之前）。与 <see cref="DescriptionMods"/> 同键、可能为空。
    /// 描述块用它单独起一行「本任务要求使用：…」，把原版没点名的枪名补出来。
    /// </summary>
    public Dictionary<string, List<string>> RequiredMods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>任务表里出现过的全部武器 ID（字典序；<c>outputAllWeapons</c> 用）。</summary>
    public List<string> AllSeenWeapons { get; } = new();
}

/// <summary>
/// AddNewWeaponsToQuest41 武器池模块（P1 并入 QuestTuner41）：
/// ① 按 config 手工映射 + 自动扫描克隆映射，构建「枪械 / 刀 / 投掷物」池；
/// ② 遍历任务 <c>AvailableForFinish</c> 的 <c>Kills.weapon</c>，把可用武器 ID 追加进去（HashSet 天然幂等）；
/// ③ 收集描述用的武器名、口径条件的合格武器名、接任务清单消息的候选名。
///
/// 与 ANWQ 的 <c>Run()/AutoAugmentPools()/TraverseAndApply()</c> 等价，差异只在这些地方：
/// <list type="bullet">
///   <item>不做 locale transformer 注册、不写 wwwroot 网页、不发导出文件、不写任何盘（只产数据）；</item>
///   <item>modsDir / 自身目录名 / 日志器由构造参数传入（原来是写死的 cwd 与硬编码目录名）；</item>
///   <item>locale 字典「每遍只解析一次」（LazyLoad.Value 每次访问都会重新反序列化整份 locale，
///         ANWQ 是每次缓存未命中就解析一次，属热路径浪费）；</item>
///   <item>不再逐项打日志：明细只在 <see cref="WeaponPoolConfig.ConsoleLogEnabled"/> 时走 ILogger.Debug（不落盘）。</item>
/// </list>
///
/// 线程安全：三层时点可能在不同线程跑，跨遍复用的缓存（扫描/池/kind/名字/口径）都是并发字典或加锁构建；
/// 本遍的累加集合统一用 <c>_stateLock</c> 保护（与 ANWQ 的 <c>_descLock</c> 同义）。
/// </summary>
public sealed class WeaponPoolRunner
{
    /// <summary>config 里「只给名字不给 ID」的占位符前缀（ANWQ 同值）。</summary>
    public const string ConfigNamePrefix = "Name:";

    /// <summary>描述块截块锚点（必须带注入格式，只用「可用武器」四个字会误伤原文含该词的任务描述）。</summary>
    public const string AvailableWeaponsBlockAnchor = "<color=yellow><i>可用武器：";

    /// <summary>清单消息标题取不到任务名时的兜底。</summary>
    public const string MessageListTitleFallback = "本任务";

    private const string WeaponClassId = "5422acb9af1c889c16000029";
    private const string KnifeClassId = "5447e1d04bdc2dff2f8b4567";
    private const string ThrowClassId = "543be6564bdc2df4348b4568";

    private readonly ISptLogger<WeaponPoolRunner> _logger;
    private readonly TemplateTable _templates;
    private readonly LocaleTable _localeTable;
    private readonly string _modsDir;
    private readonly string _selfModDirName;

    // ---- 跨时点复用（等价于 ANWQ 单例 mod 的字段）----
    private AutoScan.ScanResult? _scan;
    private string? _weaponRootId;
    private string? _knifeRootId;
    private string? _throwRootId;
    private Dictionary<string, string>? _poolGun;
    private List<string>? _poolKnife;
    private List<string>? _poolGrenade;
    private string _poolSignature = string.Empty;

    private readonly ConcurrentDictionary<string, string> _kindCache = new(StringComparer.OrdinalIgnoreCase);

    // ★必须区分大小写：物品名键是「{id} Name」、任务名键是「{id} name」，忽略大小写会互相覆盖
    private readonly ConcurrentDictionary<string, string> _localeCache = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _localeDb;
    private string? _localeDbLang;
    private bool _localeMissingWarned;

    private readonly object _caliberLock = new();
    private Dictionary<string, List<string>>? _caliberMap;

    // ---- 跨时点累加（ANWQ 的三个共享集合，供后注册的 transformer / 请求线程读）----
    private readonly object _stateLock = new();
    private readonly Dictionary<string, HashSet<string>> _descNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 任务<b>原始</b>要求的武器 ID 列表（快照于任何注入之前）。
    /// ★为什么要单列：<see cref="_descNames"/> 装的是「任务条件里最终允许的全部武器」，
    /// 而本 mod 会把自动扫描到的模组武器并进去 —— 起服实测 +27655 条。扩充之后，
    /// 「任务原本要求用哪把枪」在几百条名字里根本找不着（实测 SOCOM「杀手 - Scav」
    /// 原始只要求全自动 AK-5C 一把，扩充后清单是一长串）。
    /// 而 BSG 原版描述只写「拿上这把枪去干掉几个拾荒者」，从头到尾不点名 ——
    /// 玩家在单机 / 新档 / 乱序接任务时根本无从得知该用哪把枪，本 mod 自动补一行明确出来。
    /// ★存 ID 而非名字：名字会被染色 mod 改写（ItemNameTint41 会在物品名里插 &lt;color=…&gt;），
    ///   且同名不同色号的枪靠名字无法区分；ID 稳定，输出时才按当时 locale 解析。
    /// ★快照必须在遍历注入<b>之前</b>取（见 TraverseAndApply 内 requiredIds 局部变量），
    ///   取晚了会连扩充枪一起快照进去 —— 这正是 v1.1.1 莲花商人「本任务要求使用」
    ///   混进几百把扩充枪的原因。
    /// </summary>
    private readonly Dictionary<string, List<string>> _descRequiredIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原始白名单快照是否已抓过一次（只在本进程第一轮 Run 抓，见 Run 开头说明）。</summary>
    private volatile bool _requiredSnapshotDone;
    private readonly Dictionary<string, List<string>> _acceptLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _acceptQuestNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>被池真正 Add 过枪的任务 ID（跨轮次累积；见 <see cref="WeaponPoolReport.AddedQuestIds"/>）。
    /// ★用 <see cref="StringComparer.Ordinal"/> 而非 IgnoreCase（审查 P2-G-2）：这个集合最终要拼成
    ///   locale 键「&lt;questId&gt; name」，而宿主与 locale 字典都是 Ordinal 比较。三处口径必须一致，
    ///   否则一旦有非小写 24hex 的 questId 混进来就会静默失配（标记不生效且无日志）。
    ///   当前依赖的隐式前提：<c>MongoId.ToString()</c> 恒返回小写 24 位 hex。</summary>
    private readonly HashSet<string> _addedQuestIds = new(StringComparer.Ordinal);

    /// <summary>DryRun 下去重「(任务, 武器)」对，供 <see cref="WeaponPoolReport.Added"/> 计数用（审查 P2-G-3）。
    /// ★用元组而非 "questId|weaponId" 拼接串（审查 P3-8）：newWeapons 的键只排除 Name: 前缀，
    ///   理论上可含 | 从而撞键；元组无此面。</summary>
    private readonly HashSet<(string Quest, string Weapon)> _dryRunSeenWeapons = new();
    private readonly HashSet<string> _allSeenWeapons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keptWeaponIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 任务名两族标记后缀（宿主在配置加载后注入，见 Entry.RunWeaponPool）。
    /// 仅用于把标记从**接取清单消息标题**里剥掉（审查 P3-2/E-1）。元素可为空串（＝该族关闭）。
    /// ★初始化期写一次、之后只读；不要运行期改。
    /// </summary>
    public string[] NameMarkers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 剥掉任务名尾部本 mod 自己挂的标记（可重复出现），供接取清单消息标题用。
    /// ★为什么消费端还要再剥一次：<c>GetLocaleValue</c> 走 <see cref="_localeCache"/>，首次命中即永久缓存。
    ///   若某任务首次进入「有模组武器可用」分支发生在启动末段/延迟补扫（此时标记已写进 locale），
    ///   缓存到的就是带标记的名字，消息标题会变成「精准猎手（武器已优化） ｜ 可用武器：…」。
    /// 与 Entry.StripNameMarkers 同语义但**独立实现**：那边按族传参（要区分「本 transformer 认识的族」），
    /// 这里只需把尾部任意一枚剥净。每任务每遍只调一次，不在 locale 热路径上。
    /// </summary>
    private string StripNameMarkers(string text)
    {
        if (NameMarkers.Length == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        // ★按长度降序试（审查 review6 P3-1）：若武器族后缀以任务族后缀结尾（如把 questNameSuffix 设成「已优化）」），
        //   先剥短的那枚会把长标记撕成残渣，残渣不再匹配任何后缀就返回了。与 Entry.StripNameMarkers 同口径。
        var ordered = NameMarkers.Where(m => !string.IsNullOrEmpty(m))
                                 .OrderByDescending(m => m.Length)
                                 .ToArray();
        if (ordered.Length == 0)
        {
            return text;
        }

        while (true)
        {
            var stripped = false;
            foreach (var marker in ordered)
            {
                if (text.EndsWith(marker, StringComparison.Ordinal))
                {
                    text = text[..^marker.Length];
                    stripped = true;
                    break;
                }
            }

            if (!stripped)
            {
                return text;
            }
        }
    }

    /// <summary>
    /// 把**累计**的「被池真正 Add 过枪」任务 ID 逐个交给 <paramref name="sink"/>。
    /// ★给宿主做异常路径兜底（审查 P2-C-4）：<see cref="Run"/> 末尾把 _addedQuestIds 拷进 report 的那段
    ///   位于正常返回路径上。若 TraverseAndApply 中途抛异常，前 N 个任务的 Weapon.Add **已经写进任务表**，
    ///   但 report 从未填充 ⇒ 那批任务永远拿不到「武器已优化」标记，且后续轮次 isNew 恒 false 补不回来。
    ///   这里读的是实例累计字段（跨轮次只增），与 report 是否填充无关，故异常路径也能拿到完整集合。
    /// </summary>
    public void ForEachAddedQuestId(Action<string> sink)
    {
        if (sink is null)
        {
            return;
        }

        // ★锁内只取快照、锁外枚举（审查 P3-9）：sink 是宿主代码，持锁调外部委托一旦发生同线程重入
        //   （例如 sink 里又触发 _addedQuestIds.Add）会抛 Collection was modified。当前 sink 只是一行
        //   TryAdd 故安全，但把锁的边界收紧到「只复制」成本为零。
        string[] snapshot;
        lock (_stateLock)
        {
            snapshot = _addedQuestIds.ToArray();
        }

        foreach (var questId in snapshot)
        {
            sink(questId);
        }
    }

    public WeaponPoolRunner(
        ISptLogger<WeaponPoolRunner> logger,
        TemplateTable templates,
        LocaleTable localeTable,
        string modsDir,
        string selfModDirName)
    {
        _logger = logger;
        _templates = templates;
        _localeTable = localeTable;
        _modsDir = modsDir;
        _selfModDirName = selfModDirName;
    }

    /// <summary>
    /// 跑一遍武器池：池构建（含自动扫描，只扫一次）→ 追加到任务条件 → 收集描述/清单数据。
    /// 三个时点各调一次即可，幂等（条件层是 HashSet，重复跑 Added 归零）。
    /// </summary>
    /// <param name="phase">时点名（仅用于 Debug 明细）。</param>
    /// <param name="quests">任务表（宿主传 <c>templates.Quests</c>）。</param>
    /// <param name="cfg">武器池配置。</param>
    /// <param name="dryRun">true=只统计不改写（不往条件里写 ID；描述/清单数据照常产出，便于预览）。</param>
    public WeaponPoolReport Run(string phase, Dictionary<MongoId, Quest> quests, WeaponPoolConfig cfg, bool dryRun = false)
    {
        var report = new WeaponPoolReport();
        if (cfg is null || quests is null)
        {
            return report;
        }

        _localeDb = ResolveLocaleDb(cfg.Lang);

        // 等价于 ANWQ 每遍 Run 开头的 LoadConfig()：config 段每遍重置，自动补齐的组每遍重新推导
        var newWeapons = CloneWeapons(cfg.NewWeapons);
        report.Groups = newWeapons.Count;
        // config 手写组的键：兜底只允许对「自动建组」的 key 做并集追加，手写组一律原样保留
        var configKeys = new HashSet<string>(newWeapons.Keys, StringComparer.OrdinalIgnoreCase);

        AutoAugmentPools(phase, cfg, newWeapons, report);

        var (added, missing) = TraverseAndApply(phase, quests, cfg, newWeapons, report, dryRun);
        report.Added = added;

        // 缺失基础 → 自动新建组 → 再遍历一遍应用（第二遍幂等，只加真正缺的）
        if (cfg.AutoScanMods && _poolGun is not null && missing.Count > 0)
        {
            var (groups, targets) = AutoNewGroups(cfg, newWeapons, missing);
            if (groups > 0)
            {
                report.AutoGroups = groups;
                report.AutoTargets = targets;
                detail(phase, cfg, $"自动映射：新增 {groups} 组 / {targets} 目标（枪械=铁律 clone==任务武器；刀/雷=同类分支）");
                // 本遍总条数 = 第一遍 + 自动建组后的第二遍（ANWQ 只把第二遍的数写进后一行日志；报告字段按「本遍改动」语义求和）
                report.Added += TraverseAndApply(phase, quests, cfg, newWeapons, report, dryRun,
                    captureRequiredSnapshot: false).Added;
            }
        }

        // ★同型号兜底必须排在 AutoNewGroups **之后**（审查 P1）：克隆映射的键是「基底」，
        //   任务点名基底时要靠 AutoNewGroups 把整个克隆族补上。若兜底先给同名基底建了 key，
        //   TraverseAndApply 的 TryGetValue 会先命中兜底那条更窄的映射，AutoNewGroups 又因
        //   ContainsKey 跳过 —— 白名单会从「整个克隆族」缩成「同名件」，玩家可见地变窄。
        //   排到最后再补一遍遍历，只对「变体侧」建 key，两边互不遮挡。
        if (ApplySameModelFallback(phase, cfg, configKeys, newWeapons, report) > 0)
        {
            report.Added += TraverseAndApply(phase, quests, cfg, newWeapons, report, dryRun,
                captureRequiredSnapshot: false).Added;
        }

        lock (_stateLock)
        {
            // 本轮原始白名单快照已收集完毕 → 后续轮次 Run 不再重取
            // （那时任务条件里的 Weapon 已被上一轮改写，取到的是扩充后的脏集合）。
            _requiredSnapshotDone = true;

            foreach (var (questId, names) in _descNames)
            {
                report.DescriptionMods[$"{questId} description"] = new List<string>(names);
            }

            foreach (var (questId, ids) in _descRequiredIds)
            {
                report.RequiredMods[$"{questId} description"] = new List<string>(ids);
            }

            foreach (var (questId, list) in _acceptLists)
            {
                report.AcceptLists[questId] = new List<string>(list);
            }

            foreach (var (questId, name) in _acceptQuestNames)
            {
                report.AcceptQuestNames[questId] = name;
            }

            foreach (var questId in _addedQuestIds)
            {
                report.AddedQuestIds.Add(questId);
            }

            foreach (var id in _allSeenWeapons.OrderBy(x => x, StringComparer.Ordinal))
            {
                report.AllSeenWeapons.Add(id);
            }

            foreach (var id in _keptWeaponIds)
            {
                report.KeptWeaponIds.Add(id);
            }
        }

        detail(phase, cfg, $"本遍追加 {report.Added} 把；映射 {report.Groups} 组（自动新建 {report.AutoGroups} 组），"
                           + $"缺失 {report.Missing.Count} 个，描述块 {report.DescriptionMods.Count} 条，清单 {report.AcceptLists.Count} 条");
        return report;
    }

    /// <summary>
    /// 自动扫描（一次）+ 既有手工组追加。追加规则与手工扩展一致：
    /// 枪械只认「克隆基底 == 组键」，后装 mod（CBP/RITC 等）克隆自已覆盖基础时自动并入。
    /// </summary>
    private void AutoAugmentPools(string phase, WeaponPoolConfig cfg, Dictionary<string, List<string>> newWeapons, WeaponPoolReport report)
    {
        if (!cfg.AutoScanMods)
        {
            return;
        }

        if (_scan is null || !string.Equals(_poolSignature, PoolSignature(cfg), StringComparison.Ordinal))
        {
            var sw = Stopwatch.StartNew();
            _scan = AutoScan.Scan(_modsDir, _selfModDirName);
            sw.Stop();
            ResolveBranchRoots();
            BuildPools(cfg);
            report.ScanPerformed = true;
            report.ScanElapsedMs = sw.ElapsedMilliseconds;
            detail(phase, cfg, $"自动扫描完成：{_scan.CloneMap.Count} 条克隆映射 / 剔除 DWV 生成 ID {_scan.ExcludedIds.Count} 条 / "
                               + $"跳过坏文件 {_scan.SkippedFiles} 个 / 耗时 {sw.ElapsedMilliseconds}ms");
        }

        if (_scan is not null)
        {
            report.CloneMappings = _scan.CloneMap.Count;
            report.SkippedFiles = _scan.SkippedFiles;
            report.ExcludedDwvIds = _scan.ExcludedIds.Count;
        }

        if (_poolGun is null)
        {
            return;
        }

        var appendGroups = 0;
        var appendTargets = 0;
        foreach (var (key, list) in newWeapons)
        {
            if (KindOf(key) != "gun")
            {
                continue;
            }

            var existing = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            var cands = _poolGun
                .Where(p => p.Value == key && !existing.Contains(p.Key))
                .Select(p => p.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (cands.Count == 0)
            {
                continue;
            }

            list.AddRange(cands);
            appendGroups++;
            appendTargets += cands.Count;
        }

        report.AppendedGroups = appendGroups;
        report.AppendedTargets = appendTargets;
        if (appendGroups > 0)
        {
            detail(phase, cfg, $"自动映射：既有组追加 {appendGroups} 组 / {appendTargets} 目标（后装 mod 武器克隆自已覆盖基础）");
        }
    }

    /// <summary>
    /// 同型号兜底（v1.4.0）：任务若直接点名一个「变体」（mod 自建枪，或自称克隆的枪），
    /// 克隆映射的键永远是基底 ⇒ <see cref="TraverseAndApply"/> 里的 <c>newWeapons.TryGetValue</c>
    /// 永远匹配不到它，池子对这一条扩不动、也不会挂「（武器已优化）」标记。
    ///
    /// 这里按物品模板内部名 <c>Item.Name</c>（= items.json 的 <c>_name</c>）把同名武器归为一组，
    /// 组内**双向**互相放行：既让「要求自制枪」的任务接受原版同型号，也让「要求原版枪」的任务接受自制同型号。
    ///
    /// 为什么不走 origin / _proto：实测 Katarina Black 这批自制枪的 <c>origin</c> 全空、
    /// <c>_proto</c> 指向错位（AKM → M4A1、AXMC → 一个配件 id），照它建映射等于把任务放宽到无关的枪。
    ///
    /// 已存在的映射（config 手写组、自动建组）一律优先，不覆盖。
    /// </summary>
    /// <summary>
    /// 取容器 prefab 里的武器 bundle 名（如 <c>weapon_colt_m4a1_556x45</c>）；不是容器 prefab 时返回 null。
    /// 不用正则：路径形如 <c>assets/content/weapons/m4a1/weapon_colt_m4a1_556x45_container.bundle</c>，
    /// 取文件名去掉 <c>_container.bundle</c> 后缀即可，省掉一层转义坑。
    /// </summary>
    private static string? PrefabBundleOf(TemplateItem item)
    {
        // SPT 的 Prefab.Path 一律用正斜杠，不需要再做反斜杠归一
        var path = item.Properties?.Prefab?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        const string suffix = "_container.bundle";
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var slash = path.LastIndexOf('/');
        var file = slash >= 0 ? path[(slash + 1)..] : path;
        var bundle = file[..^suffix.Length];
        return bundle.Length > 0 ? bundle : null;
    }

    /// <returns>
    /// 本遍新建了映射的组数。<b>注意：newWeapons 每个时点都从 config 重建，所以只要有同名组，
    /// 每个时点都会返回 &gt; 0，不代表「本遍有新改动」</b> —— 它只用来决定「要不要补跑一遍遍历」，
    /// 不要拿去做「本次起服有没有实际改动」的门控。
    /// </returns>
    private int ApplySameModelFallback(string phase, WeaponPoolConfig cfg, HashSet<string> configKeys,
        Dictionary<string, List<string>> newWeapons, WeaponPoolReport report)
    {
        if (!cfg.SameModelFallback)
        {
            return 0;
        }

        // ★autoScanMods=false 时 AutoAugmentPools 会整段 early-return，而它是 ResolveBranchRoots()
        //   唯一的调用点 —— 三个根 ID 全为 null，ComputeKind 永远返回 "other"，本方法会对全表
        //   判「不是枪」并静默返回 0。这里补一次根解析（只解析，不重扫 mods：用户关掉自动扫描
        //   正是不想扫）。
        if (_weaponRootId is null)
        {
            ResolveBranchRoots();
        }

        var scan = _scan;
        var excludeWeapons = new HashSet<string>(cfg.ExcludeWeapons ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var excludeMods = new HashSet<string>(cfg.ExcludeMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // 每次 Run 现算（不缓存在 BuildPools 里）：物品表在启动末段才是终态，
        // 缓存会让「启动期那一遍没见到」的模组武器永远进不了分组。
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, item) in _templates.Items)
        {
            if (item is null || string.IsNullOrEmpty(item.Name))
            {
                continue;
            }

            var s = id.ToString();
            // ★这里刻意**不**按 scan.ExcludedIds 排除（DWV 运行时生成 id）。原因：任务点名的正是
            //   这批变体（实测主环境 29 个扩不动的条件里绝大多数是 6a93c422… 这类），把被点名的一方
            //   也排除掉，本兜底对它们就永远不生效。它们只作为「候选」被剔掉（见下面的 add 过滤），
            //   不会因此把几十个 DWV 变体塞进任务白名单。
            if (excludeWeapons.Contains(s))
            {
                continue;
            }

            if (excludeMods.Count > 0 && scan is not null && scan.IdToMod.TryGetValue(s, out var modOf) && excludeMods.Contains(modOf))
            {
                continue;
            }

            // 只收枪：刀 / 雷 / 配件同名的（如 M4A1 Upper）不该混进枪的任务条件
            if (KindOf(s) != "gun")
            {
                continue;
            }

            // 类目节点（Type=Node）直接挂在 Weapon 根下时 KindOf 也会返回 gun，
            // 但它是「分类占位」不是枪 —— TraverseAndApply 侧一直用 IsCategoryNode 挡，这里同样要挡。
            if (IsCategoryNode(s))
            {
                continue;
            }

            // 分组键两级（v1.4.1）：
            //   ① 有容器 prefab 的，按 bundle 名归原型 —— 模组变体（DWV / Epic / SOCOM / WTT…）普遍复用
            //      原版枪的模型容器，于是「变体 ↔ 原版 ↔ 同族变体」自动落进同一组。实测 M4A1 一族 38 件、
            //      ASH-12 一族 13 件；抽查确认同 bundle 的都是同族变体（DWV 那批只是 ShortName 没改），
            //      不是误并。
            //   ② 没有容器 prefab 的（自制模型），退回「内部名 + 武器大类」同型号判定：
            //      实测 Katarina 那 5 把与原版同名件的 weapClass 逐一相同（assaultRifle / sniperRifle / pistol），
            //      而「某个 mod 复用既有 _name 装另一类枪」会被这里挡住，不会静默放宽任务。
            var bundle = PrefabBundleOf(item);
            // ★两个键都必须带 weapClass：实测按裸 bundle 分组时有 5 个组混进不同大类
            //   （最典型的一个模组复制了 M4A1 模板却没改 Prefab，于是「FSSH 冲锋枪」和「M4A1」同组），
            //   带上大类后混类组 5 → 0，而主用例（M4A1 一族 38 件）一件不少。
            var weaponClass = item.Properties?.WeapClass ?? string.Empty;
            var key = !string.IsNullOrEmpty(bundle)
                ? "proto:" + bundle.ToLowerInvariant() + "|" + weaponClass
                : "name:" + item.Name + "|" + weaponClass;
            if (!byName.TryGetValue(key, out var list))
            {
                list = new List<string>();
                byName[key] = list;
            }

            list.Add(s);
        }

        var excluded = scan?.ExcludedIds;
        var groups = 0;
        var targets = 0;
        foreach (var (_, ids) in byName)
        {
            if (ids.Count < 2)
            {
                continue;
            }

            var touched = false;
            foreach (var self in ids)
            {
                // 候选 = 组内其它成员，剔掉 DWV 运行时生成 id（它们可以被点名，但不该被塞进白名单）
                var add = new List<string>(ids.Count - 1);
                foreach (var other in ids)
                {
                    if (string.Equals(other, self, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (excluded is not null && excluded.Contains(other))
                    {
                        continue;
                    }

                    add.Add(other);
                }

                if (add.Count == 0)
                {
                    continue;
                }

                if (newWeapons.TryGetValue(self, out var existing))
                {
                    // config 手写组一律原样保留（用户显式意图优先）。
                    // 自动建组（AutoNewGroups 的克隆族）→ **并集追加**：这才是「双向」的另一半，
                    // 否则「要求原版 M4A1」的任务永远拿不到「同 prefab 但没声明克隆」的模组变体。
                    if (configKeys.Contains(self))
                    {
                        continue;
                    }

                    var grew = false;
                    foreach (var x in add)
                    {
                        if (existing.Contains(x, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        existing.Add(x);
                        targets++;
                        grew = true;
                    }

                    if (grew)
                    {
                        touched = true;
                    }

                    continue;
                }

                newWeapons[self] = add;
                targets += add.Count;
                touched = true;
            }

            if (touched)
            {
                groups++;
            }
        }

        report.SameModelGroups = groups;
        report.SameModelTargets = targets;
        if (groups > 0)
        {
            detail(phase, cfg, $"同型号兜底：按模板内部名+武器大类分组 {groups} 组 / 写入 {targets} 条映射（组内双向放行）");
        }

        return groups;
    }

    /// <param name="captureRequiredSnapshot">
    /// 是否把「任务原始白名单」写进 <see cref="_descRequiredIds"/>。
    /// ★必须是「本次 Run 的第一遍」为 true、第二遍为 false：
    ///   第一遍 TraverseAndApply 已经往 <c>counterCondition.Weapon</c> 里 Add 了模组武器，
    ///   第二遍再取快照拿到的就是扩充后的集合，「本任务要求使用」会混进几百把扩充枪。
    ///   这正是 v1.1.1 莲花/全部任务清单混水的根因（起服实测 +27655 条）。
    /// </param>
    private (int Added, List<string> Missing) TraverseAndApply(
        string phase,
        Dictionary<MongoId, Quest> quests,
        WeaponPoolConfig cfg,
        Dictionary<string, List<string>> newWeapons,
        WeaponPoolReport report,
        bool dryRun,
        bool captureRequiredSnapshot = true)
    {
        var taskWeaponSet = new HashSet<string>(newWeapons.Keys, StringComparer.OrdinalIgnoreCase);
        // 已知目标（config 各组的值 + 本遍自动补齐的候选）：这些 ID 是有映射的，
        // 不能算「config 缺失」——否则第二遍会把自己刚注入的武器当成缺失项导出（ANWQ 的老毛病）。
        var knownTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targets in newWeapons.Values)
        {
            foreach (var target in targets)
            {
                if (IsHex24(target))
                {
                    knownTargets.Add(target);
                }
            }
        }

        var needAddWeapons = new List<string>();
        var addedWeaponCount = 0;

        foreach (var quest in quests.Values)
        {
            if (quest?.Conditions?.AvailableForFinish == null)
            {
                continue;
            }

            // ★在任务循环开头就取一次（审查 P3-1/P3-8）：下面的 isNew 块要立刻用它登记「武器优化」归因、
            //   DryRun 去重也要用。留在循环末尾的话，异常恰好落在本任务中途时，已经写进任务表的
            //   Weapon.Add 会拿不到标记 —— 而后续轮次 isNew 恒 false，永远补不回来。
            var questIdStr = quest.Id.ToString();

            var weaponNameSet = new HashSet<string>();
            // 本任务的「模组武器名」子集：只收来源明确的模组武器（用于接取任务后的清单消息）
            var modWeaponNames = new HashSet<string>();
            // ★★ 必须在任何注入之前取快照（下面循环会 Weapon.Add 把模组枪并进来）。
            //   v1.1.1 的 bug：快照写在循环之后，取到的已是扩充后的集合，
            //   于是「本任务要求使用」把几百把扩充枪全列了出来（莲花商人 77 条中招最明显）。
            var requiredIds = new List<string>();
            foreach (var conditionAof in quest.Conditions.AvailableForFinish)
            {
                if (conditionAof?.Counter?.Conditions == null)
                {
                    continue;
                }

                foreach (var counterCondition in conditionAof.Counter.Conditions)
                {
                    if (counterCondition?.Weapon == null)
                    {
                        continue;
                    }

                    foreach (var wid in counterCondition.Weapon)
                    {
                        if (!string.IsNullOrEmpty(wid) && !IsCategoryNode(wid))
                        {
                            requiredIds.Add(wid);
                        }
                    }
                }
            }

            foreach (var conditionAof in quest.Conditions.AvailableForFinish)
            {
                if (conditionAof?.Counter?.Conditions == null)
                {
                    continue;
                }

                foreach (var counterCondition in conditionAof.Counter.Conditions)
                {
                    if (counterCondition?.Weapon == null || counterCondition.Weapon.Count == 0)
                    {
                        continue;
                    }

                    foreach (var weaponId in counterCondition.Weapon.ToList())
                    {
                        var weaponName = IsCategoryNode(weaponId) ? null : GetLocaleItemName(weaponId);
                        if (!string.IsNullOrEmpty(weaponName))
                        {
                            weaponNameSet.Add(weaponName!);
                            if (IsModWeapon(weaponId))
                            {
                                modWeaponNames.Add(weaponName!);
                            }
                        }

                        if (newWeapons.TryGetValue(weaponId, out var newWeaponIds))
                        {
                            foreach (var newWeaponId in newWeaponIds)
                            {
                                if (newWeaponId is null || newWeaponId.StartsWith(ConfigNamePrefix, StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                var newWeaponName = IsCategoryNode(newWeaponId) ? null : GetLocaleItemName(newWeaponId);
                                if (string.IsNullOrEmpty(newWeaponName))
                                {
                                    continue;
                                }

                                // ★顺序契约：本遍新增（或 dryRun 下本会新增）的武器进保留集，prune 不得删
                                var isNew = !counterCondition.Weapon.Contains(newWeaponId);
                                if (isNew)
                                {
                                    // ★DryRun 下从不真正 Add ⇒ 第二遍遍历与后续轮次 isNew 恒 true，
                                    //   同一把枪会被重复计数（3 时点 × 2 遍 ≈ 放大 6 倍，审查 P2-G-3）。
                                    //   只有这种预览模式才维护去重集，生产路径零额外开销。
                                    var countThisWeapon = true;
                                    if (dryRun)
                                    {
                                        countThisWeapon = _dryRunSeenWeapons.Add((questIdStr, newWeaponId));
                                    }

                                    if (!dryRun)
                                    {
                                        counterCondition.Weapon.Add(newWeaponId);
                                    }

                                    weaponNameSet.Add(newWeaponName!);
                                    if (countThisWeapon)
                                    {
                                        addedWeaponCount++;
                                    }
                                    lock (_stateLock)
                                    {
                                        // ★「武器优化」归因与上面的 Weapon.Add 同处一个 isNew 块（审查 P3-1）：
                                        //   登记留在任务循环末尾时，异常恰好落在本任务中途会让该任务丢标记且补不回来。
                                        //   登记本身幂等（HashSet），第二遍遍历与后续轮次 isNew 恒 false ⇒ 只记真实增量。
                                        //   DryRun 下同样记 —— 宿主在 DryRun 不注册 transformer，不影响「只预演不写入」契约。
                                        _addedQuestIds.Add(questIdStr);
                                        _keptWeaponIds.Add(newWeaponId);
                                        if (_scan?.CloneMap.TryGetValue(newWeaponId, out var cloneBase) == true)
                                        {
                                            _keptWeaponIds.Add(cloneBase);
                                        }
                                    }
                                }

                                // 映射目标（=我们注入的新武器）天然是模组武器，同样进接取清单（ANWQ 在 Add 判定之外）
                                if (IsModWeapon(newWeaponId))
                                {
                                    modWeaponNames.Add(newWeaponName!);
                                }
                            }
                        }
                        else if (!taskWeaponSet.Contains(weaponId) && !knownTargets.Contains(weaponId)
                                 && IsHex24(weaponId) && _templates.Items.ContainsKey(new MongoId(weaponId)))
                        {
                            taskWeaponSet.Add(weaponId);
                            needAddWeapons.Add(weaponId);
                        }
                    }

                    // 口径任务的「合格武器」提示：口径条件天然放行同口径武器（含模组枪），
                    // 条件本身不加 ID（加了反而收窄），只把合格武器名并进任务描述。
                    var calibers = counterCondition.WeaponCaliber;
                    if (calibers is not null)
                    {
                        BuildCaliberMap();
                        foreach (var cal in calibers)
                        {
                            if (string.IsNullOrEmpty(cal) || _caliberMap is null || !_caliberMap.TryGetValue(cal, out var caliberIds))
                            {
                                continue;
                            }

                            foreach (var wid in caliberIds)
                            {
                                var caliberWeaponName = IsCategoryNode(wid) ? null : GetLocaleItemName(wid);
                                if (!string.IsNullOrEmpty(caliberWeaponName))
                                {
                                    weaponNameSet.Add(caliberWeaponName!);
                                }
                            }
                        }
                    }
                }
            }

            if (weaponNameSet.Count > 0)
            {
                lock (_stateLock)
                {
                    // ★只在「本进程第一轮 × 本次 Run 第一遍」捕获一次。两个维度的污染都要挡：
                    //   ① 同一轮的第二遍遍历：Weapon 已被第一遍改写；
                    //   ② 后续轮次 Run（启动期/启动末段/延迟补扫共 3 次）：同样已被上一轮改写。
                    if (captureRequiredSnapshot && !_requiredSnapshotDone && requiredIds.Count > 0)
                    {
                        // 去重但保持原始顺序（任务表里的顺序 = 玩家在描述里看到的顺序）
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _descRequiredIds[questIdStr] = requiredIds.FindAll(seen.Add);
                    }

                    _descNames[questIdStr] = weaponNameSet;
                }
            }

            // ★2026-09-27（用户报「发的不全 / 说的数目和发的武器对不上」）：清单内容改成**与任务描述同一套来源**
            //（本任务条件里实际允许的全部武器，含原版），不再只收模组武器。
            // 旧口径只收模组武器 → 消息「…等 122 把」而描述「…等 233 把」；实测 精准猎手 122/233、鹰眼狙击 29/64、近战之王 18/57。
            // 触发条件仍是「本任务确有模组武器可用」（modWeaponNames 作闸门），避免给纯原版任务也发消息。
            if (modWeaponNames.Count > 0 && weaponNameSet.Count > 0)
            {
                // 任务名在收集期查（小写 {id} name 键；物品名才是大写的 {tpl} Name），
                // 顺带清掉 MGMod 等加的标记（如【Road of 3X4】），免得撑长消息标题。
                // 顺带剥掉本 mod 自己的两族标记（审查 P3-2/E-1）：名字可能已被缓存成带标记的形态。
                var questName = StripNameMarkers(StripRichText(GetLocaleValue($"{questIdStr} name") ?? string.Empty));
                lock (_stateLock)
                {
                    _acceptLists[questIdStr] = weaponNameSet.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    _acceptQuestNames[questIdStr] = string.IsNullOrEmpty(questName) ? MessageListTitleFallback : questName;
                }
            }
        }

        // ANWQ 的 <c>seenWeapons</c> 语义：config 组键 ∪ 任务里出现过且确实存在于 item 表的武器 ID（供 outputAllWeapons）
        lock (_stateLock)
        {
            foreach (var seenId in taskWeaponSet)
            {
                _allSeenWeapons.Add(seenId);
            }
        }

        report.Missing.Clear();
        report.Missing.AddRange(needAddWeapons);
        detail(phase, cfg, $"遍历任务完毕：本遍追加 {addedWeaponCount} 把，config 缺映射 {needAddWeapons.Count} 个");
        return (addedWeaponCount, needAddWeapons);
    }

    /// <summary>
    /// 该物品是否「来源明确的模组武器」：在自动扫描的归属表里有记录，且不是
    /// export*.json 的全表导出（那是运行时快照，会把原版与它 mod 物品一并吞进来）。
    /// 接取任务后的清单只收这类物品，避免把原版武器列成「你的模组武器」。
    /// </summary>
    private bool IsModWeapon(string itemId)
    {
        var scan = _scan;
        if (scan == null || string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        return scan.IdToMod.TryGetValue(itemId, out var mod)
               && !string.IsNullOrEmpty(mod)
               && !string.Equals(mod, "export-dump", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 对缺失基础按类别生成新映射组：枪械只认「克隆基底 == 任务武器」铁律；
    /// 刀/雷按同类分支（用户 2026-09-12 拍板纳入）。DWV 生成 ID 不参与。
    /// </summary>
    private (int Groups, int Targets) AutoNewGroups(WeaponPoolConfig cfg, Dictionary<string, List<string>> newWeapons, List<string> missing)
    {
        var scan = _scan;
        if (scan == null || _poolGun == null || _poolKnife == null || _poolGrenade == null)
        {
            return (0, 0);
        }

        var groups = 0;
        var targets = 0;
        foreach (var m in missing)
        {
            if (newWeapons.ContainsKey(m) || scan.ExcludedIds.Contains(m))
            {
                continue;
            }

            List<string>? cands = null;
            switch (KindOf(m))
            {
                case "gun":
                    cands = _poolGun
                        .Where(p => p.Value == m)
                        .Select(p => p.Key)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();
                    break;
                case "knife":
                    cands = cfg.AutoScanPlatformMatch ? _poolKnife : null;
                    break;
                case "grenade":
                    cands = cfg.AutoScanPlatformMatch ? _poolGrenade : null;
                    break;
            }

            if (cands is not { Count: > 0 })
            {
                continue;
            }

            newWeapons[m] = cands.ToList();
            groups++;
            targets += cands.Count;
        }

        return (groups, targets);
    }

    private void BuildPools(WeaponPoolConfig cfg)
    {
        var scan = _scan;
        if (scan == null)
        {
            return;
        }

        var excludeWeapons = new HashSet<string>(cfg.ExcludeWeapons ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var excludeMods = new HashSet<string>(cfg.ExcludeMods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        var gun = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knife = new List<string>();
        var grenade = new List<string>();
        foreach (var (id, _) in _templates.Items)
        {
            var s = id.ToString();
            if (scan.ExcludedIds.Contains(s) || excludeWeapons.Contains(s))
            {
                continue;
            }

            if (excludeMods.Count > 0 && scan.IdToMod.TryGetValue(s, out var modOf) && excludeMods.Contains(modOf))
            {
                continue;
            }

            switch (KindOf(s))
            {
                case "gun":
                    if (scan.CloneMap.TryGetValue(s, out var c))
                    {
                        gun[s] = c;
                    }

                    break;
                case "knife":
                    knife.Add(s);
                    break;
                case "grenade":
                    grenade.Add(s);
                    break;
            }
        }

        knife.Sort(StringComparer.Ordinal);
        grenade.Sort(StringComparer.Ordinal);

        _poolGun = gun;
        _poolKnife = knife;
        _poolGrenade = grenade;
        _poolSignature = PoolSignature(cfg);
    }

    /// <summary>排除清单签名：宿主若在时点之间改了 exclude 列表，池会重建（配置稳定的正常路径下只建一次）。</summary>
    private static string PoolSignature(WeaponPoolConfig cfg)
    {
        static string Sig(IEnumerable<string>? src) =>
            string.Join(",", (src ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        return Sig(cfg.ExcludeWeapons) + "|" + Sig(cfg.ExcludeMods);
    }

    private void ResolveBranchRoots()
    {
        _weaponRootId = FindItemByName("Weapon") ?? WeaponClassId;
        _knifeRootId = FindItemByName("Knife") ?? KnifeClassId;
        _throwRootId = FindItemByName("Throwable weapon") ?? ThrowClassId;
    }

    private string? FindItemByName(string name)
    {
        foreach (var (id, item) in _templates.Items)
        {
            if (string.Equals(item.Name, name, StringComparison.Ordinal))
            {
                return id.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// 4.1 物品树里枪械挂在 Weapon 根下，而雷（Throwable weapon）与刀（Knife）是独立顶层分支；
    /// 判定不到时用克隆基底回退（mod 物品不在原版 parent 链上的兜底）。
    /// </summary>
    private string KindOf(string id)
    {
        if (_kindCache.TryGetValue(id, out var k))
        {
            return k;
        }

        var kind = ComputeKind(id, 0);
        _kindCache[id] = kind;
        return kind;
    }

    private string ComputeKind(string id, int depth)
    {
        var cur = id;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var guard = 0; guard < 16; guard++)
        {
            // 键必须先过 24hex 校验再进 MongoId，畸形 config 键不能炸掉整遍执行
            if (string.IsNullOrEmpty(cur) || !IsHex24(cur) || !seen.Add(cur))
            {
                break;
            }

            if (cur == _weaponRootId)
            {
                return "gun";
            }

            if (cur == _knifeRootId)
            {
                return "knife";
            }

            if (cur == _throwRootId)
            {
                return "grenade";
            }

            if (!_templates.Items.TryGetValue(new MongoId(cur), out var item))
            {
                break;
            }

            cur = item.Parent.ToString();
        }

        var cloneBase = _scan?.CloneMap.TryGetValue(id, out var c) == true ? c : null;
        return cloneBase != null && depth < 4 ? ComputeKind(cloneBase, depth + 1) : "other";
    }

    /// <summary>
    /// 口径 → 该口径已注册武器 ID 表（含模组枪）。4.1 的模板属性是
    /// <c>TemplateItemProperties.AmmoCaliber</c>（ANWQ 当时用反射读，这里直读属性，语义相同）；整表只建一次。
    /// </summary>
    private void BuildCaliberMap()
    {
        if (_caliberMap != null)
        {
            return;
        }

        lock (_caliberLock)
        {
            if (_caliberMap != null)
            {
                return;
            }

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, item) in _templates.Items)
            {
                var cal = item.Properties?.AmmoCaliber;
                if (string.IsNullOrEmpty(cal))
                {
                    continue;
                }

                if (!map.TryGetValue(cal!, out var list))
                {
                    list = new List<string>();
                    map[cal!] = list;
                }

                list.Add(id.ToString());
            }

            _caliberMap = map;
        }
    }

    /// <summary>
    /// 物品树分类节点（<c>TemplateItem.Type == "Node"</c>，如「突击步枪」「霰弹枪」本身）不是真实武器。
    /// 它们没有可装备的实体，名字出现在「可用武器」列表里会让玩家以为那是一把枪（实测 4f41ae57… 精准猎手
    /// 的描述里出现两条裸「突击步枪」）。★官方同口径先例：InventoryController.cs:225 / RagfairAssortGenerator.cs:53
    /// / GiveSptCommand.cs:351 全部按 <c>Type != "Node"</c> 过滤。
    /// ★只过滤**显示名**，不动任务条件本身（条件里保留类别 tpl 是原版惯例，改了会收窄可用武器）。
    /// </summary>
    private bool IsCategoryNode(string id)
    {
        if (!IsHex24(id))
        {
            return false;
        }

        return _templates.Items.TryGetValue(new MongoId(id), out var template)
               && string.Equals(template?.Type, "Node", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetLocaleItemName(string id)
    {
        var key = $"{id} Name";
        if (_localeCache.TryGetValue(key, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        return LookupLocale(key);
    }

    /// <summary>
    /// 供宿主（Entry 的 locale transformer）按物品 ID 取显示名，用于渲染「本任务要求使用」那几行。
    /// ★取的是<b>未剥色</b>的原始 locale 名 —— 剥色由渲染函数统一做（<see cref="Plain"/>），
    ///   这样「名字带染色标签」这一信息不会在这里被丢掉，两处行为一致。
    /// </summary>
    public string? GetItemNameForDescription(string id) =>
        string.IsNullOrEmpty(id) ? null : GetLocaleItemName(id);

    /// <summary>按完整 locale 键取值（任务名是小写 <c>{id} name</c>，与物品的大写 <c>Name</c> 不同）。</summary>
    private string? GetLocaleValue(string key)
    {
        if (_localeCache.TryGetValue(key, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        return LookupLocale(key);
    }

    private string? LookupLocale(string key)
    {
        var db = _localeDb;
        string? value = null;
        if (db is not null && db.TryGetValue(key, out var raw) && !string.IsNullOrEmpty(raw))
        {
            value = raw;
        }

        _localeCache[key] = value ?? string.Empty;
        return value;
    }

    /// <summary>
    /// 解析 locale 字典（每遍 Run 只解析一次；LazyLoad.Value 每次访问都重新反序列化整份 locale）。
    /// 找不到目标语言回落 en；两者都没有时返回 null 并只告警一次（此时所有武器名都取不到 → 本遍不会追加任何武器）。
    /// </summary>
    private Dictionary<string, string>? ResolveLocaleDb(string? lang)
    {
        var wanted = string.IsNullOrEmpty(lang) ? "ch" : lang!;
        if (_localeDb is not null && string.Equals(_localeDbLang, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return _localeDb;
        }

        var resolved = TryLocaleDb(wanted) ?? TryLocaleDb("en");
        _localeDb = resolved;
        _localeDbLang = wanted;

        if (resolved is null && !_localeMissingWarned)
        {
            _localeMissingWarned = true;
            _logger.Warning($"[综合任务优化/武器池] locale 表既没有 {wanted} 也没有 en —— 武器名解析不可用，本遍不会追加任何武器（请检查 lang 配置与 locale 数据）");
        }

        return resolved;
    }

    private Dictionary<string, string>? TryLocaleDb(string lang) =>
        _localeTable.Global.TryGetValue(lang, out var lazy) && lazy?.Value is { } db ? db : null;

    private static Dictionary<string, List<string>> CloneWeapons(Dictionary<string, List<string>> source)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return clone;
        }

        foreach (var (key, list) in source)
        {
            clone[key] = list is null ? new List<string>() : new List<string>(list);
        }

        return clone;
    }

    /// <summary>
    /// 去掉武器名里的富文本标签。部分模组（MGMod 等）会给名字逐字符上色，
    /// 在聊天消息里会撑成数百字符且几乎无法阅读——消息只保留纯文本名字。
    /// </summary>
    public static string StripRichText(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('<') < 0)
        {
            return name;
        }

        return System.Text.RegularExpressions.Regex.Replace(name, "<[^>]{1,60}>", string.Empty).Trim();
    }

    /// <summary>
    /// ANWQ 描述块重写逻辑（幂等）：先按锚点截掉旧块，再拼上当前武器名列表。
    /// ★宿主接线点：在 locale transformer 里对 <see cref="WeaponPoolReport.DescriptionMods"/> 的每个 key 调本方法，
    /// 把返回值写回 <c>locale[key]</c>；key 不存在/为空时跳过。
    /// </summary>
    /// <summary>「本任务要求使用」那行的锚点（同样必须带注入格式，不能用裸中文，防止被其它 mod 误伤）。</summary>
    public const string RequiredWeaponsAnchor = "<color=orange><i>本任务要求使用：";

    /// <summary>
    /// 剥掉物品名里的一切富文本标签（&lt;color=…&gt; / &lt;b&gt; / &lt;i&gt; / 逐字彩虹色 …）。
    /// ★为什么必须剥：物品名染色类 mod（ItemNameTint41 之类）会在名字里插颜色标签，
    ///   直接拼进本 mod 的描述块会形成<em>两层嵌套颜色</em> —— Unity 只认内层，
    ///   我们加的橙/黄外层被吃掉，玩家看到的是一堆彩虹字符，认不出哪行是哪把枪。
    /// </summary>
    private static string Plain(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "<[^>]{1,80}>", string.Empty).Trim();

    /// <summary>
    /// ANWQ 描述块重写逻辑（幂等）：先按锚点截掉旧块，再拼上当前武器名列表。
    /// ★宿主接线点：在 locale transformer 里对 <see cref="WeaponPoolReport.DescriptionMods"/> 的每个 key 调本方法，
    /// 把返回值写回 <c>locale[key]</c>；key 不存在/为空时跳过。
    /// </summary>
    /// <param name="requiredNames">
    /// 任务<b>原始</b>要求（扩充前）的「显示名 + 物品 ID」二元组。为空则不输出
    /// 「本任务要求使用」那一行 —— 没有原始白名单的任务本来就不限武器，编一行反而误导玩家。
    /// </param>
    public static string BuildDescriptionText(
        string original,
        IReadOnlyList<string> weaponNames,
        int maxWeaponNames,
        IReadOnlyList<(string Name, string Id)>? requiredNames = null)
    {
        var blockStart = original.IndexOf(AvailableWeaponsBlockAnchor, StringComparison.Ordinal);
        var reqStart = original.IndexOf(RequiredWeaponsAnchor, StringComparison.Ordinal);
        var cut = blockStart;
        if (reqStart >= 0 && (cut < 0 || reqStart < cut))
        {
            cut = reqStart;
        }

        var sb = new System.Text.StringBuilder(cut >= 0 ? original[..cut].TrimEnd() : original);

        // ★第一段「本任务要求使用」：原版描述只说「这把枪」「你测试过的那把枪的变种」，
        //   单词序接任务 / 单机新档都读不出是哪把 —— 这里把名字直接补出来。
        //   名字一律走 Plain() 剥色（染色 mod 的标签会盖掉我们的外层颜色），末尾附 6 位 ID 短码
        //   供同名不同色号的枪区分。
        if (requiredNames is { Count: > 0 })
        {
            sb.Append("\n\n").Append(RequiredWeaponsAnchor).Append('\n');
            foreach (var (name, id) in requiredNames)
            {
                sb.Append(Plain(name)).Append(" [").Append(ShortId(id)).Append("]\n");
            }

            sb.Append("</color></i>");
        }

        sb.Append("\n\n<color=yellow><i>可用武器：\n");
        var allNames = weaponNames.ToList();
        if (maxWeaponNames > 0 && allNames.Count > maxWeaponNames)
        {
            foreach (var name in allNames.Take(maxWeaponNames))
            {
                sb.Append(Plain(name)).Append('\n');
            }

            sb.Append($"…等 {allNames.Count} 把可用武器（完整列表见任务条件）\n");
        }
        else
        {
            foreach (var name in allNames)
            {
                sb.Append(Plain(name)).Append('\n');
            }
        }

        sb.Append("</color></i>");
        return sb.ToString();
    }

    /// <summary>物品 ID 取后 6 位作短码（24 位 hex 的后 6 位足够区分同名枪，又不至于太长）。</summary>
    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? string.Empty : (id.Length <= 6 ? id : id[^6..]);

    /// <summary>
    /// ANWQ 的 <c>BuildAcceptMessage</c> 等价物（纯函数版，供宿主的 messageProvider 直接用）：
    /// 任务无模组武器可用或开关关闭时返回 null（调用方不发消息）。
    /// </summary>
    public static string? BuildAcceptMessage(WeaponPoolReport report, string questId, WeaponPoolConfig cfg)
    {
        if (report is null || cfg is null || !cfg.SendWeaponListOnQuestAccept)
        {
            return null;
        }

        if (!report.AcceptLists.TryGetValue(questId, out var stored) || stored is null || stored.Count == 0)
        {
            return null;
        }

        // 快照：避免锁外遍历共享 List（补扫可能在请求线程并发改写）
        var names = new List<string>(stored);
        var questName = report.AcceptQuestNames.TryGetValue(questId, out var stored2) && !string.IsNullOrEmpty(stored2)
            ? stored2
            : MessageListTitleFallback;

        // 标题带任务名：聊天是流式列表，接任务后商人还会发别的消息，
        // 把清单挤下去之后玩家仍要能认出「这条是哪个任务的枪」。
        var sb = new System.Text.StringBuilder($"<color=yellow><i>{questName} ｜ 可用武器：</i></color>").Append('\n');
        // ★2026-09-27 用户裁定「消息一次列全，不再截断」：先前只列 acceptMessageMaxWeapons 条却写「…等 N 把」，
        // 玩家看到的条目数与声明的总数必然对不上（这也是「发的不全」的直接来源）。
        // cfg.AcceptMessageMaxWeapons 仍解析（兼容旧配置文件），但本方法不再使用它。
        for (var i = 0; i < names.Count; i++)
        {
            sb.Append("· ").Append(StripRichText(names[i])).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private void detail(string phase, WeaponPoolConfig cfg, string message)
    {
        // 明细一律 Debug（SPT 文件日志阈值 Information ⇒ 不落盘）；生产日志只留宿主那 1 行汇总
        if (cfg.ConsoleLogEnabled)
        {
            _logger.Debug($"[综合任务优化/武器池] [{phase}] {message}");
        }
    }

    // 编译版正则：判据在遍历内层循环里被反复调用（与 Fixups.cs 的同名判据一致）
    private static readonly System.Text.RegularExpressions.Regex Hex24 =
        new("^[0-9a-fA-F]{24}\\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsHex24(string? s) => !string.IsNullOrEmpty(s) && Hex24.IsMatch(s);
}
