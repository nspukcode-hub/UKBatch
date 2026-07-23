namespace UKBatch.AspNetCore.OpenIdConnect;

/// <summary>
/// Options for <c>AddUKBatchOpenIdConnect</c>. Bound from configuration and/or a <c>configure</c>
/// callback; validated at host start. Property types follow ConfigurationBinder constraints
/// (<see cref="List{T}"/> so appsettings binding populates them).
/// </summary>
/// <remarks>
/// The identity provider is reached purely by its <see cref="Authority"/> URL — Keycloak, Azure AD,
/// Auth0, and IdentityServer are all standard OpenID Connect providers. There is no provider-specific
/// setting here.
/// </remarks>
public sealed class UKBatchOpenIdConnectOptions
{
    /// <summary>
    /// The OpenID Connect authority (issuer) URL. For Keycloak this is
    /// <c>https://&lt;host&gt;/realms/&lt;realm&gt;</c>. Required. The handler discovers the endpoints
    /// from <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The client id registered with the identity provider for the dashboard. Required.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret for a confidential client. Leave null for a public client.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The audience the API validates bearer tokens against. When null, audience validation is
    /// disabled and ANY token the authority issued — including one minted for a different
    /// application in the same realm — is accepted on issuer and signature alone. Set this in
    /// production: register an audience with the provider (for Keycloak, an audience mapper that
    /// puts the value into the token; its default audience is <c>account</c>) and put the same
    /// value here, so only tokens minted for this API pass.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// The scopes requested at login. <c>openid</c> and <c>profile</c> are included by default; add
    /// <c>offline_access</c> to receive a refresh token, and any API scope the access token needs.
    /// </summary>
    public List<string> Scope { get; set; } = ["openid", "profile"];

    /// <summary>
    /// The role name(s) that grant write access ("operator"). At least one is required when role
    /// gating is applied. These are matched against the user's role claims (after nested-role
    /// flattening); the operator maps their own provider role name(s) here.
    /// </summary>
    public List<string> OperatorRoles { get; set; } = [];

    /// <summary>
    /// The role name(s) that grant read access ("viewer"). When empty, any authenticated user is a
    /// viewer. Operators are always viewers.
    /// </summary>
    public List<string> ViewerRoles { get; set; } = [];

    /// <summary>
    /// Claim paths whose nested JSON role arrays are flattened into standard role claims. Defaults to
    /// Keycloak's shapes: <c>realm_access.roles</c> and <c>resource_access.*.roles</c> (the <c>*</c>
    /// matches every client). Set to an empty list to disable flattening.
    /// </summary>
    /// <remarks>
    /// The <c>*</c> wildcard merges every client's roles into one namespace: a role named
    /// <c>operator</c> granted for ANY client in the realm would satisfy an operator policy that
    /// names <c>operator</c>. In a realm shared by several applications, either narrow the path to
    /// this application's client (<c>resource_access.&lt;client-id&gt;.roles</c>) or keep the
    /// gating role names unique to this application across the realm.
    /// </remarks>
    public List<string> RoleClaimPaths { get; set; } = ["realm_access.roles", "resource_access.*.roles"];

    /// <summary>
    /// Whether HTTPS metadata is required. Defaults to <c>true</c>. Set to <c>false</c> only for a
    /// local development provider served over plain HTTP: besides allowing HTTP discovery, it switches
    /// the login callback to a browser-compatible plain-HTTP mode — query response mode with
    /// SameSite=Lax correlation/nonce cookies whose <c>Secure</c> flag follows the request scheme
    /// (an HTTPS request still gets a Secure cookie). The flow stays authorization code + PKCE. Leave
    /// this <c>true</c> in production, where the framework's stricter form_post + Secure defaults apply.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>The path the identity provider redirects back to after login. Default <c>/signin-oidc</c>.</summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>The path the identity provider redirects back to after sign-out. Default <c>/signout-callback-oidc</c>.</summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
}
