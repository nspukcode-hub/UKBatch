---
title: Persistent storage (EF Core)
description: Persist batch definitions, execution history, and approvals to PostgreSQL or SQLite.
---

By default all state is in memory and resets on restart. Add
`UKBatch.Storage.EntityFrameworkCore` to persist batch definitions, execution history, and
approval records to **PostgreSQL** or **SQLite**.

```csharp
services.AddUKBatch(b => b.AddJob<HelloJob>())
        .AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite("Data Source=ukbatch.db"));
// or: o.UsePostgres("Host=localhost;Database=ukbatch;Username=ukbatch;Password=…")
```

Register the EF stores **after** `AddUKBatch` / `AddUKBatchApi` — they replace the in-memory
registrations. The package ships design-time migrations for both providers; set
`o.MigrateOnStartup = true` for dev, or run `dotnet ef database update` in production.

:::caution[Durability boundary]
A restart preserves history, definitions, and pending approval *records*, but it does
**not** resume a paused workflow — in-flight executions are reaped to `Failed`. See the
[UKBatch.Storage.EntityFrameworkCore package page](/UKBatch/packages/storage-efcore/) for
the full contract.
:::

`samples/Sample.RestApi` takes a `--storage inmemory|ef-sqlite|ef-pg` flag so you can watch
the same app run on each store. The repo's `smoke-restart-sqlite.sh` triggers a batch,
pauses it at an approval gate, `kill -9`s the process, restarts over the same `.db` file,
and shows the pending approval + execution history still there.
