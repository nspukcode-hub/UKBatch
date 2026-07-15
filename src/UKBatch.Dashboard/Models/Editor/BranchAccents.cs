using System.Globalization;
using UKBatch.Dashboard.Models.Wizard;

namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// Colour keys that tie a decision's three on-canvas surfaces together: the chip inside the diamond, the
/// edge leaving it, and the branch card it lands on. The canvas shows no condition text on the edges (a
/// long condition is unreadable there), so colour — not text — is what tells the operator which chip
/// routes to which card.
/// </summary>
/// <remarks>
/// <para>The palette is the editor's own (see <c>--color-branch-*</c>), never an alias of the status ramp.
/// The two never meet — this canvas has no run status to paint, and the run view has no branch identity —
/// but keeping them independent means restyling one can never silently restyle the other.</para>
/// <para>The key is the branch's own index, so re-colouring never cascades: flipping one branch to else
/// greys THAT branch and leaves its siblings' colours alone. Index 0 — the first condition, the "if" arm a
/// two-branch decision is read as — takes the first slot.</para>
/// </remarks>
public static class BranchAccents
{
    /// <summary>Number of distinct conditional-branch colours before the palette repeats.</summary>
    public const int PaletteSize = 6;

    /// <summary>Accent key for the else/default branch: a neutral grey, never a palette colour.</summary>
    public const string Else = "else";

    /// <summary>
    /// The accent key for <paramref name="branch"/> at <paramref name="index"/> in its decision's branch
    /// list: <see cref="Else"/> for the default branch, otherwise a 1-based palette slot.
    /// </summary>
    /// <remarks>
    /// A blank/whitespace condition key counts as else — the same rule the draft→branch projection and
    /// the label formatter use, so a chip reading "else" is never painted as a conditional branch.
    /// </remarks>
    public static string For(DecisionBranchDraft branch, int index)
    {
        ArgumentNullException.ThrowIfNull(branch);
        if (IsElse(branch))
        {
            return Else;
        }
        // InvariantCulture: the key crosses into the DOM as a data-attribute value that CSS matches
        // literally, so a comma-decimal culture must not reshape it.
        return (index % PaletteSize + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="branch"/> is the else/default (no condition that would save).</summary>
    public static bool IsElse(DecisionBranchDraft branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return branch.When is not { } c || string.IsNullOrWhiteSpace(c.ParameterKey);
    }
}
