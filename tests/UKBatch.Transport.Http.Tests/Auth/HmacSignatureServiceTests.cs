using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using UKBatch.Transport.Http.Auth;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Auth;

/// <summary>
/// HMAC SHA256 signer / verifier — pure CPU primitives. Constant-time compare,
/// tamper-detection, malformed-input handling.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HmacSignatureServiceTests
{
    private static HmacSignatureService BuildService(string secret = "TEST-SECRET-32B+")
        => new HmacSignatureService(Microsoft.Extensions.Options.Options.Create(new HttpTransportOptions { SharedSecret = secret }));

    [Fact]
    public void Sign_VerifyRoundTrip_Succeeds()
    {
        var svc = BuildService();
        var canonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        var sig = svc.Sign(canonical);
        svc.Verify(canonical, sig).Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var svc = BuildService();
        var canonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        var sig = svc.Sign(canonical);
        // Flip one character in the base64 — produces a syntactically valid but mismatching signature.
        var tampered = (sig[0] == 'A' ? 'B' : 'A') + sig.Substring(1);
        svc.Verify(canonical, tampered).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedCanonical_Fails()
    {
        var svc = BuildService();
        var canonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        var sig = svc.Sign(canonical);
        svc.Verify("POST\n/jobs/publish\n1700000000000\nXYZ\nhash==", sig).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedTimestamp_Fails()
    {
        // Different timestamp in canonical → different HMAC.
        var svc = BuildService();
        var sig = svc.Sign("POST\n/jobs/publish\n1700000000000\nabc\nhash==");
        svc.Verify("POST\n/jobs/publish\n1700000000001\nabc\nhash==", sig).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedNonce_Fails()
    {
        var svc = BuildService();
        var sig = svc.Sign("POST\n/jobs/publish\n1700000000000\nabc\nhash==");
        svc.Verify("POST\n/jobs/publish\n1700000000000\nXYZ\nhash==", sig).Should().BeFalse();
    }

    [Fact]
    public void Verify_MalformedBase64Signature_ReturnsFalse()
    {
        var svc = BuildService();
        var canonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        svc.Verify(canonical, "%%%not-base64%%%").Should().BeFalse();
    }

    [Fact]
    public void Verify_DifferentSecret_Fails()
    {
        var svc1 = BuildService("SECRET-A-32B+++");
        var svc2 = BuildService("SECRET-B-32B+++");
        var canonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        var sig = svc1.Sign(canonical);
        svc2.Verify(canonical, sig).Should().BeFalse();
    }

    [Fact]
    public void Verify_UsesConstantTimeCompare_DoesNotShortCircuitOnFirstDifferingByte()
    {
        // Sentinel — CryptographicOperations.FixedTimeEquals is constant-time. We can observe the
        // shape (no early return) by comparing many bad signatures of equal length: timings should
        // cluster, NOT spread linearly with the position of the first difference.
        // Lighter check: assert that verifying a 32-byte mismatch and a same-length zero-bytes mismatch
        // both return false without throwing — early-return on first difference would still return
        // false, so the contract test is "doesn't shortcut by length" + "doesn't throw on weird input".
        var svc = BuildService();
        var goodCanonical = "POST\n/jobs/publish\n1700000000000\nabc\nhash==";
        var goodSig = svc.Sign(goodCanonical);

        // Two distinct mismatches — both must return false in essentially identical paths.
        var allZeros = Convert.ToBase64String(new byte[32]);
        var allOnes = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        svc.Verify(goodCanonical, allZeros).Should().BeFalse();
        svc.Verify(goodCanonical, allOnes).Should().BeFalse();

        // Sanity: original signature still verifies.
        svc.Verify(goodCanonical, goodSig).Should().BeTrue();

        // The constant-time property itself cannot be observed reliably from wall-clock timing in a
        // shared-CPU test run (a stopwatch-ratio heuristic here flaked under full-suite load), so the
        // invariant is locked structurally: the verifier must delegate byte comparison to
        // CryptographicOperations.FixedTimeEquals rather than any short-circuiting equality.
        var serviceSource = File.ReadAllText(Path.Combine(
            LocateRepoRoot(), "src", "UKBatch.Transport.Http", "Auth", "HmacSignatureService.cs"));
        serviceSource.Should().Contain("CryptographicOperations.FixedTimeEquals",
            "signature comparison must be constant-time; a short-circuiting comparison leaks the first differing byte via timing");
    }

    private static string LocateRepoRoot()
    {
        var assemblyPath = typeof(HmacSignatureServiceTests).Assembly.Location;
        var dir = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UKBatch.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not locate UKBatch.sln in any parent directory.");
        return dir.FullName;
    }

    [Fact]
    public void Sign_EmptyCanonical_Throws()
    {
        var svc = BuildService();
        Action act = () => svc.Sign(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
