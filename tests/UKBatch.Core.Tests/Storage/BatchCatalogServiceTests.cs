using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

// <summary> — <see cref="BatchCatalogService"/> three-rule contract + composition.</summary>
public class BatchCatalogServiceTests
{
    private static BatchDefinition NewDef(string id, string name, BatchSource src) => new()
    {
        Id = id,
        Name = name,
        Source = src,
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
        Steps = new[]
        {
            new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
        },
    };

    private static (BatchCatalogService svc, IBatchDefinitionLookup code, IBatchDefinitionStore store) Build(
        params (BatchDefinition def, bool inCode)[] defs)
    {
        var code = Substitute.For<IBatchDefinitionLookup>();
        var store = new InMemoryBatchDefinitionStore();
        var codeAll = defs.Where(d => d.inCode).Select(d => d.def).ToList();
        foreach (var (def, inCode) in defs)
        {
            if (inCode) { /* lookup mock returns these */ }
            else { store.CreateAsync(def, default).GetAwaiter().GetResult(); }
        }
        code.All().Returns(codeAll);
        code.TryGetById(Arg.Any<string>()).Returns(ci =>
            codeAll.FirstOrDefault(d => d.Id == ci.Arg<string>()));
        code.TryGetByName(Arg.Any<string>()).Returns(ci =>
            codeAll.FirstOrDefault(d => d.Name == ci.Arg<string>()));
        return (new BatchCatalogService(code, store), code, store);
    }

    [Fact]
    public async Task GetByIdAsync_FindsCodeBatch_First()
    {
        var (svc, _, _) = Build((NewDef("c1", "alpha", BatchSource.Code), true));
        var found = await svc.GetByIdAsync("c1", default);
        found!.Source.Should().Be(BatchSource.Code);
    }

    [Fact]
    public async Task GetByIdAsync_FallsBackToStore_WhenAbsentFromCode()
    {
        var (svc, _, _) = Build((NewDef("s1", "beta", BatchSource.Dashboard), false));
        var found = await svc.GetByIdAsync("s1", default);
        found!.Source.Should().Be(BatchSource.Dashboard);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenAbsentEverywhere()
    {
        var (svc, _, _) = Build();
        (await svc.GetByIdAsync("nope", default)).Should().BeNull();
    }

    [Fact]
    public async Task GetByNameAsync_NullSource_PicksCodeOverStore_OnCollision()
    {
        var (svc, _, _) = Build(
            (NewDef("c1", "shared", BatchSource.Code), true),
            (NewDef("s1", "shared", BatchSource.Dashboard), false));
        var found = await svc.GetByNameAsync("shared", null, default);
        found!.Source.Should().Be(BatchSource.Code);
    }

    [Fact]
    public async Task GetByNameAsync_SourceCode_NeverConsultsStore()
    {
        // rule 2: source=Code MUST NOT touch persistent storage. The store DOES contain "shared" under
        // Dashboard, but with source filter set to Code, the lookup returns null (no Code-side entry).
        var (svc, code, _) = Build(
            (NewDef("s1", "shared", BatchSource.Dashboard), false));
        var found = await svc.GetByNameAsync("shared", BatchSource.Code, default);
        found.Should().BeNull();
        code.Received().TryGetByName("shared");
    }

    [Fact]
    public async Task GetByNameAsync_SourceDashboard_NeverConsultsCode()
    {
        // rule 3: source=Dashboard MUST NOT consult Code. There is no Dashboard match but Code has one.
        var (svc, code, _) = Build(
            (NewDef("c1", "shared", BatchSource.Code), true));
        var found = await svc.GetByNameAsync("shared", BatchSource.Dashboard, default);
        found.Should().BeNull();
        code.DidNotReceive().TryGetByName("shared");
    }

    [Fact]
    public async Task GetByNameAsync_Whitespace_Throws()
    {
        var (svc, _, _) = Build();
        Func<Task> act = () => svc.GetByNameAsync("", null, default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListAsync_PagesCorrectly()
    {
        var defs = Enumerable.Range(0, 10)
            .Select(i => (NewDef($"b{i:D2}", $"name-{i:D2}", BatchSource.Dashboard), false))
            .ToArray();
        var (svc, _, _) = Build(defs);
        var page1 = await svc.ListAsync(new BatchCatalogQuery { Offset = 0, Limit = 3 }, default);
        var page2 = await svc.ListAsync(new BatchCatalogQuery { Offset = 3, Limit = 3 }, default);
        page1.Items.Should().HaveCount(3);
        page2.Items.Should().HaveCount(3);
        page1.Items.Select(d => d.Id).Should().NotIntersectWith(page2.Items.Select(d => d.Id));
        page1.TotalCount.Should().Be(10);
    }

    [Fact]
    public async Task ListAsync_NameContains_FilterCaseInsensitive()
    {
        var (svc, _, _) = Build(
            (NewDef("a", "alphaCharlie", BatchSource.Dashboard), false),
            (NewDef("b", "BetaAlpha", BatchSource.Dashboard), false),
            (NewDef("c", "gamma", BatchSource.Dashboard), false));
        var page = await svc.ListAsync(new BatchCatalogQuery { NameContains = "alpha" }, default);
        page.Items.Should().HaveCount(2);
        page.Items.Select(d => d.Id).Should().Contain(new[] { "a", "b" });
    }

    [Fact]
    public async Task ListAsync_SortOrderIsByNameAscending()
    {
        var (svc, _, _) = Build(
            (NewDef("a", "zeta", BatchSource.Dashboard), false),
            (NewDef("b", "alpha", BatchSource.Dashboard), false),
            (NewDef("c", "mike", BatchSource.Dashboard), false));
        var page = await svc.ListAsync(new BatchCatalogQuery { Limit = 10 }, default);
        page.Items.Select(d => d.Name).Should().ContainInOrder(new[] { "alpha", "mike", "zeta" });
    }

    [Fact]
    public async Task ListAsync_DedupesByName_CodeFirst()
    {
        var (svc, _, _) = Build(
            (NewDef("c1", "shadowed", BatchSource.Code), true),
            (NewDef("s1", "shadowed", BatchSource.Dashboard), false));
        var page = await svc.ListAsync(new BatchCatalogQuery { Limit = 10 }, default);
        page.Items.Should().ContainSingle(d => d.Name == "shadowed");
        page.Items.Single(d => d.Name == "shadowed").Source.Should().Be(BatchSource.Code);
    }

    [Fact]
    public async Task ListAsync_SourceFilter_Respected()
    {
        var (svc, _, _) = Build(
            (NewDef("c1", "code-only", BatchSource.Code), true),
            (NewDef("s1", "dash-only", BatchSource.Dashboard), false),
            (NewDef("a1", "api-only", BatchSource.Api), false));
        var dash = await svc.ListAsync(new BatchCatalogQuery { Source = BatchSource.Dashboard, Limit = 10 }, default);
        dash.Items.Select(d => d.Name).Should().BeEquivalentTo(new[] { "dash-only" });
    }

    [Fact]
    public async Task CountAsync_MatchesListTotal()
    {
        var (svc, _, _) = Build(
            (NewDef("a", "x", BatchSource.Dashboard), false),
            (NewDef("b", "y", BatchSource.Api), false),
            (NewDef("c", "z", BatchSource.Code), true));
        var count = await svc.CountAsync(new BatchCatalogQuery(), default);
        count.Should().Be(3);
    }
}
