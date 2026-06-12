# UKBatch.Dashboard

A Blazor Server dashboard for [UKBatch](https://github.com/nspukcode-hub/UKBatch) — a lightweight, pluggable batch/job orchestration library for .NET 8 and .NET 10. It surfaces monitoring, triggering, approvals, a live DAG view, and a visual drag-and-drop batch editor across one or many services. The dashboard is purely a consumer of `UKBatch.Api` over HTTP/SignalR — it never touches the runtime directly, so the same package serves both embedded and server + workers deployments.

> **Status:** part of the UKBatch 0.1.0-alpha package family.

## Install

```bash
dotnet add package UKBatch.Dashboard
```

The package transitively brings `UKBatch.Api`, `UKBatch.AspNetCore`, `UKBatch.Core`, and `UKBatch.Abstractions`. For an embedded deployment that is the complete dependency set; for a central dashboard over a server + workers deployment, you only need this package on the dashboard host.

## Quick start (embedded)

```csharp
using UKBatch.Api;
using UKBatch.AspNetCore;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddUKBatchAspNetCore(b => b.AddJob<MyJob>());
builder.Services.AddUKBatchApi();
builder.Services.AddAntiforgery();

builder.Services.AddUKBatchDashboard(opts =>
{
    opts.Services.Add(new UKBatchServiceDescriptor
    {
        Name = "self",
        BaseUrl = new Uri("http://localhost:5050/api/"),   // trailing slash REQUIRED — see below
        DisplayName = "Local",
    });
});

var app = builder.Build();

app.UseAntiforgery();                   // REQUIRED — see below
app.MapGroup("/api").MapUKBatchApi();   // REST + SignalR hub
app.MapUKBatchDashboard();              // UI at /dashboard
app.MapStaticAssets();                  // .NET 9+; on .NET 8 call app.UseStaticFiles() instead

app.Run();
```

Open `http://localhost:5050/dashboard`. A working sample is under [`samples/Sample.Dashboard`](https://github.com/nspukcode-hub/UKBatch/tree/main/samples/Sample.Dashboard).

## Critical gotchas

- **`app.UseAntiforgery()` is required.** Razor Components emit anti-forgery metadata; without the middleware in the pipeline, `/dashboard` returns HTTP 500 ("endpoint contains anti-forgery metadata"). Place it after `UseAuthorization()`.
- **A service `BaseUrl` is auto-normalized to a trailing slash** — `http://localhost:5050/api` and `.../api/` behave identically. (A missing slash would otherwise make `HttpClient.BaseAddress` drop the last path segment per RFC 3986, resolving `jobs` to `/jobs` and 404ing.)
- **Every .NET 10 host that runs the dashboard needs an MSBuild flag.** Add `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` to your host csproj — for BOTH ProjectReference and NuGet PackageReference. NuGet cannot set it for you: the .NET Web SDK reads it during restore, before a package's build assets are imported. The package raises build warning `UKBATCH001` on .NET 10 when the flag is missing, instead of failing silently. Without it the Web SDK omits `_framework/blazor.web.js`, the dashboard renders as static HTML, and buttons silently do nothing. (.NET 8 hosts do not need it — `MapRazorComponents` serves the framework files.)
- **`MapStaticAssets()` is required on .NET 9/10** (the static-asset manifest endpoint) to serve the Blazor framework files; `UseStaticFiles()` alone does not.
- **.NET 8 hosts use `UseStaticFiles()` instead** (`MapStaticAssets` does not exist there; `_framework/blazor.web.js` is served by `MapRazorComponents` itself on .NET 8). One standard .NET 8 behavior to know: the dashboard's own CSS/JS (`_content/UKBatch.Dashboard/...`) flows through the static-web-assets manifest, which loads automatically in the **Development** environment and is physically copied to `wwwroot/` by **`dotnet publish`** — both verified working with the .NET 8 SDK. The one gap is running a Release build straight from `dotnet run` without publishing; call `builder.WebHost.UseStaticWebAssets()` explicitly if you need that.

## Multi-service configuration

The dashboard discovers services via `UKBatch:Dashboard:Services[]` in `appsettings.json` and merges any in-code entries from the `configure` callback. Add one descriptor per microservice for a central dashboard:

```jsonc
{
  "UKBatch": {
    "Dashboard": {
      "Services": [
        { "Name": "self",   "BaseUrl": "http://localhost:5050/api/", "DisplayName": "Local" },
        { "Name": "orders", "BaseUrl": "http://orders.internal:8080/api/",
          "DisplayName": "Orders Service", "Tags": [ "prod", "eu-west" ] }
      ]
    }
  }
}
```

`Name` must match `^[a-z][a-z0-9-]*$` (kebab-case) — it is the `/dashboard/{name}/...` path segment. The sidebar groups services by `Tags`. Duplicate names fail at host startup. Architecturally a central dashboard is identical to embedded mode — only each descriptor's `BaseUrl` differs.

## Authorization

Auth-off by default. Lock it down on the map return value:

```csharp
app.MapUKBatchDashboard().RequireAuthorization();
```

You choose the scheme (Cookie / OIDC / JWT) — the dashboard does not call `AddAuthentication` for you.

**Production caveat (role claims):** approval gates match user roles against `UKBatchOptions.ApprovalRoleClaimTypes`, which defaults to `[ClaimTypes.Role]`. Azure AD / Auth0 / SAML emit roles under different claim types (e.g. `roles`, a custom Auth0 URL), so configure the right claim type(s) under `UKBatch:ApprovalRoleClaimTypes` in `appsettings.json` — otherwise approve/reject returns HTTP 403 even when the role is present.

## When to use it

Add this package when you want a ready-made UI over your UKBatch runtime — embedded in your app, or as a single central dashboard fanned out across many microservices. Cancel, approve/reject, and batch create/edit/delete all flow through `UKBatch.Api`; pair it with `UKBatch.Storage.EntityFrameworkCore` so saved definitions and history survive restarts.

## License

MIT. See [LICENSE](https://github.com/nspukcode-hub/UKBatch/blob/main/LICENSE) in the repo root. Full docs: [nspukcode-hub.github.io/UKBatch](https://nspukcode-hub.github.io/UKBatch/) · [GitHub](https://github.com/nspukcode-hub/UKBatch).
