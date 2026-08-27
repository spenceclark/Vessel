using ZstdSharp;

namespace Vessel.Storage;

/// <summary>
/// zstd for body columns (§6.2 — agent contexts compress ~10×). Always applied, no
/// size carve-out: one code path, and tiny bodies cost nothing either way.
/// </summary>
public static class BodyCompression
{
    public static byte[] Compress(byte[] data)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(data).ToArray();
    }

    public static byte[] Decompress(byte[] compressed)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }
}
