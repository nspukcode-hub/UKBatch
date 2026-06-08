---
title: Add the dashboard
description: Mount the Blazor Server dashboard over your UKBatch runtime in embedded mode.
---

The Blazor Server dashboard is a pure consumer of `UKBatch.Api` over HTTP/SignalR. In
embedded mode you point it at your own loopback `/api`.

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b => b.AddJob<HelloJob>());
builder.Services.AddUKBatchApi();

builder.Services.AddUKBatchDashboard(opts =>
{
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        BaseUrl = new Uri("http://localhost:5050/api/"),
        DisplayName = "Local",
    });
});
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseAntiforgery();                  // REQUIRED for Razor Components — see Gotchas
app.MapGroup("/api").MapUKBatchApi();  // REST + SignalR hub at /api/hubs/jobs
app.MapUKBatchDashboard();             // UI at /dashboard
app.MapStaticAssets();                 // serves Blazor framework assets (.NET 9+; on .NET 8 use app.UseStaticFiles())

app.Run();
```

Open `http://localhost:5050/dashboard`. The Jobs, Batches, Executions, and Approvals pages
appear immediately.

:::caution[Three things that bite first-timers]
- `app.UseAntiforgery()` is **required** when mapping the dashboard, or `/dashboard` returns
  HTTP 500.
- On **.NET 10**, the host csproj needs `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>`
  or the UI renders as static HTML with dead buttons.
- A service `BaseUrl` is auto-normalized to a trailing slash, so `…/api` and `…/api/` behave
  identically.

All three are explained in [Gotchas](/UKBatch/concepts/gotchas/).
:::

:::tip[Runnable sample]
[`samples/Sample.Dashboard`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.Dashboard).
:::

For multi-service configuration (one central dashboard fanned out across many services),
see the [UKBatch.Dashboard package page](/UKBatch/packages/dashboard/).
