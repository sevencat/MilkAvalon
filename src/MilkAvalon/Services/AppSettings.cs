using System.Text.Json;
using System.Text.Json.Serialization;

namespace MilkAvalon.Services;

/// <summary>
/// 持久化设置：<c>&lt;exe&gt;/milkavalon.json</c>（plan.md P7 / §6.2）。
/// </summary>
/// <remarks>
/// <para>
/// 优先级：**命令行 &gt; 设置文件 &gt; 默认值**。命令行那层在 <see cref="AppOptions"/> 里，
/// 这里只负责"文件里记了什么"。
/// </para>
/// <para>
/// 序列化用 System.Text.Json 的**源生成**（P9 要上 AOT，反射式序列化会被裁掉）。
/// </para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>projectM 数据根（null = 用 <c>&lt;exe&gt;/projectm</c>）。</summary>
    public string? ProjectMDataRoot { get; set; }

    /// <summary>ShaderToy 数据根（null = 用 <c>&lt;exe&gt;/shadertoy</c>）。</summary>
    public string? ShaderToyDataRoot { get; set; }

    /// <summary>是否等垂直同步（暂时只记录，帧泵由 Avalonia 合成器控制）。</summary>
    public bool VSync { get; set; } = true;

    /// <summary>projectM 网格宽（预设的 <c>nGridX</c> 可覆盖）。</summary>
    public uint MeshWidth { get; set; } = 48;

    /// <summary>projectM 网格高。</summary>
    public uint MeshHeight { get; set; } = 36;

    /// <summary>ShaderToy buffer 分辨率模式：0=视口 / 1=固定 1024×256 / 2=0.5×。</summary>
    public int ShaderToyBufferMode { get; set; } = 0;

    /// <summary>界面缩放（1.0 = 跟随系统）。</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>默认配置文件路径。</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "milkavalon.json");

    /// <summary>读设置；文件不存在/坏了都返回默认值，不抛。</summary>
    public static AppSettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            if (!File.Exists(file))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(file);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] 读取 {file} 失败，使用默认值：{ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>写设置；失败只记日志（不因为磁盘问题打断播放）。</summary>
    /// <returns>是否写成功。</returns>
    public bool Save(string? path = null)
    {
        string file = path ?? DefaultPath;

        try
        {
            string? directory = Path.GetDirectoryName(file);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);
            File.WriteAllText(file, json);
            Console.WriteLine($"[settings] 已保存 {file}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] 保存 {file} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>复制一份（对话框里编辑用，取消时不影响当前设置）。</summary>
    public AppSettings Clone() => new()
    {
        ProjectMDataRoot = ProjectMDataRoot,
        ShaderToyDataRoot = ShaderToyDataRoot,
        VSync = VSync,
        MeshWidth = MeshWidth,
        MeshHeight = MeshHeight,
        ShaderToyBufferMode = ShaderToyBufferMode,
        UiScale = UiScale,
    };

    /// <summary>把 <paramref name="other"/> 的值抄进自己（保持对象引用不变，<see cref="AppOptions"/> 也就能看到新值）。</summary>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        ProjectMDataRoot = other.ProjectMDataRoot;
        ShaderToyDataRoot = other.ShaderToyDataRoot;
        VSync = other.VSync;
        MeshWidth = other.MeshWidth;
        MeshHeight = other.MeshHeight;
        ShaderToyBufferMode = other.ShaderToyBufferMode;
        UiScale = other.UiScale;
    }
}

/// <summary>STJ 源生成上下文（AOT 友好的设置读写）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
