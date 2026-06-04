using FluentAssertions;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>Typed exception constructors + inheritance contract.</summary>
public class TypedExceptionsTests
{
    [Fact]
    public void JobExecutionNotFoundException_InheritsInvalidOperation()
    {
        var ex = new JobExecutionNotFoundException("not found") { ExecutionId = "x1" };
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.ExecutionId.Should().Be("x1");
        ex.Message.Should().Be("not found");
    }

    [Fact]
    public void ApprovalNotFoundException_InheritsInvalidOperation()
    {
        var ex = new ApprovalNotFoundException("absent") { ApprovalId = "a1" };
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.ApprovalId.Should().Be("a1");
    }

    [Fact]
    public void ApprovalRoleMismatchException_InheritsInvalidOperation()
    {
        var ex = new ApprovalRoleMismatchException("forbidden")
        {
            ApproverIdentity = "alice",
            ApprovalId = "a1",
        };
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.ApproverIdentity.Should().Be("alice");
        ex.ApprovalId.Should().Be("a1");
    }

    [Fact]
    public void ApprovalConfigInvalidException_InheritsInvalidOperation()
    {
        var ex = new ApprovalConfigInvalidException("invalid") { ApprovalId = "a1" };
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.ApprovalId.Should().Be("a1");
    }

    [Fact]
    public void TypedExceptions_HaveInnerExceptionConstructor()
    {
        var inner = new Exception("inner");
        new JobExecutionNotFoundException("m", inner).InnerException.Should().BeSameAs(inner);
        new ApprovalNotFoundException("m", inner).InnerException.Should().BeSameAs(inner);
        new ApprovalRoleMismatchException("m", inner).InnerException.Should().BeSameAs(inner);
        new ApprovalConfigInvalidException("m", inner).InnerException.Should().BeSameAs(inner);
    }
}
