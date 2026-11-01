// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTuner41.ChainHint;

/// <summary>
/// 任务链模块的编排层。职责只有四件：
///   1) 读配置：优先 config.json 的 "ChainHint" 分区；检测到更早那份独立 mod 的 config.jsonc 就整段覆盖它（向后兼容）；
///   2) 旧 mod 目录检测：user/mods/QuestChainHint41 还在就打 Warning，提示会重复注入链条文本；
///   3) 建注入器 + 注册 locale transformer（**只注册一次**）+ 装 GetLocaleDb 的 Postfix 兜底；
///   4) 在宿主改写任务表**之后**重建链条快照（本次合并的核心收益）。
///
/// 不注册任何 [Injectable]：本类由宿主手工 new 出来，
/// 这样它天然落在宿主的三层时点（启动期 / 启动末段 / 首请求补扫）之内，不会另起一条加载链。
/// </summary>
public sealed class ChainHintRunner
{
    private const string LogPrefix = "任务链：";

    /// <summary>更早那份独立 mod 的目录名（存在即视为老用户没卸载干净）。</summary>
    private const string LegacyModFolder = "QuestChainHint41";

    /// <summary>旧 mod 的配置文件名。jsonc，带注释与行尾注释。</summary>
    private const string LegacyConfigFileName = "config.jsonc";

    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Action<string> _error;

    private ChainHintInjector? _injector;
    private ChainHintLog? _log;
    private int _rebuilds;

    public bool Enabled => _injector is not null;

    public ChainHintRunner(Action<string> info, Action<string> warn, Action<string> error)
    {
        _info = info;
        _warn = warn;
        _error = error;
    }

    private void Info(string message) => _info(LogPrefix + message);

    private void Warn(string message) => _warn(LogPrefix + message);

    private void Err(string message) => _error(LogPrefix + message);

    /// <summary>
    /// 启动期初始化。必须在宿主第一遍改写之前调用。
    /// </summary>
    /// <param name="config">config.json 里的 ChainHint 分区（可能为 null，用内置默认）。</param>
    /// <param name="templates">任务模板表。</param>
    /// <param name="localeTable">locale 表。</param>
    /// <param name="modsRoot">user/mods 目录，用于定位旧 mod 目录与其 config.jsonc。</param>
    public void Initialize(ChainHintConfig? config, TemplateTable templates, LocaleTable localeTable, string modsRoot)
    {
        var cfg = config ?? new ChainHintConfig();

        // ① 旧 mod 目录检测。永远可见（工作区铁律：Warning 永不静默）。
        var legacyDir = Path.Combine(modsRoot, LegacyModFolder);
        var legacyModPresent = Directory.Exists(legacyDir);
        if (legacyModPresent)
        {
            Warn($"检测到旧独立 mod 目录 {legacyDir}：它与本 mod 会各自往任务描述注入一遍链条文本，造成前缀重复。"
                 + "功能已并入本 mod，请删除（或移出）该目录；它的 config.jsonc 会在本次启动时被读入并覆盖 ChainHint 分区。");
        }

        // ② 旧 config.jsonc 向后兼容：老用户从旧包升级，config.json 里还没有 ChainHint 分区，
        //    不导入就等于把他们的调参（颜色 / 文案 / 开关 / 黑名单）静默扔回默认值。
        if (legacyModPresent)
        {
            ImportLegacyConfig(cfg, Path.Combine(legacyDir, LegacyConfigFileName));
        }

        // Normalize 必须在建注入器之前：Text/Color/Tag/三个 List 被写成 null 时在这里补回默认值，
        // 否则注入期会空引用（System.Text.Json 读到 null 是不抛异常的）。
        cfg.Normalize();
        _log = new ChainHintLog(cfg.EffectiveLogLevel, Info, Err);

        // 旧配置里普遍留着 ConsoleLog=true，它现在被 LogLevel 盖过了。
        // 不提示的话，用户会看到「配了 summary 却还是老样子」而不知道原因。
        if (cfg.LegacyConsoleLogIgnored)
        {
            Warn($"配置里同时存在旧开关 ConsoleLog={cfg.ConsoleLog} 与新开关 LogLevel={cfg.LogLevel?.Trim()}，"
                 + $"已按 {cfg.EffectiveLogLevel} 执行；要开回全量请把 LogLevel 改成 full 或删掉旧键");
        }

        if (!cfg.Enabled)
        {
            _log.Summary("已在 config.json 的 ChainHint 分区中禁用（Enabled=false），不注册 transformer");
            return;
        }

        try
        {
            var injector = new ChainHintInjector(cfg, templates, _log);
            var languages = injector.Register(localeTable);
            ChainHintLocalePostfix.Injector = injector;
            _injector = injector;

            _log.Summary(
                $"已注册 {languages} 种语言的 transformer（每种语言只挂一次，重复挂载会被门控挡掉）；"
                + $"链条快照随任务表改写自动重建（位置={cfg.InsertPosition}）；日志档位={cfg.EffectiveLogLevel}");

            // 只靠 transformer 不够：别的 mod 会在首请求时把自己的 transformer 追加到队尾，
            // 而游戏只在启动时拉一次 locale —— 那一次必然被盖掉。这里再挂一道「返回后补齐」。
            if (ChainHintLocalePostfix.Install())
            {
                _log.Summary("已在 LocaleService.GetLocaleDb 上加装响应末段补齐（不受后注册 transformer 覆盖影响）");
            }
            else
            {
                Warn("响应末段补齐未装上，仅依赖 transformer 注入，链条文本可能被后注册的 transformer 覆盖。原因："
                     + (ChainHintLocalePostfix.InstallError ?? "未知"));
            }
        }
        catch (Exception ex)
        {
            Err($"初始化失败，本模块本次不工作：{ex}");
            _injector = null;
            ChainHintLocalePostfix.Injector = null;
        }
    }

    /// <summary>
    /// 任务表被宿主改写完成后的收口。**这是并入之后的核心收益点。**
    ///
    /// 两个 mod 各自独立加载时，「链数据构建」与「任务表改写」的先后完全不可控：
    /// 灯塔前置清空 / 收藏家前置重建一旦先于链数据构建发生，快照里就留着改写前的旧前置，
    /// 于是出现「任务明明已被放开、界面仍写着前置任务」的自相矛盾。
    /// 合并后宿主在每一遍改写跑完都回调这里，按**当前**任务表重建，前置/后续/等级判定必然吃到改写后的表。
    ///
    /// ★这里只重建快照，**不再补挂 transformer**：4.1 的 LazyLoad 每次读取都会重跑全部 transformer，
    /// 每多挂一个就多一整轮全表注入。早先版本在这里补挂，是每语言累计 6 个 transformer 的根源。
    /// 补扫阶段新增的 locale 键由响应末段那道 Postfix 兜住，与注册顺序无关。
    ///
    /// 三个时点（启动期 / 启动末段 / 首个客户端请求补扫）都会走到这里，每次都重建一次；
    /// 幂等，重复调用无害。
    /// </summary>
    public void AfterTaskRewrite(string phase)
    {
        var injector = _injector;
        var log = _log;
        if (injector is null || log is null)
        {
            return;
        }

        try
        {
            var data = injector.RebuildDataNow();
            var rounds = Interlocked.Increment(ref _rebuilds);

            log.Summary(
                $"[{phase}] 任务表改写后已按当前表重建链条快照：任务模板 {data.QuestIds.Length} 个，"
                + $"本次起服第 {rounds} 次重建（transformer 启动期只挂一次，此处不补挂）");
        }
        catch (Exception ex)
        {
            // 失败永远可见：宁可这一轮链条用旧快照，也不能让整条改写链断掉。
            Err($"任务表改写后的链条重建失败（不影响其它功能）：{ex}");
        }
    }

    /// <summary>
    /// 读旧 config.jsonc 并整段覆盖 ChainHint 分区。
    /// 旧文件是 jsonc（带注释与行尾注释），必须开注释与尾逗号容忍，否则读不出来。
    /// </summary>
    private void ImportLegacyConfig(ChainHintConfig target, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string? text = null;
        try
        {
            // 选项统一走 ConfigJson（注释 + 尾逗号容忍），与主配置一套语义，别再各写一份。
            text = File.ReadAllText(path, Encoding.UTF8);
            var legacy = JsonSerializer.Deserialize<ChainHintConfig>(text, ConfigJson.SerializerOptions);
            if (legacy is null)
            {
                Warn($"旧配置 {path} 内容为空，ChainHint 分区维持 config.json 里的值");
                return;
            }

            target.OverrideFrom(legacy);
            Info($"已从旧 mod 配置 {path} 导入并覆盖 config.json 的 ChainHint 分区（老用户调参不丢）");
        }
        catch (Exception ex)
        {
            Err($"读取旧配置 {path} 失败，ChainHint 分区维持 config.json 里的值：{ConfigJson.DescribeError(text, ex)}");
        }
    }
}
