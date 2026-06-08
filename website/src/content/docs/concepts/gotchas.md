---
title: Gotchas
description: The handful of footguns worth knowing before you ship — antiforgery, base URLs, .NET 10 web assets, role claims, and macOS port 5000.
---

A short list of things that bite first-timers. None are bugs — they are environment and
framework realities the library surfaces loudly.

## `app.UseAntiforgery()` is required when mapping the dashboard

Razor Components emit anti-forgery metadata; without the middleware, `/dashboard` returns
HTTP 500 ("endpoint contains anti-forgery metadata"). Place `app.UseAntiforgery()` after
`UseAuthorization()`.

## A dashboard service `BaseUrl` is auto-normalized to a trailing slash

`http://localhost:5050/api` and `.../api/` behave identically. (A missing slash would
otherwise make `HttpClient.BaseAddress` drop the last path segment per RFC 3986, resolving
`jobs` to `/jobs` and 404ing; the library appends the slash for you.)

## On .NET 10, a dashboard host needs `<RequiresAspNetWebAssets>`

ANY host that runs the dashboard needs this in its own csproj — for **both** ProjectReference
and NuGet PackageReference:

```xml
<PropertyGroup>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
```

NuGet cannot supply it automatically: the .NET Web SDK reads this property during restore,
before a package's build assets are imported. The package instead raises build warning
`UKBATCH001` on .NET 10 when it is missing. Without the prop the dashboard renders as static
HTML and buttons do nothing (a silent runtime 404 for `_framework/blazor.web.js`, visible only
in browser DevTools).

.NET 8 hosts do **not** need it — `MapRazorComponents` serves the assets there. On .NET 8 use
`app.UseStaticFiles()` instead of `app.MapStaticAssets()`.

## Approval roles read `ClaimTypes.Role` by default

Azure AD / Auth0 / SAML emit other claim types — configure `UKBatch:ApprovalRoleClaimTypes`
in `appsettings.json` (it binds `UKBatchOptions.ApprovalRoleClaimTypes`), or approvals 403
even with the right role present.

## macOS port 5000 is held by AirPlay Receiver

AirPlay Receiver answers every request on port 5000 with `403`. The samples use ports 5050+;
pick a non-5000 port or disable AirPlay Receiver in System Settings.
