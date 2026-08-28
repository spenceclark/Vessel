using System.IO.Compression;
using System.Text;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R05 — decoded output must be bounded. The capture cap bounds compressed wire bytes,
/// which says nothing about expansion: the review's probe turned a 2,082-byte gzip body
/// into a 2,097,163-byte stored body with the row still marked untruncated.
/// </summary>
public class BodyDecoderTests
{
    private const long OneMb = 1024 * 1024;

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Brotli(byte[] data)
    {
        using var output = new MemoryStream();
        using (var br = new BrotliStream(output, CompressionLevel.Optimal))
        {
            br.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Zstd(byte[] data)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>Highly compressible, so a small wire body expands enormously — the bomb shape.</summary>
    private static byte[] Expandable(int bytes) => new byte[bytes];

    // The review's case, with its numbers: ~2 KB of gzip that wants to become 2 MB, against
    // a 1 MB budget. Bounded to the budget, and reported as truncated rather than silently
    // handed back as if complete.
    [Fact]
    public void Gzip_ExpandingPastBudget_IsBoundedAndFlagged()
    {
        byte[] wire = Gzip(Expandable(2 * 1024 * 1024));
        Assert.True(wire.Length < 16 * 1024, $"fixture should be a small wire body, was {wire.Length}");

        BodyDecoder.Result result = BodyDecoder.Decode(wire, "gzip", OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.TruncatedDecode, result.Status);
        Assert.False(result.IsComplete);
        Assert.NotNull(result.Bytes);
        Assert.Equal(OneMb, result.Bytes!.Length);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("deflate")]
    public void EveryCodec_IsBounded(string encoding)
    {
        byte[] payload = Expandable(2 * 1024 * 1024);
        byte[] wire = encoding switch
        {
            "br" => Brotli(payload),
            "zstd" => Zstd(payload),
            "deflate" => Deflate(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

        BodyDecoder.Result result = BodyDecoder.Decode(wire, encoding, OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.TruncatedDecode, result.Status);
        Assert.Equal(OneMb, result.Bytes!.Length);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal))
        {
            z.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    // Stacked encodings compound expansion, so the budget has to hold across the chain, not
    // just per layer.
    [Fact]
    public void StackedEncodings_BudgetHoldsAcrossTheChain()
    {
        byte[] wire = Gzip(Gzip(Expandable(2 * 1024 * 1024)));

        BodyDecoder.Result result = BodyDecoder.Decode(wire, "gzip, gzip", OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.TruncatedDecode, result.Status);
        Assert.True(result.Bytes!.Length <= OneMb);
    }

    [Fact]
    public void StackedEncodings_WithinBudget_DecodeFully()
    {
        byte[] payload = """{"hello":"world"}"""u8.ToArray();
        byte[] wire = Gzip(Brotli(payload));

        // Applied last-to-first: br first (the inner layer), then gzip.
        BodyDecoder.Result result = BodyDecoder.Decode(wire, "br, gzip", OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.Decoded, result.Status);
        Assert.Equal("""{"hello":"world"}""", Encoding.UTF8.GetString(result.Bytes!));
    }

    // The everyday case must be untouched by the budget: a normal compressed capture decodes
    // completely and reports Decoded.
    [Fact]
    public void NormalCompressedBody_DecodesCompletely()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', 50_000));
        BodyDecoder.Result result = BodyDecoder.Decode(Gzip(payload), "gzip", OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.Decoded, result.Status);
        Assert.True(result.IsComplete);
        Assert.Equal(payload, result.Bytes);
    }

    // Exactly at the budget is complete, not truncated — the probe-for-one-more-byte has to
    // distinguish "fits" from "there was more".
    [Fact]
    public void BodyExactlyAtBudget_IsNotFlaggedTruncated()
    {
        byte[] payload = Expandable(1000);
        BodyDecoder.Result result = BodyDecoder.Decode(Gzip(payload), "gzip", 1000);

        Assert.Equal(BodyDecoder.DecodeStatus.Decoded, result.Status);
        Assert.Equal(1000, result.Bytes!.Length);
    }

    [Fact]
    public void OneByteOverBudget_IsFlaggedTruncated()
    {
        byte[] payload = Expandable(1001);
        BodyDecoder.Result result = BodyDecoder.Decode(Gzip(payload), "gzip", 1000);

        Assert.Equal(BodyDecoder.DecodeStatus.TruncatedDecode, result.Status);
        Assert.Equal(1000, result.Bytes!.Length);
    }

    [Fact]
    public void UnknownEncoding_Fails()
    {
        BodyDecoder.Result result = BodyDecoder.Decode([1, 2, 3], "made-up", OneMb);

        Assert.Equal(BodyDecoder.DecodeStatus.Failed, result.Status);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void NoEncoding_PassesThroughUnchanged()
    {
        byte[] body = "plain"u8.ToArray();

        Assert.Equal(body, BodyDecoder.Decode(body, null, OneMb).Bytes);
        Assert.Equal(body, BodyDecoder.Decode(body, "identity", OneMb).Bytes);
        Assert.Equal(BodyDecoder.DecodeStatus.Decoded, BodyDecoder.Decode(body, "", OneMb).Status);
    }
}
