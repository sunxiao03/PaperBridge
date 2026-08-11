using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Domain.Tests.Glossaries;

public sealed class GlossaryResolverTests
{
    [Fact]
    public void Resolve_UserTermOverridesBuiltInTermRegardlessOfNumericPriority()
    {
        var terms = new[]
        {
            new GlossaryTerm("prompt neutron", "内置译名", GlossarySource.BuiltIn, priority: 999),
            new GlossaryTerm("  Prompt   Neutron ", "瞬发中子", GlossarySource.User)
        };

        var resolved = GlossaryResolver.Resolve(terms);

        Assert.Equal("瞬发中子", resolved["prompt neutron"].PreferredChinese);
    }

    [Fact]
    public void Resolve_HigherPriorityWinsWithinSameSource()
    {
        var terms = new[]
        {
            new GlossaryTerm("neutron flux", "旧译名", GlossarySource.User, priority: 1),
            new GlossaryTerm("neutron flux", "中子通量", GlossarySource.User, priority: 2)
        };

        var resolved = GlossaryResolver.Resolve(terms);

        Assert.Equal("中子通量", resolved["neutron flux"].PreferredChinese);
    }
}

