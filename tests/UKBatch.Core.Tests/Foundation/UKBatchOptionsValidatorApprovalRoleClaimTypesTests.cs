using System.Security.Claims;
using FluentAssertions;
using UKBatch;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Validator coverage for the <see cref="UKBatchOptions.ApprovalRoleClaimTypes"/> field.
/// </summary>
public class UKBatchOptionsValidatorApprovalRoleClaimTypesTests
{
    private static UKBatchOptionsValidator Validator => new();

    [Fact]
    public void Validate_DefaultClaimTypes_Succeeds()
    {
        var opts = new UKBatchOptions();
        opts.ApprovalRoleClaimTypes.Should().ContainSingle().Which.Should().Be(ClaimTypes.Role);
        Validator.Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyClaimTypes_Fails()
    {
        var opts = new UKBatchOptions { ApprovalRoleClaimTypes = new List<string>() };
        var result = Validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApprovalRoleClaimTypes");
        result.FailureMessage.Should().Contain("at least 1");
    }

    [Fact]
    public void Validate_NullClaimTypes_Fails()
    {
        var opts = new UKBatchOptions { ApprovalRoleClaimTypes = null! };
        var result = Validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApprovalRoleClaimTypes");
    }

    [Fact]
    public void Validate_WhitespaceEntry_Fails()
    {
        var opts = new UKBatchOptions { ApprovalRoleClaimTypes = new List<string> { "", "role" } };
        var result = Validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApprovalRoleClaimTypes");
        result.FailureMessage.Should().Contain("whitespace");
    }

    [Fact]
    public void Validate_DuplicateEntries_Fails()
    {
        var opts = new UKBatchOptions { ApprovalRoleClaimTypes = new List<string> { "role", "role" } };
        var result = Validator.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ApprovalRoleClaimTypes");
        result.FailureMessage.Should().Contain("duplicate");
    }
}
