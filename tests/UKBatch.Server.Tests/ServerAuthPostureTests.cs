using System.Net;
using FluentAssertions;
using UKBatch.Server.Tests.Common;
using Xunit;

namespace UKBatch.Server.Tests;

/// <summary>
/// <c>UKBatch.Server</c> is fail-closed on auth posture: with neither <c>UKBATCH_ALLOW_ANONYMOUS</c>
/// nor <c>UKBATCH_DEV_AUTH</c> set, boot throws rather than exposing every endpoint anonymously. Either
/// explicit posture lets the server start.
/// </summary>
public sealed class ServerAuthPostureTests
{
    [Fact]
    public void Boot_NoAuthPosture_ThrowsAtStartup()
    {
        // Override the factory's baseline anonymous default back to false and leave DevAuth unset →
        // no explicit posture → the server must refuse to start.
        using var factory = new ServerFactory
        {
            ConfigOverrides = new Dictionary<string, string?>
            {
                ["UKBATCH_ALLOW_ANONYMOUS"] = "false",
            },
        };

        var act = () => factory.CreateClient();

        // WebApplicationFactory may surface the startup throw directly or wrapped — assert on the
        // exception itself or any inner exception carrying the actionable message.
        act.Should().Throw<Exception>()
            .Where(ex => ContainsRefusalMessage(ex),
                "an unset auth posture must throw an actionable startup error");
    }

    [Fact]
    public async Task Boot_AllowAnonymous_Boots_HealthzOk()
    {
        // The default factory sets UKBATCH_ALLOW_ANONYMOUS=true → boots anonymously.
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an explicit anonymous posture is a valid choice and the server boots");
    }

    [Fact]
    public async Task Boot_DevAuth_Boots_HealthzOk()
    {
        using var factory = new ServerFactory
        {
            ConfigOverrides = new Dictionary<string, string?>
            {
                ["UKBATCH_DEV_AUTH"] = "true",
                ["UKBATCH_ALLOW_ANONYMOUS"] = "false",
            },
        };
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "DevAuth is an explicit posture and the server boots with it");
    }

    private static bool ContainsRefusalMessage(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(
                    "refuses to start without an explicit auth posture",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
