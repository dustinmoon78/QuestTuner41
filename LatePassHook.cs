// SPDX-License-Identifier: AGPL-3.0-or-later
using HarmonyLib;

namespace QuestTuner41;

/// <summary>
/// 延迟补扫钩子：反射定位 SPT.Server 的请求管线中间件（SptLoggerMiddleware），
/// 在首个客户端请求上触发一次补扫。HTTP 只在全部启动阶段完成后才开始接受连接，
/// 因此该时点必然晚于一切启动期 mod——比 TypePriority / MLOM 位次更可靠。
/// 找不到目标类型时返回 false，安静降级为"仅启动期一遍"。
/// </summary>
public static class LatePassHook
{
    private static Action? _latePass;
    private static int _fired;

    public static bool Install(Action latePass)
    {
        try
        {
            var type = AccessTools.TypeByName("SPTarkov.Server.Middleware.SptLoggerMiddleware");
            var mi = type?.GetMethods()
                .Where(m => m.Name is "InvokeAsync" or "Invoke")
                .OrderBy(m => m.IsGenericMethod ? 1 : 0)
                .FirstOrDefault();
            if (type == null || mi == null)
            {
                return false;
            }

            _latePass = latePass;
            new Harmony("com.zcode.questtuner41.latepass").Patch(
                mi,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(LatePassHook), nameof(Prefix))));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Prefix()
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _latePass?.Invoke();
        }
    }
}
