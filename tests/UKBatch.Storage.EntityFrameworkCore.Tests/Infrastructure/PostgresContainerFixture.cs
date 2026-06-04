using Testcontainers.PostgreSql;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL Testcontainers fixture (one container per test class via
/// <c>IClassFixture&lt;PostgresContainerFixture&gt;</c>). Test classes that consume it carry
/// <c>[Trait("Category","RequiresDocker")]</c>, so the Docker-free CI path filters them out
/// (<c>--filter Category!=RequiresDocker</c>). Each test creates its OWN database inside this
/// container for isolation; the container itself is started once and torn down after the class.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    /// <summary>Connection string to the container's default (admin) database — used to CREATE/DROP per-test databases.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
