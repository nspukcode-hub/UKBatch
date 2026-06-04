using System.Net;
using FluentAssertions;

namespace UKBatch.AspNetCore.Tests.Helpers;

/// <summary>Common assertion helpers for the WebApplicationFactory-based integration tests.</summary>
public static class WebFactoryExtensions
{
    /// <summary>
    /// Asserts that <paramref name="response"/> has the expected status code and returns the body
    /// for further inspection.
    /// </summary>
    public static async Task<string> ShouldBeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.StatusCode.Should().Be(expected, $"response body was: {body}");
        return body;
    }
}
