// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Quests;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Commerce;
using QuestTuner41.WeaponPool;

namespace QuestTuner41.AcceptNotify;

/// <summary>
/// 接取任务后，除商人原本那条接取消息外，额外再发一条「本任务可用武器」清单消息。
///
/// 消息文本由宿主通过 <c>messageProvider(questId)</c> 提供（典型实现：
/// <c>id =&gt; WeaponPoolRunner.BuildAcceptMessage(report, id, cfg)</c>）；本类只负责
/// 「接任务时调它、非空就发信」，找不到目标方法时安静降级返回 false。
///
/// 锚点 = <see cref="QuestCallbacks.AcceptQuest"/> 的 postfix（路由 QuestAccept 的第一层）：
/// 该方法在本类里唯一（无重载），显式参数类型查找不会有 AmbiguousMatch 风险；参数直接给出
/// 被接取的任务 ID（<c>AcceptQuestRequestData.QuestId</c>）；日常/周常也从这里分派
/// （type == "repeatable"）一并覆盖，因武器池未给它们注入武器、清单为空时自然不发消息。
/// postfix 的语义是「原流程成功后」才发：前置校验失败时不会误发。
///
/// ★与 ANWQ 的差异（本签名没有 TemplateTable，拿不到 <c>quest.TraderId</c>）：
/// 改为第二个钩子——<see cref="QuestHelper.GetQuestFromDb"/> 的 postfix 顺手把
/// 「questId → TraderId」记进一张小表，发信时按 questId 取回商人，语义与 ANWQ
/// （清单消息落回任务所属商人的对话）完全一致，只多挂一个钩子。
///
/// 全程 try/catch——本功能是锦上添花，任何异常都不得影响玩家接取任务。
/// </summary>
public static class QuestAcceptNotifyPatch
{
    private const string HarmonyId = "com.zcode.questtuner41.acceptnotify";

    /// <summary>questId → TraderId 的小缓存上限（只用于发信时取商人；超限整体清空，避免无界增长）。</summary>
    private const int TraderCacheLimit = 8192;

    private static readonly ConcurrentDictionary<string, string> QuestTraders = new(StringComparer.OrdinalIgnoreCase);

    private static volatile Func<string, string?>? _messageProvider;
    private static MailSendService? _mailSendService;
    private static ISptLogger<WeaponPoolRunner>? _logger;
    private static int _sent;
    private static int _skipped;
    private static int _installed;

    /// <summary>已发出的清单消息条数（供宿主汇总行取用）。</summary>
    public static int SentCount => Volatile.Read(ref _sent);

    /// <summary>因「无清单 / 无商人」跳过的次数（供宿主汇总行取用）。</summary>
    public static int SkippedCount => Volatile.Read(ref _skipped);

    /// <summary>
    /// 挂补丁。★宿主接线点：日志器类型是 <c>ISptLogger&lt;WeaponPoolRunner&gt;</c>
    /// （SPT 4.1.6 只有泛型 <c>ISptLogger&lt;T&gt;</c>，没有非泛型 ISptLogger；DI 容器的开放泛型注册可直接解析，
    /// 与 <see cref="WeaponPoolRunner"/> 共用同一个实例即可）。
    /// </summary>
    /// <param name="logger">日志器（只有 Debug/Error 两处输出）。</param>
    /// <param name="mailSendService">SPT 寄信服务。</param>
    /// <param name="messageProvider">questId → 消息正文；返回 null/空表示本条不发。</param>
    /// <param name="enabled">功能开关（= config 的 sendWeaponListOnQuestAccept）；false 时不挂钩子并返回 false。</param>
    /// <returns>true=钩子已装；false=未装（功能关闭或目标方法不存在）。</returns>
    public static bool Install(
        ISptLogger<WeaponPoolRunner> logger,
        MailSendService mailSendService,
        Func<string, string?> messageProvider,
        bool enabled)
    {
        _logger = logger;
        _mailSendService = mailSendService;
        _messageProvider = messageProvider;

        if (!enabled)
        {
            return false;
        }

        // 防重复挂载：Install 被调两次会重复挂同 id postfix（清单消息会发两遍）
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return true;
        }

        try
        {
            var acceptQuest = AccessTools.Method(
                typeof(QuestCallbacks),
                nameof(QuestCallbacks.AcceptQuest),
                [typeof(PmcData), typeof(AcceptQuestRequestData), typeof(MongoId)]);
            var questLookup = AccessTools.Method(
                typeof(QuestHelper),
                nameof(QuestHelper.GetQuestFromDb),
                [typeof(MongoId), typeof(PmcData)]);

            if (acceptQuest == null || questLookup == null)
            {
                logger?.Warning($"[综合任务优化] 接取任务清单消息未启用："
                                 + $"{(acceptQuest == null ? "QuestCallbacks.AcceptQuest " : string.Empty)}"
                                 + $"{(questLookup == null ? "QuestHelper.GetQuestFromDb " : string.Empty)}未找到");
                return false;
            }

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(
                questLookup,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(QuestAcceptNotifyPatch), nameof(QuestLookupPostfix))));
            harmony.Patch(
                acceptQuest,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(QuestAcceptNotifyPatch), nameof(AcceptQuestPostfix))));
            return true;
        }
        catch (Exception ex)
        {
            // 安静降级：只记一行 Error，返回 false；不影响接取任务
            logger?.Error($"[综合任务优化] 接取任务清单消息补丁启用失败: {ex}");
            return false;
        }
    }

    /// <summary>宿主在配置热更新后重新指一条消息提供者（不影响已挂的钩子）。</summary>
    public static void SetProvider(Func<string, string?> messageProvider)
    {
        _messageProvider = messageProvider;
    }

    /// <summary>
    /// 记下「任务 → 所属商人」。AcceptQuest 内部会调它取任务定义，
    /// 于是发信那一步不必依赖 TemplateTable；其它流程（完成任务/建档）也会顺手填，无害。
    /// </summary>
    private static void QuestLookupPostfix(MongoId questId, Quest? __result)
    {
        try
        {
            if (__result is null)
            {
                return;
            }

            if (QuestTraders.Count >= TraderCacheLimit)
            {
                QuestTraders.Clear();
            }

            QuestTraders[questId.ToString()] = __result.TraderId.ToString();
        }
        catch
        {
            // 取商人失败绝不影响任务流程
        }
    }

    private static void AcceptQuestPostfix(PmcData pmcData, AcceptQuestRequestData info, MongoId sessionID)
    {
        try
        {
            var mail = _mailSendService;
            var provider = _messageProvider;
            if (mail == null || provider == null || info?.QuestId is null)
            {
                return;
            }

            var questId = info.QuestId.ToString();
            var message = provider(questId);
            if (string.IsNullOrEmpty(message))
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            // 任务所属商人：清单消息与接取消息落在同一个商人对话里
            if (!QuestTraders.TryGetValue(questId, out var trader) || string.IsNullOrEmpty(trader))
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            mail.SendDirectNpcMessageToPlayer(
                sessionID,
                trader,
                MessageType.QuestStart,
                message,
                null);

            var sent = Interlocked.Increment(ref _sent);
            _logger?.Debug($"[综合任务优化] 已发送任务武器清单消息：任务 {questId}（累计 {sent} 条，跳过 {SkippedCount} 条）");
        }
        catch (Exception ex)
        {
            // 绝不向上抛：接取任务本身不能被这条附赠消息搞失败
            _logger?.Error($"[综合任务优化] 发送任务武器清单消息失败（不影响接取）: {ex}");
        }
    }
}
