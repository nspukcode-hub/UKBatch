using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Typed exception unit tests for the batch/job definition exceptions:
/// <see cref="JobNotRegisteredException"/>, <see cref="BatchDefinitionNotFoundException"/> and
/// <see cref="BatchDefinitionDuplicateNameException"/>.
/// <see cref="BatchConcurrencyConflictException"/> property locks are exercised at its throw site
/// and via the endpoint 409 mapping; only its inner-exception constructor is checked here.
/// </summary>
public class BatchTypedExceptionsTests
{
    [Fact]
    public void JobNotRegisteredException_PreservesJobName()
    {
        var ex = new JobNotRegisteredException("not registered") { JobName = "Test.Job" };
        ex.JobName.Should().Be("Test.Job");
        ex.Message.Should().Be("not registered");
    }

    [Fact]
    public void JobNotRegisteredException_InheritsInvalidOperationException()
    {
        var ex = new JobNotRegisteredException("not registered");
        ex.Should().BeAssignableTo<InvalidOperationException>("existing Assert.ThrowsAsync<InvalidOperationException> setups must still pass.");
    }

    [Fact]
    public void JobNotRegisteredException_PreservesInnerException()
    {
        var inner = new Exception("inner");
        var ex = new JobNotRegisteredException("outer", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void BatchDefinitionNotFoundException_PreservesBatchDefinitionId()
    {
        var ex = new BatchDefinitionNotFoundException("missing") { BatchDefinitionId = "def-X" };
        ex.BatchDefinitionId.Should().Be("def-X");
        ex.Message.Should().Be("missing");
    }

    [Fact]
    public void BatchDefinitionNotFoundException_InheritsInvalidOperationException()
    {
        var ex = new BatchDefinitionNotFoundException("missing");
        ex.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public void BatchDefinitionDuplicateNameException_PreservesNameAndSource()
    {
        var ex = new BatchDefinitionDuplicateNameException("dup")
        {
            Name = "myBatch",
            BatchSource = BatchSource.Dashboard,
        };
        ex.Name.Should().Be("myBatch");
        ex.BatchSource.Should().Be(BatchSource.Dashboard);
        ex.Message.Should().Be("dup");
    }

    [Fact]
    public void BatchDefinitionDuplicateNameException_InheritsInvalidOperationException()
    {
        var ex = new BatchDefinitionDuplicateNameException("dup");
        ex.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public void BatchTypedExceptions_HaveInnerExceptionConstructor()
    {
        var inner = new Exception("inner");
        new JobNotRegisteredException("m", inner).InnerException.Should().BeSameAs(inner);
        new BatchDefinitionNotFoundException("m", inner).InnerException.Should().BeSameAs(inner);
        new BatchDefinitionDuplicateNameException("m", inner).InnerException.Should().BeSameAs(inner);
        new BatchConcurrencyConflictException("m", inner).InnerException.Should().BeSameAs(inner);
    }
}
