using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Registry;
using Xunit;

namespace UKBatch.Core.Tests.Registry;

/// <summary>
/// direct unit tests against <see cref="BatchDefinitionRegistry"/>
/// exposed through its public seam <see cref="IBatchDefinitionLookup"/>. These tests do NOT
/// go through DI; integration via <c>AddUKBatch</c> is covered in
/// <c>tests/UKBatch.Core.Tests/Builders/UKBatchBuilderTests.cs</c>.
/// </summary>
public class BatchDefinitionRegistryTests
{
    private static BatchDefinition Def(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Source = BatchSource.Code,
        Steps = new[]
        {
            new BatchStep
            {
                StepId = "step-1",
                Order = 0,
                StepType = BatchStepType.Job,
                Job = new JobStepData { JobName = "noop" },
            },
        },
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
    };

    [Fact]
    public void TryGetByName_KnownName_ReturnsDefinition()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        var result = ((IBatchDefinitionLookup)registry).TryGetByName("foo");

        result.Should().NotBeNull();
        result!.Name.Should().Be("foo");
        result.Id.Should().Be("id1");
    }

    [Fact]
    public void TryGetByName_UnknownName_ReturnsNull()
    {
        var registry = new BatchDefinitionRegistry();

        var result = ((IBatchDefinitionLookup)registry).TryGetByName("nope");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryGetByName_NullOrEmpty_Throws(string? name)
    {
        var registry = new BatchDefinitionRegistry();
        IBatchDefinitionLookup lookup = registry;

        Action act = () => lookup.TryGetByName(name!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryGetByName_CaseSensitive()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "Foo"));
        IBatchDefinitionLookup lookup = registry;

        lookup.TryGetByName("Foo").Should().NotBeNull();
        lookup.TryGetByName("foo").Should().BeNull();
        lookup.TryGetByName("FOO").Should().BeNull();
    }

    [Fact]
    public void TryGetById_KnownId_ReturnsDefinition()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        var result = ((IBatchDefinitionLookup)registry).TryGetById("id1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("id1");
        result.Name.Should().Be("foo");
    }

    [Fact]
    public void TryGetById_UnknownId_ReturnsNull()
    {
        var registry = new BatchDefinitionRegistry();

        var result = ((IBatchDefinitionLookup)registry).TryGetById("missing");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryGetById_NullOrEmpty_Throws(string? id)
    {
        var registry = new BatchDefinitionRegistry();
        IBatchDefinitionLookup lookup = registry;

        Action act = () => lookup.TryGetById(id!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void All_EmptyRegistry_ReturnsEmptyList()
    {
        var registry = new BatchDefinitionRegistry();

        var all = ((IBatchDefinitionLookup)registry).All();

        all.Should().NotBeNull();
        all.Count.Should().Be(0);
    }

    [Fact]
    public void All_MultipleBatches_ReturnsAllInRegistrationOrder()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "first"));
        registry.Register(Def("id2", "second"));
        registry.Register(Def("id3", "third"));

        var all = ((IBatchDefinitionLookup)registry).All();

        all.Should().HaveCount(3);
        all.Select(d => d.Id).Should().Equal("id1", "id2", "id3");
        all.Select(d => d.Name).Should().Equal("first", "second", "third");
    }

    [Fact]
    public void All_ReturnsDefensiveCopy()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));
        IBatchDefinitionLookup lookup = registry;

        var snapshot = lookup.All();
        // Returned type is IReadOnlyList<BatchDefinition>; the implementation is List<BatchDefinition>.
        // Attempt to mutate the underlying list and assert the registry is unaffected.
        if (snapshot is List<BatchDefinition> mutable)
        {
            mutable.Add(Def("id2", "bar"));
        }

        var fresh = lookup.All();
        fresh.Count.Should().Be(1, "All() must return a defensive copy — caller mutation must not leak");
        fresh[0].Id.Should().Be("id1");
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        Action act = () => registry.Register(Def("id1", "bar"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*id1*");
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        Action act = () => registry.Register(Def("id2", "foo"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*foo*already registered*");
    }

    [Fact]
    public void Register_DuplicateName_RollsBackIdInsertion()
    {
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        // Attempt to register a different id but same name — must throw AND roll back id2.
        Action act = () => registry.Register(Def("id2", "foo"));
        act.Should().Throw<InvalidOperationException>();

        IBatchDefinitionLookup lookup = registry;
        lookup.TryGetById("id2").Should().BeNull(
            "id2 must NOT remain in _byId after name-collision rollback");
        lookup.TryGetById("id1").Should().NotBeNull("id1 must remain reachable");
        lookup.TryGetByName("foo")!.Id.Should().Be("id1", "name 'foo' still maps to the original def");
    }

    [Fact]
    public void Register_NullDefinition_Throws()
    {
        var registry = new BatchDefinitionRegistry();

        Action act = () => registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_AfterFailedRollback_NewRegistrationStillSucceeds()
    {
        // Test #20 — locks down the post-rollback functional invariant:
        // registry stays usable after a name collision; ordered list does NOT
        // include the rolled-back definition.
        var registry = new BatchDefinitionRegistry();
        registry.Register(Def("id1", "foo"));

        // Attempt to register B with id2 + same name "foo" — must throw.
        Action attemptB = () => registry.Register(Def("id2", "foo"));
        attemptB.Should().Throw<InvalidOperationException>();

        // Now register C with a fresh id and name — must succeed.
        registry.Register(Def("id3", "bar"));

        IBatchDefinitionLookup lookup = registry;
        lookup.TryGetByName("bar").Should().NotBeNull();
        lookup.TryGetByName("bar")!.Id.Should().Be("id3");
        lookup.TryGetById("id2").Should().BeNull("rollback held");
        lookup.TryGetById("id3").Should().NotBeNull();

        var all = lookup.All();
        all.Count.Should().Be(2);
        all.Select(d => d.Id).Should().Equal("id1", "id3");
    }
}
