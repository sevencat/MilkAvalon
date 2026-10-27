using System.Buffers.Binary;
using MilkDrop.ShaderToy.Audio;
using NAudio.Wave;

// 本仓库只发布 Windows（plan.md §1.2），所有输出后端都是 Windows API，CA1416 在这里是噪音。
#pragma warning disable CA1416

// NAudio 3.x 自己也叫 AudioBuffer（不同用途），命名冲突，这里显式指向引擎的那个。
using ShaderAudioBuffer = MilkDrop.ShaderToy.Audio.AudioBuffer;

namespace MilkAvalon.Services.Audio;

/// <summary>
/// 把 <see cref="ShaderAudioBuffer"/> 里的 float 采样转成 16 位交错立体声喂给声卡。
/// </summary>
/// <remarks>
/// <para>
/// waveOut 和 WASAPI 两个后端共用这一份：后端差异只在"谁来按什么节奏调 <see cref="Read"/>"
/// 和"设备要什么采样率"，读缓冲 + 格式转换的逻辑没必要写两遍。
/// </para>
/// <para>
/// 采样率差异交给 <see cref="ShaderAudioBuffer"/> 自己重采样（它本来就是按设备采样率取数据的），
/// 所以这里直接把 <see cref="WaveFormat"/> 设成设备采样率即可，不用额外接重采样器。
/// </para>
/// </remarks>
internal sealed class Pcm16Provider : IWaveProvider
{
    private const int Channels = 2;

    private readonly float[] _scratch = new float[4096];

    private ShaderAudioBuffer? _buffer;

    public Pcm16Provider(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        WaveFormat = new WaveFormat(sampleRate, 16, Channels);
    }

    public WaveFormat WaveFormat { get; }

    public void Bind(ShaderAudioBuffer? buffer) => _buffer = buffer;

    public int Read(Span<byte> buffer)
    {
        // 16 位立体声：一帧 4 字节；不足一帧的尾巴填静音。
        int frames = buffer.Length / 4;
        int written = frames * 4;

        if (_buffer is null || frames == 0)
        {
            buffer.Clear();
            return buffer.Length;
        }

        if (_scratch.Length < frames * 2)
        {
            // 极端情况下（很大的缓冲请求）只读能放下的部分，剩下的填静音。
            frames = _scratch.Length / 2;
            written = frames * 4;
        }

        _buffer.Read(_scratch.AsSpan(0, frames * Channels), frames, WaveFormat.SampleRate);

        for (var frame = 0; frame < frames; frame++)
        {
            int target = frame * 4;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(target), ToPcm(_scratch[frame * 2]));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(target + 2), ToPcm(_scratch[(frame * 2) + 1]));
        }

        if (written < buffer.Length)
        {
            buffer[written..].Clear();
        }

        return buffer.Length;
    }

    private static short ToPcm(float sample)
    {
        float value = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        return (short)(value * short.MaxValue);
    }
}
