using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using StbImageSharp;
using Buffer = System.Buffer;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Resources;

/// <summary>
/// 把图片文件解码成 GL 纹理。
/// </summary>
/// <remarks>
/// <para>
/// 上游用 stb_image：<c>stbi_set_flip_vertically_on_load(1)</c> + 强制 4 通道
/// （<c>viewport.cpp:731-748</c>）。这里用 StbImageSharp 做同样的事，vflip 自己翻行
/// （StbImageSharp 的 flip 是全局状态，多线程/多次加载容易互相干扰）。
/// </para>
/// <para>
/// 图片一律按 RGBA8 上传：Shadertoy 的作品仓库里都是 png/jpg，没有 HDR/exr 那种
/// （真遇到 exr 再按 <c>--hdr</c> 特判）。
/// </para>
/// </remarks>
public static class TextureLoader
{
    /// <summary>立方体贴图六个面的文件名后缀，顺序与 GL 的 <c>+X, -X, +Y, -Y, +Z, -Z</c> 一致。</summary>
    public static readonly string[] CubeFaceSuffixes = ["px", "nx", "py", "ny", "pz", "nz"];

    /// <summary>加载 2D 图片。<paramref name="vflip"/> 为 true 时上下翻转（Shadertoy 的 <c>vflip</c>）。</summary>
    /// <exception cref="IOException">文件读不出来。</exception>
    /// <exception cref="InvalidDataException">不是能解码的图片。</exception>
    public static Texture Load2D(string path, bool vflip, bool generateMipmaps = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ImageResult image = Decode(path);

        int width = image.Width;
        int height = image.Height;
        byte[] pixels = image.Data;

        if (vflip)
        {
            FlipRows(pixels, width, height);
        }

        var texture = new Texture(
            Path.GetFileNameWithoutExtension(path),
            width,
            height,
            (int)GLEnum.Rgba8,
            GLEnum.Rgba,
            GLEnum.UnsignedByte,
            isUserTexture: true);

        Upload2D(texture, width, height, pixels);

        if (generateMipmaps)
        {
            Renderer.MipmapHelper.Generate(texture);
        }

        return texture;
    }

    /// <summary>
    /// 加载六面立方体贴图（面序 <c>+X, -X, +Y, -Y, +Z, -Z</c>）。
    /// </summary>
    /// <param name="faces">六个面的图片路径，顺序必须与 <see cref="CubeFaceSuffixes"/> 一致。</param>
    /// <param name="vflip">每个面是否上下翻转（对应通道设置里的 VFlip）。</param>
    /// <param name="generateMipmaps">是否生成 mip 链（Filter = mipmap 时需要）。</param>
    /// <exception cref="IOException">文件读不出来。</exception>
    /// <exception cref="InvalidDataException">不是能解码的图片，或六个面不是同尺寸的正方形。</exception>
    public static Texture LoadCube(IReadOnlyList<string> faces, bool vflip, bool generateMipmaps = false)
    {
        ArgumentNullException.ThrowIfNull(faces);

        if (faces.Count != 6)
        {
            throw new ArgumentException($"立方体贴图需要 6 个面，实际 {faces.Count} 个", nameof(faces));
        }

        var pixels = new byte[6][];
        int size = 0;

        for (var face = 0; face < 6; face++)
        {
            ImageResult image = Decode(faces[face]);

            if (image.Width != image.Height)
            {
                throw new InvalidDataException($"立方体贴图的面必须是正方形: {faces[face]} ({image.Width}x{image.Height})");
            }

            if (size == 0)
            {
                size = image.Width;
            }
            else if (image.Width != size)
            {
                throw new InvalidDataException($"六个面尺寸不一致: {faces[face]} ({image.Width}x{image.Height})，期望 {size}x{size}");
            }

            if (vflip)
            {
                FlipRows(image.Data, image.Width, image.Height);
            }

            pixels[face] = image.Data;
        }

        return UploadCube(faces[0], size, pixels, generateMipmaps);
    }

    /// <summary>
    /// 从一张竖条带加载立方体贴图：<b>高 = 6 × 宽</b>，自上而下就是 <c>+X, -X, +Y, -Y, +Z, -Z</c>。
    /// </summary>
    /// <exception cref="IOException">文件读不出来。</exception>
    /// <exception cref="InvalidDataException">比例不是 6:1 的竖条带。</exception>
    public static Texture LoadCubeStrip(string path, bool vflip, bool generateMipmaps = false)
    {
        ImageResult image = Decode(path);

        if (image.Width <= 0 || image.Height != image.Width * 6)
        {
            throw new InvalidDataException($"不是立方体贴图条带（需要高 = 6×宽）: {path} ({image.Width}x{image.Height})");
        }

        int size = image.Width;
        int stride = size * 4;
        var pixels = new byte[6][];

        for (var face = 0; face < 6; face++)
        {
            var slice = new byte[stride * size];
            Buffer.BlockCopy(image.Data, face * slice.Length, slice, 0, slice.Length);

            if (vflip)
            {
                FlipRows(slice, size, size);
            }

            pixels[face] = slice;
        }

        return UploadCube(path, size, pixels, generateMipmaps);
    }

    private static ImageResult Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = File.ReadAllBytes(path);

        using var stream = new MemoryStream(bytes, writable: false);

        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
            ?? throw new InvalidDataException($"无法解码图片: {path}");
    }

    private static unsafe Texture UploadCube(string name, int size, byte[][] faces, bool generateMipmaps)
    {
        var gl = GlContext.Gl;
        uint textureId = gl.GenTexture();

        try
        {
            gl.BindTexture(GLEnum.TextureCubeMap, textureId);
            gl.PixelStore(GLEnum.UnpackAlignment, 4);

            for (var face = 0; face < 6; face++)
            {
                fixed (byte* data = faces[face])
                {
                    gl.TexImage2D(
                        (GLEnum)((int)GLEnum.TextureCubeMapPositiveX + face),
                        0,
                        (int)GLEnum.Rgba8,
                        (uint)size,
                        (uint)size,
                        0,
                        GLEnum.Rgba,
                        GLEnum.UnsignedByte,
                        data);
                }
            }

            gl.BindTexture(GLEnum.TextureCubeMap, 0);
        }
        catch
        {
            gl.BindTexture(GLEnum.TextureCubeMap, 0);
            gl.DeleteTextures([textureId]);
            throw;
        }

        var texture = new Texture(
            Path.GetFileNameWithoutExtension(name),
            textureId,
            GLEnum.TextureCubeMap,
            size,
            size,
            isUserTexture: true);

        if (generateMipmaps)
        {
            Renderer.MipmapHelper.Generate(texture);
        }

        return texture;
    }

    private static unsafe void Upload2D(Texture texture, int width, int height, byte[] pixels)
    {
        var gl = GlContext.Gl;
        gl.BindTexture(GLEnum.Texture2D, texture.TextureId);
        gl.PixelStore(GLEnum.UnpackAlignment, 4);

        fixed (byte* data = pixels)
        {
            gl.TexImage2D(
                GLEnum.Texture2D,
                0,
                (int)GLEnum.Rgba8,
                (uint)width,
                (uint)height,
                0,
                GLEnum.Rgba,
                GLEnum.UnsignedByte,
                data);
        }

        gl.BindTexture(GLEnum.Texture2D, 0);
    }

    /// <summary>原地按行翻转。</summary>
    internal static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        var temp = new byte[stride];

        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            int topOffset = top * stride;
            int bottomOffset = bottom * stride;
            Buffer.BlockCopy(pixels, topOffset, temp, 0, stride);
            Buffer.BlockCopy(pixels, bottomOffset, pixels, topOffset, stride);
            Buffer.BlockCopy(temp, 0, pixels, bottomOffset, stride);
        }
    }
}
