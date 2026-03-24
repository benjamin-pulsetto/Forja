using Forja.Core.Models;

namespace Forja.Core.Tests.Models;

public class StageResultTests
{
    [Fact]
    public void Duration_ReturnsCorrectTimeSpan()
    {
        var start = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(42.5);

        var result = new StageResult
        {
            StartedAt = start,
            CompletedAt = end
        };

        Assert.Equal(TimeSpan.FromSeconds(42.5), result.Duration);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var result = new StageResult();

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.RawOutput);
        Assert.Equal(string.Empty, result.Summary);
    }
}
