using Forja.Core.Models;
using Forja.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Forja.Core.Tests.Services;

public class SpecGeneratorTests
{
    private readonly IClaudeCliRunner _claude = Substitute.For<IClaudeCliRunner>();
    private readonly ILogger<SpecGenerator> _logger = Substitute.For<ILogger<SpecGenerator>>();
    private readonly SpecGenerator _sut;

    public SpecGeneratorTests()
    {
        _sut = new SpecGenerator(_claude, _logger);
    }

    [Fact]
    public void FromYaml_ParsesCleanYaml()
    {
        var yaml = """
            name: add-logging
            type: feature
            description: |
              Add structured logging
            requirements:
              - Log all HTTP requests
              - Include correlation IDs
            target:
              project: src/Api
              namespace: MyApp.Api
            constraints:
              - Use Serilog
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("add-logging", spec.Name);
        Assert.Equal("feature", spec.Type);
        Assert.Contains("structured logging", spec.Description);
        Assert.Equal(2, spec.Requirements.Count);
        Assert.Equal("Log all HTTP requests", spec.Requirements[0]);
        Assert.Equal("Include correlation IDs", spec.Requirements[1]);
        Assert.Equal("src/Api", spec.Target.Project);
        Assert.Equal("MyApp.Api", spec.Target.Namespace);
        Assert.Single(spec.Constraints);
        Assert.Equal("Use Serilog", spec.Constraints[0]);
    }

    [Fact]
    public void FromYaml_StripsMarkdownFences()
    {
        var yaml = """
            Here is your YAML:
            ```yaml
            name: fix-auth
            type: bugfix
            description: Fix auth bug
            requirements:
              - Fix token refresh
            target:
              project: src/Auth
              namespace: App.Auth
            constraints: []
            ```
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("fix-auth", spec.Name);
        Assert.Equal("bugfix", spec.Type);
    }

    [Fact]
    public void FromYaml_StripsPreambleText()
    {
        var yaml = """
            Sure! Here's the spec for your request:

            name: refactor-db
            type: refactor
            description: Clean up DB layer
            requirements:
              - Extract repository pattern
            target:
              project: src/Data
              namespace: App.Data
            constraints: []
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("refactor-db", spec.Name);
        Assert.Equal("refactor", spec.Type);
    }

    [Fact]
    public void FromYaml_HandlesYamlDocumentSeparators()
    {
        var yaml = """
            ---
            name: add-cache
            type: feature
            description: Add Redis cache
            requirements:
              - Cache user sessions
            target:
              project: src/Cache
              namespace: App.Cache
            constraints: []
            ---
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("add-cache", spec.Name);
    }

    [Fact]
    public void FromYaml_IgnoresUnknownProperties()
    {
        var yaml = """
            name: test-spec
            type: feature
            description: Test
            unknownField: should be ignored
            requirements: []
            target:
              project: src/Test
              namespace: Test
            constraints: []
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("test-spec", spec.Name);
    }

    [Fact]
    public void FromYaml_HandlesClaudePreambleWithLongDescription()
    {
        var yaml = """
            Now I have all the information needed. Here's the YAML spec:

            name: pulsetto-mobile-api-endpoint-tester
            type: feature
            description: |
              Build a standalone .NET console application that automatically discovers and tests
              every HTTP endpoint exposed by the Pulsetto.Mobile.Api project.
              The app acts as an automated QA replacement.
            requirements:
              - Discover all endpoints automatically
              - Authenticate as regular user and admin
              - Test all 51 controllers
            target:
              project: src/EndpointTester
              namespace: Pulsetto.EndpointTester
            constraints:
              - Must be standalone console app

            Let me know if you need any changes to this spec!
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("pulsetto-mobile-api-endpoint-tester", spec.Name);
        Assert.Equal("feature", spec.Type);
        Assert.Contains("standalone", spec.Description);
        Assert.Equal(3, spec.Requirements.Count);
        Assert.Equal("src/EndpointTester", spec.Target.Project);
    }

    [Fact]
    public void FromYaml_HandlesMarkdownFencesWithSpaces()
    {
        var yaml = """
            Here is the spec:

            ```yaml
            name: my-feature
            type: feature
            description: A feature
            requirements:
              - Req 1
            target:
              project: src/App
              namespace: App
            constraints: []
            ```

            Hope this helps!
            """;

        var spec = _sut.FromYaml(yaml);

        Assert.Equal("my-feature", spec.Name);
    }

    [Fact]
    public void ToYaml_ProducesValidYaml()
    {
        var spec = new Spec
        {
            Name = "my-feature",
            Type = "feature",
            Description = "A test feature",
            Requirements = ["Req 1", "Req 2"],
            Target = new SpecTarget { Project = "src/App", Namespace = "App" },
            Constraints = ["No breaking changes"]
        };

        var yaml = _sut.ToYaml(spec);

        Assert.Contains("my-feature", yaml);
        Assert.Contains("Req 1", yaml);
        Assert.Contains("Req 2", yaml);

        // Round-trip: parse back
        var parsed = _sut.FromYaml(yaml);
        Assert.Equal(spec.Name, parsed.Name);
        Assert.Equal(spec.Type, parsed.Type);
        Assert.Equal(spec.Requirements.Count, parsed.Requirements.Count);
    }

    [Fact]
    public async Task FromNaturalLanguageAsync_ReturnsSpec_WhenClaudeSucceeds()
    {
        var claudeYaml = """
            name: add-endpoint
            type: feature
            description: Add health check endpoint
            requirements:
              - Return 200 OK
            target:
              project: src/Api
              namespace: Api
            constraints: []
            """;

        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, claudeYaml));

        var spec = await _sut.FromNaturalLanguageAsync("Add a health check", "/fake/repo");

        Assert.Equal("add-endpoint", spec.Name);
        Assert.Equal("feature", spec.Type);
    }

    [Fact]
    public async Task FromNaturalLanguageAsync_Throws_WhenClaudeFails()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, "CLI error"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.FromNaturalLanguageAsync("Add something", "/fake/repo"));
    }

    [Fact]
    public async Task FromNaturalLanguageAsync_Throws_WhenOutputIsInvalidYaml()
    {
        _claude.RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "name: [[[invalid: {yaml: broken\n  - : :\n  {{{}}}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.FromNaturalLanguageAsync("Add something", "/fake/repo"));
    }
}
