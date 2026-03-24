using Forja.Core.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Forja.Core.Services;

public class SpecGenerator
{
    private readonly IClaudeCliRunner _claude;
    private readonly ILogger<SpecGenerator> _logger;

    public SpecGenerator(IClaudeCliRunner claude, ILogger<SpecGenerator> logger)
    {
        _claude = claude;
        _logger = logger;
    }

    public async Task<Spec> FromNaturalLanguageAsync(
        string description, string repoPath, CancellationToken ct = default)
    {
        var prompt = $"""
            Convert the following natural language description into a structured YAML spec.
            Output ONLY valid YAML, no markdown fences, no explanation.

            Use this exact format:
            name: kebab-case-name
            type: feature|bugfix|refactor
            description: |
              Multi-line description of what to build
            requirements:
              - First requirement
              - Second requirement
            target:
              project: relative/path/to/project
              namespace: The.Namespace
            constraints:
              - Follow existing patterns
              - Any other constraints

            Description to convert:
            {description}

            Analyze the repository structure to determine appropriate target project and namespace.
            """;

        var (success, output) = await _claude.RunAsync(prompt, repoPath, ct);

        _logger.LogInformation("Claude spec output:\n{Output}", output);

        if (!success)
            throw new InvalidOperationException($"Failed to generate spec: {output}");

        try
        {
            return ParseYaml(output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse YAML spec from Claude output:\n{Output}", output);
            throw new InvalidOperationException(
                $"Failed to parse spec YAML. Claude returned:\n{output[..Math.Min(output.Length, 500)]}", ex);
        }
    }

    public Spec FromYaml(string yaml)
    {
        return ParseYaml(yaml);
    }

    private Spec ParseYaml(string yaml)
    {
        yaml = yaml.Trim();

        // Strip markdown fences if Claude wrapped it (handles ```yaml, ``` yaml, etc.)
        if (yaml.Contains("```"))
        {
            var lines = yaml.Split('\n').ToList();
            var openIdx = lines.FindIndex(l => l.TrimStart().StartsWith("```"));
            if (openIdx >= 0)
            {
                var closeIdx = lines.FindIndex(openIdx + 1, l => l.TrimStart().StartsWith("```"));
                if (closeIdx >= 0)
                    lines = lines[(openIdx + 1)..closeIdx];
                else
                    lines = lines[(openIdx + 1)..];
            }
            yaml = string.Join('\n', lines);
        }

        // Find the first line that looks like a top-level YAML key (key: value)
        var allLines = yaml.Split('\n').ToList();
        var yamlStartIndex = allLines.FindIndex(l =>
        {
            var trimmed = l.TrimStart();
            return trimmed.StartsWith("---") ||
                   trimmed.StartsWith("name:") ||
                   trimmed.StartsWith("type:") ||
                   trimmed.StartsWith("description:");
        });

        if (yamlStartIndex > 0)
        {
            allLines = allLines[yamlStartIndex..];
            if (allLines.Count > 0 && allLines[0].Trim() == "---")
                allLines.RemoveAt(0);
        }

        // Strip trailing non-YAML content: after the YAML block ends, Claude sometimes
        // appends explanatory text. Find where the YAML ends by looking for lines that
        // aren't indented (not part of a block scalar) and don't look like YAML keys.
        var yamlEndIndex = allLines.Count;
        var knownTopKeys = new[] { "name:", "type:", "description:", "requirements:", "target:", "constraints:", "---" };
        var inBlockScalar = false;
        var blockIndent = 0;

        for (int i = 0; i < allLines.Count; i++)
        {
            var line = allLines[i];
            var trimmed = line.TrimStart();

            // Track block scalar context (description: |)
            if (!inBlockScalar && trimmed.EndsWith("|") && knownTopKeys.Any(k => trimmed.StartsWith(k)))
            {
                inBlockScalar = true;
                blockIndent = line.Length - line.TrimStart().Length + 2; // expect at least 2-space indent
                continue;
            }

            if (inBlockScalar)
            {
                // Still in block scalar if line is blank or indented
                if (string.IsNullOrWhiteSpace(line) || (line.Length > 0 && line.Length - line.TrimStart().Length >= blockIndent))
                    continue;
                inBlockScalar = false;
            }

            // A non-blank, non-indented line that isn't a known YAML key = end of YAML
            if (!string.IsNullOrWhiteSpace(trimmed) &&
                line.Length - trimmed.Length == 0 && // no leading indent
                !knownTopKeys.Any(k => trimmed.StartsWith(k)) &&
                !trimmed.StartsWith("- ") &&
                !trimmed.StartsWith("  "))
            {
                yamlEndIndex = i;
                break;
            }
        }

        allLines = allLines[..yamlEndIndex];

        // Remove trailing "---" if present
        if (allLines.Count > 0 && allLines[^1].Trim() == "---")
            allLines.RemoveAt(allLines.Count - 1);

        yaml = string.Join('\n', allLines).Trim();

        _logger.LogDebug("Cleaned YAML:\n{Yaml}", yaml);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<Spec>(yaml);
    }

    public string ToYaml(Spec spec)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        return serializer.Serialize(spec);
    }
}
