# UKBatch.Storage.EntityFrameworkCore

EF Core 10 persistent storage adapter for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a plug-in replacement for
the in-memory stores so **batch definitions**, **execution history**, and **pending approval records**
survive a host restart. Supports **PostgreSQL** (Npgsql) and **SQLite**.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Storage.EntityFrameworkCore
```

## Quick start

Register the EF stores *after* `AddUKBatch` — this replaces the default in-memory stores. **No other
code changes:** your jobs, batches, REST API, and dashboard keep working — only where the state lives
changes.

```csharp
services.AddUKBatch(b => b.AddJob<MyJob>())
        .AddUKBatchEntityFrameworkCoreStores(o => o.UsePostgres(
            "Host=localhost;Database=ukbatch;Username=ukbatch;Password=…"));

// or SQLite (a single file — great for a single-node deployment or local dev):
//     .AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite("Data Source=ukbatch.db"));
```

Provider selection is exclusive — one provider per deployment. Calling both `UsePostgres` and `UseSqlite`
is last-wins.

### Try the storage swap

`Sample.RestApi` exposes a `--storage` flag so you can watch the exact same app run on each store:

```bash
dotnet run --project samples/Sample.RestApi -- --storage inmemory      # default (volatile)
dotnet run --project samples/Sample.RestApi -- --storage ef-sqlite      # DataSource=ukbatch-sample.db
dotnet run --project samples/Sample.RestApi -- --storage ef-pg \
    --storage-connection "Host=localhost;Database=ukbatch;Username=…;Password=…"
```

`samples/Sample.RestApi/smoke-restart-sqlite.sh` triggers a batch, pauses it at an approval gate,
`kill -9`s the process, restarts over the same `.db` file, and shows the pending approval + execution
history still there.

## Migrations

The package ships design-time migrations for both providers under `Migrations/Postgres` and
`Migrations/Sqlite`. Apply them in one of two ways:

- **Production (recommended):** `dotnet ef database update -c UKBatchDbContext` (ops controls schema).
- **Dev/demo:** set `o.MigrateOnStartup = true` to apply pending migrations at boot.

`EnsureCreated()` is **not** supported (no migration history, not evolvable).

## Durability boundary

This package delivers durable **record + re-decidability + orphan cleanup**, **not** durable workflow
**resume**. After a restart: execution history persists, batch definitions are intact, and a pending
approval gate survives as a re-decidable record — but the batch it paused does **not** auto-resume, and
its in-flight executions are reaped to `Failed`. Mid-flight resume is a v0.2 concern.

The `OrphanGracePeriod` (default 2 min) is the window an interrupted, non-terminal execution is given
before the reaper marks it `Failed` — long enough to cover a normal graceful restart in flight. Set it to
`TimeSpan.Zero` to disable the reaper.

## Schema versioning

The `0.1.0-alpha` schema is the `Initial` migration for each provider. Forward-compatible columns
(`Parameters`/`Steps` JSON, `BatchDefinitionId`, `SourceService`/`TargetService`) are already in place,
so v0.2 additive changes ship as new migrations you apply with `dotnet ef database update`. Enums inside
JSON columns are stored **as names** (not ordinals), so a v0.2 reader round-trips a v0.1 blob.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [UKBatch on GitHub](https://github.com/nspukcode-hub/UKBatch).
