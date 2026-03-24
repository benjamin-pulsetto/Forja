using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forja.Core.Models;
using Microsoft.Extensions.Logging;

namespace Forja.Core.Services;

public class JsonPipelineRunStore : IPipelineRunStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonPipelineRunStore> _logger;
    private readonly ConcurrentDictionary<string, PipelineRun> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonPipelineRunStore(string filePath, ILogger<JsonPipelineRunStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
        LoadFromDisk();
    }

    public Task SaveAsync(PipelineRun run)
    {
        _cache[run.Id] = run;
        return FlushToDiskAsync();
    }

    public Task<PipelineRun?> GetAsync(string runId)
    {
        _cache.TryGetValue(runId, out var run);
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<PipelineRun>> GetRecentAsync(int count = 20)
    {
        var runs = _cache.Values
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<PipelineRun>>(runs);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var runs = JsonSerializer.Deserialize<List<PipelineRun>>(json, JsonOpts);
            if (runs is null) return;

            foreach (var run in runs)
                _cache[run.Id] = run;

            _logger.LogInformation("Loaded {Count} pipeline runs from {Path}", runs.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pipeline runs from {Path}", _filePath);
        }
    }

    private async Task FlushToDiskAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var runs = _cache.Values
                .OrderByDescending(r => r.CreatedAt)
                .ToList();

            var json = JsonSerializer.Serialize(runs, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save pipeline runs to {Path}", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
