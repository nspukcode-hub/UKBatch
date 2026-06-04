using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Serializes the Docker-bound integration classes into a single xUnit collection so they do not run in
/// parallel (each class still owns its own broker container via <see cref="RabbitMqContainerFixture"/>,
/// but running many heavy containers concurrently strains CI Docker hosts). Per-test isolation inside a
/// class is by unique topology prefix.
/// </summary>
[CollectionDefinition("RabbitMQ integration", DisableParallelization = true)]
#pragma warning disable CA1711 // "Collection" suffix is the idiomatic xUnit collection-definition naming.
public sealed class RabbitMqIntegrationCollection
#pragma warning restore CA1711
{
}
