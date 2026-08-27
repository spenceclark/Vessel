namespace Vessel.Capture;

/// <summary>
/// Append-only byte buffer with a hard cap. Beyond the cap, appends are dropped and
/// <see cref="Truncated"/> is set — the stored copy is truncated, never the traffic.
/// </summary>
public sealed class CaptureBuffer(long maxBytes)
{
    private readonly MemoryStream _bytes = new();

    public bool Truncated { get; private set; }

    public long Length => _bytes.Length;

    public void Append(ReadOnlySpan<byte> data)
    {
        long room = maxBytes - _bytes.Length;
        if (room <= 0)
        {
            Truncated |= data.Length > 0;
            return;
        }

        if (data.Length > room)
        {
            _bytes.Write(data[..(int)room]);
            Truncated = true;
        }
        else
        {
            _bytes.Write(data);
        }
    }

    /// <summary>The captured bytes, or null when nothing was captured (empty body → NULL column).</summary>
    public byte[]? ToArrayOrNull() => _bytes.Length == 0 ? null : _bytes.ToArray();
}
