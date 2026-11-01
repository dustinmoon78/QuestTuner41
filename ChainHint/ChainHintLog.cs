// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;

namespace QuestTuner41.ChainHint;

/// <summary>
/// 任务链模块的日志档位。
/// Quiet   —— 只报异常；
/// Summary —— 启动摘要 + 注入里程碑（默认）；
/// Full    —— 逐轮构建的完整诊断明细（排查时临时开）。
/// </summary>
public enum ChainHintLogLevel
{
    Quiet = 0,
    Summary = 1,
    Full = 2,
}

/// <summary>
/// 分级日志出口，外加一个「限次但不静默」的异常上报器。
///
/// 为什么要分档：同一次起服里 locale 会被读多次，而 4.1 的 LazyLoad 每读一次就重新反序列化
/// 并重跑一遍全部 transformer，所以「已注入 N 条 / 缺描述键 M 条」这类数字在起服过程中反复抖动。
/// 这些中间态没有行动价值，长期该留在日志里的只有【稳定态结论】与【异常】。
///
/// Error 任何档位都打、且走 Error 级别：降噪只降明细，不能把失败信息一起降掉。
/// </summary>
public sealed class ChainHintLog
{
    /// <summary>
    /// 同类异常的重复节奏：第 1 次必报，之后每 <see cref="ErrorRepeatEvery"/> 次再报一条。
    /// ★不能做成「同类异常此后完全静默」——那是把失败藏起来，违反本工作区「Error 永不静默」。
    /// 折中办法是限次，但每条限次日志都要带上「第几次、合并了几次」，让看日志的人知道它还在持续发生。
    /// </summary>
    private const int ErrorRepeatEvery = 50;

    private readonly ChainHintLogLevel _level;
    private readonly Action<string> _info;
    private readonly Action<string> _error;

    /// <summary>按「场景 + 异常类型 + 异常消息」分别计数，不共用一个全局闩锁：
    /// 共用闩锁会把两条不同异常路径的口径压成一样，那不是降噪是失真。</summary>
    private readonly ConcurrentDictionary<string, int> _errorCounts = new(StringComparer.Ordinal);

    public ChainHintLog(ChainHintLogLevel level, Action<string> info, Action<string> error)
    {
        _level = level;
        _info = info;
        _error = error;
    }

    public ChainHintLogLevel Level => _level;

    /// <summary>常驻摘要：启动一次性信息、注入里程碑。Summary 及以上才打。</summary>
    public void Summary(string message)
    {
        if (_level >= ChainHintLogLevel.Summary)
        {
            _info(message);
        }
    }

    /// <summary>逐轮诊断明细：只有 Full 档才打。默认档位下调用点连字符串都不拼。</summary>
    public void Diagnostic(string message)
    {
        if (_level >= ChainHintLogLevel.Full)
        {
            _info(message);
        }
    }

    /// <summary>异常：任何档位都打，且走 Error 级别。</summary>
    public void Error(string message)
    {
        _error(message);
    }

    /// <summary>
    /// 异常上报（限次不静默）。同一个异常反复出现时，第 1 次与每 <see cref="ErrorRepeatEvery"/> 次各报一条；
    /// 两条不同的异常互不影响。限次只在「同一条异常刷屏」这一种场景生效，
    /// 不会掩盖任何一种新的失败原因。
    /// </summary>
    public void ReportThrottled(string scenario, Exception ex)
    {
        var key = string.Concat(scenario, "|", ex.GetType().Name, "|", ex.Message);
        var count = _errorCounts.AddOrUpdate(key, 1, (_, previous) => previous + 1);

        if (count > 1 && count % ErrorRepeatEvery != 0)
        {
            return;
        }

        var note = count == 1
            ? string.Empty
            : $"（本次起服第 {count} 次；按每 {ErrorRepeatEvery} 次一条汇总，中间 {count - 1} 次同类错误已合并）";

        _error($"{scenario}：{ex.GetType().Name}: {ex.Message}{note}");
    }
}
