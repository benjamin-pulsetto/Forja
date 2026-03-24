using Forja.Core.Models;

namespace Forja.Core.Tests.Models;

public class SpecTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var spec = new Spec();

        Assert.Equal(string.Empty, spec.Name);
        Assert.Equal("feature", spec.Type);
        Assert.Equal(string.Empty, spec.Description);
        Assert.Empty(spec.Requirements);
        Assert.NotNull(spec.Target);
        Assert.Empty(spec.Constraints);
    }

    [Fact]
    public void SpecTarget_Defaults_AreCorrect()
    {
        var target = new SpecTarget();

        Assert.Equal(string.Empty, target.Project);
        Assert.Equal(string.Empty, target.Namespace);
    }
}
