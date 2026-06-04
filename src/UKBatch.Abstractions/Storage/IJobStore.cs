namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Pluggable persistent store for job execution history. A composition of
/// <see cref="IJobExecutionReader"/> and <see cref="IJobExecutionWriter"/> — adapter implementations
/// typically implement this aggregate interface and consumers narrow to the side they need.
/// </summary>
public interface IJobStore : IJobExecutionReader, IJobExecutionWriter
{
}
