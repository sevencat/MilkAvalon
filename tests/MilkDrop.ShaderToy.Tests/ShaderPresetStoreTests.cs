using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Preset;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// 作品落盘（P13）：命名规则、按 <c>[meta] id</c> 覆盖、改标题后删旧文件、额外 meta。
/// </summary>
public sealed class ShaderPresetStoreTests : IDisposable
{
    private readonly string _root;

    public ShaderPresetStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "milkavalon-store-" + Guid.NewGuid().ToString("N"));
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
    public void SaveProject_Writes_Preset_And_Replaces_On_Rename()
    {
        ShaderToyProject project = CreateProject("abc123", "Hello");

        ShaderPresetSaveResult first = ShaderPresetStore.SaveProject(_root, project);

        Assert.False(first.Replaced);
        Assert.Equal(Path.Combine(_root, "presets", "Hello [abc123].shaderx"), first.FilePath);
        Assert.True(File.Exists(first.FilePath));

        ShaderPresetSaveResult second = ShaderPresetStore.SaveProject(_root, project);

        Assert.True(second.Replaced);
        Assert.Equal(first.FilePath, second.FilePath);

        // 站点上改了标题：写新名字，旧文件删掉（按 [meta] id 认人）
        project.Metadata.Name = "Hello Again";
        ShaderPresetSaveResult renamed = ShaderPresetStore.SaveProject(_root, project);

        Assert.True(renamed.Replaced);
        Assert.Equal(Path.Combine(_root, "presets", "Hello Again [abc123].shaderx"), renamed.FilePath);
        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(renamed.FilePath));
    }

    [Fact]
    public void SaveProject_Writes_Extra_Meta()
    {
        ShaderToyProject project = CreateProject("abc123", "Hello");

        ShaderPresetSaveResult result = ShaderPresetStore.SaveProject(
            _root,
            project,
            new Dictionary<string, string>
            {
                ["captured"] = "2026-11-04T12:00:00+08:00",
                ["source_url"] = ShaderPresetStore.SourceUrlPrefix + "abc123",
            });

        ShaderPresetDocument document = ShaderPresetReader.ReadFile(result.FilePath);

        Assert.Equal("2026-11-04T12:00:00+08:00", document.Find("captured"));
        Assert.Equal("https://www.shadertoy.com/view/abc123", document.Find("source_url"));
        Assert.NotNull(document.FindPass("image"));
    }

    [Fact]
    public void SaveProject_Needs_Id()
    {
        ShaderToyProject project = CreateProject(id: string.Empty, title: "No id");

        Assert.Throws<InvalidOperationException>(() => ShaderPresetStore.SaveProject(_root, project));
    }

    /// <summary>造一个最小工程（1 个 image pass）。</summary>
    private static ShaderToyProject CreateProject(string id, string title)
    {
        var project = new ShaderToyProject();
        project.Metadata.Id = id;
        project.Metadata.Name = title;
        project.Metadata.Author = "me";
        project.Passes.Add(new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = "void mainImage(out vec4 o, in vec2 f) { o = vec4(1.0); }",
        });

        return project;
    }
}
