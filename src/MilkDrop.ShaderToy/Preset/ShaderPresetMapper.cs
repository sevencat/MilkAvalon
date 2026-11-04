using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Preset;

/// <summary>
/// <see cref="ShaderPresetDocument"/>（文件内容）⇄ <see cref="ShaderToyProject"/>（渲染用的内存模型）互转。
/// </summary>
/// <remarks>
/// <para>
/// 段名里的"角色"是两边唯一需要约定的东西：<c>common</c> / <c>image</c> / <c>buffer_a..d</c> /
/// <c>cubemap_a..</c> / <c>sound</c>。角色 ⇄ pass 类型/名字的映射在这里集中，外加
/// "重名就追加 <c>_2</c>" 的去重规则。
/// </para>
/// <para>
/// <see cref="ShaderToyProject.RawJson"/> **不参与**这条路径：文本里存的就是完整源码，
/// 再由转译器按通道类型现算。导出器要用它时自己从 db 读。
/// </para>
/// </remarks>
public static class ShaderPresetMapper
{
    /// <summary>文件里的 <c>kind</c> 值。</summary>
    public const string Kind = "shadertoy";

    // ---- 文件 → 内存模型 ----

    /// <summary>
    /// 文档 → 工程。未知角色（用户手写的怪段名）只记警告，不阻断其余段。
    /// </summary>
    public static ShaderToyProject ToProject(ShaderPresetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var project = new ShaderToyProject();
        project.Metadata.Id = document.Find("id") ?? string.Empty;
        project.Metadata.Name = document.Find("title") ?? string.Empty;
        project.Metadata.Author = document.Find("author") ?? string.Empty;
        project.Metadata.Description = document.Find("description") ?? string.Empty;

        foreach (string tag in (document.Find("tags") ?? string.Empty)
                     .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            project.Metadata.Tags.Add(tag);
        }

        project.Warnings.AddRange(document.Warnings);

        foreach (ShaderPresetPass pass in document.Passes)
        {
            if (!TryParseRole(pass.Role, out ShaderPassType type, out string name))
            {
                project.Warnings.Add($"忽略未知角色 [{ShaderPresetDocument.SectionPrefix}{pass.Role}]");
                continue;
            }

            var shaderPass = new ShaderPass
            {
                Type = type,
                Name = name,
                Code = pass.Code,
            };

            foreach (ShaderPresetChannel channel in pass.Inputs)
            {
                var input = new ShaderPassInput
                {
                    Channel = channel.Channel,
                    Type = channel.Type,
                    Sampler = new ShaderInputSampler
                    {
                        Filter = channel.Filter,
                        Wrap = channel.Wrap,
                        VFlip = channel.VFlip,
                    },
                };

                switch (channel.Type)
                {
                    case ShaderInputType.Buffer:
                    case ShaderInputType.Cubemap:
                    case ShaderInputType.Volume:
                        input.Id = channel.Target;
                        break;

                    case ShaderInputType.Texture:
                        input.Path = channel.Target;
                        break;
                }

                shaderPass.Inputs.Add(input);
            }

            foreach (ShaderPresetOutput output in pass.Outputs)
            {
                shaderPass.Outputs.Add(new ShaderPassOutput { Id = output.Id, Channel = output.Channel });
            }

            project.Passes.Add(shaderPass);
        }

        return project;
    }

    // ---- 内存模型 → 文件 ----

    /// <summary>
    /// 工程 → 文档。<paramref name="extraMeta"/> 里的键（<c>captured</c> / <c>source_url</c> 等）
    /// 会原样进 <c>[meta]</c>；<c>kind</c> 与 <c>code_hash</c> 由这里补。
    /// </summary>
    public static ShaderPresetDocument FromProject(
        ShaderToyProject project,
        IReadOnlyDictionary<string, string>? extraMeta = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        var document = new ShaderPresetDocument();

        document.Meta["kind"] = Kind;

        AddIfAny(document, "id", project.Metadata.Id);
        AddIfAny(document, "title", project.Metadata.Name);
        AddIfAny(document, "author", project.Metadata.Author);
        AddIfAny(document, "description", project.Metadata.Description);
        AddIfAny(document, "tags", string.Join(", ", project.Metadata.Tags));

        if (extraMeta is not null)
        {
            foreach ((string key, string value) in extraMeta)
            {
                if (value.Length > 0 && !document.Meta.ContainsKey(key))
                {
                    document.Meta[key] = value;
                }
            }
        }

        document.Meta["code_hash"] = ComputeCodeHash(project);

        var usedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ShaderPass pass in OrderForFile(project))
        {
            string role = Unique(usedRoles, RoleOf(pass));

            var presetPass = new ShaderPresetPass(role);

            // 代码按行存（保留空行），最后再补一个换行语义由 writer 负责
            presetPass.CodeLines.AddRange(CodeLines(pass.Code));

            foreach (ShaderPassInput input in pass.Inputs)
            {
                presetPass.Inputs.Add(new ShaderPresetChannel(
                    input.Channel,
                    input.Type,
                    TargetOf(input),
                    input.Sampler.Filter,
                    input.Sampler.Wrap,
                    input.Sampler.VFlip));
            }

            foreach (ShaderPassOutput output in pass.Outputs)
            {
                presetPass.Outputs.Add(new ShaderPresetOutput(output.Channel, output.Id));
            }

            document.Passes.Add(presetPass);
        }

        document.Warnings.AddRange(project.Warnings);
        return document;
    }

    /// <summary>pass 代码的指纹（角色 + 代码，取 sha256 前 16 位小写十六进制）。</summary>
    /// <remarks>
    /// 参与哈希的是<b>文件里的那套文本</b>：角色名（而非 pass 显示名，显示名往返后可能被规范化）+
    /// 统一成 <c>\n</c> 并去掉结尾空行的代码行。所以"写 → 读 → 写"不会因为尾部换行/行尾差异让
    /// <c>code_hash</c> 漂移。
    /// </remarks>
    public static string ComputeCodeHash(ShaderToyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var sb = new StringBuilder();

        foreach (ShaderPass pass in OrderForFile(project))
        {
            sb.Append(RoleOf(pass)).Append('\n');
            AppendCode(sb, pass.Code);
        }

        return Hash(sb);
    }

    /// <summary>
    /// 按文件内容算指纹（角色 + 代码），用于判断"这个 <c>.shaderx</c> 有没有被手改过"。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ComputeCodeHash(ShaderToyProject)"/> 算的是同一段文本，
    /// 所以导出器写出的文件重新读进来再算，结果一定等于文件里的 <c>code_hash</c>。
    /// </remarks>
    public static string ComputeCodeHash(ShaderPresetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        foreach (ShaderPresetPass pass in document.Passes)
        {
            sb.Append(pass.Role).Append('\n');
            AppendCode(sb, pass.Code);
        }

        return Hash(sb);
    }

    private static void AppendCode(StringBuilder sb, string code)
    {
        foreach (string line in CodeLines(code))
        {
            sb.Append(line).Append('\n');
        }
    }

    private static string Hash(StringBuilder sb)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>代码 → 文件里的行（<c>\n</c> 换行，去掉结尾空行；段尾那个空行由 writer 负责）。</summary>
    private static string[] CodeLines(string code)
    {
        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int count = lines.Length;

        while (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        return count == lines.Length ? lines : lines[..count];
    }

    /// <summary>写文件时的顺序：<c>common</c> 必须在前（拼接语义），其余保持原顺序。</summary>
    private static IEnumerable<ShaderPass> OrderForFile(ShaderToyProject project) =>
        project.Passes.OrderBy(pass => pass.Type == ShaderPassType.Common ? 0 : 1);

    /// <summary>pass → 文件里的角色名。</summary>
    private static string RoleOf(ShaderPass pass) => pass.Type switch
    {
        ShaderPassType.Common => "common",
        ShaderPassType.Image => "image",
        ShaderPassType.Sound => "sound",
        ShaderPassType.Buffer => "buffer_" + LetterOf(pass.Name).ToLowerInvariant(),
        ShaderPassType.Cubemap => "cubemap_" + LetterOf(pass.Name).ToLowerInvariant(),
        _ => "image",
    };

    /// <summary>从 pass 名字里取字母（<c>Buffer A</c> → <c>A</c>），取不到用 <c>A</c> 兜底，重复由调用方去重。</summary>
    private static string LetterOf(string name)
    {
        for (int i = name.Length - 1; i >= 0; i--)
        {
            if (char.IsLetter(name[i]))
            {
                return char.ToUpperInvariant(name[i]).ToString(CultureInfo.InvariantCulture);
            }
        }

        return "A";
    }

    private static string Unique(HashSet<string> used, string role)
    {
        if (used.Add(role))
        {
            return role;
        }

        for (var suffix = 2; ; suffix++)
        {
            string candidate = role + "_" + suffix.ToString(CultureInfo.InvariantCulture);

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>角色 → pass 类型 + 显示名（<c>buffer_b</c> → Buffer / <c>Buffer B</c>）。</summary>
    private static bool TryParseRole(string role, out ShaderPassType type, out string name)
    {
        type = ShaderPassType.Image;
        name = "Image";

        string lower = role.ToLowerInvariant();

        if (lower == "common" || lower.StartsWith("common_", StringComparison.Ordinal))
        {
            type = ShaderPassType.Common;
            name = "Common";
            return true;
        }

        if (lower == "image")
        {
            type = ShaderPassType.Image;
            name = "Image";
            return true;
        }

        if (lower == "sound")
        {
            type = ShaderPassType.Sound;
            name = "Sound";
            return true;
        }

        if (lower.StartsWith("buffer_", StringComparison.Ordinal))
        {
            type = ShaderPassType.Buffer;
            name = "Buffer " + LetterSuffix(role);
            return true;
        }

        if (lower.StartsWith("cubemap_", StringComparison.Ordinal))
        {
            type = ShaderPassType.Cubemap;
            name = "Cubemap " + LetterSuffix(role);
            return true;
        }

        // 兼容简写：buffer_a / cubemap 之外，也允许只写 "buffer"（多 pass 时角色会被去重成 buffer_2…）
        if (lower == "buffer")
        {
            type = ShaderPassType.Buffer;
            name = "Buffer A";
            return true;
        }

        if (lower == "cubemap")
        {
            type = ShaderPassType.Cubemap;
            name = "Cubemap A";
            return true;
        }

        return false;
    }

    private static string LetterSuffix(string role)
    {
        int underscore = role.LastIndexOf('_');

        if (underscore >= 0 && underscore + 1 < role.Length && char.IsLetter(role[underscore + 1]))
        {
            return char.ToUpperInvariant(role[underscore + 1]).ToString(CultureInfo.InvariantCulture);
        }

        return "A";
    }

    /// <summary>输入 → 文件里的"目标"字段（buffer 的 id / 纹理路径）。</summary>
    private static string? TargetOf(ShaderPassInput input) => input.Type switch
    {
        ShaderInputType.Buffer or ShaderInputType.Cubemap or ShaderInputType.Volume => input.Id,
        ShaderInputType.Texture => input.Path,
        _ => null,
    };

    private static void AddIfAny(ShaderPresetDocument document, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            document.Meta[key] = value.Trim();
        }
    }
}
