// The built-in OpenAPI document generator (Microsoft.AspNetCore.OpenApi) requires net9+, so this
// transformer compiles only on net10.0. On net8.0 the package ships REST + SignalR without
// document generation.
#if NET10_0_OR_GREATER
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace UKBatch.Api.OpenApi;

/// <summary>
/// Strips a trailing slash from every <c>servers[].url</c> in the OpenAPI document. The default
/// server URL is emitted with a trailing slash (e.g. <c>http://host:port/</c>). Tooling that
/// imports the document verbatim — Postman, openapi-generator — treats that URL as a base and
/// joins relative paths onto it, producing a double slash (<c>http://host:port//api/jobs</c>)
/// that fails to match any route. Trimming the slash here makes the published base URL safe to
/// concatenate with the relative route paths.
/// </summary>
public sealed class ServersTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Servers is null)
        {
            return Task.CompletedTask;
        }

        foreach (var server in document.Servers)
        {
            if (server.Url is { Length: > 0 } url && url.EndsWith('/'))
            {
                server.Url = url.TrimEnd('/');
            }
        }

        return Task.CompletedTask;
    }
}
#endif
