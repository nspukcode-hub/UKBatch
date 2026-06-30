namespace Sample.Dashboard.Jobs;

/// <summary>
/// A small order record used by the order-pipeline demo to show that a whole object (not just a scalar)
/// is forwarded from one step to the next. Must be JSON-serializable.
/// </summary>
public sealed record class DemoOrder
{
    public required int Id { get; init; }
    public required string Customer { get; init; }
    public required decimal Total { get; init; }
}
