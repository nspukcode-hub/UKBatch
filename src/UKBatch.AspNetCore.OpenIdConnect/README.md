# UKBatch.AspNetCore.OpenIdConnect

OpenID Connect login and role-gating for the UKBatch dashboard and REST API. It is a thin, opt-in layer over Microsoft's first-party `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `.JwtBearer`, and `.Cookies` handlers — it works with any standards-compliant OpenID Connect identity provider (Keycloak, Azure AD, Auth0, IdentityServer, …) via the provider's `Authority` URL. There is no provider-specific code.

## What it adds

- **Dashboard login** — an interactive OpenID Connect code flow with a cookie session and sign-out.
- **Viewer / operator role-gating** — read endpoints require an authenticated viewer; write endpoints require a configurable operator role. Approval gates keep their own per-gate allowed-roles check.
- **Per-user token forwarding** — the signed-in user's access token flows from the dashboard to the API, so the API sees the real user and roles and records approvals under the real person.
- **Nested-role flattening** — identity providers that emit roles nested inside a claim (for example Keycloak's `realm_access.roles` / `resource_access.*.roles`) are flattened to standard role claims automatically.

## Usage

```csharp
builder.Services.AddUKBatchOpenIdConnect(o =>
{
    o.Authority = "https://keycloak.example.com/realms/ukbatch";
    o.ClientId = "ukbatch-dashboard";
    o.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
    o.Audience = "ukbatch-api";
    o.OperatorRoles = ["batch-operator"];
});

// API: gate reads to viewers, writes to operators.
app.MapGroup("/api").MapUKBatchApi().RequireUKBatchRoleAuthorization();

// Dashboard: require a signed-in session and forward the user's token.
app.MapUKBatchDashboard().RequireAuthorization();
app.MapUKBatchSignOut();
```

See the repository documentation for the full options and a runnable Keycloak sample.
