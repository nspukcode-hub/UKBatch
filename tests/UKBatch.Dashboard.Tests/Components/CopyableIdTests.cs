using Bunit;
using FluentAssertions;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Unit tests for <see cref="CopyableId"/> — the selectable full-id + copy-to-clipboard affordance.
/// The page title abbreviates ids; this component surfaces the WHOLE id so it can be pasted into the
/// Executions exact-match filter. The copy itself is performed by a delegated client-side listener
/// (keyed on <c>data-ukbatch-copy</c>) because a Blazor Server round-trip spends Safari's
/// user-activation window — so these tests pin the markup contract the listener depends on and the
/// one-time module bootstrap, not the in-browser copy behaviour (which bunit cannot exercise).
/// </summary>
public sealed class CopyableIdTests : TestContext
{
    private const string FullId = "0192a9c1-7b3e-7def-bc01-aabbccddee99";
    private const string ModulePath = "./_content/UKBatch.Dashboard/js/copy-id.js";

    private BunitJSModuleInterop SetupCopyModule()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("init").SetVoidResult();
        return module;
    }

    [Fact]
    public void Renders_FullValue_NotAbbreviated()
    {
        SetupCopyModule();
        var cut = RenderComponent<CopyableId>(p => p.Add(c => c.Value, FullId));

        // The full id is present verbatim — abbreviation lives on the page <h1>, not here.
        cut.Find(".copyable-id__value").TextContent.Should().Be(FullId);
        cut.Markup.Should().NotContain("…");
    }

    [Fact]
    public void Renders_Label_WhenProvided()
    {
        SetupCopyModule();
        var cut = RenderComponent<CopyableId>(p => p
            .Add(c => c.Value, FullId)
            .Add(c => c.Label, "Run id"));

        cut.Find(".copyable-id__label").TextContent.Should().Be("Run id");
    }

    [Fact]
    public void OmitsLabel_WhenNullOrEmpty()
    {
        SetupCopyModule();
        var cut = RenderComponent<CopyableId>(p => p.Add(c => c.Value, FullId));

        cut.FindAll(".copyable-id__label").Should().BeEmpty("no label markup when none supplied");
    }

    [Fact]
    public void CopyButton_CarriesFullId_InDataAttribute()
    {
        SetupCopyModule();
        var cut = RenderComponent<CopyableId>(p => p.Add(c => c.Value, FullId));

        // The delegated client-side listener copies whatever data-ukbatch-copy carries — it MUST
        // be the full id, not the abbreviation, or the Executions exact-match filter never matches.
        cut.Find("button.copyable-id__copy").GetAttribute("data-ukbatch-copy").Should().Be(FullId);
    }

    [Fact]
    public void FirstRender_LoadsCopyModule_AndInitializesOnce()
    {
        var module = SetupCopyModule();
        var cut = RenderComponent<CopyableId>(p => p.Add(c => c.Value, FullId));

        cut.WaitForAssertion(() =>
            module.Invocations["init"].Should().ContainSingle("the listener bootstrap runs once per component"));

        // A re-render must not bootstrap again (firstRender gate).
        cut.Render();
        module.Invocations["init"].Should().ContainSingle();
    }

    [Fact]
    public void ModuleImportFailure_DoesNotBreakRender()
    {
        // Helper asset unavailable (404 / prerender without JS): the bootstrap throws, the
        // component must swallow it — the button is inert, but the full id stays selectable.
        var module = JSInterop.SetupModule(ModulePath);
        module.SetupVoid("init").SetException(new InvalidOperationException("helper unavailable"));

        var cut = RenderComponent<CopyableId>(p => p.Add(c => c.Value, FullId));

        cut.Find(".copyable-id__value").TextContent.Should().Be(FullId);
        cut.Find("button.copyable-id__copy").GetAttribute("data-ukbatch-copy").Should().Be(FullId);
    }
}
