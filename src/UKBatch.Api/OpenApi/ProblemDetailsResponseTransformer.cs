using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace UKBatch.Api.OpenApi;

/// <summary>
/// Annotates every operation with the standard Problem Details responses (400, 403, 404, 409, 500)
/// so OpenAPI consumers can identify the failure shape deterministically.
/// </summary>
public sealed class ProblemDetailsResponseTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc/>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Responses ??= new OpenApiResponses();
        AddProblemDetailsResponse(operation, "400", "Validation failed");
        AddProblemDetailsResponse(operation, "403", "Forbidden");
        AddProblemDetailsResponse(operation, "404", "Not found");
        AddProblemDetailsResponse(operation, "409", "Concurrency conflict");
        AddProblemDetailsResponse(operation, "500", "Internal server error");
        return Task.CompletedTask;
    }

    private static void AddProblemDetailsResponse(OpenApiOperation operation, string status, string description)
    {
        if (operation.Responses!.ContainsKey(status))
        {
            return;
        }
        var schema = new OpenApiSchema { Type = JsonSchemaType.Object };
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType { Schema = schema },
            },
        };
    }
}
