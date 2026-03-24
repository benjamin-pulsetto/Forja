namespace Forja.Core.Models;

public class Spec
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "feature"; // feature, bugfix, refactor
    public string Description { get; set; } = string.Empty;
    public List<string> Requirements { get; set; } = [];
    public SpecTarget Target { get; set; } = new();
    public List<string> Constraints { get; set; } = [];
}

public class SpecTarget
{
    public string Project { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
}
