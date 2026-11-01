// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace QuestTuner41;

/// <summary>
/// 配置读取的统一选项与错误定位。
///
/// 加固背景（2026-10-09，玩家反馈）：<c>config.json</c> 是给玩家手改的文件，实测两类毛病最常见 ——
/// <list type="number">
///   <item>对象/数组最后一项留了尾逗号（手删一项时留下的）；</item>
///   <item>把两段配置拼在一起，顶层 <c>}</c> 之后又多出一个逗号。</item>
/// </list>
/// 前者以前会让整份配置作废（mod 走 Safe 静默全关）；后者以前只打 STJ 原文，
/// 而它的 LineNumber 是 <b>0 起算</b>，玩家照抄「第 64 行」去文件里数永远差一行。
/// 现在：① 选项允许尾逗号；② 解析失败时行号换算成 1 起算并带出该行原文。
/// </summary>
internal static class ConfigJson
{
    /// <summary>反序列化用（主配置）：允许 // 与 /* */ 注释、允许尾逗号。</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>按分区取键时用（武器池、第三方配置）：语义与 <see cref="SerializerOptions"/> 保持一致。</summary>
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>出错行前后各带一行即可定位，别把整份配置倒进日志。</summary>
    private const int ContextRadius = 1;

    /// <summary>长行以出错点为中心开窗时，出错点前面留这么多字符。</summary>
    private const int WindowLead = 40;

    /// <summary>单行超过这么长就截断（excludeMods 那类长数组行不该刷屏）。</summary>
    private const int ContextLineMax = 160;

    /// <summary>
    /// 把解析异常变成「玩家照着就能改」的一句话：
    /// 原始消息 + 1 起算的**行号** + 出错行原文（行首 <c>&gt;</c> 标记），前后各一行便于看结构。
    /// ★列号故意不打印（STJ 给的是字节偏移，中文行会误导），只用于长行的开窗定位。
    /// 非 JSON 异常（例如读文件失败）原样返回消息，不假装有位置。
    /// </summary>
    internal static string DescribeError(string? text, Exception ex)
    {
        var sb = new StringBuilder(ex.Message);
        if (text is null || ex is not JsonException json || json.LineNumber is not long zeroBasedLine)
        {
            return sb.ToString();
        }

        var lines = text.Split('\n');
        var index = (int)Math.Clamp(zeroBasedLine, 0, Math.Max(0, lines.Length - 1));
        sb.Append(" ｜ 出错位置：第 ").Append(index + 1).Append(" 行");

        // 列号不直接打印：STJ 的 BytePositionInLine 是**字节**偏移（且是出错 token 之后的已消耗量），
        // 本文件通篇中文注释，当成字符列报给玩家会偏大一截，反而添乱。只在下面开窗时内部用一下。
        var byteColumn = json.BytePositionInLine is long col ? (int)Math.Clamp(col, 0, int.MaxValue) : (int?)null;

        for (var i = Math.Max(0, index - ContextRadius); i <= Math.Min(lines.Length - 1, index + ContextRadius); i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var hint = i == index && byteColumn is int bytes ? ColumnToCharIndex(raw, bytes) : (int?)null;
            sb.Append('\n').Append(i == index ? "> " : "  ").Append(i + 1).Append("| ").Append(Excerpt(raw, hint));
        }

        return sb.ToString();
    }

    /// <summary>字节列 → 字符下标：STJ 给的是字节偏移，中文行直接当字符列会指错位置。</summary>
    private static int ColumnToCharIndex(string line, int byteColumn)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            return Encoding.UTF8.GetCharCount(bytes, 0, Math.Min(byteColumn, bytes.Length));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 长行截断：默认从行首取；出错点落在 160 字符之外时改为以它为中心开窗（两端加省略号）。
    /// 否则 excludeMods 那种长数组行报错时，玩家盯着的前 160 字符里恰好没有出错的那个字符。
    /// </summary>
    private static string Excerpt(string line, int? errorCharIndex)
    {
        if (line.Length <= ContextLineMax)
        {
            return line;
        }

        if (errorCharIndex is not int hint || hint <= ContextLineMax - WindowLead)
        {
            return line[..ContextLineMax] + "…";
        }

        var start = Math.Clamp(hint - WindowLead, 0, line.Length - ContextLineMax);
        return (start > 0 ? "…" : string.Empty)
            + line.Substring(start, ContextLineMax)
            + (start + ContextLineMax < line.Length ? "…" : string.Empty);
    }
}
