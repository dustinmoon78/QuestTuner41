// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using HarmonyLib;

namespace QuestTuner41.ChainHint;

/// <summary>
/// locale 端点的最后一道保险。
///
/// SPT 的 /client/locale/&lt;lang&gt; 走 LocaleService.GetLocaleDb，内部取 LazyLoad.Value，
/// 而 LazyLoad 会把全部 transformer 按**注册顺序**跑一遍，谁最后注册谁的值就是最终值。
/// 刻印者（Operator Traits）会在「启动期 / 启动末段 / 首请求补扫」三层各追加一次自己的 transformer，
/// 三层都晚于本模组加载期的注册；而**游戏只在启动时拉一次 locale**，那一次恰好会被它盖掉。
///
/// 所以这里不去跟它抢注册顺序，而是在 GetLocaleDb **返回之后**再写一遍。
/// 该时点必然晚于所有 transformer，与谁先注册无关，因此首次请求就是最终结果。
/// 用反射按类型名定位（与刻印者同一手法），避免编译期耦合 SPT 内部命名空间。
///
/// ★HarmonyId 与更早那份独立 mod 的补丁 id 刻意不同，避免老用户没删干净旧目录时
/// 两个补丁抢同一个 patch 槽。
/// </summary>
internal static class ChainHintLocalePostfix
{
    internal const string HarmonyId = "com.zcode.questtuner41.chainhint.localepostfix";

    private const string TargetTypeName = "SPTarkov.Server.Core.Services.Locales.LocaleService";

    private const string TargetMethodName = "GetLocaleDb";

    private static int _installAttempted;
    private static ChainHintInjector? _injector;

    /// <summary>由 <see cref="ChainHintRunner"/> 在建好注入器后挂上；未启用时保持 null，Postfix 直接空转。</summary>
    internal static ChainHintInjector? Injector
    {
        get => _injector;
        set => _injector = value;
    }

    /// <summary>装不上时的原因，交给调用方打进日志 —— 失败必须可感知，不能只剩一个 false。</summary>
    internal static string? InstallError { get; private set; }

    public static bool Install()
    {
        // 幂等：只试一次。失败也不重试，理由是重复安装补丁比少一道保险更糟，
        // 而失败原因已经存在 InstallError 里，由调用方报成 Warning。
        if (Interlocked.Exchange(ref _installAttempted, 1) != 0)
        {
            return false;
        }

        try
        {
            var type = AccessTools.TypeByName(TargetTypeName);
            var method = type?.GetMethod(
                TargetMethodName,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                [typeof(string)],
                null);

            if (method is null)
            {
                InstallError = $"未找到 {TargetTypeName}.{TargetMethodName}(string)（SPT 改了方法名或可见性？）";
                return false;
            }

            new Harmony(HarmonyId).Patch(
                method,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ChainHintLocalePostfix), nameof(Postfix))));

            return true;
        }
        catch (Exception ex)
        {
            InstallError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>参数名必须与目标方法一致（Harmony 按名注入），language 可空是因为 GetLocaleDb 的入参是 string?。</summary>
    private static void Postfix(string? language, ref Dictionary<string, string> __result)
    {
        var injector = _injector;
        if (injector is null || __result is null)
        {
            return;
        }

        injector.Enhance(language ?? "?", __result);
    }
}
