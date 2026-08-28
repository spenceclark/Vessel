using System.IO.Compression;
using ZstdSharp;

namespace Vessel.Formats;

/// <summary>
/// D3 — content-encoding handling. Bodies are decoded into a scratch buffer so adapters see
/// JSON text and the detail endpoint can display it; the stored bytes are always the
/// original wire bytes (phase-2 D3), never the decoded form.
/// <para>
/// R05: the capture cap bounds the *compressed* bytes Vessel keeps, which says nothing about
/// how large they expand to — a 2 KB gzip body decoded to 2 MB with the row still marked
/// untruncated, and stacked encodings compound it. Every codec now runs through one bounded
/// streaming copy that stops at <c>maxDecodedBytes</c> and never allocates past it, and the
/// outcome is explicit: <see cref="DecodeStatus.TruncatedDecode"/> is a real answer callers
/// must surface, not a silent success.
/// </para>
/// Unknown or undecodable encodings surface as <see cref="DecodeStatus.Failed"/> so the
/// enricher can fall the row back to <c>raw</c> + <c>parse_error</c> with its wire bytes
/// intact.
/// </summary>
public static class BodyDecoder
{
    /// <summary>Streaming copy chunk. Independent of the budget — the budget bounds the total, this bounds one read.</summary>
    private const int CopyChunkBytes = 64 * 1024;

    public enum DecodeStatus
    {
        /// <summary>Fully decoded (or nothing to decode) — <c>Bytes</c> is the complete content.</summary>
        Decoded,

        /// <summary>Decoding hit the budget — <c>Bytes</c> holds the first <c>maxDecodedBytes</c> and there was more.</summary>
        TruncatedDecode,

        /// <summary>An encoding token was unrecognized or the stream would not decode.</summary>
        Failed,
    }

    public readonly record struct Result(byte[]? Bytes, DecodeStatus Status)
    {
        /// <summary>True when the bytes are usable as-is (complete). Truncated output is deliberately excluded.</summary>
        public bool IsComplete => Status == DecodeStatus.Decoded;
    }

    /// <summary>
    /// Decodes <paramref name="body"/> according to <paramref name="contentEncoding"/>
    /// (the raw header value, possibly a comma-separated list applied last-to-first),
    /// producing at most <paramref name="maxDecodedBytes"/> bytes. A null/empty encoding,
    /// or <c>identity</c>, passes the bytes through unchanged — an already-stored body is
    /// bounded by the capture cap, so pass-through needs no budget check.
    /// </summary>
    public static Result Decode(byte[]? body, string? contentEncoding, long maxDecodedBytes)
    {
        if (body is null || string.IsNullOrWhiteSpace(contentEncoding))
        {
            return new Result(body, DecodeStatus.Decoded);
        }

        string[] encodings = contentEncoding
            .Split(',', StringOptions);

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
                (byte[] decoded, bool truncated) = encoding switch
                {
                    "gzip" or "x-gzip" => Bounded(current, static s => new GZipStream(s, CompressionMode.Decompress), maxDecodedBytes),
                    "br" => Bounded(current, static s => new BrotliStream(s, CompressionMode.Decompress), maxDecodedBytes),
                    "deflate" => Inflate(current, maxDecodedBytes),
                    "zstd" => Bounded(current, static s => new DecompressionStream(s), maxDecodedBytes),
                    _ => throw new NotSupportedException(encoding),
                };

                // Stop at the first truncation: the remaining layers would be decoding a
                // deliberately incomplete stream, and reporting "truncated" is more honest
                // than the "failed" that a partial inner stream would otherwise produce.
                if (truncated)
                {
                    return new Result(decoded, DecodeStatus.TruncatedDecode);
                }

                current = decoded;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new Result(null, DecodeStatus.Failed);
            }
        }

        return new Result(current, DecodeStatus.Decoded);
    }

    private const StringSplitOptions StringOptions =
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

    /// <summary>
    /// R05 — copies at most <paramref name="budget"/> bytes out of the decompressor, then
    /// probes for one more byte to distinguish "exactly fits" from "there was more". The
    /// output buffer never exceeds the budget, so a decompression bomb costs bounded memory
    /// rather than whatever the stream claims to expand to.
    /// </summary>
    private static (byte[] Bytes, bool Truncated) Bounded(byte[] data, Func<Stream, Stream> wrap, long budget)
    {
        if (budget <= 0)
        {
            return ([], true);
        }

        using var source = new MemoryStream(data, writable: false);
        using Stream decompressor = wrap(source);
        using var output = new MemoryStream();

        byte[] buffer = new byte[CopyChunkBytes];
        long remaining = budget;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = decompressor.Read(buffer, 0, want);
            if (read <= 0)
            {
                return (output.ToArray(), false);
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }

        bool more = decompressor.ReadByte() >= 0;
        return (output.ToArray(), more);
    }

    // HTTP "deflate" is ambiguous: some servers send zlib-wrapped, some raw. Try zlib
    // first (the correct-per-spec framing), fall back to raw DEFLATE.
    private static (byte[] Bytes, bool Truncated) Inflate(byte[] data, long budget)
    {
        try
        {
            return Bounded(data, static s => new ZLibStream(s, CompressionMode.Decompress), budget);
        }
        catch (InvalidDataException)
        {
            return Bounded(data, static s => new DeflateStream(s, CompressionMode.Decompress), budget);
        }
    }
}
