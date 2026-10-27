namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// GL 包装对象的**存活计数**（P5 的泄漏自检埋点）。
/// </summary>
/// <remarks>
/// <para>
/// 只包 <see cref="Texture"/> / <see cref="Framebuffer"/> / <see cref="Shader"/> / <see cref="Sampler"/>
/// 这四种"自己拥有 GL 名字"的对象；它们在构造时 +1、真正删掉 GL 名字时 -1
/// （重复 Dispose 不会重复扣，与各 <c>Dispose</c> 里的"ID 归零即已释放"一致）。
/// </para>
/// <para>
/// 用途：切 Tab 前后各打一次。如果页面拆掉后计数没有回到基线，就是漏了 Dispose；
/// 计数对但进程私有内存一直涨，那问题在驱动侧（例如纹理被别处引用着没删）。
/// 开销只是每次构造/销毁一个 <see cref="System.Threading.Interlocked"/> 操作。
/// </para>
/// </remarks>
public static class GlObjectStats
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _byName = new(StringComparer.Ordinal);

    private static int _textures;
    private static int _framebuffers;
    private static int _shaders;
    private static int _samplers;

    public static int LiveTextures => Volatile.Read(ref _textures);

    public static int LiveFramebuffers => Volatile.Read(ref _framebuffers);

    public static int LiveShaders => Volatile.Read(ref _shaders);

    public static int LiveSamplers => Volatile.Read(ref _samplers);

    public static int LiveTotal => LiveTextures + LiveFramebuffers + LiveShaders + LiveSamplers;

    public static void TextureCreated() => Interlocked.Increment(ref _textures);

    public static void TextureDisposed() => Interlocked.Decrement(ref _textures);

    /// <summary>
    /// 按纹理名记账（诊断用）：漏纹理时能看出漏的是哪一类。
    /// </summary>
    public static void TextureCreated(string name)
    {
        TextureCreated();
        _byName.AddOrUpdate(Normalize(name), 1, static (_, count) => count + 1);
    }

    /// <summary>按纹理名销账。</summary>
    public static void TextureDisposed(string name)
    {
        TextureDisposed();
        _byName.AddOrUpdate(Normalize(name), -1, static (_, count) => count - 1);
    }

    /// <summary>按名字列出"还活着"的纹理（只列 > 0 的，最多 <paramref name="take"/> 类）。</summary>
    public static string DescribeTexturesByName(int take = 8)
    {
        var alive = _byName
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .Take(take)
            .Select(pair => $"{pair.Key}×{pair.Value}");

        return string.Join("，", alive);
    }

    public static void FramebufferCreated() => Interlocked.Increment(ref _framebuffers);

    public static void FramebufferDisposed() => Interlocked.Decrement(ref _framebuffers);

    public static void ShaderCreated() => Interlocked.Increment(ref _shaders);

    public static void ShaderDisposed() => Interlocked.Decrement(ref _shaders);

    public static void SamplerCreated() => Interlocked.Increment(ref _samplers);

    public static void SamplerDisposed() => Interlocked.Decrement(ref _samplers);

    /// <summary>一行摘要（日志/界面用）。</summary>
    public static string Describe() =>
        $"GL 对象存活：texture {LiveTextures}，framebuffer {LiveFramebuffers}，"
        + $"shader {LiveShaders}，sampler {LiveSamplers}，合计 {LiveTotal}";

    private static string Normalize(string name) => name.Length == 0 ? "(无名)" : name;
}
