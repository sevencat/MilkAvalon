using Silk.NET.OpenGLES;

namespace MilkAvalon.Rendering;

/// <summary>
/// GL 上下文的能力快照（P1 的探测物）。两条引擎线都依赖 ES3 + 浮点 FBO，
/// 所以这里把"够不够用"一次性打全，省得后面靠猜。
/// </summary>
public sealed class GlInfo
{
    /// <summary>要探测的扩展（缺了会直接影响哪条线，见 plan.md 风险表）。</summary>
    private static readonly string[] WatchedExtensions =
    [
        "EXT_color_buffer_float",        // RGBA32F 作为渲染目标（ShaderToy sound pass）
        "EXT_color_buffer_half_float",   // RGBA16F 作为渲染目标（pass 图 / 两个引擎的离屏表面）
        "OES_texture_float_linear",      // 浮点纹理线性过滤
        "EXT_float_blend",
        "GL_EXT_shader_texture_lod",
        "OES_standard_derivatives",      // 老作品会写 dFdx
    ];

    public required string Vendor { get; init; }

    public required string Renderer { get; init; }

    public required string Version { get; init; }

    public required string GlslVersion { get; init; }

    public required IReadOnlyDictionary<string, bool> Extensions { get; init; }

    /// <summary>上下文报告的扩展总数。</summary>
    public required int ExtensionCount { get; init; }

    /// <summary>全部扩展名（诊断用）。</summary>
    public required IReadOnlyList<string> AllExtensions { get; init; }

    public required int MaxTextureSize { get; init; }

    /// <summary>ES 主版本号（从 <see cref="Version"/> 里解析，解析不出来返回 0）。</summary>
    public int EsMajor { get; init; }

    /// <summary>ES3 是两条线的硬要求。</summary>
    public bool IsEs3 => EsMajor >= 3;

    /// <summary>RGBA16F 作为渲染目标的自检结果（pass 图 / 两个引擎的离屏表面）。</summary>
    public required string Float16Target { get; init; }

    /// <summary>RGBA32F 作为渲染目标的自检结果（ShaderToy 的 sound pass）。</summary>
    public required string Float32Target { get; init; }

    public bool HasFloatRenderTarget => Extensions["EXT_color_buffer_float"] || Extensions["EXT_color_buffer_half_float"];

    /// <summary>查询当前上下文的全部信息（必须在该上下文 current 的线程调用）。</summary>
    public static GlInfo Query(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);

        string version = gl.GetStringS(StringName.Version) ?? "(null)";

        // GLES3 里 glGetString(GL_EXTENSIONS) 是非法的（只有 ES2 才允许），
        // 必须用 glGetStringi 逐条取 —— 写成 GetString 的话所有扩展都会显示"无"，很容易误判。
        var present = new HashSet<string>(StringComparer.Ordinal);
        int extensionCount = gl.GetInteger(GetPName.NumExtensions);
        for (var i = 0; i < extensionCount; i++)
        {
            string? name = gl.GetStringS(StringName.Extensions, (uint)i);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // ANGLE 的 glGetStringi 会带上 "GL_" 前缀（"GL_EXT_color_buffer_float"），
            // 而浏览器/规范里的名字没有前缀。两套都收进集合，否则永远查不到。
            present.Add(name);
            if (name.StartsWith("GL_", StringComparison.Ordinal))
            {
                present.Add(name[3..]);
            }
        }

        var watched = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string name in WatchedExtensions)
        {
            watched[name] = present.Contains(name);
        }

        return new GlInfo
        {
            Vendor = gl.GetStringS(StringName.Vendor) ?? "(null)",
            Renderer = gl.GetStringS(StringName.Renderer) ?? "(null)",
            Version = version,
            GlslVersion = gl.GetStringS(StringName.ShadingLanguageVersion) ?? "(null)",
            Extensions = watched,
            ExtensionCount = extensionCount,
            AllExtensions = [.. present],
            MaxTextureSize = gl.GetInteger(GetPName.MaxTextureSize),
            EsMajor = ParseEsMajor(version),
            Float16Target = ProbeFloatTarget(gl, GLEnum.Rgba16f),
            Float32Target = ProbeFloatTarget(gl, GLEnum.Rgba32f),
        };
    }

    /// <summary>
    /// 真建一个浮点 FBO 并回读一个像素 —— 扩展"有"不等于"能用"，这一步才是两条引擎线真正的门槛。
    /// </summary>
    private static unsafe string ProbeFloatTarget(GL gl, GLEnum internalFormat)
    {
        const int size = 4;

        uint texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)internalFormat, size, size, 0,
            PixelFormat.Rgba, PixelType.Float, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        uint framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(GLEnum.Framebuffer, framebuffer);
        gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, texture, 0);

        GLEnum status = gl.CheckFramebufferStatus(GLEnum.Framebuffer);
        string result;

        if (status == GLEnum.FramebufferComplete)
        {
            gl.Viewport(0, 0, size, size);
            gl.ClearColor(0.5f, 0.25f, 0.125f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            var pixels = new float[4];
            fixed (float* pointer = pixels)
            {
                gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, pointer);
            }

            // 期望读回 (0.5, 0.25, 0.125, 1)；误差给宽一点，半浮点的量化比 1e-3 粗。
            bool ok = Math.Abs(pixels[0] - 0.5f) < 1e-2f && Math.Abs(pixels[1] - 0.25f) < 1e-2f;
            result = ok
                ? $"完整（回读 {pixels[0]:0.###}, {pixels[1]:0.###}, {pixels[2]:0.###}, {pixels[3]:0.###}）"
                : $"完整但回读异常（{pixels[0]:0.###}, {pixels[1]:0.###}, {pixels[2]:0.###}, {pixels[3]:0.###}）";
        }
        else
        {
            result = $"不完整（0x{(uint)status:X}）";
        }

        gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        gl.DeleteFramebuffer(framebuffer);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.DeleteTexture(texture);

        return result;
    }

    /// <summary>多行文本，给控制台与界面共用。</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"GL_VERSION  : {Version}",
            $"GL_RENDERER : {Renderer}",
            $"GL_VENDOR   : {Vendor}",
            $"GLSL        : {GlslVersion}",
            $"ES3         : {(IsEs3 ? "是" : "否 ← 两条引擎线都跑不起来")}",
            $"MAX_TEXTURE : {MaxTextureSize}",
            $"EXT 总数    : {ExtensionCount}",
        };

        foreach ((string name, bool ok) in Extensions)
        {
            lines.Add($"{name,-28}: {(ok ? "有" : "无")}");
        }

        lines.Add($"RGBA16F FBO : {Float16Target}");
        lines.Add($"RGBA32F FBO : {Float32Target}");

        return string.Join(Environment.NewLine, lines);
    }

    private static int ParseEsMajor(string version)
    {
        // ANGLE 的形状： "OpenGL ES 3.0 (ANGLE 2.1.0 ...)"
        const string marker = "OpenGL ES ";
        int index = version.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 0;
        }

        index += marker.Length;
        int end = index;
        while (end < version.Length && char.IsAsciiDigit(version[end]))
        {
            end++;
        }

        return end > index && int.TryParse(version.AsSpan(index, end - index), out int major) ? major : 0;
    }
}
