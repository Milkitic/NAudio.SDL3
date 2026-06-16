using NAudio.Wave;

namespace NAudio.SDL3.WaveProviders;

public sealed class Pcm24ToS32Provider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private byte[]? _scratch;

    public Pcm24ToS32Provider(IWaveProvider source)
    {
        var src = source.WaveFormat;
        if (src.Encoding != WaveFormatEncoding.Pcm || src.BitsPerSample != 24)
        {
            throw new ArgumentException(
                "Pcm24ToS32Provider requires a 24-bit PCM source.", nameof(source));
        }

        _source = source;
        WaveFormat = new WaveFormat(src.SampleRate, 32, src.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || (count & 3) != 0)
        {
            return 0;
        }

        int inputBytes = (count / 4) * 3;
        var scratch = _scratch;
        if (scratch == null || scratch.Length < inputBytes)
        {
            scratch = GC.AllocateUninitializedArray<byte>(inputBytes);
            _scratch = scratch;
        }

        int read = _source.Read(scratch, 0, inputBytes);
        if (read <= 0)
        {
            return 0;
        }

        int validSamples = read / 3;
        int outputBytes = validSamples * 4;

        for (int i = 0; i < validSamples; i++)
        {
            int srcIdx = i * 3;
            int dstIdx = offset + i * 4;
            byte b0 = scratch[srcIdx];
            byte b1 = scratch[srcIdx + 1];
            byte b2 = scratch[srcIdx + 2];
            buffer[dstIdx] = b0;
            buffer[dstIdx + 1] = b1;
            buffer[dstIdx + 2] = b2;
            buffer[dstIdx + 3] = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
        }

        return outputBytes;
    }
}
