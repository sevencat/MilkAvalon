using System.Text;
using MilkDrop.PresetScript.Convert;
using MilkDrop.PresetScript.Milk;
using MilkDrop.PresetScript.Milkx;

namespace MilkDrop.PresetScript.Tests;

/// <summary>
/// 桥接：<c>.milkx</c> 文档 → 内存里的 <c>.milk</c> 文本 → 引擎那套解析 → 带标记的代码段
/// 走脚本前端执行。这条链路就是播放时真正走的路径。
/// </summary>
public sealed class BridgeTests
{
    static BridgeTests()
    {
        // 宿主（MilkAvalon）启动时会做同样的事
        CompileContext.ScriptParser = static (code, context) => new Syntax.CsParser().Parse(code, context);
    }

    [Fact]
    public void Milkx_Round_Trips_Through_InMemory_Milk_Text()
    {
        const string eel = "zoom = 1 + 0.1 * bass; wave_r = 0.5 + 0.5 * sin(time);";

        var converter = new PresetConverter();
        MilkPresetSource source = MilkPresetReader.ReadText(
            "[preset00]\nfDecay=0.98\nper_frame_1=" + eel + "\n", "demo.milk");

        ConversionResult converted = converter.Convert(source, "hash");
        Assert.True(converted.Success, converted.Error);
        Assert.NotNull(converted.Document);

        string milkText = MilkxToMilkText.Write(converted.Document);

        // 还原出来的文本仍能被普通 .milk 解析器读，参数与代码都在
        MilkPresetSource reparsed = MilkPresetReader.ReadText(milkText, "demo.milk");
        Assert.Equal("0.98", reparsed.Keys["fDecay"]);

        string code = reparsed.GetCode("per_frame_");
        Assert.StartsWith(CompileContext.ScriptMarker, code.TrimStart(), StringComparison.Ordinal);

        var context = new CompileContext();
        EvalProgram? program = context.Compile(code);
        Assert.NotNull(program);

        context.GetOrCreateVariable("bass").Storage[context.GetOrCreateVariable("bass").Slot] = 2;
        program.Execute();
        Assert.Equal(1.2, context.FindVariable("zoom")!.Value, 9);
    }

    [Fact]
    public void Shader_Sections_Get_Backticks_Back()
    {
        var document = new MilkxDocument { SourceFileName = "x.milk" };
        var shader = new MilkxSection("shader.warp");
        shader.Lines.Add("shader_body { ret = tex2D(sampler_main, uv); }");
        document.Sections.Add(shader);

        string text = MilkxToMilkText.Write(document);
        Assert.Contains("warp_1=`shader_body", text, StringComparison.Ordinal);

        MilkPresetSource reparsed = MilkPresetReader.ReadText(text);
        Assert.Equal("shader_body { ret = tex2D(sampler_main, uv); }", reparsed.GetCode("warp_").TrimEnd());
    }

    [Fact]
    public void Wave_Parameters_Get_Their_Prefix_Back()
    {
        var document = new MilkxDocument { SourceFileName = "x.milk" };
        var wave = new MilkxSection("wave_1.params");
        wave.Lines.Add("samples = 512");
        document.Sections.Add(wave);

        MilkPresetSource reparsed = MilkPresetReader.ReadText(MilkxToMilkText.Write(document));

        Assert.Equal("512", reparsed.Keys["wave_1_samples"]);
    }
}
