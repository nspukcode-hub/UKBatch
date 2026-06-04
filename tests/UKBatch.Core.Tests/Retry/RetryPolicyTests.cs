using FluentAssertions;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Retry;

/// <summary>
/// Decision matrix for the three retry policies. No Polly — pure hand-rolled logic.
/// </summary>
public class RetryPolicyTests
{
    [Fact]
    public void Immediate_BelowBudget_ReturnsRetryWithZeroDelay()
    {
        var p = new ImmediateRetryPolicy();
        var d = p.Decide(attemptNumber: 1, maxRetries: 3, new Exception("boom"));
        d.ShouldRetry.Should().BeTrue();
        d.Delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Immediate_AtBudget_ReturnsRetry()
    {
        var p = new ImmediateRetryPolicy();
        var d = p.Decide(attemptNumber: 3, maxRetries: 3, new Exception("boom"));
        d.ShouldRetry.Should().BeTrue();
    }

    [Fact]
    public void Immediate_OverBudget_ReturnsTerminal()
    {
        var p = new ImmediateRetryPolicy();
        var d = p.Decide(attemptNumber: 4, maxRetries: 3, new Exception("boom"));
        d.ShouldRetry.Should().BeFalse();
        d.Should().BeSameAs(RetryDecision.Terminal);
    }

    [Fact]
    public void FixedDelay_ReturnsConfiguredDelay()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var p = new FixedDelayRetryPolicy(delay);
        var d = p.Decide(attemptNumber: 1, maxRetries: 5, new Exception("boom"));
        d.ShouldRetry.Should().BeTrue();
        d.Delay.Should().Be(delay);
    }

    [Fact]
    public void FixedDelay_NegativeDelay_ThrowsArgumentOutOfRange()
    {
        Action act = () => new FixedDelayRetryPolicy(TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FixedDelay_OverBudget_ReturnsTerminal()
    {
        var p = new FixedDelayRetryPolicy(TimeSpan.FromSeconds(1));
        var d = p.Decide(attemptNumber: 5, maxRetries: 3, new Exception("boom"));
        d.ShouldRetry.Should().BeFalse();
    }

    [Fact]
    public void Exponential_FirstAttempt_ReturnsBaseDelay()
    {
        var p = new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(60));
        var d = p.Decide(attemptNumber: 1, maxRetries: 5, new Exception("boom"));
        d.ShouldRetry.Should().BeTrue();
        d.Delay.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Exponential_SecondAttempt_DelayIsMultipliedByFactor()
    {
        var p = new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(60));
        var d = p.Decide(attemptNumber: 2, maxRetries: 5, new Exception("boom"));
        d.Delay.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Exponential_HighAttempt_CapsAtMaxDelay()
    {
        var p = new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(1));
        var d = p.Decide(attemptNumber: 20, maxRetries: 100, new Exception("boom"));
        d.Delay.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Exponential_NegativeBaseDelay_ThrowsArgumentOutOfRange()
    {
        Action act = () => new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(-1), 2.0, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Exponential_ZeroFactor_ThrowsArgumentOutOfRange()
    {
        Action act = () => new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(100), 0.0, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Exponential_OverBudget_ReturnsTerminal()
    {
        var p = new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(60));
        var d = p.Decide(attemptNumber: 4, maxRetries: 3, new Exception("boom"));
        d.ShouldRetry.Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(NotSupportedException))]
    public void AllPolicies_ExceptionType_DoesNotAffectDecision(Type exType)
    {
        var ex = (Exception)Activator.CreateInstance(exType)!;
        // No policy inspects exception type in — every exception retries until budget.
        new ImmediateRetryPolicy().Decide(1, 3, ex).ShouldRetry.Should().BeTrue();
        new FixedDelayRetryPolicy(TimeSpan.FromMilliseconds(10)).Decide(1, 3, ex).ShouldRetry.Should().BeTrue();
        new ExponentialRetryPolicy(TimeSpan.FromMilliseconds(10), 2.0, TimeSpan.FromSeconds(1))
            .Decide(1, 3, ex).ShouldRetry.Should().BeTrue();
    }
}
