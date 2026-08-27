namespace Vessel.Capture;

/// <summary>
/// D4 — the active session id. Set once at startup (the newest <c>sessions</c> row, or a
/// freshly created "session 1"), before Kestrel accepts any traffic, and again whenever
/// <c>POST /sessions</c> completes. <see cref="CaptureContext"/> reads it once per request,
/// at construction — this singleton only ever reflects "the session new requests join".
/// </summary>
public sealed class CurrentSession
{
    private long _id;

    public long Id => Interlocked.Read(ref _id);

    public void Set(long id) => Interlocked.Exchange(ref _id, id);
}
