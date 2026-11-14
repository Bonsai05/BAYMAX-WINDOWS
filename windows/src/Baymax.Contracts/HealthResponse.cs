namespace Baymax.Contracts;

public sealed record HealthResponse(
    string Status,
    string Version,
    DateTimeOffset Timestamp);
