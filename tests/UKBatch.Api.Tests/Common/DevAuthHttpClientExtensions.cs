using System.Net.Http.Headers;
using System.Text.Json;

namespace UKBatch.Api.Tests.Common;

/// <summary>Convenience helpers for the DevAuth header pattern (X-Dev-User / X-Dev-Roles).</summary>
internal static class DevAuthHttpClientExtensions
{
    /// <summary>Adds DevAuth headers; mutates <paramref name="client"/>.</summary>
    public static HttpClient WithDevAuth(this HttpClient client, string user, string roles)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Remove("X-Dev-User");
        client.DefaultRequestHeaders.Remove("X-Dev-Roles");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-User", user);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Roles", roles);
        return client;
    }

    /// <summary>Builds <see cref="StringContent"/> as application/json from a serializable object.</summary>
    public static HttpContent JsonContent<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Polls <c>GET /api/approvals</c> until at least one pending approval exists for
    /// <paramref name="batchId"/>. Returns the approval id. Use after triggering a batch with an
    /// approval gate step. Times out after ~10s of polling at 50ms intervals.
    /// </summary>
    public static async Task<string> PollForPendingApprovalAsync(this HttpClient client, string batchId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        for (var i = 0; i < 200; i++)
        {
            var resp = await client.GetAsync(new Uri("/api/approvals", UriKind.Relative));
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (item.GetProperty("batchId").GetString() == batchId)
                    {
                        return item.GetProperty("approvalId").GetString()!;
                    }
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"No pending approval surfaced for batch {batchId} within ~10s.");
    }

    /// <summary>
    /// Triggers a batch by name and returns the assigned batch run id. Optional auth headers are
    /// applied by the caller via <see cref="WithDevAuth"/> before calling this helper.
    /// </summary>
    public static async Task<string> TriggerBatchByNameAsync(this HttpClient client, string batchName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(batchName);
        var resp = await client.PostAsync(
            new Uri($"/api/batches/by-name/{batchName}/run", UriKind.Relative),
            JsonContent(new { }));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("batchId").GetString()!;
    }
}
