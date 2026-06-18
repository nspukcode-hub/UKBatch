namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Time unit the wizard uses to express the schedule catch-up window as a whole-number magnitude.
/// Operators think in minutes/hours/days, so the wizard captures a numeric value plus one of these
/// units rather than a raw <see cref="System.TimeSpan"/> string.
/// </summary>
public enum CatchUpWindowUnit
{
    /// <summary>The magnitude counts minutes.</summary>
    Minutes = 0,

    /// <summary>The magnitude counts hours.</summary>
    Hours = 1,

    /// <summary>The magnitude counts days.</summary>
    Days = 2,
}

/// <summary>
/// Converts between a (magnitude, <see cref="CatchUpWindowUnit"/>) pair and a <see cref="System.TimeSpan"/>.
/// </summary>
public static class CatchUpWindowDuration
{
    /// <summary>
    /// Builds a <see cref="System.TimeSpan"/> from a magnitude + unit. Returns <c>null</c> when
    /// <paramref name="value"/> is <c>null</c> or non-positive (empty / zero ⇒ no catch-up).
    /// </summary>
    public static TimeSpan? ToTimeSpan(int? value, CatchUpWindowUnit unit)
    {
        if (value is not { } v || v <= 0) return null;
        return unit switch
        {
            CatchUpWindowUnit.Minutes => TimeSpan.FromMinutes(v),
            CatchUpWindowUnit.Hours => TimeSpan.FromHours(v),
            CatchUpWindowUnit.Days => TimeSpan.FromDays(v),
            _ => TimeSpan.FromMinutes(v),
        };
    }

    /// <summary>
    /// Decomposes a stored <see cref="System.TimeSpan"/> back into the largest whole unit that divides it
    /// evenly (days, then hours, then minutes), so an edit-load shows the value the way an operator would
    /// have entered it. Returns <c>(null, Minutes)</c> for <c>null</c> or non-positive input.
    /// </summary>
    public static (int? Value, CatchUpWindowUnit Unit) FromTimeSpan(TimeSpan? window)
    {
        if (window is not { } w || w <= TimeSpan.Zero) return (null, CatchUpWindowUnit.Minutes);

        // Whole minutes is the finest granularity the wizard offers; round to it first so sub-minute
        // remainders (which the UI can't represent) don't force the Minutes unit on an otherwise-even value.
        var totalMinutes = (long)Math.Round(w.TotalMinutes);
        if (totalMinutes <= 0) return (null, CatchUpWindowUnit.Minutes);

        // Clamp an absurdly large window (e.g. a near-TimeSpan.MaxValue value posted directly to the REST
        // API, which the server only checks for non-negativity) to the largest magnitude the wizard's int
        // field can hold, so the unit-decomposition casts below cannot overflow and wrap to a garbage or
        // negative value. A multi-year window is already nonsensical; this just keeps the editor honest.
        if (totalMinutes > int.MaxValue) return (int.MaxValue, CatchUpWindowUnit.Minutes);

        if (totalMinutes % (60 * 24) == 0) return ((int)(totalMinutes / (60 * 24)), CatchUpWindowUnit.Days);
        if (totalMinutes % 60 == 0) return ((int)(totalMinutes / 60), CatchUpWindowUnit.Hours);
        return ((int)totalMinutes, CatchUpWindowUnit.Minutes);
    }

    /// <summary>
    /// Human-readable rendering for the Review pane: <c>"6h"</c> / <c>"30m"</c> / <c>"2d"</c>, or
    /// <c>"none"</c> when there is no catch-up window.
    /// </summary>
    public static string Describe(TimeSpan? window)
    {
        var (value, unit) = FromTimeSpan(window);
        if (value is not { } v) return "none";
        return unit switch
        {
            CatchUpWindowUnit.Days => $"{v}d",
            CatchUpWindowUnit.Hours => $"{v}h",
            _ => $"{v}m",
        };
    }
}
