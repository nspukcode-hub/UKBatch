namespace UKBatch.Dashboard.Models;

/// <summary>Derived progress percentages for <c>ProgressBar</c> rendering. Computed once per render.</summary>
public sealed record class ProgressViewModel
{
    /// <summary>Items successfully processed.</summary>
    public required long Processed { get; init; }

    /// <summary>Items permanently failed.</summary>
    public required long Failed { get; init; }

    /// <summary>Total expected items; <c>null</c> when unknown.</summary>
    public required long? Total { get; init; }

    /// <summary>Items remaining; null when <see cref="Total"/> is unknown.</summary>
    public long? Remaining => Total is { } t ? Math.Max(0, t - Processed - Failed) : null;

    /// <summary>Percent succeeded (0-100); 0 when total is null/zero.</summary>
    public int PercentSucceeded => PercentOf(Processed - Failed);

    /// <summary>Percent failed (0-100); 0 when total is null/zero.</summary>
    public int PercentFailed => PercentOf(Failed);

    /// <summary>Percent remaining (0-100); 0 when total is null/zero.</summary>
    public int PercentRemaining => PercentOf(Remaining ?? 0);

    /// <summary>Whether the bar should display in a "complete" visual state.</summary>
    public bool IsComplete => Total is { } t && t > 0 && Processed + Failed >= t;

    private int PercentOf(long value)
    {
        if (Total is not { } t || t <= 0) return 0;
        return (int)Math.Clamp(value * 100 / t, 0, 100);
    }
}
