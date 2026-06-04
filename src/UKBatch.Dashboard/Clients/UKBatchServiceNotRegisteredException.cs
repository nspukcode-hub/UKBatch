namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Thrown by <see cref="IUKBatchClientFactory.GetClient"/> when the requested service name is
/// not in <see cref="Configuration.IUKBatchServiceRegistry"/>.
/// </summary>
public sealed class UKBatchServiceNotRegisteredException : InvalidOperationException
{
    /// <summary>Constructs with the unregistered service name.</summary>
    public UKBatchServiceNotRegisteredException(string serviceName)
        : base($"UKBatch service '{serviceName}' is not registered. Configure it under UKBatch:Dashboard:Services in appsettings.json.")
    {
        ServiceName = serviceName;
    }

    /// <summary>Service name that was not found in the registry.</summary>
    public string ServiceName { get; }
}
