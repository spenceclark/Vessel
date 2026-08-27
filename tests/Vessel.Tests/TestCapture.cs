using System.Text;
using Vessel.Capture;

namespace Vessel.Tests;

/// <summary>Builds minimal <see cref="CaptureRecord"/>s for the enricher/writer unit tests.</summary>
public static class TestCapture
{
    public static CaptureRecord Record(
        string path = "/x",
        string? requestBody = null,
        string? responseBody = null,
        bool streamed = false,
        string? error = null,
        int? status = 200) => new(
        StartedAt: "2026-08-27T00:00:00.0000000Z",
        Backend: "test",
        TagsJson: null,
        Method: "POST",
        Path: path,
        Format: "raw",
        StatusCode: status,
        Error: error,
        Streamed: streamed,
        DurationMs: 100,
        TtftMs: null,
        VesselOverheadMs: 0.1,
        FirstResponseByteMs: null,
        LastResponseByteMs: null,
        RequestHeadersJson: "{\"Content-Type\":[\"application/json\"]}",
        ResponseHeadersJson: "{\"Content-Type\":[\"application/json\"]}",
        RequestBody: requestBody is null ? null : Encoding.UTF8.GetBytes(requestBody),
        ResponseBody: streamed || responseBody is null ? null : Encoding.UTF8.GetBytes(responseBody),
        ResponseRaw: streamed && responseBody is not null ? Encoding.UTF8.GetBytes(responseBody) : null,
        Truncated: false,
        UsageInjected: false);
}
