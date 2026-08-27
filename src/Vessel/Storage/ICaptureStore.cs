using Vessel.Formats;

namespace Vessel.Storage;

/// <summary>
/// The writer's view of the capture store. A seam over <see cref="SqliteCaptureStore"/> so
/// the writer's resilience (drop a failing batch, give up after N in a row) can be tested
/// against a store that throws on demand.
/// </summary>
public interface ICaptureStore
{
    void Initialize();

    void InsertBatch(IReadOnlyList<EnrichedRecord> batch);

    void EnforceRetention();
}
