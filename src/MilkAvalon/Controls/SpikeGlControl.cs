using System.Numerics;
using MilkAvalon.Rendering;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using GlShader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkAvalon.Controls;

/// <summary>
/// P1 的探针控件：用 ES3 全屏三角画一张会动的渐变网格。
/// </summary>
/// <remarks>
/// 存在的意义不是"好看"，而是把下面四件事一次证实：
/// <list type="number">
/// <item>Avalonia 的 ANGLE 上下文能给 Silk.NET 用（<see cref="Shader"/> 能编译、能画）。</item>
/// <item>上下文是 <c>#version 300 es</c>（编不过就直接抛 <see cref="ShaderException"/>）。</item>
/// <item>数据能画进 Avalonia 给的 FBO（网格与中心点位置可肉眼/截图核对）。</item>
/// <item>帧泵稳定（左上角状态里的 FPS）。</item>
/// </list>
/// 网格 16×9 + 中心亮点可以同时验证"y 轴没翻"和"没有拉伸"。
/// </remarks>
public sealed class SpikeGlControl : GlControlBase
{
    private const string VertexSource = """
        #version 300 es
        out vec2 vUv;

        void main()
        {
            // 无顶点缓冲的全屏三角：3 个顶点直接由 gl_VertexID 算出来
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 300 es
        precision highp float;

        in vec2 vUv;
        out vec4 fragColor;

        uniform float uTime;
        uniform vec2 uResolution;

        void main()
        {
            vec2 uv = vUv;

            // 缓慢流动的双色渐变
            float wave = 0.5 + 0.5 * sin(uTime * 1.2 + uv.x * 6.2831853);
            vec3 color = mix(vec3(0.10, 0.02, 0.30), vec3(0.00, 0.55, 0.55), wave);

            // 正方形网格（按宽高比校正）：格子被拉长 = 缩放/视口算错了
            vec2 aspect = vec2(uResolution.x / max(uResolution.y, 1.0), 1.0);
            vec2 grid = abs(fract(uv * aspect * 9.0) - 0.5);
            float line = smoothstep(0.45, 0.50, max(grid.x, grid.y));
            color += line * 0.18;

            // 屏幕中心亮点：验证没有上下/左右翻转
            vec2 centered = (uv - 0.5) * uResolution / uResolution.y;
            color += smoothstep(0.020, 0.0, length(centered)) * vec3(1.0, 0.85, 0.25);

            fragColor = vec4(color, 1.0);
        }
        """;

    private GlShader? _shader;
    private uint _vao;

    protected override void OnGlReady()
    {
        _shader = new GlShader();
        _shader.CompileProgram(VertexSource, FragmentSource);

        // ES3 即使不传顶点属性也要求绑定一个 VAO
        _vao = Gl.GenVertexArray();

        Console.WriteLine("[GL] ES3 着色器编译通过（#version 300 es）");
    }

    protected override void OnFrame(double deltaSeconds, double elapsedSeconds)
    {
        if (_shader is null)
        {
            return;
        }

        Gl.BindVertexArray(_vao);
        _shader.Bind();
        _shader.SetUniformFloat("uTime", (float)elapsedSeconds);
        _shader.SetUniformFloat2("uResolution", new Vector2(PixelWidth, PixelHeight));

        // 先铺一次纯色，确保即使着色器有问题也能看出"这块区域被 GL 接管了"
        Gl.ClearColor(0.02f, 0.02f, 0.03f, 1f);
        Gl.Clear(ClearBufferMask.ColorBufferBit);

        Gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Gl.BindVertexArray(0);
    }

    protected override void OnGlReleased()
    {
        _shader?.Dispose();
        _shader = null;

        if (_vao != 0)
        {
            Gl.DeleteVertexArray(_vao);
            _vao = 0;
        }
    }
}
