using System.Text.Json;
using System.Text.Json.Serialization;

namespace MilkAvalon.Services;

/// <summary>
/// 预设清单的磁盘索引：<c>&lt;projectm 根&gt;/presets.index.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 9795 个 <c>.milk</c> 递归枚举在机械盘/网络盘上要好几秒，而这份清单只在预设增删后才变。
/// 所以第一次扫完把结果写成索引文件，之后启动直接读它；**只有**用户点「重新扫描」才会再扫盘。
/// </para>
/// <para>
/// 文件里存的是**相对路径**（相对扫描目录），数据根整个搬走也不会失效；同时记录目录与时间，
/// 目录对不上（比如设置里换了数据根）就判为无效，退回扫盘。
/// </para>
/// <para>
/// 用 STJ 源生成（<see cref="PresetIndexJsonContext"/>），AOT 下也能用，和 <c>milkavalon.json</c> 同一套做法。
/// </para>
/// </remarks>
public static class PresetIndexCache
{
    /// <summary>索引文件名（放在 projectm 数据根下）。</summary>
    public const string FileName = "presets.index.json";

    /// <summary>格式版本：结构变了就加一，旧文件会被忽略并重扫。</summary>
    public const int FormatVersion = 1;

    /// <summary>索引文件的内容。</summary>
    public sealed class Index
    {
        /// <summary>格式版本。</summary>
        public int Version { get; set; }

        /// <summary>扫描时用的目录（绝对路径，用于校验）。</summary>
        public string Directory { get; set; } = string.Empty;

        /// <summary>生成时间（本地时间，仅用于显示/排查）。</summary>
        public string ScannedAt { get; set; } = string.Empty;

        /// <summary>相对 <see cref="Directory"/> 的文件列表。</summary>
        public List<string> Files { get; set; } = [];
    }

    /// <summary>索引文件完整路径。</summary>
    public static string PathFor(string projectMRoot) => System.IO.Path.Combine(projectMRoot, FileName);

    /// <summary>
    /// 尝试从索引加载清单。文件不存在、损坏、版本不符、目录不符或清单为空都返回 false（调用方去扫盘）。
    /// </summary>
    /// <param name="indexPath">索引文件路径。</param>
    /// <param name="directory">当前要用的扫描目录。</param>
    /// <param name="library">加载出的清单。</param>
    /// <param name="reason">失败原因（日志用）。</param>
    public static bool TryLoad(
        string indexPath,
        string directory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PresetLibrary? library,
        out string reason)
    {
        library = null;

        if (!File.Exists(indexPath))
        {
            reason = "没有索引文件";
            return false;
        }

        Index? index;

        try
        {
            string json = File.ReadAllText(indexPath);
            index = JsonSerializer.Deserialize(json, PresetIndexJsonContext.Default.Index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reason = "索引读取失败：" + ex.Message;
            return false;
        }

        if (index is null || index.Version != FormatVersion)
        {
            reason = "索引版本不符";
            return false;
        }

        if (!string.Equals(Path.GetFullPath(index.Directory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"索引目录不符（{index.Directory}）";
            return false;
        }

        if (index.Files.Count == 0)
        {
            reason = "索引是空的";
            return false;
        }

        var result = new PresetLibrary();

        foreach (string relative in index.Files)
        {
            if (!result.AddFile(Path.GetFullPath(Path.Combine(directory, relative))))
            {
                continue;
            }
        }

        library = result;
        reason = $"命中（{result.All.Count} 条，生成于 {index.ScannedAt}）";
        return true;
    }

    /// <summary>把扫描结果写成索引；失败只打日志（只读目录/磁盘满都不该影响播放）。</summary>
    public static bool TrySave(string indexPath, string directory, PresetLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        try
        {
            string root = Path.GetFullPath(directory);

            var index = new Index
            {
                Version = FormatVersion,
                Directory = root,
                ScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            foreach (string file in library.All)
            {
                index.Files.Add(Path.GetRelativePath(root, file));
            }

            string? folder = Path.GetDirectoryName(indexPath);

            if (folder is { Length: > 0 })
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, PresetIndexJsonContext.Default.Index));
            Console.WriteLine($"[projectM] 预设索引已保存：{indexPath}（{index.Files.Count} 条）");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine("[projectM] 预设索引写入失败（下次仍会扫盘）：" + ex.Message);
            return false;
        }
    }
}

/// <summary>STJ 源生成上下文：预设索引（AOT 友好）。</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PresetIndexCache.Index))]
internal sealed partial class PresetIndexJsonContext : JsonSerializerContext;
