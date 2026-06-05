// The built-in OpenAPI document generator (Microsoft.AspNetCore.OpenApi) requires net9+, so this
// transformer compiles only on net10.0. On net8.0 the package ships REST + SignalR without
// document generation.
#if NET10_0_OR_GREATER
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace UKBatch.Api.OpenApi;

/// <summary>
/// Renders all enums (<c>JobStatus</c>, <c>BatchSource</c>, <c>BatchFailurePolicy</c>,
/// <c>ApprovalTimeoutAction</c>, etc.) as strings rather than integers, so OpenAPI consumers
/// see <c>"Pending"</c> / <c>"Running"</c> instead of <c>0 / 1</c>.
/// </summary>
public sealed class EnumStringTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        if (context.JsonTypeInfo.Type.IsEnum)
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = null;
            schema.Enum = Enum
                .GetNames(context.JsonTypeInfo.Type)
                .Select(n => (JsonNode)JsonValue.Create(n)!)
                .ToList();
        }
        return Task.CompletedTask;
    }
}
#endif
