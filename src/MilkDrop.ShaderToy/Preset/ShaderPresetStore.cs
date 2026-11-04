using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>把工程写成 <c>.shaderx</c> 的结果。</summary>
/// <param name="FilePath">写出的文件（<c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c>）。</param>
/// <param name="Replaced">同 id 的旧文件已存在（标题改了名时会顺手删掉旧的）。</param>
public sealed record ShaderPresetSaveResult(string FilePath, bool Replaced);

/// <summary>
/// 作品落盘：把内存里的工程写成 <c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c>。
/// </summary>
/// <remarks>
/// <para>
/// P13 之后<strong>只有一条写入路径</strong>（抓取、db 兜底作品首次被编辑都走这里）：
/// 文件是唯一真相，没有数据库、没有 <c>shaders/&lt;id&gt;/</c> 目录。
/// </para>
/// <para>
/// 覆盖按"文件里的 <c>[meta] id</c>"认人：站点上改了标题、用户手动改过名字，都不会在目录里留两份。
/// </para>
/// </remarks>
public static class ShaderPresetStore
{
    /// <summary>作品文件所在的子目录名（<c>&lt;数据根&gt;/presets</c>）。</summary>
    public const string PresetsFolderName = "presets";

    /// <summary>站点作品页前缀（写进 <c>[meta] source_url</c>）。</summary>
    public const string SourceUrlPrefix = "https://www.shadertoy.com/view/";

    /// <summary>
    /// 把一个工程写成 <c>presets/&lt;title&gt; [&lt;id&gt;].shaderx</c>。
    /// </summary>
    /// <param name="dataRoot">ShaderToy 数据根。</param>
    /// <param name="project">工程（必须有 id）。</param>
    /// <param name="extraMeta">额外写进 <c>[meta]</c> 的键（<c>captured</c> / <c>source_url</c> 等）。</param>
    /// <returns>写出的路径 + 是否覆盖了同 id 的旧文件。</returns>
    public static ShaderPresetSaveResult SaveProject(
        string dataRoot,
        ShaderToyProject project,
        IReadOnlyDictionary<string, string>? extraMeta = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        if (project.Metadata.Id.Length == 0)
        {
            throw new InvalidOperationException("工程没有 id，无法写成 .shaderx。");
        }

        string presets = Path.Combine(Path.GetFullPath(dataRoot), PresetsFolderName);
        string target = Path.Combine(presets, ShaderPresetWriter.FileNameFor(project));
        string? existing = ShaderPresetLibrary.FindPathById(presets, project.Metadata.Id);

        Directory.CreateDirectory(presets);
        ShaderPresetWriter.WriteFile(target, ShaderPresetMapper.FromProject(project, extraMeta));

        if (existing is not null && !string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(existing);
        }

        return new ShaderPresetSaveResult(target, existing is not null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[shaderx] 旧文件删不掉（可手动删）：{path}（{ex.Message}）");
        }
    }
}
