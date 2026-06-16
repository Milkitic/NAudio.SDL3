using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using NAudio.Wave;

namespace NAudio.SDL3.WaveProviders;

public sealed class Pcm24ToS32Provider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private byte[]? _scratch;

    // Shuffle control: duplicate the high byte (b2) of each 24-bit sample into
    // the sign-extension slot. After this shuffle the low 24 bits of every int32
    // lane hold the 24-bit sample with the MSB replicated into bit31, so a
    // (shl 8, sar 8) pair sign-extends it to a full 32-bit value.
    //
    //   input  bytes: b0 b1 b2 | b3 b4 b5 | b6 b7 b8 | b9 b10 b11
    //   output bytes: b0 b1 b2 b2 | b3 b4 b5 b5 | b6 b7 b8 b8 | b9 b10 b11 b11
    private static readonly Vector128<byte> PackedShuffle128 = Vector128.Create(
        (byte)0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11);

    // Same idea, scaled to 8 samples (24 -> 32 bytes) for AVX2.
    private static readonly Vector256<byte> PackedShuffle256 = Vector256.Create(
        (byte)0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11,
        12, 13, 14, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 22, 23, 23);

    // Pad the input scratch buffer so SIMD loads at the tail can overread a
    // full vector width without walking off the end. AVX2 reads 32 bytes per
    // iteration; we never *use* the padded bytes (the shuffle indices stay
    // inside the 12/24-byte input window), they only need to be addressable.
    private const int SimdTailPadding = 32;

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
        if (scratch == null || scratch.Length < inputBytes + SimdTailPadding)
        {
            scratch = GC.AllocateUninitializedArray<byte>(inputBytes + SimdTailPadding);
            _scratch = scratch;
        }

        int read = _source.Read(scratch, 0, inputBytes);
        if (read <= 0)
        {
            return 0;
        }

        int validSamples = read / 3;
        if (validSamples == 0)
        {
            return 0;
        }

        ConvertPcm24ToS32(ref scratch[0], ref buffer[offset], validSamples);

        return validSamples * 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertPcm24ToS32(ref byte src, ref byte dst, int samples)
    {
        if (Avx2.IsSupported)
        {
            ConvertAvx2(ref src, ref dst, samples);
        }
        else if (Sse2.IsSupported)
        {
            ConvertSse2(ref src, ref dst, samples);
        }
        else if (AdvSimd.IsSupported)
        {
            ConvertAdvSimd(ref src, ref dst, samples);
        }
        else
        {
            ConvertScalar(ref src, ref dst, samples);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertAvx2(ref byte src, ref byte dst, int samples)
    {
        nuint sOff = 0;
        nuint dOff = 0;
        int vectorSamples = samples & ~7;
        int i = 0;

        for (; i < vectorSamples; i += 8)
        {
            Vector256<byte> v = Vector256.LoadUnsafe(ref src, sOff);
            Vector256<byte> shuffled = Vector256.Shuffle(v, PackedShuffle256);
            Vector256<int> v32 = shuffled.AsInt32();
            Vector256<int> shifted = Vector256.ShiftLeft(v32, 8);
            Vector256<int> extended = Vector256.ShiftRightArithmetic(shifted, 8);
            extended.AsByte().StoreUnsafe(ref dst, dOff);
            sOff += 24;
            dOff += 32;
        }

        for (; i < samples; i++)
        {
            byte b0 = Unsafe.Add(ref src, sOff);
            byte b1 = Unsafe.Add(ref src, sOff + 1);
            byte b2 = Unsafe.Add(ref src, sOff + 2);
            Unsafe.Add(ref dst, dOff) = b0;
            Unsafe.Add(ref dst, dOff + 1) = b1;
            Unsafe.Add(ref dst, dOff + 2) = b2;
            Unsafe.Add(ref dst, dOff + 3) = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
            sOff += 3;
            dOff += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertSse2(ref byte src, ref byte dst, int samples)
    {
        nuint sOff = 0;
        nuint dOff = 0;
        int vectorSamples = samples & ~3;
        int i = 0;

        for (; i < vectorSamples; i += 4)
        {
            Vector128<byte> v = Vector128.LoadUnsafe(ref src, sOff);
            Vector128<byte> shuffled = Vector128.Shuffle(v, PackedShuffle128);
            Vector128<int> v32 = shuffled.AsInt32();
            Vector128<int> shifted = Vector128.ShiftLeft(v32, 8);
            Vector128<int> extended = Vector128.ShiftRightArithmetic(shifted, 8);
            extended.AsByte().StoreUnsafe(ref dst, dOff);
            sOff += 12;
            dOff += 16;
        }

        for (; i < samples; i++)
        {
            byte b0 = Unsafe.Add(ref src, sOff);
            byte b1 = Unsafe.Add(ref src, sOff + 1);
            byte b2 = Unsafe.Add(ref src, sOff + 2);
            Unsafe.Add(ref dst, dOff) = b0;
            Unsafe.Add(ref dst, dOff + 1) = b1;
            Unsafe.Add(ref dst, dOff + 2) = b2;
            Unsafe.Add(ref dst, dOff + 3) = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
            sOff += 3;
            dOff += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertAdvSimd(ref byte src, ref byte dst, int samples)
    {
        nuint sOff = 0;
        nuint dOff = 0;
        int vectorSamples = samples & ~3;
        int i = 0;

        for (; i < vectorSamples; i += 4)
        {
            Vector128<byte> v = Vector128.LoadUnsafe(ref src, sOff);
            Vector128<byte> shuffled = Vector128.Shuffle(v, PackedShuffle128);
            Vector128<int> v32 = shuffled.AsInt32();
            Vector128<int> shifted = Vector128.ShiftLeft(v32, 8);
            Vector128<int> extended = Vector128.ShiftRightArithmetic(shifted, 8);
            extended.AsByte().StoreUnsafe(ref dst, dOff);
            sOff += 12;
            dOff += 16;
        }

        for (; i < samples; i++)
        {
            byte b0 = Unsafe.Add(ref src, sOff);
            byte b1 = Unsafe.Add(ref src, sOff + 1);
            byte b2 = Unsafe.Add(ref src, sOff + 2);
            Unsafe.Add(ref dst, dOff) = b0;
            Unsafe.Add(ref dst, dOff + 1) = b1;
            Unsafe.Add(ref dst, dOff + 2) = b2;
            Unsafe.Add(ref dst, dOff + 3) = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
            sOff += 3;
            dOff += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertScalar(ref byte src, ref byte dst, int samples)
    {
        for (int i = 0; i < samples; i++)
        {
            byte b0 = src;
            byte b1 = Unsafe.Add(ref src, 1);
            byte b2 = Unsafe.Add(ref src, 2);
            dst = b0;
            Unsafe.Add(ref dst, 1) = b1;
            Unsafe.Add(ref dst, 2) = b2;
            Unsafe.Add(ref dst, 3) = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
            src = ref Unsafe.Add(ref src, 3);
            dst = ref Unsafe.Add(ref dst, 4);
        }
    }
}
