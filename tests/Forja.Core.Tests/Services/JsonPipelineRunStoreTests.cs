using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Services;

public class JsonPipelineRunStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly ILogger<JsonPipelineRunStore> _logger = Substitute.For<ILogger<JsonPipelineRunStore>>();

    public JsonPipelineRunStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forja-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "runs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        var store = new JsonPipelineRunStore(_filePath, _logger);
        var run = new PipelineRun
        {
            Id = "abc12345",
            Spec = new Spec { Name = "test-spec", Type = "feature", Description = "Test" },
            RepoPath = "/test/repo",
            Status = PipelineStatus.Completed
        };

        await store.SaveAsync(run);
        var retrieved = await store.GetAsync("abc12345");

        Assert.NotNull(retrieved);
        Assert.Equal("abc12345", retrieved.Id);
        Assert.Equal("test-spec", retrieved.Spec.Name);
        Assert.Equal(PipelineStatus.Completed, retrieved.Status);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var store = new JsonPipelineRunStore(_filePath, _logger);

        var result = await store.GetAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsOrderedByDate()
    {
        var store = new JsonPipelineRunStore(_filePath, _logger);

        var old = new PipelineRun { Id = "old00001", CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) };
        var mid = new PipelineRun { Id = "mid00001", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var recent = new PipelineRun { Id = "new00001", CreatedAt = DateTimeOffset.UtcNow };

        await store.SaveAsync(old);
        await store.SaveAsync(mid);
        await store.SaveAsync(recent);

        var runs = await store.GetRecentAsync(10);

        Assert.Equal(3, runs.Count);
        Assert.Equal("new00001", runs[0].Id);
        Assert.Equal("mid00001", runs[1].Id);
        Assert.Equal("old00001", runs[2].Id);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsCountLimit()
    {
        var store = new JsonPipelineRunStore(_filePath, _logger);

        for (int i = 0; i < 5; i++)
        {
            await store.SaveAsync(new PipelineRun
            {
                Id = $"run{i:D5}00",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        var runs = await store.GetRecentAsync(3);

        Assert.Equal(3, runs.Count);
    }

    [Fact]
    public async Task PersistsToDisk_AndReloads()
    {
        // Save with one store instance
        var store1 = new JsonPipelineRunStore(_filePath, _logger);
        await store1.SaveAsync(new PipelineRun
        {
            Id = "persist1",
            Spec = new Spec { Name = "persisted", Type = "bugfix" },
            Status = PipelineStatus.Failed,
            Error = "Some error"
        });

        // Create a new store instance pointing to the same file
        var store2 = new JsonPipelineRunStore(_filePath, _logger);
        var run = await store2.GetAsync("persist1");

        Assert.NotNull(run);
        Assert.Equal("persisted", run.Spec.Name);
        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal("Some error", run.Error);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingRun()
    {
        var store = new JsonPipelineRunStore(_filePath, _logger);
        var run = new PipelineRun { Id = "update01", Status = PipelineStatus.Pending };
        await store.SaveAsync(run);

        run.Status = PipelineStatus.Completed;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(run);

        var retrieved = await store.GetAsync("update01");
        Assert.Equal(PipelineStatus.Completed, retrieved!.Status);

        var all = await store.GetRecentAsync(100);
        Assert.Single(all);
    }

    [Fact]
    public void Constructor_HandlesNonexistentFile()
    {
        var store = new JsonPipelineRunStore(
            Path.Combine(_tempDir, "does-not-exist.json"), _logger);

        // Should not throw
        Assert.NotNull(store);
    }

    [Fact]
    public void Constructor_HandlesCorruptFile()
    {
        File.WriteAllText(_filePath, "THIS IS NOT JSON {{{");

        // Should not throw, just logs a warning
        var store = new JsonPipelineRunStore(_filePath, _logger);
        Assert.NotNull(store);
    }
}
