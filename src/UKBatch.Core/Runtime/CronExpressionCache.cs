using System.Collections.Concurrent;
using Cronos;

namespace UKBatch.Runtime;

/// <summary>
/// Parse-once cache for <see cref="CronExpression"/> instances keyed by
/// (expression text, <see cref="CronFormat"/>). Cron parsing is non-trivial; this cache lets
/// the scheduler and builders share resolved instances.
/// </summary>
internal sealed class CronExpressionCache
{
    private readonly ConcurrentDictionary<(string Expression, CronFormat Format), CronExpression> _cache = new();

    /// <summary>Returns a cached instance, parsing on first access.</summary>
    public CronExpression Get(string expression, CronFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression);
        return _cache.GetOrAdd((expression, format), static key => CronExpression.Parse(key.Expression, key.Format));
    }
}
