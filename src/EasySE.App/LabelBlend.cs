using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasySE.App;

/// <summary>Premultiplied label over opaque BGRA video. SIMD and scalar use identical integer rounding.</summary>
internal static class LabelBlend
{
    private static readonly Vector256<byte> AlphaShuffle = Vector256.Create(
        (byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15,
        3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
    internal static bool Vectorized => Avx2.IsSupported;

    internal static void Row(byte[] output, int destination, byte[] pixels, int source, int byteCount, bool useVector = true)
    {
        // Array bounds remain explicit even though the intrinsic loads themselves are unaligned/unchecked.
        if (source < 0 || destination < 0 || byteCount < 0 || byteCount % 4 != 0 ||
            source > pixels.Length - byteCount || destination > output.Length - byteCount)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        var end = source + byteCount;
        if (useVector && Avx2.IsSupported)
        {
            ref var src = ref MemoryMarshal.GetArrayDataReference(pixels);
            ref var dst = ref MemoryMarshal.GetArrayDataReference(output);
            var rounding = Vector256.Create((ushort)128); var opaque = Vector256.Create(0xff000000u).AsByte();
            for (; source <= end - 32; source += 32, destination += 32)
            {
                var s = Vector256.LoadUnsafe(ref src, (nuint)source);
                var d = Vector256.LoadUnsafe(ref dst, (nuint)destination);
                var inverse = Avx2.Subtract(Vector256.Create((byte)255), Avx2.Shuffle(s, AlphaShuffle));
                var low = Mix(Avx2.UnpackLow(s, Vector256<byte>.Zero).AsUInt16(),
                    Avx2.UnpackLow(d, Vector256<byte>.Zero).AsUInt16(), Avx2.UnpackLow(inverse, Vector256<byte>.Zero).AsUInt16(), rounding);
                var high = Mix(Avx2.UnpackHigh(s, Vector256<byte>.Zero).AsUInt16(),
                    Avx2.UnpackHigh(d, Vector256<byte>.Zero).AsUInt16(), Avx2.UnpackHigh(inverse, Vector256<byte>.Zero).AsUInt16(), rounding);
                Avx2.Or(Avx2.PackUnsignedSaturate(low.AsInt16(), high.AsInt16()), opaque).StoreUnsafe(ref dst, (nuint)destination);
            }
        }
        for (; source < end; source += 4, destination += 4)
        {
            var alpha = pixels[source + 3]; if (alpha == 0) continue;
            var inverse = 255 - alpha;
            for (var channel = 0; channel < 3; channel++)
                output[destination + channel] = (byte)Math.Min(255, pixels[source + channel] + (output[destination + channel] * inverse + 127) / 255);
            output[destination + 3] = 255;
        }
    }
    private static Vector256<ushort> Mix(Vector256<ushort> source, Vector256<ushort> destination, Vector256<ushort> inverse, Vector256<ushort> rounding)
    {
        var product = Avx2.Add(Avx2.MultiplyLow(destination, inverse), rounding);
        // For 0..65025, (x+128+((x+128)>>8))>>8 equals (x+127)/255 exactly.
        return Avx2.Add(source, Avx2.ShiftRightLogical(Avx2.Add(product, Avx2.ShiftRightLogical(product, 8)), 8));
    }
}
