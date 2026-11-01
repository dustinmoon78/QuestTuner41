// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Json;
using SPTarkov.Server.Core.Services.Commerce;
using IOPath = System.IO.Path;
using QuestTuner41.AcceptNotify;
using QuestTuner41.ChainHint;
using QuestTuner41.WeaponPool;

namespace QuestTuner41;

/// <summary>
/// QuestTuner 4.1（P1）：把"改任务"的入口与账本收到一处。
///
/// 能力面（P1）：
///   1) 击杀目标泛化（Scavs4All 的能力）：Scav → 任意目标，PMC 可选 + 倍率
///   2) 条件移除：target / weapon / weaponMods / distance / time / map / zone
///   3) 数值面板：击杀数、上交数的"固定值或百分比"
///   4) 排除清单：exemptQuests / onlyQuests / questOverrides，并读取 sgtlaggyQuestTweaks 的排除名单
///   5) 诊断账本 + DryRun + 任务表导出（供与 sgtlaggy 做等价对照）
///
/// 时点：启动期一遍 + 首个客户端请求时补扫一遍（HTTP 在全部启动阶段后才开放，故补扫必然最晚）。
/// 幂等：目标类改动带"已是 Any 就跳过"的判定；列表/数值类改动带"取值已达标就跳过"的判定；
///       百分比型数值另用"已处理过的条件 ID 集合"记账，避免补扫二次缩放。
///
/// 本实现只依据 sgtlaggyQuestTweaks 的公开 README 描述的行为规格编写，未使用其代码或反编译产物。
/// </summary>
[Injectable(InjectionType.Singleton, int.MaxValue, TypePriority = 1000002)]
public class QuestTuner41 : IOnLoad
{
    private const string ConfigFilePath = "user/mods/QuestTuner41/config.json";
    private const string AnyTarget = "Any";
    private const string SavageTarget = "Savage";
    private const string PmcTarget = "AnyPmc";
    private const string BearTarget = "Bear";
    private const string UsecTarget = "Usec";

    /// <summary>钥匙 / 钥匙卡的基类 ID（实测反推：sgtlaggy 的上交数量改写会跳过这两类）。</summary>
    private static readonly HashSet<string> KeyBaseClasses = new(StringComparer.Ordinal)
    {
        "5c99f98d86f7745c314214b3",
        "5c164d2286f774194c5e69fa",
    };

    private readonly ISptLogger<QuestTuner41> _logger;
    private readonly TemplateTable _templates;
    private readonly LocaleTable _localeTable;
    private readonly ItemHelper _itemHelper;

    private TunerConfig _config = new();
    private ForeignConfig _foreign = new();

    /// <summary>
    /// 待追加标记的任务条件文本 ID（去重，供 transformer 一次性套用）。
    /// ★用并发字典而不是 HashSet：延迟补扫跑在**首个客户端请求线程**上会往里 Add，
    /// 而并发请求正拿它做快照 —— HashSet 读写并发时 CopyTo 撞扩容会抛
    /// IndexOutOfRangeException，异常从 transformer 逃出即整个 /client/locale 请求 500。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _questTextIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 被改写过、需要在**任务名**尾部打标记的任务 ID（去重）。
    /// 判定口径＝本任务任一条件改写命中，见 <see cref="ApplyChanges"/> 里的计数差分。
    /// 并发理由同 <see cref="_questTextIds"/>。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _questNameIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 「武器优化」标记：被武器池**真的往完成条件里 Add 过枪**的任务 ID（去重）。
    /// ★口径是 <c>WeaponPoolReport.AddedQuestIds</c>（池确实 Add 过），**不是** AcceptLists
    ///   （那是「任务有模组枪可用」，早已列全枪的任务也会进去，会误标一大批）。
    /// 与 <see cref="_questNameIds"/> 是两个独立维度，同一任务可同时命中。
    /// 并发理由同 <see cref="_questTextIds"/>。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _weaponNameIds = new(StringComparer.Ordinal);

    /// <summary>已经改过数值的条件 ID：补扫时跳过，避免百分比被二次缩放。</summary>
    private readonly HashSet<string> _valuedConditionIds = new(StringComparer.Ordinal);

    /// <summary>修正类改写执行器（角色白名单放宽 / 地图白名单补漏 / 死引用武器 ID 剔除 / 非法 target 修正）。</summary>
    private readonly FixupRunner _fixups = new();

    /// <summary>
    /// 任务链模块（v1.0.0 并入自原独立 mod QuestChainHint41）。
    /// 由本类手工 new 而非走 DI，这样它天然活在宿主的三层时点之内、不另起加载链；
    /// 空 = 任务链被禁用或初始化失败，此时所有回调都是空转。
    /// </summary>
    private ChainHintRunner? _chainHint;

    /// <summary>user/mods 目录（QuestTuner41 目录的父），供任务链模块定位旧 mod 目录与旧 config.jsonc。</summary>
    private readonly string _modsRoot;

    /// <summary>任务级 location（地图 MongoId）→ 地图 Id，供 removeZone「展开到这张图」用。</summary>
    private readonly MapResolver _mapResolver;

    // ── 武器池（2026-09-21 并入自 AddNewWeaponsToQuest41）──
    private readonly ISptLogger<WeaponPoolRunner> _weaponPoolLogger;
    private readonly MailSendService _mailSendService;
    private readonly WeaponPoolRunner _weaponPool;
    private readonly WeaponPoolConfig _weaponPoolConfig;
    // 最近一遍武器池结果：transformer 在请求线程按运行时读取，故用 volatile 整体替换引用
    private volatile WeaponPoolReport? _weaponPoolLast;
    // 跨遍累加口径（池幂等 ⇒ 只有首次真正写入那遍 Added>0；汇总行与「累计」都用这三个）
    private int _poolAddedTotal;
    private int _poolMissing;

    // 同型号兜底（v1.4.0）的**单遍**口径：分组与目标每遍重算且值相同，用覆盖而非累加，
    // 否则三时点 × 两遍会把它放大成 6 倍（与 _poolAddedTotal 的幂等累加语义不同）。
    private int _poolSameModelGroups;
    private int _poolSameModelTargets;
    private int _poolDescCount;
    private int _poolSkipWarned;

    /// <summary>本遍是否为「物品表已终态」的时点（启动末段/首请求补扫）。启动期不剔死引用武器 ID。</summary>
    private bool _itemTableFinal;

    private int _lateRan;

    /// <summary>removeTimeGates 的"未实现"提示只打一次。</summary>
    private int _nTimeGates;

    private int _scanned;
    private int _skipExempt;
    private int _skipOnly;
    private int _nTarget;

    /// <summary>
    /// 击杀目标**真的**被改写掉（写成 Any）的次数，不含「target 本来就是 Any、只清掉了 savageRole」的情形。
    /// ★配对中和的闸门只看它：只清角色并没有制造新的矛盾，那种任务身上的矛盾是原版自带的，不该由本改动去删失败条件。
    /// </summary>
    private int _nTargetWidened;

    /// <summary>因完成条件被放宽成「任意目标」而被一并去掉的通用失败条件（「击杀某类 AI 即失败」）。</summary>

    /// <summary>本次起服里「角色族放宽」条数（白名单整族命中 relaxRolePrefixes 的击杀条件）。</summary>
    private int _nRoleFamilyRelaxed;

    /// <summary>因角色族放宽而导致完成侧变成「任意目标」的任务 ID —— 这些任务的 Fail 组需要额外配对中和。</summary>
    private readonly HashSet<string> _roleRelaxedQuestIds = new(StringComparer.Ordinal);
    private int _nFailNeutralized;

    private int _nWeapon;
    private int _nWeaponMods;
    private int _nDistance;
    private int _nTime;
    private int _nDroppedMap;
    private int _nDroppedZone;

    /// <summary>removeZone 里定位不到地图、保留原区域限定的区域条件（按 条件ID|任务|zoneIds 去重：三个时点各跑一遍也只算一处）。</summary>
    private readonly HashSet<string> _zoneUnresolvedIds = new(StringComparer.Ordinal);

    /// <summary>上面那些条件的明细（最多 40 条，只在第一个看到它们的时点打一次）。</summary>
    private readonly List<string> _zoneUnresolvedDetail = new();

    private bool _zoneUnresolvedReported;
    private int _nElimValue;
    private int _nHandoverValue;
    private int _nRepeatables;

    // P2 / P3 计数
    private int _nBodyPart;
    private int _nSelfGear;
    private int _nEnemyGear;
    private int _nSelfHealthFx;
    private int _nEnemyHealthFx;
    private int _nFindInRaid;
    private int _nRevealed;
    private int _nUnknownRewards;
    private int _nM10;
    private int _nLightkeeper;
    private int _nCollector;

    public QuestTuner41(
        ISptLogger<QuestTuner41> logger,
        TemplateTable templates,
        LocaleTable localeTable,
        ItemHelper itemHelper,
        LocationTable locations,
        MailSendService mailSendService,
        ISptLogger<WeaponPoolRunner> weaponPoolLogger)
    {
        _logger = logger;
        _templates = templates;
        _localeTable = localeTable;
        _itemHelper = itemHelper;
        _mailSendService = mailSendService;
        _weaponPoolLogger = weaponPoolLogger;
        _mapResolver = new MapResolver(locations);

        // 武器池：目录全部从配置路径推出（ConfigFilePath = user/mods/QuestTuner41/config.json），不依赖当前工作目录。
        // selfDir = 本 mod 目录（user/mods/QuestTuner41）；modsRoot = 其父目录（user/mods，自动扫描的根）。
        var selfDir = IOPath.GetDirectoryName(ConfigFilePath) ?? "user/mods/QuestTuner41";
        var modsRoot = IOPath.GetDirectoryName(selfDir) ?? "user/mods";
        _modsRoot = modsRoot;
        _weaponPool = new WeaponPoolRunner(weaponPoolLogger, templates, localeTable, modsRoot, IOPath.GetFileName(selfDir));
        _weaponPoolConfig = WeaponPoolConfig.Load(selfDir);
        if (!string.IsNullOrEmpty(_weaponPoolConfig.LoadError))
        {
            _logger.Warning($"[QuestTuner41] 武器池配置未读到，本次不改武器：{_weaponPoolConfig.LoadError}");
        }

        // 配置在构造期读取：启动末段（PostLoad 注入本类）可能早于 OnLoadAsync 执行，
        // 若那时还没读配置就会用默认值跑，等于用一套用户没选的开关去改任务。
        try
        {
            _config = TunerConfig.Load(ConfigFilePath);
        }
        catch (Exception ex)
        {
            _config = TunerConfig.Safe();
            _logger.Error($"[QuestTuner41] 配置解析失败，本次不做任何修改：{ex.Message}");
        }

        // 老配置自动升级的告知行：只在校验确实改动了历史出厂默认值时才打，正常用户零噪声。
        if (_config.DefaultMigrations.Count > 0)
        {
            _logger.Info($"[QuestTuner41] 配置已自动升级 {_config.DefaultMigrations.Count} 项（无需手改）：" + string.Join("；", _config.DefaultMigrations));
        }

        _foreign = ForeignConfig.Load();
        foreach (var w in _foreign.Warnings)
        {
            _logger.Warning($"[QuestTuner41] {w}");
        }
    }

    /// <summary>供启动末段那一层调用（同一实例，配置已就绪）。</summary>
    internal void RunOnce(string phase)
    {
        // ★任务链必须在**任何一遍改写之前**建好：启动末段层（TypePriority=int.MaxValue）
        // 的执行顺序不保证晚于 OnLoadAsync（构造期注释已写明这点），若只在 OnLoadAsync 里初始化，
        // 就会出现"改写已经跑完、链条模块才建起来"的错序，本次合并的核心收益（改写后重建快照）直接落空。
        // InitChainHint 自带幂等门控，两条路径都调也只会建一次。
        InitChainHint();

        try
        {
            Run(phase);
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] {phase} 阶段执行失败: {ex}");
        }
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ReportForeign();

        // 任务链模块：必须早于第一遍 Run，让它在"任务表尚未改写"的时点先挂上 transformer，
        // 真正的重建与补挂由 Run 结束后的 AfterTaskRewrite 收口（见 ChainHintRunner 的说明）。
        InitChainHint();

        try
        {
            Run("启动期");
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] 执行失败: {ex}");
        }

        // 钩子安装放在 try 之外：启动期那遍即便失败，补扫也还有一次机会。
        var installed = LatePassHook.Install(RunLate);
        if (installed)
        {
            Detail("[QuestTuner41] 延迟补扫钩子已安装（首个客户端请求时再扫一遍，晚于一切启动期 mod，覆盖不受加载顺序影响）");
        }
        else
        {
            // 失败永远可见，不受 debug 开关影响
            _logger.Warning("[QuestTuner41] 延迟补扫钩子安装失败，降级为仅启动期一遍（覆盖仍受加载顺序影响）");
        }

        // 接任务清单消息（并入自 ANWQ 2.4.0）：正文按最近一遍武器池结果现算；
        // 目标方法缺失或功能关闭时安静返回 false，不影响起服。
        if (_weaponPoolConfig.SendWeaponListOnQuestAccept && string.IsNullOrEmpty(_weaponPoolConfig.LoadError))
        {
            var notifyOk = QuestAcceptNotifyPatch.Install(_weaponPoolLogger, _mailSendService, BuildAcceptMessageFor, true);
            Detail("[QuestTuner41] 接任务武器清单消息：" + (notifyOk ? "已挂钩子" : "未挂钩子（目标方法缺失）"));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 建任务链模块并初始化。
    /// 独立 try：任务链是附加功能，它炸了不许连累任务改写本身（错误永远可见，但不升级成中断）。
    /// </summary>
    private void InitChainHint()
    {
        // 幂等门控放在最前面：RunOnce 与 OnLoadAsync 都会调它，
        // 两条路径谁先到都只建一次（重复建会造成注入器与 transformer 各注册两遍）。
        if (_chainHint is not null)
        {
            return;
        }

        try
        {
            var runner = new ChainHintRunner(
                message => _logger.Info(message),
                message => _logger.Warning(message),
                message => _logger.Error(message));

            // ★ChainHint 分区被写成 null 时先补实例，且**必须在 Initialize 之前**：
            //   Initialize 内部是 config ?? new ChainHintConfig()，若不先补，它自建的那个实例才持有
            //   注入器，而我们在外面改的是另一个对象，追加永远传不进去。
            var chainCfg = _config.ChainHint ??= new ChainHintConfig();

            runner.Initialize(chainCfg, _templates, _localeTable, _modsRoot);
            _chainHint = runner;

            // ★P1 修复（2026-10-11 两轮审查）：链条文本是拿共享 locale 字典里的 "<questId> name" 拼的，
            //   而本 mod 往同一个键的尾部加了「（已优化）」后缀 ⇒ 不清洗的话前置/后续列表里会显示成
            //   「前置：某任务（任务已优化）」。ChainHintConfig.NameStripTexts 本来就是为这类泄漏设的
            //   （原注释：MGMod 的【Road of 3X4】不清洗就会出现在前置/后续列表里）。
            //   ★必须放在 Initialize **之后**：Initialize 内的 ImportLegacyConfig → OverrideFrom
            //   （ChainHintConfig.cs:163）会无条件整体赋值 NameStripTexts，放前面会被静默冲掉，
            //   且老用户升级路径（存在 mods\QuestChainHint41\）才会触发 —— 正是最难发现的那种。
            //   注入器持有的是同一个 ChainHintConfig 实例，CleanName 是调用时才读该列表，所以这里改得到。
            //   ★两族标记都要并进来：漏掉武器族＝原样重演这个 P1（后缀泄漏进链条文本）。
            chainCfg.NameStripTexts ??= new List<string>();
            foreach (var nameSuffixForChain in new[] { _config.QuestNameSuffix, _config.WeaponNameSuffix })
            {
                if (!string.IsNullOrEmpty(nameSuffixForChain)
                    && !chainCfg.NameStripTexts.Contains(nameSuffixForChain, StringComparer.Ordinal))
                {
                    chainCfg.NameStripTexts.Add(nameSuffixForChain);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] 任务链模块初始化失败，本次不注入链条文本：{ex}");
        }
    }

    /// <summary>跑一遍武器池（并入自 ANWQ）：池构建 → 追加到 Kills.Weapon → 登记保留集。</summary>
    /// <summary>本遍与累计口径都含武器池添加数（审查 P2-2：池是幂等的，单遍数字会整行消失）。</summary>
    private int PooledChanges() => TotalChanges() + _poolAddedTotal;

    /// <summary>跑一遍武器池（并入自 ANWQ）：池构建 → 追加到 Kills.Weapon → 登记保留集。</summary>
    private void RunWeaponPool(string phase)
    {
        // ★与 removeWeapon 互斥：本类 removeWeapon 会把 Kills.weapon 整表清空（ApplyFieldRemovals），
        // 先加后清等于白做、还会让汇总行多报数字 ⇒ 直接跳过并一次性告警（审查 P2-1）。
        if (_config.RemoveWeapon)
        {
            if (Interlocked.Exchange(ref _poolSkipWarned, 1) == 0)
            {
                _logger.Warning("[QuestTuner41] removeWeapon 已开启：武器池本次不生效（两者语义相反）。要让任务带上可用武器，请把 removeWeapon 置 false");
            }
            return;
        }

        // 配置没读到就不跑：此时 NewWeapons 为空，自动扫描会凭空建组，等于绕开用户的 151 组映射乱加武器（审查 P2-3）。
        // 构造期已就 LoadError 打过 Warning，这里不重复刷。
        if (!string.IsNullOrEmpty(_weaponPoolConfig.LoadError))
        {
            return;
        }

        // ★作用范围契约（审查 P1-D-1）：exemptQuests＝「这些任务不处理」、onlyQuests＝「只处理这些任务」，
        //   是**用户级全局承诺**。武器池此前直接吃整张任务表，等于绕过这两个开关：被豁免的任务照样被加枪，
        //   而 1.3.0 起这件事还会以「（武器已优化）」写在任务名上被玩家看见。这里按同一契约先收窄再交给池。
        //   onlyQuests 非空时它优先（与任务改写侧同口径），否则按 exemptQuests 排除。
        // 接取清单消息标题要剥掉我们自己的两族标记（审查 P3-2/E-1）。放这里而非构造期：本方法一定在
        // 配置加载之后被调用，且每时点赋一次同值、幂等。
        _weaponPool.NameMarkers = new[] { _config.QuestNameSuffix, _config.WeaponNameSuffix };

        // ★比较器与任务改写侧统一为 Ordinal（审查 P3-5）：那边是 List.Contains（默认 Ordinal），
        //   此处原先用 IgnoreCase，同一个 ID 在两条路径上可能一个命中一个不命中。依赖的隐式前提：
        //   MongoId.ToString() 恒返回小写 24 位 hex。
        // ★豁免必须先于白名单，且顺序与任务改写侧一致（审查 review6 P3-2）：改写侧是「命中 exempt 就 continue」，
        //   再看 onlyQuests；池侧原先反过来（onlyQuests 优先、exempt 只在 else 生效），
        //   同一个 ID 同时出现在两名单时，被豁免的任务仍会被加枪并挂上「武器已优化」。
        // ★豁免名单必须含 sgtlaggy 外来配置（审查 review5 P2-1）：改写侧就是
        //   「_config.ExemptQuests || (HonorSgtlaggyConfig && _foreign.ExemptQuests)」，
        //   而出厂 HonorSgtlaggyConfig=true。只看自己那份＝同一契约缺口换个来源原样复现。
        var ownExemptCount = _config.ExemptQuests.Count;
        var exempt = new HashSet<string>(_config.ExemptQuests, StringComparer.Ordinal);
        if (_config.HonorSgtlaggyConfig)
        {
            exempt.UnionWith(_foreign.ExemptQuests);
        }

        var pool = _templates.Quests;
        if (exempt.Count > 0)
        {
            var matched = _templates.Quests.Count(kv => exempt.Contains(kv.Key.ToString()));
            pool = _templates.Quests.Where(kv => !exempt.Contains(kv.Key.ToString()))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            Detail($"[QuestTuner41] [{phase}] 武器池作用域＝排除 exemptQuests {exempt.Count} 条 → 余 {pool.Count} 个任务");

            // ★填错的 ID 不能静默（审查 review5 P3-4）：上面是 Detail，非 Debug 下不落盘，用户会以为名单生效了。
            //   来源不拆开报（审查 review6 P3-5）：只说「含 sgtlaggy 名单」，免得用户只翻自己的 config 找不着。
            if (matched < exempt.Count)
            {
                _logger.Warning($"[QuestTuner41] [{phase}] exemptQuests 有 {exempt.Count - matched} 条 ID 在任务表里不存在，已忽略（本 mod {ownExemptCount} 条，其余含 sgtlaggy 名单；ID 须为小写 24 位 hex）");
            }
        }

        var scopedQuests = pool;
        if (_config.OnlyQuests.Count > 0)
        {
            var only = new HashSet<string>(_config.OnlyQuests, StringComparer.Ordinal);
            var matched = pool.Count(kv => only.Contains(kv.Key.ToString()));
            scopedQuests = pool.Where(kv => only.Contains(kv.Key.ToString()))
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            Detail($"[QuestTuner41] [{phase}] 武器池作用域＝onlyQuests {only.Count} 条 → 实收 {scopedQuests.Count} 个任务");

            if (matched < only.Count)
            {
                _logger.Warning($"[QuestTuner41] [{phase}] onlyQuests 有 {only.Count - matched} 条 ID 在任务表里不存在或已被豁免，已忽略（ID 须为小写 24 位 hex）");
            }
        }

        try
        {
            var report = _weaponPool.Run(phase, scopedQuests, _weaponPoolConfig, _config.DryRun);
            _weaponPoolLast = report;
            _poolAddedTotal += report.Added;
            _poolMissing = report.Missing.Count;
            _poolSameModelGroups = report.SameModelGroups;
            _poolSameModelTargets = report.SameModelTargets;
            _poolDescCount = report.DescriptionMods.Count;
            // 即使 DryRun 也登记：保留集只影响 prune 判据，不改任务数据
            _fixups.KeepWeaponIds(report.KeptWeaponIds);
        }
        catch (Exception ex)
        {
            // 失败永远可见；武器池挂了不阻断后续泛化/修正
            _logger.Error($"[QuestTuner41] [{phase}] 武器池执行失败：{ex}");
        }

        // ★「武器优化」标记的归因入口，刻意放在 try/catch **之外**（审查 P2-C-4）：
        //   池中途抛异常时 report 不会填充，但已经写进任务表的 Weapon.Add 必须照样拿到标记 ——
        //   后续轮次 isNew 恒 false，漏了就永远补不回来，而汇总行只会偏小、无从察觉。
        //   这里读的是池实例的**累计**集合（跨轮次只增），与 report 是否填充无关，TryAdd 重复无害。
        //   DryRun 下也登记：那时不注册 transformer，不影响「只预演不写入」契约。
        _weaponPool.ForEachAddedQuestId(id => _weaponNameIds.TryAdd(id, 0));
    }

    /// <summary>接任务后的清单消息正文（QuestAcceptNotifyPatch 的回调）。</summary>
    private string? BuildAcceptMessageFor(string questId)
    {
        var report = _weaponPoolLast;
        return report is null ? null : WeaponPoolRunner.BuildAcceptMessage(report, questId, _weaponPoolConfig);
    }

    private void RunLate()
    {
        if (Interlocked.Exchange(ref _lateRan, 1) != 0)
        {
            return;
        }

        if (!_lateStageRan)
        {
            // 兜底心跳：末段层没执行时默认会「一行都不打」，这里补一条 Warning 免得静默
            _logger.Warning("[QuestTuner41] 启动末段层未执行（SPT 升级/DI 变更？）—— 本次只有启动期那一遍；要看逐项明细请把 config 的 debug 置 true");
        }

        try
        {
            Run("延迟补扫");
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] 延迟补扫失败: {ex}");
        }
    }

    private void Run(string phase)
    {
        var before = PooledChanges();
        _scanned = 0;
        _skipExempt = 0;
        _skipOnly = 0;
        // 物品表在启动链末段即为终态（SPT 自身在该时点对新增物品直接抛 DatabaseModifiedAfterCutoffException），
        // 因此只有"启动期"那一遍不剔死引用武器 ID。
        _itemTableFinal = !string.Equals(phase, "启动期", StringComparison.Ordinal);

        // ★顺序契约：武器池先跑并登记保留集，Fixups.prune 只删「不在 item 表且不在保留集」的死引用。
        RunWeaponPool(phase);

        ApplyChanges();

        var added = PooledChanges() - before;
        RegisterLocaleTransformers(phase);

        // ★合并的核心收益点：任务表改写（含 fixups 与武器池）跑完之后，才让任务链按新表重建并补挂 transformer。
        // 放在 RegisterLocaleTransformers 之后 ⇒ 本模块的 transformer 排在宿主自己的之后，
        // 在 transformer 链里更靠近末端，第三方 transformer 覆盖不到它；
        // 而 LocaleService.GetLocaleDb 的 Postfix 兜底与注册顺序无关，任何情况下都是最后一道。
        _chainHint?.AfterTaskRewrite(phase);

        DumpQuestTable();
        DumpRepeatables();
        PrintSummary(phase, added);
    }

    private int TotalChanges()
    {
        return _nTarget + _nFailNeutralized + _nWeapon + _nWeaponMods + _nDistance + _nTime
               + _nDroppedMap + _nDroppedZone + _nElimValue + _nHandoverValue
               + _nBodyPart + _nSelfGear + _nEnemyGear + _nSelfHealthFx + _nEnemyHealthFx + _nFindInRaid
               + _nRevealed + _nUnknownRewards + _nTimeGates + _nM10 + _nLightkeeper + _nCollector
               + _fixups.TotalApplied;
    }

    /// <summary>跑一遍改写。整遍可重复执行（幂等），DryRun 时只统计不写入。</summary>
    private void ApplyChanges()
    {
        foreach (var quest in _templates.Quests.Values)
        {
            _scanned++;

            var questId = quest.Id.ToString();

            if (_config.ExemptQuests.Contains(questId)
                || (_config.HonorSgtlaggyConfig && _foreign.ExemptQuests.Contains(questId)))
            {
                _skipExempt++;
                continue;
            }

            if (_config.OnlyQuests.Count > 0 && !_config.OnlyQuests.Contains(questId))
            {
                _skipOnly++;
                continue;
            }

            var cfg = _config.ForQuest(questId);

            // removeZone 要把区域限定展开到**这张图**：地图由任务级 location（地图 MongoId）反查。
            // 定位不到（"any" / 自定义串 / 跨图任务）时保持原样，绝不写 ["Any"] 变成「哪张图都算」。
            var questMap = _mapResolver.ResolveQuestMap(quest.Location);

            // ★任务名标记的判定基准：本任务处理之前的累计改动数。必须在任何改写动作之前取。
            var changeBefore = TotalChanges();

            // 任务级处理（QOL 三项 + 三个特殊任务）不依赖 AFF 是否存在，先于条件级执行
            ApplyQuestLevel(quest, cfg);

            var finish = quest.Conditions?.AvailableForFinish;
            if (finish is not null)
            {
                var nTargetBefore = _nTargetWidened;

                foreach (var condition in finish)
                {
                    if (condition is not null)
                    {
                        ProcessCondition(condition, cfg, questMap: questMap, questId: questId);
                    }
                }

                // ★配对中和：本任务刚把某条完成击杀条件放宽成「任意目标」。Fail 组不在改写作用域内，
                //   若它还挂着「击杀 X 即失败」，同一次击杀会同时踩完成与失败两边 ⇒ 任务必败。
                if (_nTargetWidened > nTargetBefore)
                {
                    NeutralizeContradictoryFailConditions(quest, cfg);
                }
            }

            // ★用「计数涨没涨」判定，而不是在二十多个改写点逐个登记：改写点散在目标 / 字段移除 /
            //   数值 / fixups / 配对中和 / QOL / 特例里，逐个登记漏一个就静默少标一批任务，
            //   而这里一处就能覆盖全族。
            // ★口径取 TotalChanges()——它**不含** _poolAddedTotal。★注意武器池确实会改条件本体
            //   （WeaponPoolRunner:465 往 Kills.weapon 里 Add），不是「只改描述」；排除它是**产品口径**
            //   而非技术限制：武器池表达的是「哪些枪算数」，且已在任务描述里以「可用武器」块自述，
            //   再挂到任务名上会把标记覆盖率翻一倍、让标记失去信息量。
            //   要改成「凡动过即标」，把这里换成 PooledChanges() 即可。
            if (TotalChanges() > changeBefore)
            {
                _questNameIds.TryAdd(questId, 0);
            }
        }

        if (_config.AffectRepeatables)
        {
            ApplyToRepeatables();
        }
    }

    /// <summary>
    /// 日常/周常（repeatable）模板：结构与任务同构（conditions.AvailableForFinish），
    /// 用反射取到列表后走与静态任务完全相同的处理逻辑（改模板即改后续生成的日常）。
    /// 作用范围按 sgtlaggy 的「🔃 五类」语义裁剪：只有击杀目标 / 武器与配件 / 部位 / 距离 / 战局内获得进日常，
    /// 地图·区域·时间、装备·状态、数值面板、QOL 与特例都不进（实测对方的 affectRepeatables 只覆盖这五类）。
    /// </summary>
    private void ApplyToRepeatables()
    {
        var templates = _templates.RepeatableQuests?.Templates;
        if (templates is null)
        {
            return;
        }

        var cfg = new TunerConfig
        {
            RemoveTarget = _config.RemoveTarget,
            RemoveWeapon = _config.RemoveWeapon,
            RemoveWeaponMods = _config.RemoveWeaponMods,
            RemoveBodyPart = _config.RemoveBodyPart,
            RemoveDistance = _config.RemoveDistance,
            RemoveFindInRaid = _config.RemoveFindInRaid,
            DryRun = _config.DryRun,
        };

        foreach (var name in new[] { "Elimination", "Completion", "Exploration", "Pickup" })
        {
            try
            {
                var template = templates.GetType().GetProperty(name)?.GetValue(templates);
                var conditions = template?.GetType().GetProperty("Conditions")?.GetValue(template);
                var finish = conditions?.GetType().GetProperty("AvailableForFinish")?.GetValue(conditions) as IEnumerable;
                if (finish is null)
                {
                    continue;
                }

                foreach (var item in finish)
                {
                    if (item is QuestCondition qc)
                    {
                        // 日常/周常模板不跑修正类：作用范围按 sgtlaggy 的「🔃 五类」语义裁剪
                        ProcessCondition(qc, cfg, applyFixups: false);
                    }
                }

                _nRepeatables++;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[QuestTuner41] 日常/周常模板 {name} 处理失败：{ex.Message}");
            }
        }
    }

    /// <summary>处理一条任务级条件（静态任务与日常/周常模板共用同一套逻辑）。</summary>
    private void ProcessCondition(QuestCondition condition, TunerConfig cfg, bool applyFixups = true, string? questMap = null, string? questId = null)
    {
        if (condition.ConditionType is "HandoverItem" or "FindItem")
        {
            // 战局内获得（found-in-raid）标记
            if (cfg.RemoveFindInRaid && condition.OnlyFoundInRaid == true)
            {
                if (!cfg.DryRun)
                {
                    condition.OnlyFoundInRaid = false;
                }

                _nFindInRaid++;
            }

            // 上交物是任务物品/钥匙/钥匙卡时不动数量（与 sgtlaggy 的行为一致，实测 126 处差异即此）
            if (!IsProtectedTarget(condition))
            {
                ApplyCount(condition, cfg.HandoverItemCount, cfg.HandoverItemPercent, ref _nHandoverValue);
            }

            return;
        }

        if (condition.ConditionType != "CounterCreator")
        {
            return;
        }

        var subs = condition.Counter?.Conditions;
        if (subs is null)
        {
            return;
        }

        // 修正类（v0.4.0）先跑：此刻 Location 子条件还是数据里原样的地图清单；
        // 放在后面跑就分不清它是原版地图条件、还是 removeZone 把 InZone 改写来的产物。
        if (applyFixups)
        {
            // 缺 Fixups 键 → 用默认实例（出厂即全开）；显式写 null → 视为整段关闭（与 Safe() 的「什么都不做」一致）
            _fixups.Apply(subs, cfg.Fixups ?? FixupConfig.Off(), cfg.DryRun, _itemTableFinal, _templates.Items, cfg.RemoveTarget);
        }

        var hasKills = false;
        var dropIdx = new List<int>();

        // 删除原因：0=区域 1=地点 2=自身装备 3=自身状态（分别计数）
        var dropKind = new List<int>();

        for (var i = 0; i < subs.Count; i++)
        {
            var sub = subs[i];
            if (sub is null)
            {
                continue;
            }

            switch (sub.ConditionType)
            {
                case "Kills":
                    hasKills = true;
                    ApplyTarget(sub, condition, cfg, questId);
                    ApplyFieldRemovals(sub, cfg);
                    break;

                case "Shots":
                    // 射击类条件也带武器/距离/时间字段（实测 sgtlaggy 同样处理）
                    ApplyFieldRemovals(sub, cfg);
                    break;

                case "Equipment":
                    // 自身装备要求：实测 sgtlaggy 是**整条删除**该子条件（26 个条件），不是清空内容
                    if (cfg.RemoveSelfGear)
                    {
                        dropIdx.Add(i);
                        dropKind.Add(2);
                    }

                    break;

                case "HealthEffect":
                    // 自身状态要求：同样整条删除（实测「生存者之路 - 脱水」等）
                    if (cfg.RemoveSelfHealthEffect)
                    {
                        dropIdx.Add(i);
                        dropKind.Add(3);
                    }

                    break;

                case "InZone":
                    // "区域"条件 = InZone；VisitPlace 是"到访某地"的目标，不属于区域限定（实测 sgtlaggy 不动它）
                    // 语义（2026-09-19 修订）＝「展开到**这张图**」，不是「展开到任意图」：
                    //   ① 同一 counter 里已有 Location 子条件 → InZone 冗余，直接删（地图限定原样保留）；
                    //   ② 否则把 InZone 改写成 Location，target = 该任务所属地图（quest.Location 的地图 MongoId
                    //      经 LocationTable 反查，如 5704e5fa… → "RezervBase"）——玩家在**这张图的任何位置**
                    //      击杀都算、别的图不算；
                    //   ③ 地图定位不到（quest.Location 是 "any"/自定义串，或任务跨多图）→ **原样保留 InZone**，
                    //      宁可继续要求"在区域内击杀"也不跨图解锁（旧版写 target=["Any"] 会变成哪张图都算）。
                    // ★必须同时补 Target：Location 条件的硬性不变量是 target 非空（原版 208 处全带
                    //   target，如 ["Woods"]），而 InZone 只有 zoneIds、没有 target。只改 ConditionType
                    //   会造出「Location 但 target=null」，客户端 ConditionLocationProgressChecker.Test
                    //   里的 `Condition.target.Any(...)` 就抛 ArgumentNullException —— 该异常发生在击杀瞬间
                    //   的 OnEnemyKill 链上（Player.OnDead 内），会打断 OnDead，导致尸体不生成、击杀不
                    //   计数、死亡善后不执行。详见 docs\变异体打不死真凶_QuestTuner41缺Target_20260917.md。
                    if (cfg.RemoveZone)
                    {
                        var hasLocationSibling = false;
                        for (var j = 0; j < subs.Count; j++)
                        {
                            if (j != i && subs[j]?.ConditionType == "Location")
                            {
                                hasLocationSibling = true;
                                break;
                            }
                        }

                        if (hasLocationSibling)
                        {
                            dropIdx.Add(i);
                            dropKind.Add(0);
                        }
                        else if (questMap is null)
                        {
                            // 定位不到地图：这条子条件原样保留，只记账（按内容去重，多个时点不重复计数）
                            var zoneIds = sub.Zones is null ? string.Empty : string.Join(",", sub.Zones);
                            if (_zoneUnresolvedIds.Add($"{sub.Id}|{questId}|{zoneIds}") && _zoneUnresolvedDetail.Count < 40)
                            {
                                _zoneUnresolvedDetail.Add($"任务 {questId ?? "?"} 的区域条件 [{zoneIds}]（保持原样：定位不到所属地图）");
                            }
                        }
                        else
                        {
                            if (!cfg.DryRun)
                            {
                                sub.ConditionType = "Location";
                                sub.Zones = null;
                                // target = 该任务所属地图的 location Id（客户端上报的就是它，如 "RezervBase"）。
                                // 给 List（数组形态，与原版一致）而非 Item：ListOrTConverter 把 IsList
                                // 序列化成数组、IsItem 序列化成裸值，原版 Location 的 target 是数组。
                                sub.Target = new ListOrT<string>(new List<string> { questMap }, null);
                            }

                            _nDroppedZone++;
                        }
                    }

                    break;

                case "Location":
                    if (cfg.RemoveMap)
                    {
                        dropIdx.Add(i);
                        dropKind.Add(1);
                    }

                    break;
            }
        }

        // 击杀数上限存在 CounterCreator 条件级 value 上（不在 Kills 子条件上，实测两版此处一致印证）
        if (hasKills)
        {
            ApplyCount(condition, cfg.EliminationCount, cfg.EliminationPercent, ref _nElimValue);
        }

        for (var j = dropIdx.Count - 1; j >= 0; j--)
        {
            if (!cfg.DryRun)
            {
                subs.RemoveAt(dropIdx[j]);
            }

            switch (dropKind[j])
            {
                case 1:
                    _nDroppedMap++;
                    break;
                case 2:
                    _nSelfGear++;
                    break;
                case 3:
                    _nSelfHealthFx++;
                    break;
                default:
                    _nDroppedZone++;
                    break;
            }
        }
    }

    /// <summary>改写击杀目标。相关分支都带"已达标即跳过"，保证补扫幂等。</summary>
    private void ApplyTarget(QuestConditionCounterCondition sub, QuestCondition counterCondition, TunerConfig cfg, string? questId = null)
    {
        var targetValue = sub.Target?.Item;
        var alreadyAny = string.Equals(targetValue, AnyTarget, StringComparison.Ordinal);
        var hasBossRole = sub.SavageRole is not null && sub.SavageRole.Any();

        if (cfg.RemoveTarget)
        {
            // "移除击杀目标限定" = 目标置任意 + 清掉 savageRole（Boss 角色限定）。
            // 实测 sgtlaggy 同样会清 savageRole（120 处差异即此），故按同一语义实现。
            var hasRole = sub.SavageRole is not null && sub.SavageRole.Any();
            var roleCleared = hasRole;
            if (hasRole && !cfg.DryRun)
            {
                sub.SavageRole = new List<string>();
            }

            if (!alreadyAny || roleCleared)
            {
                if (!cfg.DryRun)
                {
                    sub.Target = new ListOrT<string>(null, AnyTarget);
                }

                // target 真的被改掉才算「放宽」；alreadyAny 时这里只是清角色，不进中和闸门
                if (!alreadyAny)
                {
                    _nTargetWidened++;
                }

                _nTarget++;
                MarkQuestText(counterCondition.Id);
            }
        }
        else if (cfg.ReplaceScavsWithAny && targetValue == SavageTarget && CanRelaxRoleWhitelist(sub.SavageRole, cfg))
        {
            // ★口径（2026-10-20 用户点名的两族）：角色白名单**整族都可放宽**时才放宽 ——
            //   空白名单 → 照旧放宽（原行为）；
            //   全部角色命中 RelaxRolePrefixes（默认 pmcBot=掠夺者 / sectant=邪教徒）→ 放宽并清空白名单；
            //   含 boss* / follower* 等 BOSS 族角色 → 整条保持严格（原 !hasBossRole 闸门的语义）。
            // ★为什么必须清空白名单：客户端对 savageRole 另做一次精确相等匹配
            //   （ConditionHitProgressChecker.cs:164，比对 WildSpawnType 枚举名原文），
            //   只改 target 会留下「只认那一种 AI」的钉子 —— target=Any 而白名单仍钉死一种 AI ＝ 改了等于没改。
            // ★字段不变量（2026-10-20 复核）：target 用 Item 形态（裸值 "Any"，与原版 40 条任务一致，
            //   客户端 ConditionHit.target 是 string）；savageRole 清成 [] 而非 null
            //   （客户端 :164 有 Length != 0 守卫，null 反而会炸）。
            // ★幂等：放宽后 target=Any，补扫时不再命中本分支（与原分支同口径）。
            // ★族放宽（白名单非空且整族命中 relaxRolePrefixes）与普通 Scav 放宽要分开记账：
            //   族放宽把「击杀某个角色」变成「击杀任意目标」，该任务 Fail 组里同侧（Scav 系）的
            //   「击杀某类 AI 即失败」条件会与之矛盾（杀一只丧尸同时满足完成与失败 ⇒ 任务直接失败），
            //   故登记任务 ID，交给 NeutralizeContradictoryFailConditions 额外中和。
            var roleFamilyRelaxed = sub.SavageRole is { Count: > 0 };
            if (!cfg.DryRun)
            {
                sub.Target = new ListOrT<string>(null, AnyTarget);

                if (sub.SavageRole is { Count: > 0 })
                {
                    sub.SavageRole = new List<string>();
                }
            }

            _nTargetWidened++;
            _nTarget++;
            MarkQuestText(counterCondition.Id);

            if (roleFamilyRelaxed)
            {
                _nRoleFamilyRelaxed++;
                if (!string.IsNullOrEmpty(questId))
                {
                    _roleRelaxedQuestIds.Add(questId);
                }
            }
        }
        else if (cfg.ReplacePmcWithAny && targetValue is PmcTarget or BearTarget or UsecTarget)
        {
            if (!cfg.DryRun)
            {
                sub.Target = new ListOrT<string>(null, AnyTarget);

                // ★角色白名单必须同步清空（与 RemoveTarget 分支同一语义）：客户端对 savageRole
                // 另做一次精确相等匹配（ConditionHitProgressChecker.cs:164，比对 WildSpawnType 枚举名原文），
                // 只改 target 会留下「击杀 pmcUSEC」这类钉子 —— target 已是 Any，白名单却仍只认那一种 AI。
                // 实测数据里有 1 条：target=AnyPmc + savageRole=[pmcUSEC]（全库仅此一条）。
                if (sub.SavageRole is { Count: > 0 })
                {
                    sub.SavageRole = new List<string>();
                }
            }

            _nTargetWidened++;
            _nTarget++;
            MarkQuestText(counterCondition.Id);

            if (cfg.HarderPmc && counterCondition.Value.HasValue && !cfg.DryRun)
            {
                // QT-1：与 ApplyCountCore 同一记账，避免补扫把倍率在上次结果上再乘一遍。
                if (_valuedConditionIds.Add(counterCondition.Id.ToString()))
                {
                    counterCondition.Value = (int)Math.Round(counterCondition.Value.Value * cfg.HarderPmcMultiplier);
                }
            }
        }

        ApplyFieldRemovals(sub, cfg);
    }

    /// <summary>
    /// 判定这条击杀条件的角色白名单能否整族放宽成「击杀任意目标」。
    /// 
    /// 规则（2026-10-20 用户口径）：
    /// 1. 白名单为空 → 可放宽（原行为：纯 Scav 任务）。
    /// 2. 白名单里**每个**角色都命中 <c>RelaxRolePrefixes</c>（默认 pmcBot=掠夺者、sectant=邪教徒）→ 可放宽。
    ///    放宽时 target 置 Any 并清空白名单。
    /// 3. 只要含一个不在名单里的角色（典型是 boss* / follower* 等 BOSS 族）→ **整条保持严格**，
    ///    与原 <c>!hasBossRole</c> 闸门语义一致。
    /// 
    /// ★角色名是客户端比对用的 WildSpawnType 枚举名原文，**大小写敏感**，故这里用 OrdinalIgnoreCase 比较前缀，
    ///   但比对时仍按原样枚举（客户端那边是精确相等匹配）。
    /// </summary>
    private static bool CanRelaxRoleWhitelist(List<string>? savageRole, TunerConfig cfg)
    {
        if (savageRole is null || savageRole.Count == 0)
        {
            return true;
        }

        var prefixes = cfg.RelaxRolePrefixes ?? new List<string>();
        if (prefixes.Count == 0)
        {
            return false;
        }

        foreach (var role in savageRole)
        {
            if (string.IsNullOrEmpty(role))
            {
                continue;
            }

            var relaxed = false;
            foreach (var prefix in prefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relaxed = true;
                    break;
                }
            }

            if (!relaxed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 配对中和：完成侧刚被放宽成「任意目标」时，把同一任务 Fail 组里与之矛盾的
    /// 「击杀某类 AI 即失败」条件整条去掉。
    ///
    /// ★为什么必须做：本类只改 <c>AvailableForFinish</c>，Fail 组原样保留（全文无 Fail 引用）。
    /// 实测「陌生人的朋友」（Friend Among Strangers, 66631489acf8442f8b05319f）是
    /// 完成=击杀 7 个 PMC、失败=击杀 1 个 Scav；开启 replacePmcWithAny 后完成侧变成「击杀任意 7 个」，
    /// 失败侧仍是「杀 1 个 Scav」⇒ 杀一只丧尸/狗同时满足两边，任务直接失败。
    /// 同构的另有两条灯塔竞技场任务（完成与失败都是 击杀 10 个 PMC + 装备 + 地点）。
    ///
    /// ★只动「通用」失败条件：savageRole 非空的（击杀 Boss Sanitar / 邪教徒 priest 失败等）一律保留，
    /// 那是任务设计本身要的针对型限制，与完成侧的放宽无关。
    /// ★整条计数器一起删，而不是只摘 Kills 子条件：剩下的 Equipment/Location 子条件会变成
    /// 「带着这身装备站在这张图就失败」的隐形地雷（客户端计数器只要子条件全过就计数）。
    /// </summary>
    private void NeutralizeContradictoryFailConditions(Quest quest, TunerConfig cfg)
    {
        var fail = quest.Conditions?.Fail;
        if (fail is null || fail.Count == 0)
        {
            return;
        }

        var dropIdx = new List<int>();

        for (var i = 0; i < fail.Count; i++)
        {
            var condition = fail[i];
            if (condition?.ConditionType != "CounterCreator")
            {
                continue;
            }

            var subs = condition.Counter?.Conditions;
            if (subs is null || subs.Count == 0)
            {
                continue;
            }

            var genericKill = subs.FirstOrDefault(s =>
                s is not null
                && s.ConditionType == "Kills"
                && s.SavageRole is not { Count: > 0 }
                && KillTargetValue(s.Target) is SavageTarget or PmcTarget or BearTarget or UsecTarget);

            var genericKillTarget = genericKill is null ? null : KillTargetValue(genericKill.Target);

            // ★族放宽任务的额外中和（2026-10-20）：该任务完成侧刚被放宽成「击杀任意目标」，
            //   Fail 组里**带角色白名单**的同侧（Scav 系）失败条件同样会与之矛盾 ——
            //   例如「击杀 assault/cursedAssault/marksman/savage 即失败」遇到完成侧「任意目标」时，
            //   杀一只普通丧尸会同时满足完成与失败 ⇒ 任务直接失败。
            //   只动同侧（target=Savage 系）的；其它侧（杀 PMC 即失败等）保持原样。
            var roleRelaxed = _roleRelaxedQuestIds.Contains(quest.Id.ToString());
            var roleKill = roleRelaxed
                ? subs.FirstOrDefault(s =>
                    s is not null
                    && s.ConditionType == "Kills"
                    && s.SavageRole is { Count: > 0 }
                    && KillTargetValue(s.Target) == SavageTarget)
                : null;

            if (genericKillTarget is null && roleKill is null)
            {
                continue;
            }

            dropIdx.Add(i);
            _nFailNeutralized++;
            if (genericKillTarget is not null)
            {
                Detail($"[QuestTuner41] 配对中和：{quest.QuestName} 的失败条件「击杀 {genericKillTarget} 即失败」"
                       + $"与放宽后的完成条件矛盾，已移除（quest={quest.Id}）");
            }
            else
            {
                Detail($"[QuestTuner41] 配对中和（族放宽）：{quest.QuestName} 的失败条件「击杀带角色白名单的 Scav 即失败」"
                       + $"与放宽后的完成条件矛盾，已移除（quest={quest.Id}）");
            }
        }

        if (cfg.DryRun || dropIdx.Count == 0)
        {
            return;
        }

        // 构造新表 + 整体替换引用，不就地 RemoveAt：补扫跑在请求线程上，
        // 改的是共享模板表的活列表（与 FixupRunner 同一约定）。
        var kept = new List<QuestCondition>(fail.Count - dropIdx.Count);
        for (var i = 0; i < fail.Count; i++)
        {
            if (!dropIdx.Contains(i))
            {
                kept.Add(fail[i]);
            }
        }

        quest.Conditions!.Fail = kept;
    }

    /// <summary>
    /// 取击杀条件的 target 取值：标量形态读 <c>Item</c>，数组形态取首个非空元素。
    /// ★数组形态必须单独处理：<c>ListOrTConverter</c> 读到 StartArray 时走 <c>new ListOrT(list, default)</c>，
    /// <c>Item</c> 恒为 null，只读 Item 会把 "target": ["Savage"] 静默漏判
    /// （同文件 Fixups.EnsureLocationTarget 就是为这个形态写的 IsList 分支）。
    /// </summary>
    private static string? KillTargetValue(ListOrT<string>? target)
    {
        if (target is null)
        {
            return null;
        }

        return target.IsList
            ? target.List?.FirstOrDefault(v => !string.IsNullOrEmpty(v))
            : target.Item;
    }

    /// <summary>按字段移除限定（而非按条件类型）：Kills 与 Shots 都带这些字段。</summary>
    private void ApplyFieldRemovals(QuestConditionCounterCondition sub, TunerConfig cfg)
    {
        if (cfg.RemoveWeapon
            && (ClearIfAny(sub.Weapon as ICollection<string>, cfg.DryRun)
                | ClearIfAny(sub.WeaponCaliber as ICollection<string>, cfg.DryRun)))
        {
            _nWeapon++;
        }

        if (cfg.RemoveWeaponMods)
        {
            if (ClearIfAny(sub.WeaponModsInclusive as ICollection<List<string>>, cfg.DryRun))
            {
                _nWeaponMods++;
            }
            else if (!cfg.DryRun && sub.WeaponModsInclusive is null)
            {
                // 字段缺失时补成空表（与 sgtlaggy 输出一致：null 与 [] 语义相同，但快照可比）
                sub.WeaponModsInclusive = new List<List<string>>();
            }

            if (ClearIfAny(sub.WeaponModsExclusive as ICollection<List<string>>, cfg.DryRun))
            {
                _nWeaponMods++;
            }
            else if (!cfg.DryRun && sub.WeaponModsExclusive is null)
            {
                sub.WeaponModsExclusive = new List<List<string>>();
            }
        }

        if (cfg.RemoveDistance && sub.Distance is not null
            && (sub.Distance.Value != 0 || sub.Distance.CompareMethod != ">="))
        {
            if (!cfg.DryRun)
            {
                sub.Distance.CompareMethod = ">=";
                sub.Distance.Value = 0;
            }

            _nDistance++;
        }

        if (cfg.RemoveTime && sub.Daytime is not null && (sub.Daytime.From != 0 || sub.Daytime.To != 0))
        {
            if (!cfg.DryRun)
            {
                sub.Daytime.From = 0;
                sub.Daytime.To = 0;
            }

            _nTime++;
        }

        // P2：部位 / 敌人装备 / 敌人状态（都挂在 Kills 子条件上）
        if (cfg.RemoveBodyPart && ClearIfAny(sub.BodyPart as ICollection<string>, cfg.DryRun))
        {
            _nBodyPart++;
        }

        if (cfg.RemoveEnemyGear)
        {
            // 实测 sgtlaggy 会把这两个字段补成空表（即使是 null），故这里显式 materialize
            if (sub.EnemyEquipmentInclusive is IList egInc && egInc.Count > 0)
            {
                if (!cfg.DryRun)
                {
                    egInc.Clear();
                }

                _nEnemyGear++;
            }
            else if (!cfg.DryRun && sub.EnemyEquipmentInclusive is null)
            {
                sub.EnemyEquipmentInclusive = new List<List<string>>();
            }

            if (sub.EnemyEquipmentExclusive is IList egExc && egExc.Count > 0)
            {
                if (!cfg.DryRun)
                {
                    egExc.Clear();
                }

                _nEnemyGear++;
            }
            else if (!cfg.DryRun && sub.EnemyEquipmentExclusive is null)
            {
                sub.EnemyEquipmentExclusive = new List<List<string>>();
            }
        }

        if (cfg.RemoveEnemyHealthEffect
            && sub.EnemyHealthEffects is IList ehfx && ehfx.Count > 0)
        {
            if (!cfg.DryRun)
            {
                ehfx.Clear();
            }

            _nEnemyHealthFx++;
        }
    }

    /// <summary>集合非空则清空；返回是否真的处理了（DryRun 只报告不写入）。</summary>
    private static bool ClearIfAny<T>(ICollection<T>? collection, bool dryRun)
    {
        if (collection is null || collection.Count == 0)
        {
            return false;
        }

        if (!dryRun)
        {
            collection.Clear();
        }

        return true;
    }

    /// <summary>
    /// 上交物受保护时跳过数量改写。规则由实测反推：任务物品（_props.QuestItem 标记）
    /// 或钥匙/钥匙卡（两类基类 ID）——与 sgtlaggy 的跳过集完全吻合（126 处 = 108 任务物品 + 18 钥匙类）。
    /// </summary>
    private bool IsProtectedTarget(QuestCondition condition)
    {
        foreach (var tpl in GetTargetTemplates(condition))
        {
            if (string.IsNullOrEmpty(tpl))
            {
                continue;
            }

            try
            {
                if (_templates.Items.TryGetValue(tpl, out var item) && item is not null)
                {
                    if (item.Properties?.QuestItem == true)
                    {
                        return true;
                    }

                    var parent = item.Parent;
                    if (!string.IsNullOrEmpty(parent) && KeyBaseClasses.Contains(parent))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 查表失败按不保护处理
            }
        }

        return false;
    }

    private static IEnumerable<string> GetTargetTemplates(QuestCondition condition)
    {
        var single = condition.Target?.Item;
        if (!string.IsNullOrEmpty(single))
        {
            yield return single;
        }

        var list = condition.Target?.List;
        if (list is null)
        {
            yield break;
        }

        foreach (var tpl in list)
        {
            yield return tpl;
        }
    }

    /// <summary>
    /// 数值面板：固定值优先于百分比；同一条件只处理一次（用 ID 记账），
    /// 否则补扫会把百分比在上次结果上再缩一遍。
    /// </summary>
    private void ApplyCount(QuestCondition condition, double flat, double percent, ref int counter)
    {
        ApplyCountCore(condition.Id, condition.Value, flat, percent, v => condition.Value = v, ref counter);
    }

    private void ApplyCountCore(MongoId id, double? currentValue, double flat, double percent, Action<double> apply, ref int counter)
    {
        if (flat < 0 && percent < 0)
        {
            return;
        }

        var condId = id.ToString();
        if (_valuedConditionIds.Contains(condId))
        {
            return;
        }

        if (!currentValue.HasValue)
        {
            return;
        }

        if (!_config.DryRun)
        {
            // 不截断成 int：原版存在 9999999999 这类超大计数，转 int 会溢出成 2147483647
            if (flat >= 0)
            {
                // QT-2：固定值同样保底 1，配置填 0 时不把计数清零。
                apply(Math.Max(1, Math.Round(flat)));
            }
            else
            {
                apply(Math.Max(1, Math.Round(currentValue.Value * percent / 100.0)));
            }

            _valuedConditionIds.Add(condId);
        }

        counter++;
    }

    // ── P3 特例涉及的任务 / 交易商 / 物品 ID（全部实测自 4.1 基础数据）
    private const string NetworkProviderPart1Id = "625d6ff5ddc94657c21a1625";
    private const string CollectorQuestId = "5c51aac186f77432ea65c552";
    private const string TrgM10Tpl = "673cab3e03c6a20581028bc1";
    private const string FenceId = "579dc571d53a0658a154fbec";

    private static readonly string[] TarkovShooterIds =
    {
        "5bc4776586f774512d07cf05", "5bc479e586f7747f376c7da3", "5bc47dbf86f7741ee74e93b9",
        "5bc480a686f7741af0342e29", "5bc4826c86f774106d22d88b", "5bc4836986f7740c0152911c",
    };

    private static readonly string[] CollectorTraders =
    {
        "54cb50c76803fa8b248b4571", // Prapor
        "54cb57776803fa99248b456e", // Therapist
        "58330581ace78e27b8b10cee", // Skier
        "5935c25fb3acc3127c3d8cd9", // Peacekeeper
        "5a7c2eca46aef81a7ca2145d", // Mechanic
        "5ac3b934156ae10c4430e83c", // Ragman
        "5c0647fdd443bc2504c2d371", // Jaeger
    };

    private static readonly string[] CollectorQuestPrereqs =
    {
        "5c0bde0986f77479cf22c2f8", // A Shooter Born in Heaven
        "5bc480a686f7741af0342e29", // The Tarkov Shooter - Part 4
        "5ae4497b86f7744cf402ed00", // Sew It Good - Part 4
    };

    /// <summary>「或」组：三者任一完成即可。SPT 的任务前置是 AND 语义，实测 sgtlaggy 也把它们
    /// 拆成三个独立的 Quest 条件（顺序：Chemical - Part 4 / Out of Curiosity / Big Customer），
    /// 这里照同一形状实现，保证行为一致。</summary>
    private static readonly string[] CollectorOrQuests =
    {
        "597a0f5686f774273b74f676", // Chemical - Part 4
        "597a160786f77477531d39d2", // Out of Curiosity
        "597a171586f77405ba6887d3", // Big Customer
    };

    /// <summary>任务级处理：QOL 三项 + 三个特殊任务。与条件级改写共用同一份配置与幂等语义。</summary>
    private void ApplyQuestLevel(Quest quest, TunerConfig cfg)
    {
        var questId = quest.Id.ToString();

        // QOL-1 显示隐藏目标：清空条件级 visibilityConditions
        if (cfg.QualityOfLife.RevealAllQuestObjectives)
        {
            foreach (var c in AllConditions(quest))
            {
                if (c.VisibilityConditions is IList vc && vc.Count > 0)
                {
                    if (!cfg.DryRun)
                    {
                        vc.Clear();
                    }

                    _nRevealed++;
                }
            }
        }

        // QOL-2 未知奖励：去掉 unknown 标记（原版基础数据里为 0 条）
        if (cfg.QualityOfLife.RevealUnknownRewards && quest.Rewards is not null)
        {
            foreach (var (status, rewards) in quest.Rewards)
            {
                foreach (var r in rewards ?? [])
                {
                    if (r is null)
                    {
                        continue;
                    }

                    // 实测 sgtlaggy 只处理非 "Started" 状态组的奖励（Started 组保持原样），故这里跳过
                    if (string.Equals(status, "Started", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // 且会把 unknown 显式写成 false（即使原本是 null）
                    // ★计数必须与「真的会改」同门：原来无条件自增，会把已经是 Unknown==false 的任务
                    //   也算成改过一处 ⇒ 任务名被误打标记（审查 P3）。DryRun 下仍要计数（那是预演口径）。
                    if (r.Unknown != false)
                    {
                        if (!cfg.DryRun)
                        {
                            r.Unknown = false;
                        }

                        _nUnknownRewards++;
                    }
                }
            }
        }

        // QOL-3 移除等待期：等待期是前置条件上的 availableAfter 字段（不是一种条件类型——
        // 原版数据 781 处写入、43 处非空、涉及 42 个任务，全是勾在 AvailableForStart 的 Quest/Level 型条件上），归零即解除等待。
        if (cfg.QualityOfLife.RemoveTimeGates)
        {
            foreach (var c in AllConditions(quest))
            {
                if (c.AvailableAfter is not null and not 0)
                {
                    if (!cfg.DryRun)
                    {
                        c.AvailableAfter = 0;
                    }

                    _nTimeGates++;
                }
            }
        }

        // P3-1 灯塔：Network Provider - Part 1 只要等级
        if (cfg.SpecialCases.LightkeeperOnlyRequireLevel > 0 && questId == NetworkProviderPart1Id)
        {
            ApplyLightkeeper(quest, cfg);
        }

        // P3-2 Sako TRG M10 加进 The Tarkov Shooter Part 1–6 的武器列表
        if (cfg.SpecialCases.TarkovShooterM10 && TarkovShooterIds.Contains(questId))
        {
            foreach (var sub in AllKills(quest))
            {
                if (sub.Weapon is ICollection<string> weapons && !weapons.Contains(TrgM10Tpl))
                {
                    if (!cfg.DryRun)
                    {
                        weapons.Add(TrgM10Tpl);
                    }

                    _nM10++;
                }
            }
        }

        // P3-3 收藏家前置回填
        if (cfg.SpecialCases.CollectorPrerequisiteBackport && questId == CollectorQuestId)
        {
            ApplyCollectorPrereqs(quest, cfg);
        }
    }

    private static IEnumerable<QuestCondition> AllConditions(Quest quest)
    {
        foreach (var c in quest.Conditions?.AvailableForStart ?? [])
        {
            if (c is not null)
            {
                yield return c;
            }
        }

        foreach (var c in quest.Conditions?.AvailableForFinish ?? [])
        {
            if (c is not null)
            {
                yield return c;
            }
        }
    }

    private static IEnumerable<QuestConditionCounterCondition> AllKills(Quest quest)
    {
        foreach (var c in quest.Conditions?.AvailableForFinish ?? [])
        {
            foreach (var sub in c?.Counter?.Conditions ?? [])
            {
                if (sub is not null && sub.ConditionType == "Kills")
                {
                    yield return sub;
                }
            }
        }
    }

    /// <summary>灯塔：清掉全部任务/声望前置，只留一个指定等级的 Level 条件。</summary>
    private void ApplyLightkeeper(Quest quest, TunerConfig cfg)
    {
        var afs = quest.Conditions?.AvailableForStart;
        if (afs is null)
        {
            return;
        }

        var level = cfg.SpecialCases.LightkeeperOnlyRequireLevel;
        var hasLevel = false;
        QuestCondition? spare = null;
        var drop = new List<int>();

        for (var i = 0; i < afs.Count; i++)
        {
            var c = afs[i];
            if (c is null)
            {
                continue;
            }

            switch (c.ConditionType)
            {
                case "Level":
                    hasLevel = true;
                    if (!cfg.DryRun && c.Value != level)
                    {
                        c.Value = level;
                        _nLightkeeper++;
                    }

                    break;

                case "Quest":
                case "TraderStanding":
                    spare ??= c;
                    drop.Add(i);
                    break;
            }
        }

        for (var j = drop.Count - 1; j >= 0; j--)
        {
            if (!cfg.DryRun)
            {
                afs.RemoveAt(drop[j]);
            }

            _nLightkeeper++;
        }

        // 原本没有 Level 条件：复用刚被移除条件的 Id 新建一个（避免引入 Id 生成器）。
        // 注意：这一条**不复制** VisibilityConditions —— 实测 sgtlaggy 在这里留 null（与收藏家那批不同）
        if (!hasLevel && !cfg.DryRun && spare is not null)
        {
            afs.Add(new QuestCondition
            {
                DynamicLocale = false,
                ConditionType = "Level",
                Value = level,
                Id = spare.Id,
            });
        }
    }

    /// <summary>收藏家：按 README 的前置表整体重建 AvailableForStart。</summary>
    private void ApplyCollectorPrereqs(Quest quest, TunerConfig cfg)
    {
        var afs = quest.Conditions?.AvailableForStart;
        if (afs is null)
        {
            return;
        }

        if (cfg.DryRun)
        {
            _nCollector = CollectorTraders.Length + 2 + CollectorQuestPrereqs.Length + 1;
            return;
        }

        // 复用被替换条件的 Id 与默认字段（够用：原 136 个前置）
        // 带上 VisibilityConditions 等默认字段，形状才与 sgtlaggy 的输出一致（实测其在新建条件里写了空表）
        var spares = new List<QuestCondition>();
        foreach (var c in afs)
        {
            if (c is not null)
            {
                spares.Add(c);
            }
        }

        var needed = CollectorTraders.Length + 2 + CollectorQuestPrereqs.Length + CollectorOrQuests.Length;
        if (spares.Count < needed)
        {
            _logger.Warning($"[QuestTuner41] 收藏家前置回填跳过：可用空位不足（需要 {needed}，实有 {spares.Count}）");
            return;
        }

        afs.Clear();
        var idx = 0;

        foreach (var trader in CollectorTraders)
        {
            var spare = spares[idx++];
            afs.Add(new QuestCondition
            {
                DynamicLocale = false,
                ConditionType = "TraderLoyalty",
                Target = new ListOrT<string>(null, trader),
                Value = 4,
                Id = spare.Id,
                VisibilityConditions = spare.VisibilityConditions,
            });
        }

        var fence = spares[idx++];
        afs.Add(new QuestCondition
        {
            DynamicLocale = false,
            ConditionType = "TraderStanding",
            Target = new ListOrT<string>(null, FenceId),
            Value = 3,
            Id = fence.Id,
            VisibilityConditions = fence.VisibilityConditions,
        });

        var lvl = spares[idx++];
        afs.Add(new QuestCondition
        {
            DynamicLocale = false,
            ConditionType = "Level",
            Value = 40,
            Id = lvl.Id,
            VisibilityConditions = lvl.VisibilityConditions,
        });

        foreach (var id in CollectorQuestPrereqs)
        {
            var spare = spares[idx++];
            afs.Add(new QuestCondition
            {
                DynamicLocale = false,
                ConditionType = "Quest",
                Target = new ListOrT<string>(null, id),
                Id = spare.Id,
                VisibilityConditions = spare.VisibilityConditions,
            });
        }

        foreach (var id in CollectorOrQuests)
        {
            var spare = spares[idx++];
            afs.Add(new QuestCondition
            {
                DynamicLocale = false,
                ConditionType = "Quest",
                Target = new ListOrT<string>(null, id),
                Id = spare.Id,
                VisibilityConditions = spare.VisibilityConditions,
            });
        }

        _nCollector = afs.Count;
    }

    private void MarkQuestText(MongoId? conditionId)
    {
        if (conditionId is null)
        {
            return;
        }

        var id = conditionId.Value.ToString();
        if (!string.IsNullOrEmpty(id))
        {
            _questTextIds.TryAdd(id, 0);
        }
    }

    /// <summary>
    /// 任务名标记的「最新键集快照」，**三份 transformer 共享**：注册时整体替换引用，绝不原地改内容。
    /// ★为什么必须共享（审查 P2-B-2）：本方法在启动期/启动末段/延迟补扫三个时点各注册一次，
    ///   每族键集只会增长。若每份 transformer 各自捕获注册时点的快照，一个键集更小的早期实例
    ///   一旦在晚期实例之后执行，就会把刚挂上的标记吃掉（实测逆序执行会产出
    ///   「X（武器已优化）（任务已优化）」这类错序/重复结果）。改成读最新快照后，
    ///   「谁先注册、谁先执行」都不影响最终文本。
    /// volatile 只保证引用赋值/读取的可见性与原子性；快照内部集合发布后只读，故无需锁。
    /// </summary>
    private volatile NameMarkerSnapshot? _nameMarker;

    /// <summary>任务名标记的一次性不可变快照（发布后只读）。</summary>
    private sealed class NameMarkerSnapshot
    {
        public string[] Keys = Array.Empty<string>();
        public HashSet<string> Quest = new(StringComparer.Ordinal);
        public HashSet<string> Weapon = new(StringComparer.Ordinal);
        public string QuestSuffix = string.Empty;
        public string WeaponSuffix = string.Empty;
    }

    /// <summary>
    /// 剥掉任务名尾部已有的标记（同一枚可重复出现），供「先剥净再按固定顺序重拼」使用。
    /// 调用方只传**本 transformer 会重拼的那几族**后缀（不认识的族传 null），这是顺序无关性的关键：
    /// 本方法在三个时点各注册一次，每族键集是各自时点的快照且只会增长；若无条件剥两族，
    /// 一个只认识武器族的早期 transformer 会把晚期 transformer 刚挂上的任务标记吃掉
    /// （T1 对「名字+任务标记」不是恒等变换），今天不出事全靠「后注册者键集更大且按注册序执行」。
    /// ★两枚后缀按**长度降序**尝试：若一族后缀恰是另一族的真后缀（病态配置，例如把
    ///   questNameSuffix 配成「已优化）」而 weaponNameSuffix 是「（武器已优化）」），先剥短的那个
    ///   会把长后缀撕成残渣（审查 P2-A-5）。长的先剥即无此风险。出厂默认两枚互不为后缀。
    /// </summary>
    private static string StripNameMarkers(string text, string? suffixA, string? suffixB)
    {
        var first = suffixA;
        var second = suffixB;
        if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second) && second.Length > first.Length)
        {
            (first, second) = (second, first);
        }

        while (true)
        {
            if (!string.IsNullOrEmpty(first) && text.EndsWith(first, StringComparison.Ordinal))
            {
                text = text[..^first.Length];
                continue;
            }

            if (!string.IsNullOrEmpty(second) && text.EndsWith(second, StringComparison.Ordinal))
            {
                text = text[..^second.Length];
                continue;
            }

            return text;
        }
    }

    /// <summary>
    /// 把标记追加到已登记的条件文本。必须走 AddTransformer：补扫阶段新登记的 ID 只有
    /// 挂上 transformer 后，后续 locale 读取才会被加工。
    /// ★4.1 的 LazyLoad<T> 没有任何缓存：Value 每次访问都重新 deserialize 并重跑全部 transformer
    ///   （官方 XML 注释里 "the final result is cached" 是过期描述，类里无 cache 字段）。
    ///   所以每多挂一个 transformer＝每次 locale 读取多跑一遍全表加工，重复挂载直接乘算成本。
    /// 迭代快照：补扫跑在请求线程上，可能与另一个并发请求的 locale 构建撞集合。
    /// </summary>
    /// <summary>
    /// 单一 locale 管道（2026-09-21 并入武器池后确立）。段序固定：
    ///   ① 条件文本追加任务标记（markerSuffix，幂等：已以标记结尾的跳过）— 2026-10-11 起默认关闭
    ///   ③ 任务名追加标记（两族独立可叠加，先剥净再重拼 ⇒ 幂等且与注册顺序无关）：
    ///      「任务优化」questNameSuffix＝条件改写命中；「武器优化」weaponNameSuffix＝武器池真的 Add 过枪
    ///   ② 任务描述追加「可用武器」块（武器池产出，锚点截块、幂等）
    /// 必须走 AddTransformer：Global 的 LazyLoad 有缓存，注册会 Clear() 强制重建，
    /// 否则补扫新增的 ID / 武器池新数据不生效。迭代快照：补扫跑在请求线程上，可能与并发请求的 locale 构建撞集合。
    /// </summary>
    private void RegisterLocaleTransformers(string phase)
    {
        // ★DryRun 的契约是「只预演不写入」。transformer 一注册就会在每次 locale 读取时改写共享字典
        //   （① 目标行标记 / ③ 任务名标记 / ② 可用武器块），那都是写入 —— 所以在 DryRun 下整段不注册，
        //   否则汇总行印着「（DryRun：未写入）」而客户端其实看得见标记。
        //   ★范围限定：这里只管本方法注册的 transformer。ChainHint 的链提示另有自己的注册路径
        //   且不看 DryRun，属既有行为，本次未动（已知差异，见 docs）。
        if (_config.DryRun)
        {
            Detail($"[QuestTuner41] [{phase}] DryRun：跳过本模块的 locale transformer 注册（目标行标记 / 任务名标记 / 可用武器块都不写）");
            return;
        }

        var ids = _questTextIds.Keys.ToArray();
        var marker = _config.MarkerSuffix;
        var markWork = ids.Length > 0 && !string.IsNullOrEmpty(marker);

        // ★键串在这里预拼一次。transformer 每次 locale 读取都会跑，且本方法在
        //   启动期/启动末段/延迟补扫 三个时点各注册一次 ⇒ 若把 Concat 放进循环，
        //   每语言每读一次就要为每个任务重新分配一遍键串（N 可达数百）。LazyLoad 无缓存，代价是实打实的。
        // ★两族标记各自一组键：「任务优化」（条件改写）与「武器优化」（池真的加过枪）是**独立维度**，
        //   同一任务可同时命中，两枚后缀叠加。两族各自的键集在注册时预拼一次（理由同上）。
        var questSuffix = _config.QuestNameSuffix;
        var weaponSuffix = _config.WeaponNameSuffix;
        var questKeySet = string.IsNullOrEmpty(questSuffix)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_questNameIds.Keys.Select(qid => string.Concat(qid, " name")), StringComparer.Ordinal);
        var weaponKeySet = string.IsNullOrEmpty(weaponSuffix)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(_weaponNameIds.Keys.Select(qid => string.Concat(qid, " name")), StringComparer.Ordinal);
        var nameKeys = new HashSet<string>(questKeySet, StringComparer.Ordinal);
        nameKeys.UnionWith(weaponKeySet);
        var nameKeyList = nameKeys.ToArray();
        var nameWork = nameKeyList.Length > 0;
        var descWork = _weaponPoolLast is not null && _weaponPoolLast.DescriptionMods.Count > 0;

        // ★发布最新快照供**所有** transformer 共享（见 _nameMarker 注释）：本方法每注册一次就整体替换，
        //   先前注册的实例下次被调用时取到的也是这一份，故与注册顺序、执行顺序完全无关。
        //   三个集合在这里构建后不再改动，发布后只读，可安全跨线程共享。
        _nameMarker = new NameMarkerSnapshot
        {
            Keys = nameKeyList,
            Quest = questKeySet,
            Weapon = weaponKeySet,
            QuestSuffix = questSuffix,
            WeaponSuffix = weaponSuffix,
        };
        if (!markWork && !nameWork && !descWork)
        {
            return;
        }

        foreach (var (language, lazyLoad) in _localeTable.Global)
        {
            if (lazyLoad is null)
            {
                continue;
            }

            lazyLoad.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return null;
                }

                var marked = 0;
                var named = 0;
                if (markWork)
                {
                    foreach (var id in ids)
                    {
                        if (!localeData.TryGetValue(id, out var text) || string.IsNullOrEmpty(text))
                        {
                            continue;
                        }

                        if (text.EndsWith(marker, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        localeData[id] = text + marker;
                        marked++;
                    }
                }

                // ③ 任务名后缀：两族标记各自独立、可叠加。
                //   「任务优化」＝被本 mod 改写过条件（questNameSuffix）；
                //   「武器优化」＝武器池真的往该任务条件里 Add 过枪（weaponNameSuffix）。
                //   键名格式与 ChainHint 一致＝"<questId> name"（ChainHintInjector.ApplyNameTag 同款）。
                //   ChainHint 那侧只剥/加**前缀**（主线任务、灯塔之匙），与本段的**后缀**互不干扰；
                //   且它的兜底挂在 LocaleService.GetLocaleDb 的 Postfix 上，跑在本 transformer 之后，
                //   顺序无论如何都不会把后缀吃掉。
                //   ★幂等判据不能只写「已以本族后缀结尾就跳过」：本方法在启动期/启动末段/延迟补扫
                //     三个时点各注册一次，而每族的键集是**各自时点的快照**、集合只会增长 ⇒ 一个任务
                //     可能先在 A 时点只进武器族、后在 B 时点两族都进；B 的 transformer 看到的尾巴是
                //     「（武器已优化）」并不以「（任务已优化）」结尾，直接追加就会叠出三枚标记。
                //     故这里先剥净尾巴上已有的**两族**标记，再按固定顺序重拼 —— 与 transformer 的
                //     注册顺序、执行顺序完全无关，任意次数重复执行结果一致。
                // ★键集与后缀一律取「最新已发布快照」，**不用**注册时的闭包捕获（见 _nameMarker 注释）。
                //   本方法在三个时点各注册一次且键集只增不减，若各用各的快照，早期实例后执行时会吃标记。
                var nameSnap = _nameMarker;
                // ★门控判**快照本身**而不是注册期捕获的 nameWork（审查 P3-6）：nameWork 是注册时点的
                //   闭包捕获，虽然「只增集合投影的长度>0」单调故当前无害，但把判据换成调用期的快照长度后
                //   连这层结构脆弱性也消掉。
                if (nameSnap is not null && nameSnap.Keys.Length > 0)
                {
                    var questKeys = nameSnap.Quest;
                    var weaponKeys = nameSnap.Weapon;
                    var questSuffixNow = nameSnap.QuestSuffix;
                    var weaponSuffixNow = nameSnap.WeaponSuffix;
                    var bothSuffixEqual = string.Equals(questSuffixNow, weaponSuffixNow, StringComparison.Ordinal);

                    foreach (var nameKey in nameSnap.Keys)
                    {
                        if (!localeData.TryGetValue(nameKey, out var questName) || string.IsNullOrEmpty(questName))
                        {
                            // 缺键任务（实测 55 个）没有名字可挂，跳过即可，不算失败
                            continue;
                        }

                        var hasQuest = questKeys.Contains(nameKey);
                        var hasWeapon = weaponKeys.Contains(nameKey);

                        // 只剥本 transformer 会重拼的那几族（见 StripNameMarkers 注释）
                        var core = StripNameMarkers(questName,
                                                    hasQuest ? questSuffixNow : null,
                                                    hasWeapon ? weaponSuffixNow : null);

                        // 两族后缀被用户配成同一串时只挂一枚，否则会叠出「（已优化）（已优化）」（审查 P2-G-1）
                        var wanted = hasQuest && hasWeapon && bothSuffixEqual
                            ? questSuffixNow
                            : (hasQuest ? questSuffixNow : string.Empty) + (hasWeapon ? weaponSuffixNow : string.Empty);

                        // core 恒为 questName 的前缀 ⇒ 等值判定可先比长度与尾部，省掉为绝大多数键白造两个串
                        // 的分配（审查 P3-F-1）。与直接比 rebuilt == questName 完全等价。
                        if (core.Length + wanted.Length == questName.Length
                            && questName.EndsWith(wanted, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        localeData[nameKey] = core + wanted;
                        named++;
                    }
                }

                // ② 描述「可用武器」块：按运行时读取最近一遍的武器池结果（快照数组，避免与请求线程写撞集合）
                var descWritten = 0;
                var descSnapshot = _weaponPoolLast?.DescriptionMods;
                var requiredSnapshot = _weaponPoolLast?.RequiredMods;
                if (descSnapshot is not null)
                {
                    var maxNames = _weaponPoolConfig.MaxWeaponNames;
                    foreach (var pair in descSnapshot.ToArray())
                    {
                        var names = pair.Value;
                        if (names is null || names.Count == 0)
                        {
                            continue;
                        }

                        if (!localeData.TryGetValue(pair.Key, out var original) || original is null)
                        {
                            continue;
                        }

                        // ★任务原始要求（扩充前）：原版描述只写「这把枪」，不点名，玩家无从得知该用哪把。
                        //   存的是武器 ID，这里按当前 locale 解析成「名字 + ID」二元组交给渲染函数。
                        List<(string Name, string Id)>? requiredNames = null;
                        if (requiredSnapshot is not null && requiredSnapshot.TryGetValue(pair.Key, out var reqIds))
                        {
                            requiredNames = new List<(string, string)>(reqIds.Count);
                            foreach (var rid in reqIds)
                            {
                                var rname = _weaponPool.GetItemNameForDescription(rid);
                                if (!string.IsNullOrEmpty(rname))
                                {
                                    requiredNames.Add((rname, rid));
                                }
                            }
                        }

                        var updated = WeaponPoolRunner.BuildDescriptionText(original, names, maxNames, requiredNames);
                        if (string.Equals(updated, original, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        localeData[pair.Key] = updated;
                        descWritten++;
                    }
                }

                if (_config.Debug)
                {
                    _logger.Info($"[QuestTuner41] locale[{language}] transformer 已触发：目标行标记 {marked} 处 / 任务名标记 {named} 处（任务族 {_nameMarker?.Quest.Count ?? 0} 键 / 武器族 {_nameMarker?.Weapon.Count ?? 0} 键）/ 描述块 {descWritten} 条");
                }

                return localeData;
            });
        }

        Detail($"[QuestTuner41] [{phase}] 已注册 locale transformer：{_localeTable.Global.Count} 种语言 / 标记 {ids.Length} 个条件文本 / 任务名 {nameKeyList.Length} 个（任务族 {questKeySet.Count} / 武器族 {weaponKeySet.Count}）/ 描述块 {(_weaponPoolLast?.DescriptionMods.Count ?? 0)} 条"
                     + (_config.Debug ? $"（样例 ID：{string.Join(", ", ids.Take(3))}）" : string.Empty));
    }

    /// <summary>导出任务表（条件部分）供外部做等价对照；未配置路径时不产生任何开销。</summary>
    private void DumpQuestTable()
    {
        var path = _config.DumpQuestTablePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var project = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (id, quest) in _templates.Quests)
            {
                project[id.ToString()] = new Dictionary<string, object?>
                {
                    ["aff"] = ProjectConditions(quest.Conditions?.AvailableForFinish),
                    ["afs"] = ProjectConditions(quest.Conditions?.AvailableForStart),
                    ["rewards"] = ProjectRewards(quest),
                };
            }

            var dir = IOPath.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(project));
            _logger.Info($"[QuestTuner41] 任务表已导出：{path}（{project.Count} 个任务）");
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] 任务表导出失败：{ex.Message}");
        }
    }

    /// <summary>导出用：条件列表投影（AFF 与 AFS 共用）。</summary>
    private static List<object?> ProjectConditions(IEnumerable<QuestCondition>? conditions)
    {
        var list = new List<object?>();
        foreach (var condition in conditions ?? [])
        {
            if (condition is null)
            {
                continue;
            }

            list.Add(new Dictionary<string, object?>
            {
                ["id"] = condition.Id.ToString(),
                ["type"] = condition.ConditionType,
                ["value"] = condition.Value,
                ["availableAfter"] = condition.AvailableAfter,
                ["target"] = condition.Target?.Item,
                ["targets"] = condition.Target?.List,
                ["onlyFoundInRaid"] = condition.OnlyFoundInRaid,
                ["visibility"] = CountOf(condition.VisibilityConditions),
                ["subs"] = ProjectSubs(condition),
            });
        }

        return list;
    }

    private static List<object?> ProjectSubs(QuestCondition condition)
    {
        var subs = new List<object?>();
        foreach (var sub in condition.Counter?.Conditions ?? [])
        {
            if (sub is null)
            {
                continue;
            }

            subs.Add(new Dictionary<string, object?>
            {
                ["id"] = sub.Id.ToString(),
                ["type"] = sub.ConditionType,
                ["value"] = sub.Value,
                ["target"] = sub.Target?.Item,
                ["targets"] = sub.Target?.List,
                ["savageRole"] = sub.SavageRole,
                ["weapon"] = sub.Weapon,
                ["caliber"] = sub.WeaponCaliber,
                ["modsIncl"] = sub.WeaponModsInclusive,
                ["modsExcl"] = sub.WeaponModsExclusive,
                ["zones"] = sub.Zones,
                ["distance"] = sub.Distance is null ? null : $"{sub.Distance.CompareMethod}:{sub.Distance.Value}",
                ["daytime"] = sub.Daytime is null ? null : $"{sub.Daytime.From}-{sub.Daytime.To}",
                ["bodyPart"] = sub.BodyPart,
                ["enemyEquipIncl"] = sub.EnemyEquipmentInclusive,
                ["enemyEquipExcl"] = sub.EnemyEquipmentExclusive,
                ["enemyHealthFx"] = CountOf(sub.EnemyHealthEffects),
                ["equipIncl"] = CountOf(sub.EquipmentInclusive),
                ["equipExcl"] = CountOf(sub.EquipmentExclusive),
                ["healthFx"] = CountOf(sub.BodyPartsWithEffects),
            });
        }

        return subs;
    }

    private static List<object?> ProjectRewards(Quest quest)
    {
        var list = new List<object?>();
        if (quest.Rewards is null)
        {
            return list;
        }

        // Rewards 是按结果分组的字典（Success / Fail …），每组是奖励条目列表
        foreach (var (status, rewards) in quest.Rewards)
        {
            foreach (var reward in rewards ?? [])
            {
                if (reward is null)
                {
                    continue;
                }

                var entry = new Dictionary<string, object?> { ["status"] = status, ["unknown"] = reward.Unknown };
                try
                {
                    entry["json"] = JsonSerializer.Serialize(reward);
                }
                catch
                {
                    entry["json"] = null;
                }

                list.Add(entry);
            }
        }

        return list;
    }

    private static int? CountOf(object? collection)
    {
        return collection is ICollection c ? c.Count : null;
    }

    /// <summary>导出日常/周常模板条件（供与 sgtlaggy 的 affectRepeatables 做等价对照）。</summary>
    private void DumpRepeatables()
    {
        var path = _config.DumpRepeatablesPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var templates = _templates.RepeatableQuests?.Templates;
            var project = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var name in new[] { "Elimination", "Completion", "Exploration", "Pickup" })
            {
                var list = new List<object?>();
                var template = templates?.GetType().GetProperty(name)?.GetValue(templates);
                var conditions = template?.GetType().GetProperty("Conditions")?.GetValue(template);
                var finish = conditions?.GetType().GetProperty("AvailableForFinish")?.GetValue(conditions) as IEnumerable;

                if (finish is not null)
                {
                    foreach (var item in finish)
                    {
                        if (item is not QuestCondition qc)
                        {
                            continue;
                        }

                        var subs = new List<object?>();
                        foreach (var sub in qc.Counter?.Conditions ?? [])
                        {
                            if (sub is null)
                            {
                                continue;
                            }

                            subs.Add(new Dictionary<string, object?>
                            {
                                ["id"] = sub.Id.ToString(),
                                ["type"] = sub.ConditionType,
                                ["value"] = sub.Value,
                                ["target"] = sub.Target?.Item,
                                ["targets"] = sub.Target?.List,
                                ["savageRole"] = sub.SavageRole,
                                ["weapon"] = sub.Weapon,
                                ["caliber"] = sub.WeaponCaliber,
                                ["modsIncl"] = sub.WeaponModsInclusive,
                                ["modsExcl"] = sub.WeaponModsExclusive,
                                ["zones"] = sub.Zones,
                                ["distance"] = sub.Distance is null ? null : $"{sub.Distance.CompareMethod}:{sub.Distance.Value}",
                                ["daytime"] = sub.Daytime is null ? null : $"{sub.Daytime.From}-{sub.Daytime.To}",
                            });
                        }

                        list.Add(new Dictionary<string, object?>
                        {
                            ["id"] = qc.Id.ToString(),
                            ["type"] = qc.ConditionType,
                            ["value"] = qc.Value,
                            ["target"] = qc.Target?.Item,
                            ["subs"] = subs,
                        });
                    }
                }

                project[name] = list;
            }

            var dir = IOPath.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(project));
            _logger.Info($"[QuestTuner41] 日常/周常模板已导出：{path}");
        }
        catch (Exception ex)
        {
            _logger.Error($"[QuestTuner41] 日常/周常模板导出失败：{ex.Message}");
        }
    }

    private void ReportForeign()
    {
        if (_foreign.DetectedOthers.Count > 0)
        {
            Detail($"[QuestTuner41] 检测到其它可能改任务的 mod：{string.Join("；", _foreign.DetectedOthers)}（仅提示，未做适配）");
        }

        if (!_foreign.Loaded)
        {
            return;
        }

        var parts = new List<string>();
        if (_foreign.RemoveTarget is true)
        {
            parts.Add("removeTarget=true（与我们的泛化开关重叠，两者结果等价）");
        }

        if (_foreign.AffectsRepeatables is true)
        {
            parts.Add("affectRepeatables=true（日常/周常归它，本 mod 不处理）");
        }

        parts.Add($"exemptQuests={_foreign.ExemptQuests.Count} 条"
                  + (_config.HonorSgtlaggyConfig ? "（已并入本 mod 的跳过判定）" : "（honorSgtlaggyConfig=false，未采纳）"));

        if (_foreign.OnlyQuests.Count > 0)
        {
            parts.Add($"onlyQuests={_foreign.OnlyQuests.Count} 条（语义与本 mod 不同，未套用）");
        }

        Detail($"[QuestTuner41] 检测到 sgtlaggyQuestTweaks：{string.Join("；", parts)}");
    }

    /// <summary>末段层是否已执行（三层时点的中间档）；用于在它被 SPT 升级搞没时补一条 Warning 心跳。</summary>
    private volatile bool _lateStageRan;

    /// <summary>供 LateRegistration 调用（同一实例）：记录末段层已执行。</summary>
    internal void MarkLateStageRan() => _lateStageRan = true;

    /// <summary>供 LateRegistration 调用（同一实例）：按 debug 开关决定 Info/Debug。</summary>
    internal void DetailLine(string message) => Detail(message);

    /// <summary>逐项明细：只有 debug=true 才进日志文件（默认走 Debug 级；SPT 的文件日志阈值为 Information）。</summary>
    private void Detail(string message)
    {
        if (_config.Debug)
        {
            _logger.Info(message);
        }
        else
        {
            _logger.Debug(message);
        }
    }

    /// <summary>
    /// 汇总输出。默认（debug=false）每个启动周期只留 1 行：**启动末段**那次必打（三层时点的收口，累计值即最终值）；
    /// 启动期是过程值不打；延迟补扫只有真补改了东西才再打一行。逐项明细（P1/P2/P3、修正类、未展开逐条、跳过、日常）
    /// 一律走 <see cref="Detail"/>（默认 Debug 级 → 不落盘）。debug=true 时恢复原行为（每时点都打汇总 + 逐项 Info）。
    /// </summary>
    private void PrintSummary(string phase, int added)
    {
        var verbose = _config.Debug;
        var isStartup = string.Equals(phase, "启动期", StringComparison.Ordinal);
        var isLateStage = string.Equals(phase, "启动末段", StringComparison.Ordinal);
        // ★标记数非空时也必须落盘（审查 review5 P3-7）：原条件在「启动末段层未执行 + 启动期 added==0」
        //   组合下会让整行只进 Debug，「关了/没关一目了然」在这些场景下仍落空。
        // ★但启动期行仍然不打（审查 review6 P3-3）：标记计数只增、恒非空，不排掉启动期就会每次启动多一行，
        //   且那行印的是过程值（本遍改动 N 处），与方法语义「启动期是过程值不打」冲突。
        var hasMarkers = _questNameIds.Count > 0 || _weaponNameIds.Count > 0;
        var printSummary = verbose || isLateStage || (!isStartup && (added > 0 || hasMarkers));

        var tail = new List<string>();
        if (_zoneUnresolvedIds.Count > 0)
        {
            tail.Add($"未展开 {_zoneUnresolvedIds.Count}");
        }

        if (_skipExempt > 0 || _skipOnly > 0)
        {
            tail.Add($"跳过 排除 {_skipExempt}/白名单外 {_skipOnly}");
        }

        if (_config.AffectRepeatables)
        {
            tail.Add($"日常 {_nRepeatables}");
        }

        // G4 冲突检测：默认也要留痕（每启动一次，成本为零）
        if (_foreign.DetectedOthers.Count > 0)
        {
            tail.Add($"其它改任务 mod {_foreign.DetectedOthers.Count}");
        }

        if (_foreign.Loaded)
        {
            tail.Add("sgtlaggy 名单在读");
        }

        // 任务名标记：累计口径，与武器池同理（幂等集合只有首遍有增量，单遍读法会让整行消失）
        // 两族分开报数：任务优化（条件改写）与武器优化（池真的加过枪）是独立维度，可同时命中。
        // ★后缀被关掉时也要报数（审查 P3-G-4）：否则「关掉标记」与「一个都没命中」在日志里长得一样，
        //   排查时分不清是配置生效还是功能失效。
        if (_questNameIds.Count > 0)
        {
            tail.Add(string.IsNullOrEmpty(_config.QuestNameSuffix)
                ? $"任务名标记 {_questNameIds.Count}（后缀已关闭）"
                : $"任务名标记 {_questNameIds.Count}");
        }
        if (_weaponNameIds.Count > 0)
        {
            tail.Add(string.IsNullOrEmpty(_config.WeaponNameSuffix)
                ? $"武器名标记 {_weaponNameIds.Count}（后缀已关闭）"
                : $"武器名标记 {_weaponNameIds.Count}");
        }

        // 武器池（并入自 ANWQ）：累计口径（幂等池只有首遍 Added>0，单遍读法会让整行消失）
        if (_poolAddedTotal > 0)
        {
            tail.Add($"武器池 +{_poolAddedTotal}");
        }
        if (_poolMissing > 0)
        {
            tail.Add($"池缺失 {_poolMissing}");
        }
        if (_poolSameModelGroups > 0)
        {
            // 口径是「映射条数」不是「注入把数」：同一把枪在多个同名组里各算一条，
            // 且大多数 key 可能根本没有任务点名。故意与前面的「武器池 +N」区分开写「条」。
            tail.Add($"同型号兜底 {_poolSameModelGroups} 组/{_poolSameModelTargets} 条映射");
        }

        var detail = $"目标 {_nTarget}、武器 {_nWeapon}、配件 {_nWeaponMods}、距离 {_nDistance}、时间 {_nTime}"
                     + (_nFailNeutralized > 0 ? $"、配对中和(删失败条件) {_nFailNeutralized}" : string.Empty)                      + (_nRoleFamilyRelaxed > 0 ? $"、角色族放宽 {_nRoleFamilyRelaxed}" : string.Empty)
                     + $"、移除地点 {_nDroppedMap}、移除区域 {_nDroppedZone}、击杀数 {_nElimValue}、上交数 {_nHandoverValue}"
                     + (_fixups.TotalApplied > 0
                         ? $"、修正类 角色 +{_fixups.RolesAdded}/地图 +{_fixups.MapsAdded}/死引用武器 -{_fixups.WeaponIdsPruned}"
                         + $"/非法 target +{_fixups.TargetsFixed}/Location 补 target +{_fixups.LocationTargetsFixed}"
                         : string.Empty)
                     + (tail.Count > 0 ? "；" + string.Join("；", tail) : string.Empty);

        var line = $"[QuestTuner41] [{phase}] 扫描 {_scanned} 个任务 / 本遍改动 {added} 处 / 累计 {PooledChanges()} 处（{detail}）"
                   + (_config.DryRun ? "（DryRun：未写入）" : string.Empty);

        if (printSummary)
        {
            _logger.Info(line);
        }
        else
        {
            _logger.Debug(line);
        }

        // ── 以下逐项明细：默认只进 Debug（不落盘），debug=true 时与原行为一致
        Detail($"[QuestTuner41] [{phase}] 明细：目标 {_nTarget}、武器 {_nWeapon}、配件 {_nWeaponMods}、距离 {_nDistance}、时间 {_nTime}、"
               + $"移除地点 {_nDroppedMap}、移除区域 {_nDroppedZone}、击杀数 {_nElimValue}、上交数 {_nHandoverValue}");
        Detail($"[QuestTuner41] [{phase}] P2 明细：部位 {_nBodyPart}、自身装备 {_nSelfGear}、敌人装备 {_nEnemyGear}、"
               + $"自身状态 {_nSelfHealthFx}、敌人状态 {_nEnemyHealthFx}、战局内获得 {_nFindInRaid}、"
               + $"显示隐藏目标 {_nRevealed}、未知奖励 {_nUnknownRewards}、移除等待期 {_nTimeGates}");
        if (_nM10 > 0 || _nLightkeeper > 0 || _nCollector > 0)
        {
            Detail($"[QuestTuner41] [{phase}] P3 明细：TRG M10 {_nM10} 处、灯塔前置 {_nLightkeeper} 处、收藏家前置 {_nCollector} 条");
        }

        if (_fixups.TotalApplied > 0)
        {
            Detail($"[QuestTuner41] [{phase}] 修正类明细（v0.4.0）：角色白名单 +{_fixups.RolesAdded}、地图白名单 +{_fixups.MapsAdded}、"
                   + $"死引用武器 ID -{_fixups.WeaponIdsPruned}、非法 target 修正 {_fixups.TargetsFixed}、"
                   + $"Location 补 target +{_fixups.LocationTargetsFixed}");
        }

        if (_zoneUnresolvedIds.Count > 0)
        {
            if (!_zoneUnresolvedReported)
            {
                _zoneUnresolvedReported = true;
                for (var i = 0; i < _zoneUnresolvedDetail.Count && i < 40; i++)
                {
                    Detail($"[QuestTuner41] [{phase}] removeZone 未展开：{_zoneUnresolvedDetail[i]}");
                }
            }

            Detail($"[QuestTuner41] [{phase}] removeZone：任务级 location 定位不到地图的区域条件 {_zoneUnresolvedIds.Count} 处，已保留原区域限定（不跨图展开）");
        }

        if (_skipExempt > 0 || _skipOnly > 0)
        {
            Detail($"[QuestTuner41] [{phase}] 跳过：排除清单 {_skipExempt} 个任务、白名单外 {_skipOnly} 个任务");
        }

        if (_config.AffectRepeatables)
        {
            Detail($"[QuestTuner41] [{phase}] 日常/周常模板已处理 {_nRepeatables} 个");
        }
    }
}
