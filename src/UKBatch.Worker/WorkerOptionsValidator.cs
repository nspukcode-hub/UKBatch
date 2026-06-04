using Microsoft.Extensions.Options;

namespace UKBatch.Worker;

/// <summary>
/// Validates <see cref="WorkerOptions"/> at host startup (eager via <c>ValidateOnStart</c> is the
/// caller's choice; this runs on first resolution otherwise). Enforces: <see cref="WorkerOptions.WorkerName"/>
/// is non-whitespace; and, when <see cref="WorkerOptions.Heartbeat"/> is enabled,
/// <see cref="WorkerOptions.ServerUrl"/> is a valid absolute URI AND
/// <see cref="WorkerOptions.HeartbeatInterval"/> is strictly positive.
/// </summary>
internal sealed class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.WorkerName))
        {
            failures.Add($"{nameof(WorkerOptions.WorkerName)} is required and must be non-whitespace.");
        }

        if (options.Heartbeat)
        {
            if (string.IsNullOrWhiteSpace(options.ServerUrl)
                || !Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out _))
            {
                failures.Add(
                    $"{nameof(WorkerOptions.ServerUrl)} must be a valid absolute URI when {nameof(WorkerOptions.Heartbeat)} is enabled " +
                    $"(e.g. http://ukbatch-server:8080).");
            }

            if (options.HeartbeatInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(WorkerOptions.HeartbeatInterval)} must be greater than TimeSpan.Zero when {nameof(WorkerOptions.Heartbeat)} is enabled.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
