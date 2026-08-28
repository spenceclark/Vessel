namespace Vessel.Config;

/// <summary>
/// R05/D01 — the single definition of <c>capture.maxBodyMb</c> in bytes. Wire capture, the
/// enricher's scratch decode, and the detail endpoint's display decode all bound themselves
/// by the same number: decoded output shares the capture cap rather than having a separate
/// budget, so "how much of one request can Vessel hold in memory" has one answer.
/// </summary>
public static class CaptureBudget
{
    public static long MaxWireBytes(VesselConfig config) => (long)config.Capture.MaxBodyMb * 1024 * 1024;

    public static long MaxDecodedBytes(VesselConfig config) => MaxWireBytes(config);
}
