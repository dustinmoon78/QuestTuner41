// SPDX-License-Identifier: AGPL-3.0-or-later
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace QuestTuner41;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.zcode.questtuner41";

    // ★2026-09-19 并入 AddNewWeaponsToQuest41 后改显示名（GUID 保持不变：老用户 cfg/汉化按 GUID 继承）
    // ★2026-10-08 正式发布：去掉内测序号「BE 版 1」，显示名回正式名。版本串不动（1.0.0 本就是线上正式号，
    //    BE 序号只挂显示名、不占正式版本号序列，见本文件 Version 字段注释）。
    // ★改名有代价：MLOM 的 mod_load_order 按 Name 匹配，老条目会脱钩 —— 改名同批必须同步改 MLOM 配置
    //   （主环境 00MLOM\mod_load_order.json 共 2 处：活动加载数组 + modInfo 段）。
    public string Name { get; init; } = "综合任务优化";

    public string Author { get; init; } = "DSH";

    // ★2026-10-08：本 mod 全部代码为独立编写，不含任何第三方源码，故 Contributors 留空。
    //   下面两行只是功能思路 / 呈现形式的来源，不是代码来源，登记在《安装必看》的
    //   【实现信息 / 授权】一节里，玩家可查：
    //     · 任务链提示的呈现形式参考了 HiddenHiragi 的 4.0「永恒HUD」
    //     · 击杀任务可用武器的功能思路受 hcy 的 AddNewWeaponsToQuest 启发
    //   ★注意 SPT 的 ModValidator 会把本字段打到起服日志里，写第三方名字容易被误读成"该 mod 由其移植"。
    public List<string>? Contributors { get; init; } = [];

    public bool HasPrepatcher { get; init; }

    // 0.5.0 → 1.0.0：并入任务链提示后本 mod 功能面首次对外完整发布（该版本号是线上正式号，BE 序号不占序列）
    // 1.0.0 → 1.1.0：修「开启 replacePmcWithAny 后杀丧尸直接失败」——完成条件被放宽成「任意目标」时，
    //   Fail 组里配对的「击杀某类 AI 即失败」会与完成侧同时成立（实测：陌生人的朋友）；
    //   本版起完成侧放宽时同步移除该配对失败条件，并让 PMC 分支同步清空 savageRole。
    //   ★走次版本号而非补丁号：它会整条移除一批失败条件，属功能面变化（与 v1.0.1 内部迭代版的判断不同）。
    // 1.1.0 → 1.1.1（2026-10-09）：配置读取加固 —— ① 容忍尾逗号（以前会让整份配置作废、mod 静默全关）；
    //   ② 解析失败时把 STJ 的 0 起算行号换算成 1 起算，并带出出错行前后原文，玩家能自己定位。
    //   行为面变化只涉及「配置读不读得进来」，不动任务改写逻辑，故走补丁号而非次版本号。
    // 1.1.1 → 1.2.0（2026-10-11）：任务名「已优化」标记 —— 被本 mod 改写过条件的任务，在**任务名**
    //   尾部追加标记（新键 questNameSuffix，出厂「（已优化）」）。判定口径＝该任务任一条件改写命中
    //   （击杀目标 / 限时限图限区域 / 数量 / 角色白名单 / 配对中和 / QOL / 特例），描述里的「可用武器」
    //   块不算。旧的 markerSuffix（任务目标行尾 " (S4A)"）同步降级为默认关：它是暗号且在列表页看不到，
    //   而任务名是玩家接取前唯一能看到的显眼位置。★显示名本次不动 —— 改名会连累 MLOM 的
    //   mod_load_order（按 Name 匹配，2 处），留到打包发布那一步与 BE 版序号一起处理。
    // 1.2.0 → 1.3.0（2026-10-11，用户裁定「任务优化是任务优化，武器优化是武器优化」）：任务名标记拆成
    //   **两个独立维度**，可同时命中、两枚叠加：
    //     ①「任务优化」questNameSuffix＝被改写过条件（原 1.2.0 口径不变，出厂文案由「（已优化）」改为
    //       「（任务已优化）」以与武器族对称、避免玩家把「已优化」误读成武器那一族）；
    //     ②「武器优化」weaponNameSuffix＝武器池**真的往该任务条件里 Add 过枪**（新键，出厂「（武器已优化）」）。
    //   ★归因口径用 WeaponPoolReport.AddedQuestIds，**不是** AcceptLists —— 后者门控是「任务有模组枪
    //     可用」，早已列全模组枪的任务也会进去，会误标一大批。两族标记改走「先剥净尾部两族标记、再按
    //     固定顺序重拼」：transformer 在启动期/末段/补扫三个时点各注册一次、键集是各自时点的快照且只增
    //     不减，只判「已以本族后缀结尾」会在后注册者那里叠出重复标记。
    //   两族后缀都并进 ChainHint 的 NameStripTexts（漏掉武器族＝重演 1.2.0 那个 P1 泄漏）。
    // 1.3.0 → 1.4.0（2026-10-22，用户报「Katarina 的任务枪械优化没生效」）：武器池「同型号 + 原型兜底」。
    //   问题：池子的映射键永远是「克隆基底」，任务若直接点名一个模组自建变体（DWV / SOCOM / WTT / Epic…，
    //   它们没有克隆声明），池子永远 TryGetValue 不到它 —— 任务扩不动、也不挂「（武器已优化）」，
    //   玩家看到的就是「功能没生效」。实测 Katarina 24 个任务里 4 个扩过、5 个指定自制枪的一个没扩。
    //   修法：新增 sameModelFallback（出厂开），分组键两级 ——
    //     ① 有容器 prefab 的按 bundle 名归原型（模组变体普遍复用原版枪的模型容器，实测 M4A1 一族 38 件）；
    //     ② 没有 prefab 的自制模型，退回「内部名 + 武器大类」同型号判定；
    //   组内**双向**放行。两个键都必须带 weapClass：裸 bundle 分组实测有 5 组混进不同大类。
    //   ★时序：必须排在 AutoNewGroups 之后并补跑一遍遍历。克隆族的键是「基底」，兜底若先给同名基底
    //     建了窄 key，点名原版枪的任务白名单会从整个克隆族缩成同名件（审查 P1）。
    //   ★DWV 运行时生成 id 只当「被点名方」、不当候选（AutoScan 已把它们放进 ExcludedIds）：
    //     分组时不能排除它们（任务点名的正是这批），写候选时必须剔除（否则一次塞几十把进白名单）。
    public SemanticVersioning.Version Version { get; init; } = new(1, 4, 0);

    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }

    public string? Url { get; init; }

    public bool IsBundleMod { get; init; }

    // 2026-10-08：MIT → CC BY-NC-SA 4.0。允许自由使用与改造，但禁止商用，且衍生作品须同协议发布。
    // 前提是 ChainHint/ 已整体重写为原创（10-06），无第三方代码血缘。第三方设计来源的署名写在《安装必看》
    //   的【实现信息 / 授权】里；Contributors 刻意留空——SPT 会把它打进起服日志，写第三方名字易被误读成「由其移植」。
    public string License { get; init; } = "AGPL-3.0-or-later";
}
