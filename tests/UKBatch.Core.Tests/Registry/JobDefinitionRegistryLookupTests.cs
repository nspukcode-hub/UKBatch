using FluentAssertions;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Registry;
using Xunit;

namespace UKBatch.Core.Tests.Registry;

/// <summary>
/// <see cref="IJobDefinitionLookup"/> implementation on
/// <see cref="JobDefinitionRegistry"/>: registration-order contract + ordering invariants.
/// </summary>
public class JobDefinitionRegistryLookupTests
{
    private static JobDefinition NewDef(string name) => new()
    {
        Name = name,
        IsPartitioned = false,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
    };

    [Fact]
    public void All_ReturnsRegistrationOrder()
    {
        var reg = new JobDefinitionRegistry();
        reg.Register(NewDef("alpha"), typeof(object), null);
        reg.Register(NewDef("beta"), typeof(object), null);
        reg.Register(NewDef("gamma"), typeof(object), null);
        ((IJobDefinitionLookup)reg).All().Select(d => d.Name).Should().ContainInOrder("alpha", "beta", "gamma");
    }

    [Fact]
    public void All_ReturnsDefensiveCopy_NotSameAsInternalList()
    {
        var reg = new JobDefinitionRegistry();
        reg.Register(NewDef("a"), typeof(object), null);
        var first = ((IJobDefinitionLookup)reg).All();
        var second = ((IJobDefinitionLookup)reg).All();
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void Register_AppendsToOrderedList()
    {
        var reg = new JobDefinitionRegistry();
        reg.Register(NewDef("only"), typeof(object), null);
        ((IJobDefinitionLookup)reg).All().Should().ContainSingle().Which.Name.Should().Be("only");
    }

    [Fact]
    public void Register_DuplicateName_DoesNotAppendToOrdered()
    {
        var reg = new JobDefinitionRegistry();
        reg.Register(NewDef("dup"), typeof(object), null);
        Action act = () => reg.Register(NewDef("dup"), typeof(object), null);
        act.Should().Throw<InvalidOperationException>();
        // the duplicate throw fires BEFORE _ordered.Add; ordered list stays single-entry.
        ((IJobDefinitionLookup)reg).All().Should().ContainSingle();
    }

    [Fact]
    public void TryGet_ReturnsDefinition_WhenRegistered()
    {
        var reg = new JobDefinitionRegistry();
        reg.Register(NewDef("found"), typeof(object), null);
        ((IJobDefinitionLookup)reg).TryGet("found").Should().NotBeNull();
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenAbsent()
    {
        var reg = new JobDefinitionRegistry();
        ((IJobDefinitionLookup)reg).TryGet("missing").Should().BeNull();
    }

    [Fact]
    public void TryGet_Throws_WhenNullOrEmptyName()
    {
        var reg = new JobDefinitionRegistry();
        Action act = () => ((IJobDefinitionLookup)reg).TryGet("");
        act.Should().Throw<ArgumentException>();
    }
}
