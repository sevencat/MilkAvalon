namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// <c>.shaderx</c> 读写（P13 M1）：段识别、通道行、与 <see cref="ShaderToyProject"/> 的互转、往返稳定性。
/// </summary>
public sealed class PresetFormatTests
{
    /// <summary>造一个"什么都有"的工程：common + buffer + image + sound，通道含 texture/buffer/keyboard。</summary>
    private static ShaderToyProject CreateProject()
    {
        var project = new ShaderToyProject();
        project.Metadata.Id = "f3VSRy";
        project.Metadata.Name = "Welcome to hell";
        project.Metadata.Author = "darvish";
        project.Metadata.Description = "卷曲噪声 + 光线步进";
        project.Metadata.Tags.Add("raymarching");
        project.Metadata.Tags.Add("volumetric");

        var common = new ShaderPass { Type = ShaderPassType.Common, Name = "Common", Code = "const float PI = 3.14159;\n" };
        project.Passes.Add(common);

        var buffer = new ShaderPass
        {
            Type = ShaderPassType.Buffer,
            Name = "Buffer A",
            Code = "void mainImage(out vec4 o, in vec2 f)\n{\n    o = texture(iChannel0, f / iResolution.xy);\n}\n",
        };
        buffer.Outputs.Add(new ShaderPassOutput { Id = "4dfGRr", Channel = 0 });
        buffer.Inputs.Add(new ShaderPassInput
        {
            Channel = 0,
            Type = ShaderInputType.Buffer,
            Id = "4dfGRr",
            Sampler = new ShaderInputSampler { Filter = SamplerFilter.Mipmap, Wrap = SamplerWrap.Repeat, VFlip = true },
        });
        project.Passes.Add(buffer);

        var image = new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = "void mainImage(out vec4 o, in vec2 f)\n{\n    o = vec4(1.0);   // 注释要原样保留\n\n    o *= texture(iChannel1, f / iResolution.xy);\n}\n",
        };
        image.Outputs.Add(new ShaderPassOutput { Id = "Xn3fD1", Channel = 0 });
        image.Inputs.Add(new ShaderPassInput
        {
            Channel = 1,
            Type = ShaderInputType.Texture,
            Path = "/media/a/deadbeef.png",
            Sampler = new ShaderInputSampler { Filter = SamplerFilter.Nearest, Wrap = SamplerWrap.Clamp },
        });
        image.Inputs.Add(new ShaderPassInput { Channel = 2, Type = ShaderInputType.Keyboard });
        project.Passes.Add(image);

        var sound = new ShaderPass { Type = ShaderPassType.Sound, Name = "Sound", Code = "vec2 mainSound(float t) { return vec2(sin(t * 440.0)); }" };
        project.Passes.Add(sound);

        return project;
    }

    [Fact]
    public void Write_Then_Read_Then_Write_Is_Idempotent()
    {
        ShaderToyProject project = CreateProject();

        string first = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(project));
        string second = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(ShaderPresetMapper.ToProject(ShaderPresetReader.ReadText(first))));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Common_Section_Is_Written_First()
    {
        string text = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(CreateProject()));

        int common = text.IndexOf("[shadertoy.common]", StringComparison.Ordinal);
        int image = text.IndexOf("[shadertoy.image]", StringComparison.Ordinal);

        Assert.True(common >= 0 && image > common);
    }

    [Fact]
    public void Code_Is_Preserved_Without_Preprocessing()
    {
        string text = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(CreateProject()));
        ShaderPresetDocument document = ShaderPresetReader.ReadText(text);

        ShaderPresetPass? image = document.FindPass("image");

        Assert.NotNull(image);
        Assert.Contains("// 注释要原样保留", image!.Code, StringComparison.Ordinal);
        Assert.Contains("\n\n", image.Code, StringComparison.Ordinal);
        Assert.Contains("void mainImage", image.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Channel_Lines_Carry_Type_Target_Sampler()
    {
        string text = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(CreateProject()));
        ShaderPresetDocument document = ShaderPresetReader.ReadText(text);

        ShaderPresetPass image = document.FindPass("image")!;
        ShaderPresetChannel texture = image.Inputs.Single(channel => channel.Channel == 1);

        Assert.Equal(ShaderInputType.Texture, texture.Type);
        Assert.Equal("/media/a/deadbeef.png", texture.Target);
        Assert.Equal(SamplerFilter.Nearest, texture.Filter);
        Assert.Equal(SamplerWrap.Clamp, texture.Wrap);
        Assert.False(texture.VFlip);

        ShaderPresetChannel buffer = document.FindPass("buffer_a")!.Inputs.Single();
        Assert.Equal(ShaderInputType.Buffer, buffer.Type);
        Assert.Equal("4dfGRr", buffer.Target);
        Assert.Equal(SamplerFilter.Mipmap, buffer.Filter);
        Assert.Equal(SamplerWrap.Repeat, buffer.Wrap);
        Assert.True(buffer.VFlip);
    }

    [Fact]
    public void Bracket_Line_Inside_Code_Is_Not_A_Section()
    {
        var document = new ShaderPresetDocument();
        document.Meta["kind"] = "shadertoy";

        var pass = new ShaderPresetPass("image");
        pass.CodeLines.Add("uniform float data[4];");
        pass.CodeLines.Add("[2] = 1.0;");
        pass.CodeLines.Add("void mainImage(out vec4 o, in vec2 f) { o = vec4(1.0); }");
        document.Passes.Add(pass);

        ShaderPresetDocument read = ShaderPresetReader.ReadText(ShaderPresetWriter.Write(document));

        Assert.Single(read.Passes);
        Assert.Contains("[2] = 1.0;", read.Passes[0].CodeLines);
        Assert.Empty(read.Warnings);
    }

    [Fact]
    public void Unknown_Section_Is_Warned_Once_And_Ignored()
    {
        const string Text = """
            [meta]
            kind = shadertoy

            [nosuch.section]
            [nosuch.section]
            """;

        ShaderPresetDocument document = ShaderPresetReader.ReadText(Text);

        Assert.Empty(document.Passes);
        Assert.Single(document.Warnings);
    }

    [Fact]
    public void Roles_Map_To_Pass_Types_And_Names()
    {
        const string Text = """
            [meta]
            kind = shadertoy
            id = abc123
            title = T

            [shadertoy.buffer_b]
            void mainImage(out vec4 o, in vec2 f) { o = vec4(1.0); }

            [shadertoy.buffer_b.outputs]
            0 = id_b

            [shadertoy.image]
            void mainImage(out vec4 o, in vec2 f) { o = texture(iChannel0, f / iResolution.xy); }

            [shadertoy.image.inputs]
            0 = buffer id_b linear clamp 0
            """;

        ShaderToyProject project = ShaderPresetMapper.ToProject(ShaderPresetReader.ReadText(Text));

        Assert.Equal("abc123", project.Metadata.Id);
        Assert.Equal("T", project.Metadata.Name);
        Assert.Collection(
            project.Passes,
            buffer =>
            {
                Assert.Equal(ShaderPassType.Buffer, buffer.Type);
                Assert.Equal("Buffer B", buffer.Name);
                Assert.Equal("id_b", buffer.Id);
            },
            image =>
            {
                Assert.Equal(ShaderPassType.Image, image.Type);
                Assert.Equal(ShaderInputType.Buffer, image.Inputs[0].Type);
                Assert.Equal("id_b", image.Inputs[0].Id);
            });
    }

    [Fact]
    public void Missing_Target_Type_Is_Warned()
    {
        const string Text = """
            [shadertoy.image.inputs]
            0 = noise something
            """;

        ShaderPresetDocument document = ShaderPresetReader.ReadText(Text);

        Assert.Single(document.Warnings);
    }

    [Fact]
    public void FileName_Is_Title_Then_Id()
    {
        ShaderToyProject project = CreateProject();

        Assert.Equal("Welcome to hell [f3VSRy].shaderx", ShaderPresetWriter.FileNameFor(project));

        // 标题里的非法字符换成 _；缺标题时只留 id
        Assert.Equal("a_b [abc123].shaderx", ShaderPresetWriter.FileNameFor("a/b", "abc123"));
        Assert.Equal("abc123.shaderx", ShaderPresetWriter.FileNameFor(null, "abc123"));
    }

    [Fact]
    public void Multi_Line_Meta_Value_Is_Flattened_To_One_Line()
    {
        ShaderToyProject project = CreateProject();
        project.Metadata.Description = "第一行\n\n第二行  https://example.com/a\n";

        string first = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(project));
        ShaderPresetDocument document = ShaderPresetReader.ReadText(first);

        Assert.Equal("第一行 第二行 https://example.com/a", document.Find("description"));

        // 压成一行后仍要往返稳定
        string second = ShaderPresetWriter.Write(ShaderPresetMapper.FromProject(ShaderPresetMapper.ToProject(document)));
        Assert.Equal(first, second);
    }

    [Fact]
    public void CodeHash_Stable_And_Sensitive_To_Code()
    {
        ShaderToyProject project = CreateProject();
        string hash = ShaderPresetMapper.ComputeCodeHash(project);

        Assert.Equal(hash, ShaderPresetMapper.ComputeCodeHash(project));
        Assert.Equal(16, hash.Length);

        project.Passes[1].Code = "void mainImage(out vec4 o, in vec2 f) { o = vec4(0.5); }";
        Assert.NotEqual(hash, ShaderPresetMapper.ComputeCodeHash(project));
    }
}
