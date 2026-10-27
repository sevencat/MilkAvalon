using System.Numerics;
using MilkAvalon.Rendering;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using GlShader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkAvalon.Controls;

/// <summary>
/// P1/P2 的探针控件：用 ES3 全屏三角画一张会动的渐变网格，并把指针与按键画出来。
/// </summary>
/// <remarks>
/// 存在的意义不是"好看"，而是把下面几件事一次证实：
/// <list type="number">
/// <item>Avalonia 的 ANGLE 上下文能给 Silk.NET 用（<see cref="GlShader"/> 能编译、能画）。</item>
/// <item>上下文是 <c>#version 300 es</c>（编不过就直接抛 <see cref="ShaderException"/>）。</item>
/// <item>数据能画进 Avalonia 给的 FBO（网格与中心点位置可截图核对）。</item>
/// <item>帧泵稳定（状态栏的 FPS）。</item>
/// <item>指针坐标没翻（十字准星应跟着"左上角注入的 (0.25, 0.25)"出现在左上象限）。</item>
/// <item>键盘能到引擎（按住 A 时准星从红变绿）。</item>
/// </list>
/// P3/P4 接入真引擎后本控件保留，作为"GL 出问题时用来隔离"的对照物。
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
        uniform vec2 uMouse;      // 0..1，左上原点（与 Avalonia 一致）
        uniform float uKeyAlpha;  // 0/1：按住 A 时准星变绿

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

            // 指针准星：uMouse 是左上原点，而 uv 原点在左下，所以 y 要翻
            vec2 mouseUv = vec2(uMouse.x, 1.0 - uMouse.y);
            vec2 px = (uv - mouseUv) * uResolution;
            float radius = length(px);
            float ring = smoothstep(22.0, 20.0, radius) - smoothstep(18.0, 16.0, radius);
            float cross = (abs(px.x) < 1.5 || abs(px.y) < 1.5) ? 1.0 : 0.0;
            color += max(ring, cross) * mix(vec3(1.0, 0.25, 0.25), vec3(0.25, 1.0, 0.45), uKeyAlpha);

            fragColor = vec4(color, 1.0);
        }
        """;

    private GlShader? _shader;
    private uint _vao;
    private float _mouseX = 0.5f;
    private float _mouseY = 0.5f;
    private float _keyAlpha;

    protected override void OnGlReady()
    {
        _shader = new GlShader();
        _shader.CompileProgram(VertexSource, FragmentSource);

        // ES3 即使不传顶点属性也要求绑定一个 VAO
        _vao = Gl.GenVertexArray();

        Console.WriteLine("[GL] ES3 着色器编译通过（#version 300 es）");
    }

    protected override void OnSizeChanged(int pixelWidth, int pixelHeight)
        => Console.WriteLine($"[GL] 渲染目标尺寸 → {pixelWidth}x{pixelHeight}");

    protected override void OnPointerMoved(double normalizedX, double normalizedY)
    {
        _mouseX = (float)normalizedX;
        _mouseY = (float)normalizedY;
    }

    protected override void OnKeyChanged(int keyCode, bool down)
    {
        // A=65；只关心"按下/松开"，不关心按了几次
        if (keyCode == 65)
        {
            _keyAlpha = down ? 1f : 0f;
        }

        Console.WriteLine($"[GL] 键 {keyCode} {(down ? "按下" : "松开")}");
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
        _shader.SetUniformFloat2("uMouse", new Vector2(_mouseX, _mouseY));
        _shader.SetUniformFloat("uKeyAlpha", _keyAlpha);

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
