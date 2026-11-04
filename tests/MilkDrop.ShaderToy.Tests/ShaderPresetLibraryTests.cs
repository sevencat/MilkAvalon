using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// <c>presets/**/*.shaderx</c> 的列表扫描（P13 M3）：只读 <c>[meta]</c>、排序、坏文件只警告。
/// </summary>
public sealed class ShaderPresetLibraryTests : IDisposable
{
    private readonly string _root;
    private readonly string _presets;

    public ShaderPresetLibraryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "milkavalon-presets-" + Guid.NewGuid().ToString("N"));
        _presets = Path.Combine(_root, ShaderPresetLibrary.PresetsFolderName);
        Directory.CreateDirectory(_presets);
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
    public void Scans_Preset_Files_Newest_First()
    {
        Write("Older [aaaaaa].shaderx", "aaaaaa", "Older", "2026-11-01T10:00:00+08:00", "void mainImage(out vec4 o, in vec2 f) { o = vec4(0.1); }");
        Write("Newer [bbbbbb].shaderx", "bbbbbb", "Newer", "2026-11-03T10:00:00+08:00", "void mainImage(out vec4 o, in vec2 f) { o = vec4(0.9); }");

        ShaderPresetLibrary library = ShaderPresetLibrary.Scan(_root);

        Assert.Empty(library.Warnings);
        Assert.Collection(
            library.Entries,
            first =>
            {
                Assert.Equal("bbbbbb", first.Id);
                Assert.Equal("Newer", first.Title);
                Assert.Equal("me", first.Author);
            },
            second => Assert.Equal("aaaaaa", second.Id));
    }

    [Fact]
    public void Loads_Project_From_Preset_File()
    {
        Write(
            "Hello [abc123].shaderx",
            "abc123",
            "Hello",
            "2026-11-03T10:00:00+08:00",
            "void mainImage(out vec4 o, in vec2 f)\n{\n    o = vec4(1.0);\n}");

        ShaderPresetLibrary library = ShaderPresetLibrary.Scan(_root);
        ShaderPresetEntry entry = Assert.Single(library.Entries);

        ShaderToyProject? project = library.TryLoad(entry, out string? error);

        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal("abc123", project!.Metadata.Id);
        Assert.Equal("Hello", project.Metadata.Name);
        Assert.Contains("vec4(1.0)", project.Passes[0].Code, StringComparison.Ordinal);
    }

    [Fact]
    public void File_Without_Id_Is_Warned_And_Skipped()
    {
        File.WriteAllText(
            Path.Combine(_presets, "broken.shaderx"),
            """
            [meta]
            title = 没有 id 的文件

            [shadertoy.image]
            void mainImage(out vec4 o, in vec2 f) { o = vec4(1.0); }
            """);

        ShaderPresetLibrary library = ShaderPresetLibrary.Scan(_root);

        Assert.Empty(library.Entries);
        Assert.Single(library.Warnings);
    }

    [Fact]
    public void Duplicate_Id_Keeps_First_And_Warns()
    {
        Write("A [dup123].shaderx", "dup123", "A", "2026-11-03T10:00:00+08:00", "void mainImage(out vec4 o, in vec2 f) { o = vec4(0.1); }");
        Write("B [dup123].shaderx", "dup123", "B", "2026-11-02T10:00:00+08:00", "void mainImage(out vec4 o, in vec2 f) { o = vec4(0.2); }");

        ShaderPresetLibrary library = ShaderPresetLibrary.Scan(_root);

        Assert.Single(library.Entries);
        Assert.Single(library.Warnings);
        Assert.Contains("重复 id", library.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Presets_Directory_Is_Empty_Not_Thrown()
    {
        ShaderPresetLibrary library = ShaderPresetLibrary.Scan(Path.Combine(_root, "nope"));

        Assert.Empty(library.Entries);
        Assert.Empty(library.Warnings);
    }

    private void Write(string fileName, string id, string title, string captured, string code)
    {
        var project = new ShaderToyProject();
        project.Metadata.Id = id;
        project.Metadata.Name = title;
        project.Metadata.Author = "me";
        project.Passes.Add(new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = code,
        });

        ShaderPresetDocument document = ShaderPresetMapper.FromProject(
            project,
            new Dictionary<string, string> { ["captured"] = captured });

        ShaderPresetWriter.WriteFile(Path.Combine(_presets, fileName), document);
    }
}
