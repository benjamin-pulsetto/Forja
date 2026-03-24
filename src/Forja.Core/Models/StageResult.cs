namespace Forja.Core.Models;

public class StageResult
{
    public bool Success { get; set; }
    public string RawOutput { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}
