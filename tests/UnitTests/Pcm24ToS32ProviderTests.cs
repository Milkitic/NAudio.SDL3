using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NAudio.SDL3.WaveProviders;
using NAudio.Wave;

namespace UnitTests;

/// <summary>
/// Tests for <see cref="Pcm24ToS32Provider"/> that exercise the scalar tail
/// as well as every SIMD width we dispatch on. The sample sizes are chosen
/// to straddle each vector boundary (4 / 8) and to also cover "looks like
/// one path but actually a tail of the other" combinations.
/// </summary>
public class Pcm24ToS32ProviderTests
{
    // Sizes spanning: pure scalar, SSE2 head + tail, AVX2 head + SSE2/NEON
    // tail, AVX2-only, and a few pathological values.
    public static IEnumerable<object[]> SampleSizes
    {
        get
        {
            int[] sizes =
            {
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 16, 17, 23, 24, 25, 31, 32, 33,
                63, 64, 65, 127, 128, 129, 255, 256, 257, 1023, 1024, 4096, 8191, 16384,
            };
            foreach (var s in sizes)
            {
                yield return new object[] { s };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SampleSizes))]
    public void Read_ProducesSignExtendedS32_ForKnown24BitSamples(int sampleCount)
    {
        var src24 = new WaveFormat(48000, 24, 1);
        var input = new Pcm24WithEdgeCases(sampleCount);
        var provider = new Pcm24ToS32Provider(input);

        var output = new byte[sampleCount * 4];
        int read = provider.Read(output, 0, output.Length);

        Assert.Equal(sampleCount * 4, read);
        for (int i = 0; i < sampleCount; i++)
        {
            int expected = input.SampleAt(i);
            int actual = BitConverter.ToInt32(output, i * 4);
            Assert.True(expected == actual,
                $"Sample {i} mismatch: expected 0x{expected:X8}, got 0x{actual:X8} " +
                $"(size={sampleCount}, Avx2={Avx2.IsSupported})");
        }
    }

    [Theory]
    [MemberData(nameof(SampleSizes))]
    public void Read_AgreesWithScalarReference_ForRandomSamples(int sampleCount)
    {
        var src24 = new WaveFormat(44100, 24, 2);
        var rng = new Random(sampleCount * 7919 + 1);
        var input = new RandomPcm24Source(sampleCount, rng);
        var optimized = new Pcm24ToS32Provider(input);

        var optimizedOut = new byte[sampleCount * 4];
        int optimizedRead = optimized.Read(optimizedOut, 0, optimizedOut.Length);

        var referenceOut = new byte[sampleCount * 4];
        var referenceIn = new byte[input.RawBytes];
        Array.Copy(input.RawInput, referenceIn, input.RawBytes);
        int referenceRead = ScalarConvert(referenceIn, referenceOut, 0, input.RawBytes);

        Assert.Equal(optimizedRead, referenceRead);
        Assert.Equal(referenceOut, optimizedOut);
    }

    [Fact]
    public void Read_RespectsNonZeroOffset()
    {
        const int samples = 17;
        var input = new Pcm24WithEdgeCases(samples);
        var provider = new Pcm24ToS32Provider(input);

        // 8 bytes of unrelated noise in front of the output region to make
        // sure the provider writes at `offset`, not at the start of `buffer`.
        const int leading = 8;
        var output = new byte[leading + samples * 4 + 4];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = 0xCC;
        }

        int read = provider.Read(output, leading, samples * 4);
        Assert.Equal(samples * 4, read);

        // Pre/post padding must be untouched.
        for (int i = 0; i < leading; i++)
        {
            Assert.Equal(0xCC, output[i]);
        }
        for (int i = leading + samples * 4; i < output.Length; i++)
        {
            Assert.Equal(0xCC, output[i]);
        }

        for (int i = 0; i < samples; i++)
        {
            int expected = input.SampleAt(i);
            int actual = BitConverter.ToInt32(output, leading + i * 4);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Read_ReturnsZero_WhenCountIsNotMultipleOf4()
    {
        var input = new Pcm24WithEdgeCases(8);
        var provider = new Pcm24ToS32Provider(input);
        var output = new byte[16];
        Assert.Equal(0, provider.Read(output, 0, 1));
        Assert.Equal(0, provider.Read(output, 0, 3));
        Assert.Equal(0, provider.Read(output, 0, 5));
    }

    [Fact]
    public void Read_ReturnsZero_WhenSourceReturnsNothing()
    {
        var input = new Pcm24WithEdgeCases(0);
        var provider = new Pcm24ToS32Provider(input);
        var output = new byte[64];
        Assert.Equal(0, provider.Read(output, 0, output.Length));
    }

    [Fact]
    public void Read_HandlesShortReadFromSource()
    {
        // Source only supplies 7 bytes (not a multiple of 3 -> 2 samples),
        // but a 24-byte output request was made. The provider must convert
        // the complete 2-sample prefix and stop there.
        var src24 = new WaveFormat(48000, 24, 1);
        var input = new ShortReadSource(src24, bytesToProvide: 7);
        var provider = new Pcm24ToS32Provider(input);

        var output = new byte[8 * 4];
        int read = provider.Read(output, 0, output.Length);

        Assert.Equal(2 * 4, read);
        Assert.Equal(input.ExpectedSample(0), BitConverter.ToInt32(output, 0));
        Assert.Equal(input.ExpectedSample(1), BitConverter.ToInt32(output, 4));
    }

    [Fact]
    public void WaveFormat_ReportsS32AtSameRateAndChannels()
    {
        var src24 = new WaveFormat(48000, 24, 2);
        var provider = new Pcm24ToS32Provider(new Pcm24WithEdgeCases(0, src24));
        Assert.Equal(48000, provider.WaveFormat.SampleRate);
        Assert.Equal(2, provider.WaveFormat.Channels);
        Assert.Equal(32, provider.WaveFormat.BitsPerSample);
    }

    [Fact]
    public void Constructor_RejectsNon24BitSource()
    {
        Assert.Throws<ArgumentException>(() =>
            new Pcm24ToS32Provider(new Pcm24WithEdgeCases(0, new WaveFormat(48000, 16, 1))));
        Assert.Throws<ArgumentException>(() =>
            new Pcm24ToS32Provider(new Pcm24WithEdgeCases(0, new WaveFormat(48000, 32, 1))));
    }

    private static int ScalarConvert(byte[] src, byte[] dst, int dstOffset, int srcByteCount)
    {
        int valid = srcByteCount / 3;
        for (int i = 0; i < valid; i++)
        {
            int s = i * 3;
            int d = dstOffset + i * 4;
            byte b2 = src[s + 2];
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = b2;
            dst[d + 3] = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
        }
        return valid * 4;
    }

    /// <summary>
    /// Source that returns a deterministic mix of 24-bit values, including
    /// the boundaries of the representable range and every sign-bit corner.
    /// </summary>
    private sealed class Pcm24WithEdgeCases : IWaveProvider
    {
        // A small, hand-picked spread that exercises every sign-bit transition
        // and the ends of the 24-bit range. Cycle through it for any sample
        // count and the last sample always lands on something interesting.
        private static readonly int[] Cycle =
        {
            0, 1, -1,
            0x7FFFFF, -0x800000,           // max / min
            0x123456, -0x123456,           // mixed
            0x7F0000, -0x7F0000,
            0x008000, 0x007FFF,            // sign-bit neighbours
            0xFFFF, 0x0100,
        };

        private readonly int _count;
        private int _offset;

        public Pcm24WithEdgeCases(int count, WaveFormat? format = null)
        {
            _count = count;
            WaveFormat = format ?? new WaveFormat(48000, 24, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int SampleAt(int i) => Cycle[i % Cycle.Length];

        public int Read(byte[] buffer, int offset, int count)
        {
            int bytesToWrite = Math.Min(count, _count * 3 - _offset);
            if (bytesToWrite <= 0)
            {
                return 0;
            }

            int samples = bytesToWrite / 3;
            for (int i = 0; i < samples; i++)
            {
                int sample = SampleAt((_offset / 3) + i);
                Write24Le(buffer, offset + i * 3, sample);
            }

            _offset += samples * 3;
            return samples * 3;
        }

        private static void Write24Le(byte[] dst, int dstOffset, int value)
        {
            dst[dstOffset] = (byte)(value & 0xFF);
            dst[dstOffset + 1] = (byte)((value >> 8) & 0xFF);
            dst[dstOffset + 2] = (byte)((value >> 16) & 0xFF);
        }
    }

    private sealed class RandomPcm24Source : IWaveProvider
    {
        public byte[] RawInput { get; }
        public int RawBytes { get; }

        public RandomPcm24Source(int sampleCount, Random rng)
        {
            RawInput = new byte[sampleCount * 3 + 32]; // extra room for overread
            RawBytes = sampleCount * 3;
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = rng.Next() & 0xFFFFFF;
                if ((sample & 0x800000) != 0)
                {
                    sample -= 0x1000000; // make it negative
                }

                RawInput[i * 3] = (byte)(sample & 0xFF);
                RawInput[i * 3 + 1] = (byte)((sample >> 8) & 0xFF);
                RawInput[i * 3 + 2] = (byte)((sample >> 16) & 0xFF);
            }
        }

        public WaveFormat WaveFormat { get; } = new WaveFormat(44100, 24, 2);

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, RawBytes);
            Array.Copy(RawInput, 0, buffer, offset, n);
            return n;
        }
    }

    private sealed class ShortReadSource : IWaveProvider
    {
        private readonly int _bytesToProvide;
        private int _provided;

        public ShortReadSource(WaveFormat format, int bytesToProvide)
        {
            WaveFormat = format;
            _bytesToProvide = bytesToProvide;
        }

        public WaveFormat WaveFormat { get; }

        public int ExpectedSample(int i) => i == 0 ? 0x123456 : -0x654321;

        public int Read(byte[] buffer, int offset, int count)
        {
            int remaining = _bytesToProvide - _provided;
            if (remaining <= 0)
            {
                return 0;
            }

            int n = Math.Min(count, remaining);
            // First 3 bytes -> 0x123456, next 3 bytes -> -0x654321, last byte unused.
            for (int i = 0; i < n; i++)
            {
                int sIdx = _provided + i;
                int sample = sIdx < 3 ? ExpectedSample(0) : ExpectedSample(1);
                buffer[offset + i] = (byte)((sample >> ((sIdx % 3) * 8)) & 0xFF);
            }

            _provided += n;
            return n;
        }
    }
}
