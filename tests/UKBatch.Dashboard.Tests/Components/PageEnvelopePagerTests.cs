using Bunit;
using FluentAssertions;
using UKBatch.Api.Common;
using UKBatch.Dashboard.Components.Shared;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class PageEnvelopePagerTests : TestContext
{
    private static PageEnvelope<int> Envelope(long total, int offset, int count) => new()
    {
        Items = Enumerable.Range(0, count).ToList(),
        TotalCount = total,
        Offset = offset,
        Limit = count,
    };

    [Fact]
    public void FirstPage_BackButtonsDisabled()
    {
        var cut = RenderComponent<PageEnvelopePager<int>>(p => p
            .Add(x => x.Envelope, Envelope(total: 200, offset: 0, count: 50))
            .Add(x => x.Limit, 50));

        var firstBtn = cut.Find("button[aria-label='First page']");
        var prevBtn = cut.Find("button[aria-label='Previous page']");
        firstBtn.HasAttribute("disabled").Should().BeTrue();
        prevBtn.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void LastPage_ForwardButtonsDisabled()
    {
        var cut = RenderComponent<PageEnvelopePager<int>>(p => p
            .Add(x => x.Envelope, Envelope(total: 100, offset: 50, count: 50))
            .Add(x => x.Limit, 50));

        var nextBtn = cut.Find("button[aria-label='Next page']");
        var lastBtn = cut.Find("button[aria-label='Last page']");
        nextBtn.HasAttribute("disabled").Should().BeTrue();
        lastBtn.HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task NextClick_InvokesCallbackWithIncrementedOffset()
    {
        var captured = -1;
        var cut = RenderComponent<PageEnvelopePager<int>>(p => p
            .Add(x => x.Envelope, Envelope(total: 200, offset: 0, count: 50))
            .Add(x => x.Limit, 50)
            .Add(x => x.OnOffsetChanged, (int offset) => { captured = offset; }));

        await cut.Find("button[aria-label='Next page']").ClickAsync(new());
        captured.Should().Be(50);
    }
}
