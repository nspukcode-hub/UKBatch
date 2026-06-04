using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard;

/// <summary>
/// Configures per-service named <see cref="HttpClient"/> instances at construction time —
/// applies <c>BaseAddress</c>, <c>Timeout</c>, and the optional <c>X-Api-Key</c> header from
/// the matching <see cref="UKBatchServiceDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>The configurator runs LAZILY (first <see cref="IHttpClientFactory.CreateClient"/> call
/// per named client), so the <see cref="DashboardOptions"/> snapshot captured by the
/// configurator is the post-bind value. Hot-reload of the registry is deferred to v0.2.</para>
/// <para>The naming convention <c>"UKBatch.Dashboard.{ServiceName}"</c> is the contract between
/// <see cref="Clients.UKBatchClientFactory"/> and this configurator — both ends MUST agree
/// on the prefix.</para>
/// </remarks>
internal sealed class HttpClientFactoryNamedConfigurator : IConfigureNamedOptions<HttpClientFactoryOptions>
{
    private const string NamePrefix = "UKBatch.Dashboard.";
    private readonly IOptions<DashboardOptions> _options;

    public HttpClientFactoryNamedConfigurator(IOptions<DashboardOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public void Configure(string? name, HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name is null || !name.StartsWith(NamePrefix, StringComparison.Ordinal)) return;
        var serviceName = name[NamePrefix.Length..];
        var descriptor = _options.Value.Services.FirstOrDefault(s => s.Name == serviceName);
        if (descriptor is null) return;
        var timeout = _options.Value.HttpTimeout;
        options.HttpClientActions.Add(http =>
        {
            http.BaseAddress = descriptor.BaseUrl;
            http.Timeout = timeout;
            if (!string.IsNullOrEmpty(descriptor.ApiKey))
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", descriptor.ApiKey);
            }
            // General static-header seam (bearer / API-key / dev-auth — the general form of ApiKey).
            if (descriptor.Headers is { Count: > 0 } headers)
            {
                foreach (var (k, v) in headers)
                {
                    http.DefaultRequestHeaders.Add(k, v);
                }
            }
        });
    }

    public void Configure(HttpClientFactoryOptions options) => Configure(null, options);
}
