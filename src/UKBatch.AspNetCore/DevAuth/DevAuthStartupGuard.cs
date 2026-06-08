using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.DevAuth;

/// <summary>
/// Fail-closed guard for the development-only header-trusting auth scheme. On host start it refuses to
/// run in the Production environment (unless explicitly overridden) and otherwise logs a loud warning
/// that the scheme trusts caller-supplied identity headers with no verification.
/// </summary>
internal sealed partial class DevAuthStartupGuard : IHostedService
{
    private readonly IHostEnvironment _environment;
    private readonly UKBatchDevAuthOptions _options;
    private readonly ILogger<DevAuthStartupGuard> _logger;

    public DevAuthStartupGuard(
        IHostEnvironment environment,
        IOptions<UKBatchDevAuthOptions> options,
        ILogger<DevAuthStartupGuard> logger)
    {
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_environment.IsProduction() && !_options.AllowInProduction)
        {
            throw new InvalidOperationException(
                "AddUKBatchDevAuth registered the header-trusting development auth scheme, but the host " +
                "is running in the Production environment. This scheme lets any caller self-assert " +
                "identity via X-Dev-User / X-Dev-Roles with NO verification and must never be used in " +
                "production. Remove AddUKBatchDevAuth and configure a real authentication scheme (such " +
                "as OIDC), or — only for a deliberately throwaway demo — set " +
                "UKBatchDevAuthOptions.AllowInProduction = true to override this guard.");
        }

        LogDevAuthActive(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UKBatch dev auth is active: the header-trusting development auth scheme is enabled. " +
                  "Callers self-assert identity via X-Dev-User / X-Dev-Roles with NO verification. This " +
                  "is for development and demos ONLY — never use it in production.")]
    private static partial void LogDevAuthActive(ILogger logger);
}
