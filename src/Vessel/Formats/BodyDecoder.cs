using System.IO.Compression;
using ZstdSharp;

namespace Vessel.Formats;

/// <summary>
/// D3 — content-encoding handling for both parsing and storage. Adapters always see the
/// decoded bytes; a non-streamed response also keeps them for <c>response_body</c>
/// (<see cref="FormatEnricher"/>) — the raw wire bytes stay untouched everywhere else
/// (the caller's actual response via <c>ResponseTeeStream</c>, and the streamed
/// <c>response_raw</c> column). Unknown or undecodable encodings surface as
/// <see cref="DecodeStatus.Failed"/> so the enricher can fall the row back to
/// <c>raw</c> + <c>parse_error</c> with its original wire bytes intact.
/// </summary>
public static class BodyDecoder
{
    public enum DecodeStatus
    {
        /// <summary>Decoded (or nothing to decode) — <c>Bytes</c> is usable.</summary>
        Ok,

        /// <summary>An encoding token was unrecognized or the stream would not decode.</summary>
        Failed,
    }

    public readonly record struct Result(byte[]? Bytes, DecodeStatus Status);

    /// <summary>
    /// Decodes <paramref name="body"/> according to <paramref name="contentEncoding"/>
    /// (the raw header value, possibly a comma-separated list applied last-to-first).
    /// A null/empty encoding, or <c>identity</c>, passes the bytes through unchanged.
    /// </summary>
    public static Result Decode(byte[]? body, string? contentEncoding)
    {
        if (body is null || string.IsNullOrWhiteSpace(contentEncoding))
        {
            return new Result(body, DecodeStatus.Ok);
        }

        string[] encodings = contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        byte[] current = body;
        for (int i = encodings.Length - 1; i >= 0; i--)
        {
            string encoding = encodings[i].ToLowerInvariant();
            if (encoding is "identity")
            {
                continue;
            }

            try
            {
                current = encoding switch
                {
                    "gzip" or "x-gzip" => DecompressStream(current, static s => new GZipStream(s, CompressionMode.Decompress)),
                    "br" => DecompressStream(current, static s => new BrotliStream(s, CompressionMode.Decompress)),
                    "deflate" => Inflate(current),
                    "zstd" => ZstdDecompress(current),
                    _ => throw new NotSupportedException(encoding),
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new Result(null, DecodeStatus.Failed);
            }
        }

        return new Result(current, DecodeStatus.Ok);
    }

    private static byte[] DecompressStream(byte[] data, Func<Stream, Stream> wrap)
    {
        using var source = new MemoryStream(data, writable: false);
        using Stream decompressor = wrap(source);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    // HTTP "deflate" is ambiguous: some servers send zlib-wrapped, some raw. Try zlib
    // first (the correct-per-spec framing), fall back to raw DEFLATE.
    private static byte[] Inflate(byte[] data)
    {
        try
        {
            return DecompressStream(data, static s => new ZLibStream(s, CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            return DecompressStream(data, static s => new DeflateStream(s, CompressionMode.Decompress));
        }
    }

    private static byte[] ZstdDecompress(byte[] data)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }
}
