// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTuner41;

/// <summary>
/// 任务级 \`Quest.Location\` → 客户端上报的**地图 Id**（也就是 \`Location\` 条件 target 要写的值）。
///
/// 背景：\`removeZone\` 要把「在某个区域击杀」展开成「在这张图击杀」，就必须知道这张图是哪张——
/// 原版数据里这类条件（同 counter 内没有 Location 兄弟的 InZone，共 28 处）只带 zoneIds、不带地图，
/// 唯一线索是任务级 \`location\` 字段里的**地图 MongoId**（如 RezervBase = 5704e5fad2720bc05b8b4567）；
/// \`"any"\` / \`"marathon"\` 这类自定义串定位不到，调用方走保守分支（保留原区域限定）。
///
/// 映射来源 = 运行时 \`LocationTable\`：遍历各 location 的 \`Base.IdField\`（\`_Id\`，地图 MongoId）
/// 与 \`Base.Id\`（"Woods"/"bigmap"/"RezervBase"…，与 base.json 的 Id 字段逐字一致，也正是原版
/// Location 条件 target 里写的值，如 ["Woods"]）。这样连 mod 新增地图也自动覆盖，且不依赖
/// \`configs/quest.json\` 的 locationIdMap（那份表只有 11 张图，缺 Labyrinth/Terminal/Town/Suburbs 等）。
/// </summary>
internal sealed class MapResolver
{
    // 与 Fixups 同款：\`\z\` 而非 \$（.NET 的 \$ 允许匹配结尾前的换行）
    private static readonly Regex MongoIdPattern = new(@"^[0-9a-fA-F]{24}\z", RegexOptions.Compiled);

    private readonly LocationTable _locations;
    private Dictionary<string, string>? _byMongoId;

    public MapResolver(LocationTable locations)
    {
        _locations = locations;
    }

    /// <summary>地图 MongoId（不区分大小写）→ location Id；表建不起来时返回 null（调用方走保守分支）。</summary>
    private Dictionary<string, string>? Table
    {
        get
        {
            if (_byMongoId is not null)
            {
                return _byMongoId;
            }

            var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var location in _locations.GetDictionary().Values)
                {
                    var locationBase = location?.Base;
                    if (locationBase is null)
                    {
                        continue;
                    }

                    var mongoId = locationBase.IdField.ToString();
                    if (string.IsNullOrEmpty(mongoId) || string.IsNullOrEmpty(locationBase.Id))
                    {
                        continue;
                    }

                    built[mongoId] = locationBase.Id;
                }
            }
            catch
            {
                // 表结构异常（SPT 升级 / DI 变动）→ 不猜，全部按「定位不到」处理
                return null;
            }

            if (built.Count == 0)
            {
                return null;
            }

            _byMongoId = built;
            return _byMongoId;
        }
    }

    /// <summary>
    /// \`Quest.Location\` → 地图 Id（如 "RezervBase"）；null／"any"／自定义串／表里查不到 → null。
    /// </summary>
    public string? ResolveQuestMap(string? questLocation)
    {
        if (string.IsNullOrWhiteSpace(questLocation))
        {
            return null;
        }

        var value = questLocation.Trim();
        if (!MongoIdPattern.IsMatch(value))
        {
            // "any" / "marathon" 等：不是地图 MongoId，说明这张图定位不到
            return null;
        }

        var table = Table;
        return table is not null && table.TryGetValue(value, out var mapId) ? mapId : null;
    }
}
