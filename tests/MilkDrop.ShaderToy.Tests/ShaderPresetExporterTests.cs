using MilkDrop.ShaderToy.Import;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// <c>shadertoy.db → presets/*.shaderx</c> 导出（P13 M2）：命名、幂等、手改保护、<c>--force</c>。
/// </summary>
public sealed class ShaderPresetExporterTests : IDisposable
{
    private readonly string _root;

    public ShaderPresetExporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "milkavalon-shaderx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    [Fact]
    public void Exports_One_File_Per_Work_And_Second_Run_Skips()
    {
        CreateDatabase();

        ShaderPresetExportResult first = ShaderPresetExporter.Export(_root);

        Assert.Equal(0, first.Failed);
        Assert.Equal(1, first.Written);
        Assert.Equal(0, first.Skipped);

        string file = Path.Combine(first.PresetsDirectory, "Hello [abc123].shaderx");
        Assert.True(File.Exists(file), "应写成 <title> [<id>].shaderx：" + file);

        ShaderPresetDocument document = ShaderPresetReader.ReadFile(file);

        Assert.Equal("abc123", document.Find("id"));
        Assert.Equal("Hello", document.Find("title"));
        Assert.Equal("me", document.Find("author"));
        Assert.Equal("https://www.shadertoy.com/view/abc123", document.Find("source_url"));
        Assert.Equal(16, document.Find("code_hash")!.Length);

        Assert.NotNull(document.FindPass("buffer_a"));
        ShaderPresetPass image = document.FindPass("image")!;
        Assert.Contains("mainImage", image.Code, StringComparison.Ordinal);
        Assert.Equal("4dfGRr", image.Inputs.Single().Target);

        // 同样的库再导一次：code_hash 相同 → 全部跳过，文件内容逐字节不变
        string before = File.ReadAllText(file);
        ShaderPresetExportResult second = ShaderPresetExporter.Export(_root);

        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public void Hand_Edited_File_Is_Not_Overwritten_Unless_Forced()
    {
        CreateDatabase();

        string file = Path.Combine(
            ShaderPresetExporter.Export(_root).PresetsDirectory,
            "Hello [abc123].shaderx");

        File.AppendAllText(file, "// 手改的一行\n");
        string edited = File.ReadAllText(file);

        ShaderPresetExportResult skipped = ShaderPresetExporter.Export(_root);

        Assert.Equal(0, skipped.Written);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal(edited, File.ReadAllText(file));

        ShaderPresetExportResult forced = ShaderPresetExporter.Export(_root, force: true);

        Assert.Equal(1, forced.Written);
        Assert.DoesNotContain("手改的一行", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Database_Is_Reported_Not_Thrown()
    {
        ShaderPresetExportResult result = ShaderPresetExporter.Export(_root);

        Assert.Equal(0, result.Written);
        Assert.True(result.Failed > 0);
        Assert.NotEmpty(result.Failures);
    }

    /// <summary>造一个只有表数据、没有 <c>RawJson</c> 的库（走"按表重建"那条路径）。</summary>
    private void CreateDatabase()
    {
        const string Sql = """
            create table shaders (
                Id integer primary key autoincrement,
                ShaderId text,
                Name text,
                UserName text,
                Description text,
                Tags text,
                RawJson text,
                Ver text,
                CapturedAt text);

            create table renderpasses (
                ShaderId text,
                PassIndex integer,
                Name text,
                Description text,
                Type text,
                Code text);

            create table shader_inputs (
                ShaderId text,
                PassIndex integer,
                Channel integer,
                InputId text,
                Type text,
                FilePath text,
                Filter text,
                Wrap text,
                VFlip text);

            create table shader_outputs (
                ShaderId text,
                PassIndex integer,
                Channel integer,
                OutputId text);

            insert into shaders (ShaderId, Name, UserName, Description, Tags, RawJson, Ver, CapturedAt)
            values ('abc123', 'Hello', 'me', '一个测试作品', 'raymarching, volumetric', '', '',
                    '2026-11-04 12:00:00');

            insert into renderpasses (ShaderId, PassIndex, Name, Description, Type, Code) values
                ('abc123', 0, 'Image', '', 'image', 'void mainImage(out vec4 o, in vec2 f) { o = vec4(1.0); }'),
                ('abc123', 1, 'Buffer A', '', 'buffer', 'void mainImage(out vec4 o, in vec2 f) { o = vec4(0.5); }');

            insert into shader_outputs (ShaderId, PassIndex, Channel, OutputId)
            values ('abc123', 1, 0, '4dfGRr');

            insert into shader_inputs (ShaderId, PassIndex, Channel, InputId, Type, FilePath, Filter, Wrap, VFlip)
            values ('abc123', 0, 0, '4dfGRr', 'buffer', '', 'linear', 'clamp', '0');
            """;

        Assert.True(
            ShaderDatabase.TryExecute(Path.Combine(_root, ShaderDatabase.FileName), Sql, out string? error),
            error);
    }
}
