// SPDX-License-Identifier: AGPL-3.0-or-later
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace QuestTuner41;

/// <summary>
/// 启动末段这一层：TypePriority 取 int.MaxValue，在启动回调链里排到最后
/// （MLOM 会把未登记 mod 追加到同组末尾，未装 MLOM 时按优先级排也仍是最后）。
///
/// 存在的意义是版本韧性：首请求补扫依赖服务端请求管线的类型名（SptLoggerMiddleware），
/// SPT 升级改掉它就会退化；有了这一层，即便钩子失效，仍能在启动链尾覆盖到
/// "启动期之后才注册任务"的 mod。三层共用同一套幂等逻辑，重复执行不会重复改写。
/// </summary>
[Injectable(InjectionType.Singleton, int.MaxValue, TypePriority = int.MaxValue)]
public class QuestTuner41LateRegistration : IOnLoad
{
    private readonly ISptLogger<QuestTuner41LateRegistration> _logger;
    private readonly QuestTuner41 _main;

    public QuestTuner41LateRegistration(ISptLogger<QuestTuner41LateRegistration> logger, QuestTuner41 main)
    {
        _logger = logger;
        _main = main;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        _main.MarkLateStageRan();
        _main.DetailLine("[QuestTuner41] 启动末段层已触发（三层时点的中间档）");
        _main.RunOnce("启动末段");
        return Task.CompletedTask;
    }
}
