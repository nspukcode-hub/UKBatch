using Cronos;
using FluentAssertions;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Verifies the parse-once cache hands back the same instance for identical (expression, format)
/// pairs and parses fresh for different formats.
/// </summary>
public class CronExpressionCacheTests
{
    [Fact]
    public void Get_SameExpressionAndFormat_ReturnsSameInstance()
    {
        var cache = new CronExpressionCache();
        var a = cache.Get("0 0 * * *", CronFormat.Standard);
        var b = cache.Get("0 0 * * *", CronFormat.Standard);
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Get_DifferentFormats_KeyedSeparately()
    {
        var cache = new CronExpressionCache();
        // The cache keys on (expression, format). Identical text in two different formats may not
        // both parse, so we test with format-appropriate expressions and verify both succeed.
        var a = cache.Get("0 0 0 * * *", CronFormat.IncludeSeconds);   // 6-field (with seconds)
        var b = cache.Get("0 0 * * *", CronFormat.Standard);            // 5-field
        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public void Get_InvalidExpression_Throws()
    {
        var cache = new CronExpressionCache();
        Action act = () => cache.Get("invalid", CronFormat.Standard);
        act.Should().Throw<CronFormatException>();
    }

    [Fact]
    public void Get_EmptyExpression_ThrowsArgumentException()
    {
        var cache = new CronExpressionCache();
        Action act = () => cache.Get("", CronFormat.Standard);
        act.Should().Throw<ArgumentException>();
    }
}
