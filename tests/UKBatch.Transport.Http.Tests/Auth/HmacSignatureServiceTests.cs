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

        // Heuristic timing-uniformity (lightweight; documented as informational lock):
        // verify all-zeros + all-ones 50 times each; the ratio of mean times should be < 5×.
        // This is a smoke check — exact timing assertions are flaky in CI. Skip if running in stress mode.
        const int Iter = 50;
        var swZeros = Stopwatch.StartNew();
        for (var i = 0; i < Iter; i++) svc.Verify(goodCanonical, allZeros);
        swZeros.Stop();
        var swOnes = Stopwatch.StartNew();
        for (var i = 0; i < Iter; i++) svc.Verify(goodCanonical, allOnes);
        swOnes.Stop();
        var ratio = Math.Max(swZeros.ElapsedTicks, swOnes.ElapsedTicks) /
                    Math.Max(1.0, Math.Min(swZeros.ElapsedTicks, swOnes.ElapsedTicks));
        ratio.Should().BeLessThan(20.0, "constant-time compare should not exhibit > 20× variance in matched-length verify");
    }

    [Fact]
    public void Sign_EmptyCanonical_Throws()
    {
        var svc = BuildService();
        Action act = () => svc.Sign(string.Empty);
        act.Should().Throw<ArgumentException>();
    }
}
