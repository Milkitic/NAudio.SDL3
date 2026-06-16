using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<Pcm24ToS32Benchmark>();

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class Pcm24ToS32Benchmark
{
    [Params(64, 256, 1024, 4096, 16384, 65536)]
    public int SampleCount { get; set; }

    private byte[] _input = null!;
    private byte[] _output = null!;

    [GlobalSetup]
    public void Setup()
    {
        _input = new byte[SampleCount * 3];
        _output = new byte[SampleCount * 4];
        new Random(42).NextBytes(_input);
    }

    [Benchmark(Baseline = true, Description = "Scalar")]
    public void ConvertScalar()
    {
        ref byte src = ref _input[0];
        ref byte dst = ref _output[0];
        ConvertScalarIntrinsics(ref src, ref dst, SampleCount);
    }

    [Benchmark(Description = "SIMD")]
    public void ConvertSimd()
    {
        ref byte src = ref _input[0];
        ref byte dst = ref _output[0];
        if (Avx2.IsSupported)
            ConvertAvx2(ref src, ref dst, SampleCount);
        else if (Sse2.IsSupported)
            ConvertSse2(ref src, ref dst, SampleCount);
        else if (AdvSimd.IsSupported)
            ConvertAdvSimd(ref src, ref dst, SampleCount);
        else
            ConvertScalarIntrinsics(ref src, ref dst, SampleCount);
    }

    private static readonly Vector128<byte> PackedShuffle128 = Vector128.Create(
        (byte)0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11);

    private static readonly Vector256<byte> PackedShuffle256 = Vector256.Create(
        (byte)0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11,
        12, 13, 14, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 22, 23, 23);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertAvx2(ref byte src, ref byte dst, int samples)
    {
        nuint sOff = 0, dOff = 0;
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
            sOff += 24; dOff += 32;
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
            sOff += 3; dOff += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertSse2(ref byte src, ref byte dst, int samples)
    {
        nuint sOff = 0, dOff = 0;
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
            sOff += 12; dOff += 16;
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
            sOff += 3; dOff += 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertAdvSimd(ref byte src, ref byte dst, int samples)
        => ConvertSse2(ref src, ref dst, samples);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertScalarIntrinsics(ref byte src, ref byte dst, int samples)
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
