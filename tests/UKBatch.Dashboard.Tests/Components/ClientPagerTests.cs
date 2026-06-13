using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Unit tests for <see cref="ClientPager"/> — the render-only pager for already-fetched lists.
/// It owns no data; it computes page math from TotalCount/PageIndex/PageSize and raises
/// OnPageChanged. These tests pin the page arithmetic, the disabled bounds, and the callbacks.
/// </summary>
public sealed class ClientPagerTests : TestContext
{
    private const int PageSize = ClientPager.DefaultPageSize;   // single source of truth (30)

    // A row count that spans three pages at the current page size (PageSize*2 + a partial tail), so the
    // multi-page bounds/navigation arithmetic stays valid if the page size ever changes again.
    private const int ThreePageTotal = (PageSize * 2) + 4;      // 64 at size 30 ⇒ pages 0,1,2
    private const int LastPageIndex = 2;
    private const int LastPageFirstItem = (PageSize * 2) + 1;   // 61 at size 30

    private IRenderedComponent<ClientPager> Render(int total, int pageIndex, Action<int>? onChanged = null)
        => RenderComponent<ClientPager>(p => p
            .Add(c => c.TotalCount, total)
            .Add(c => c.PageIndex, pageIndex)
            .Add(c => c.OnPageChanged, EventCallback.Factory.Create<int>(this, onChanged ?? (_ => { }))));

    [Fact]
    public void DefaultPageSize_IsThirty()
        => ClientPager.DefaultPageSize.Should().Be(30);

    [Fact]
    public void Summary_FirstPage_ShowsOneThroughPageSize()
    {
        var cut = Render(total: ThreePageTotal, pageIndex: 0);
        cut.Markup.Should().Contain($"Showing 1–{PageSize} of {ThreePageTotal}");
        cut.Markup.Should().Contain($"Page 1 of {(ThreePageTotal + PageSize - 1) / PageSize}"); // 3 pages
    }

    [Fact]
    public void Summary_LastPage_ShowsRemainderThroughTotal()
    {
        var cut = Render(total: ThreePageTotal, pageIndex: LastPageIndex);
        cut.Markup.Should().Contain($"Showing {LastPageFirstItem}–{ThreePageTotal} of {ThreePageTotal}");
        cut.Markup.Should().Contain("Page 3 of 3");
    }

    [Fact]
    public void Bounds_FirstPage_BackButtonsDisabled_ForwardEnabled()
    {
        var cut = Render(total: ThreePageTotal, pageIndex: 0);
        var buttons = cut.FindAll("button");
        // Order: first, prev, next, last.
        buttons[0].HasAttribute("disabled").Should().BeTrue("first is disabled on page 1");
        buttons[1].HasAttribute("disabled").Should().BeTrue("prev is disabled on page 1");
        buttons[2].HasAttribute("disabled").Should().BeFalse("next is enabled when more pages remain");
        buttons[3].HasAttribute("disabled").Should().BeFalse("last is enabled when more pages remain");
    }

    [Fact]
    public void Bounds_LastPage_ForwardButtonsDisabled_BackEnabled()
    {
        var cut = Render(total: ThreePageTotal, pageIndex: LastPageIndex);
        var buttons = cut.FindAll("button");
        buttons[0].HasAttribute("disabled").Should().BeFalse("first is enabled past page 1");
        buttons[1].HasAttribute("disabled").Should().BeFalse("prev is enabled past page 1");
        buttons[2].HasAttribute("disabled").Should().BeTrue("next is disabled on the last page");
        buttons[3].HasAttribute("disabled").Should().BeTrue("last is disabled on the last page");
    }

    [Fact]
    public void Bounds_SinglePage_AllButtonsDisabled()
    {
        var cut = Render(total: 5, pageIndex: 0);
        cut.FindAll("button").Should().OnlyContain(b => b.HasAttribute("disabled"),
            "with one page there is nowhere to navigate");
    }

    [Fact]
    public void Next_RaisesNextPageIndex()
    {
        int? raised = null;
        var cut = Render(total: ThreePageTotal, pageIndex: 0, onChanged: i => raised = i);
        cut.FindAll("button")[2].Click();   // next
        raised.Should().Be(1);
    }

    [Fact]
    public void Prev_RaisesPreviousPageIndex()
    {
        int? raised = null;
        var cut = Render(total: ThreePageTotal, pageIndex: LastPageIndex, onChanged: i => raised = i);
        cut.FindAll("button")[1].Click();   // prev
        raised.Should().Be(1);
    }

    [Fact]
    public void Last_RaisesFinalPageIndex()
    {
        int? raised = null;
        var cut = Render(total: ThreePageTotal, pageIndex: 0, onChanged: i => raised = i);
        cut.FindAll("button")[3].Click();   // last
        raised.Should().Be(LastPageIndex, "64 items / 30 per page ⇒ last page index is 2");
    }

    [Fact]
    public void First_RaisesZero()
    {
        int? raised = null;
        var cut = Render(total: ThreePageTotal, pageIndex: LastPageIndex, onChanged: i => raised = i);
        cut.FindAll("button")[0].Click();   // first
        raised.Should().Be(0);
    }

    [Fact]
    public void DisabledButton_AtBound_DoesNotRaise()
    {
        int raisedCount = 0;
        var cut = Render(total: ThreePageTotal, pageIndex: 0, onChanged: _ => raisedCount++);
        // Clicking the disabled prev/first must not fire OnPageChanged (the handlers guard the bound).
        cut.FindAll("button")[0].Click();   // first (disabled)
        cut.FindAll("button")[1].Click();   // prev (disabled)
        raisedCount.Should().Be(0);
    }

    [Fact]
    public void Total_ExactlyOnePageBoundary_HasSinglePage()
    {
        // Exactly PageSize items ⇒ one page, no forward navigation.
        var cut = Render(total: PageSize, pageIndex: 0);
        cut.Markup.Should().Contain("Page 1 of 1");
        cut.FindAll("button")[2].HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Total_OnePastBoundary_HasTwoPages()
    {
        var cut = Render(total: PageSize + 1, pageIndex: 0);
        cut.Markup.Should().Contain("Page 1 of 2");
        cut.FindAll("button")[2].HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ZeroTotal_ShowsDashSummary_AndSinglePage()
    {
        var cut = Render(total: 0, pageIndex: 0);
        cut.Find(".page-envelope-pager__summary").TextContent.Trim().Should().Be("—");
        cut.Markup.Should().Contain("Page 1 of 1");
    }
}
