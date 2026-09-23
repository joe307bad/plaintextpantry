/// Who is asking. Two ways in, both issued by Keycloak:
///
///   - The browser: OIDC authorization-code login (ASP.NET Core's handler does
///     PKCE, state/nonce, the token exchange and id_token validation) ending
///     in an httpOnly cookie that outlives the browser session. The login also
///     asks for an offline refresh token, which rides along inside that cookie
///     and is spent against Keycloak once a day; so a session ends when it is
///     revoked, not when something expires. See "Staying signed in" below.
///   - MCP clients: a bearer access token minted by Keycloak for the
///     `McpResource` audience. Verified against the realm's published keys;
///     a web-session token is refused here because it lacks that audience.
///
/// Keycloak's `sub` is the user id everywhere: the session, the PowerSync
/// JWT, and every row's `user_id`.
module Server.Auth

open System
open System.Collections.Generic
open System.Net.Http
open System.Security.Claims
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.IdentityModel.Tokens
open ModelContextProtocol.AspNetCore.Authentication
open ModelContextProtocol.Authentication
open Giraffe
open Thoth.Json.System.Text.Json
open Shared
open Server.Config

let private cookieScheme = CookieAuthenticationDefaults.AuthenticationScheme
let private oidcScheme = OpenIdConnectDefaults.AuthenticationScheme
/// The bearer scheme MCP requests authenticate with; also the policy name.
let mcpScheme = "McpBearer"

/// What an MCP client may ask for. Must be client scopes that exist on the
/// realm (infra/keycloak/provision.sh creates exactly these).
let mcpScopes =
    [ "mcp"; "recipes:read"; "recipes:write"; "shopping:read"; "shopping:write"; "menus:read"; "menus:write" ]

// ---------------------------------------------------------------------------
// Staying signed in
// ---------------------------------------------------------------------------
//
// The cookie is persistent (`IsPersistent`): without that ASP.NET writes it
// with no `Expires`, making it a browser-session cookie that an installed PWA
// loses every time the OS reaps the app - which is what "logged out again"
// actually was. Persistent plus a long window plus sliding expiry keeps anyone
// who opens the app signed in indefinitely.
//
// The other half is Keycloak's. The login asks for `offline_access`, so the
// refresh token it hands back is an offline token: it survives Keycloak's own
// SSO session, restarts and deploys. We keep it in the cookie and spend it
// once a day. A success re-issues the cookie (fresh expiry, fresh tokens); a
// refusal from Keycloak - the account was disabled, deleted, or signed out
// everywhere - is the one thing that ends the session.

let private http = new HttpClient()

/// Asked for at login: makes Keycloak's refresh token an offline one.
let private offlineScope = "offline_access"

/// How long a cookie may go without being checked against Keycloak. Long
/// enough that an app in daily use hardly ever calls the token endpoint,
/// short enough that a revoked account is out by tomorrow.
let private renewEvery = TimeSpan.FromHours 24.

/// Keycloak was unreachable (restarting mid-deploy, a network blip). Nobody
/// gets signed out for that; we just try again soon.
let private retryIn = TimeSpan.FromHours 1.

/// Property holding when the refresh token is next due to be spent.
let private renewAtKey = "ptp:renew_at"

let private renewAt (span: TimeSpan) = DateTimeOffset.UtcNow.Add(span).ToString "o"

let private renewalDue (props: AuthenticationProperties) =
    match props.GetString renewAtKey with
    | null -> true // a cookie minted before any of this existed
    | at ->
        match DateTimeOffset.TryParse(at, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
        | true, at -> at <= DateTimeOffset.UtcNow
        | _ -> true

/// Replaces the tokens carried by the cookie, skipping the ones we don't have.
let private storeTokens (props: AuthenticationProperties) (idToken: string option) (refreshToken: string option) =
    props.StoreTokens
        [ for name, value in [ "id_token", idToken; "refresh_token", refreshToken ] do
              match value with
              | Some v when not (String.IsNullOrEmpty v) -> AuthenticationToken(Name = name, Value = v)
              | _ -> () ]

type private Renewal =
    | Renewed of refreshToken: string * idToken: string option
    /// Keycloak answered, and the answer was no. The session is over.
    | Revoked of detail: string
    /// No answer worth acting on. Keep the session.
    | Unavailable of detail: string

let private jsonField (body: string) (name: string) =
    try
        match JsonDocument.Parse(body).RootElement.TryGetProperty name with
        | true, v -> Some(v.GetString())
        | _ -> None
    with _ ->
        None

let private form (pairs: (string * string) list) =
    new FormUrlEncodedContent(pairs |> List.map KeyValuePair)

let private tokenEndpoint (config: Config) =
    $"{config.KeycloakMetadataUrl}/protocol/openid-connect/token"

/// Trades the refresh token for a fresh one (Keycloak rotates on every use).
let private renew (config: Config) (refreshToken: string) =
    task {
        try
            use! resp =
                http.PostAsync(
                    tokenEndpoint config,
                    form
                        [ "grant_type", "refresh_token"
                          "client_id", config.KeycloakClientId
                          "client_secret", config.KeycloakClientSecret
                          "refresh_token", refreshToken ]
                )

            let! body = resp.Content.ReadAsStringAsync()

            if resp.IsSuccessStatusCode then
                match jsonField body "refresh_token" with
                | Some token -> return Renewed(token, jsonField body "id_token")
                | None -> return Unavailable "no refresh_token in the response"
            else
                // Only Keycloak's own "this grant is dead" verdict ends a
                // session. A wrong client secret or a half-started Keycloak
                // must never sign the whole userbase out.
                match jsonField body "error" with
                | Some "invalid_grant" -> return Revoked body
                | _ -> return Unavailable $"{int resp.StatusCode}: {body}"
        with e ->
            return Unavailable e.Message
    }

/// Runs on every request that carries the cookie.
let private validateSession (config: Config) (ctx: CookieValidatePrincipalContext) =
    task {
        match ctx.Properties.GetTokenValue "refresh_token" with
        // Dev auto-login without one, or a cookie from before offline tokens:
        // nothing to check against, and the cookie's own expiry still applies.
        | null -> ()
        | refreshToken when renewalDue ctx.Properties ->
            let log =
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger "Server.Auth"

            match! renew config refreshToken with
            | Renewed(refreshToken, idToken) ->
                let idToken = idToken |> Option.orElse (Option.ofObj (ctx.Properties.GetTokenValue "id_token"))
                storeTokens ctx.Properties idToken (Some refreshToken)
                ctx.Properties.SetString(renewAtKey, renewAt renewEvery)
                ctx.ShouldRenew <- true
            | Unavailable detail ->
                log.LogWarning("Session renewal deferred: {Detail}", detail)
                ctx.Properties.SetString(renewAtKey, renewAt retryIn)
                ctx.ShouldRenew <- true
            | Revoked detail ->
                log.LogInformation("Session revoked by Keycloak: {Detail}", detail)
                ctx.RejectPrincipal()
                do! ctx.HttpContext.SignOutAsync cookieScheme
        | _ -> ()
    }

let configure (services: IServiceCollection) (config: Config) =
    let metadata = $"{config.KeycloakMetadataUrl}/.well-known/openid-configuration"

    // The cookie is encrypted with the Data Protection key ring. By default
    // that lives inside the container and is re-created on every deploy,
    // which invalidates every session at once. Prod keeps it on the data
    // volume instead (unencrypted at rest, like the database next to it).
    match config.DataProtectionKeysDir with
    | Some dir ->
        services
            .AddDataProtection()
            .SetApplicationName("plaintextpantry")
            .PersistKeysToFileSystem(IO.DirectoryInfo dir)
        |> ignore
    | None -> ()

    services
        .AddAuthentication(fun o ->
            o.DefaultScheme <- cookieScheme
            o.DefaultChallengeScheme <- oidcScheme)
        .AddCookie(fun o ->
            o.Cookie.Name <- "ptp_session"
            o.Cookie.HttpOnly <- true
            o.Cookie.SameSite <- SameSiteMode.Lax
            o.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
            // 400 days is as far ahead as browsers will honour an expiry;
            // sliding expiration and the daily renewal below push it along.
            o.ExpireTimeSpan <- TimeSpan.FromDays 400.
            o.SlidingExpiration <- true
            o.Events.OnValidatePrincipal <- fun ctx -> validateSession config ctx :> Task
            // Fetches from the app get a status code, never a redirect to Keycloak.
            o.Events.OnRedirectToLogin <- fun ctx -> (ctx.Response.StatusCode <- 401; Task.CompletedTask)
            o.Events.OnRedirectToAccessDenied <- fun ctx -> (ctx.Response.StatusCode <- 403; Task.CompletedTask))
        .AddOpenIdConnect(fun o ->
            o.Authority <- config.KeycloakIssuer
            o.MetadataAddress <- metadata
            o.RequireHttpsMetadata <- not config.AllowInsecureKeycloak
            o.ClientId <- config.KeycloakClientId
            o.ClientSecret <- config.KeycloakClientSecret
            o.ResponseType <- "code"
            o.UsePkce <- true
            // The access token is never used (this server talks to its own
            // database, and mints PowerSync's JWT itself), so instead of
            // SaveTokens' four entries the cookie keeps two: the id_token,
            // which logout passes as id_token_hint so Keycloak ends its
            // session without asking, and the offline refresh token that
            // validateSession spends.
            o.SaveTokens <- false
            // Otherwise the cookie would inherit the id_token's lifetime -
            // minutes - instead of ExpireTimeSpan. It is the default; say it
            // out loud, because the failure mode is "logged out constantly".
            o.UseTokenLifetime <- false

            o.Events.OnTokenValidated <-
                fun ctx ->
                    storeTokens
                        ctx.Properties
                        (Option.ofObj ctx.TokenEndpointResponse.IdToken)
                        (Option.ofObj ctx.TokenEndpointResponse.RefreshToken)

                    ctx.Properties.SetString(renewAtKey, renewAt renewEvery)
                    Task.CompletedTask

            // Skip Keycloak's own login page: go straight to the Google
            // identity provider (the realm's browser flow is also configured
            // this way by provision.sh; this makes it explicit per request).
            o.Events.OnRedirectToIdentityProvider <-
                fun ctx ->
                    ctx.ProtocolMessage.SetParameter("kc_idp_hint", "google")
                    Task.CompletedTask

            o.Scope.Add "email"
            // The refresh token that comes back is then an offline one: it
            // does not die with Keycloak's SSO session, so a session can be
            // renewed months later without the user seeing a login page.
            o.Scope.Add offlineScope
            o.CallbackPath <- PathString "/api/auth/callback"
            o.SignedOutCallbackPath <- PathString "/api/auth/signout-callback"
            // Keep Keycloak's claim names (sub, email, name) instead of the
            // legacy SOAP-era URIs the handler maps to by default.
            o.MapInboundClaims <- false
            o.TokenValidationParameters.NameClaimType <- "name"
            o.TokenValidationParameters.ValidIssuer <- config.KeycloakIssuer)
        .AddJwtBearer(mcpScheme, fun o ->
            o.Authority <- config.KeycloakIssuer
            o.MetadataAddress <- metadata
            o.RequireHttpsMetadata <- not config.AllowInsecureKeycloak
            o.MapInboundClaims <- false
            o.TokenValidationParameters <-
                TokenValidationParameters(
                    ValidIssuer = config.KeycloakIssuer,
                    ValidAudience = config.McpResource,
                    NameClaimType = "email"
                )
            // A 401 from here goes out through the MCP handler below, which
            // adds the `resource_metadata` pointer that starts an MCP client's
            // OAuth flow.
            o.ForwardChallenge <- McpAuthenticationDefaults.AuthenticationScheme)
        // Serves /.well-known/oauth-protected-resource/mcp (RFC 9728) and
        // writes the WWW-Authenticate challenge.
        .AddMcp(fun o ->
            let rm = ProtectedResourceMetadata(Resource = config.McpResource)
            rm.AuthorizationServers.Add config.KeycloakIssuer
            mcpScopes |> List.iter rm.ScopesSupported.Add
            rm.ResourceName <- "Plaintext Pantry"
            o.ResourceMetadata <- rm)
    |> ignore

    services.AddAuthorization(fun o ->
        o.AddPolicy(
            mcpScheme,
            fun p -> p.AddAuthenticationSchemes(mcpScheme).RequireAuthenticatedUser() |> ignore
        ))
    |> ignore

// ---------------------------------------------------------------------------
// Reading the principal
// ---------------------------------------------------------------------------

let private claim (user: ClaimsPrincipal) name =
    match user.FindFirst(name: string) with
    | null -> None
    | c -> Some c.Value

let currentUser (ctx: HttpContext) : User option =
    if ctx.User.Identity <> null && ctx.User.Identity.IsAuthenticated then
        claim ctx.User "sub"
        |> Option.map (fun id ->
            let email = claim ctx.User "email" |> Option.defaultValue ""

            { Id = id
              Email = email
              Name =
                claim ctx.User "name"
                |> Option.orElse (claim ctx.User "preferred_username")
                |> Option.defaultValue email })
    else
        None

/// Scopes granted to the current (MCP) token, from Keycloak's space-separated `scope` claim.
let scopes (ctx: HttpContext) =
    claim ctx.User "scope"
    |> Option.map (fun s -> s.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray)
    |> Option.defaultValue Set.empty

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private json (value: Thoth.Json.Core.IEncodable) : HttpHandler =
    fun _ ctx ->
        ctx.SetContentType "application/json; charset=utf-8"
        ctx.WriteStringAsync(Encode.toString 0 value)

/// Gate for API routes: 401 JSON when there is no session.
let requireUser: HttpHandler =
    fun next ctx ->
        match currentUser ctx with
        | Some _ -> next ctx
        | None -> (setStatusCode 401 >=> json (Thoth.Json.Core.Encode.object [ "error", Thoth.Json.Core.Encode.string "Not signed in" ])) next ctx

/// Local dev: sign the configured Keycloak account in without a trip
/// through Keycloak's pages. Password grant (direct access, enabled on the
/// client only when the realm was provisioned in dev mode), then /userinfo
/// for the claims, then the same cookie a real login would set.
let private devAutoLogin (config: Config) (user: string) (password: string) (ctx: HttpContext) =
    task {
        use! tokenResp =
            http.PostAsync(
                tokenEndpoint config,
                form
                    [ "grant_type", "password"
                      "client_id", config.KeycloakClientId
                      "client_secret", config.KeycloakClientSecret
                      "username", user
                      "password", password
                      // offline_access here too, so dev exercises the same
                      // renewal path production runs on.
                      "scope", $"openid email profile {offlineScope}" ]
            )

        let! tokenBody = tokenResp.Content.ReadAsStringAsync()

        if not tokenResp.IsSuccessStatusCode then
            failwithf "dev auto-login: token endpoint %d: %s" (int tokenResp.StatusCode) tokenBody

        let accessToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()

        use req = new HttpRequestMessage(HttpMethod.Get, $"{config.KeycloakMetadataUrl}/protocol/openid-connect/userinfo")
        req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", accessToken)
        use! infoResp = http.SendAsync req
        let! infoBody = infoResp.Content.ReadAsStringAsync()
        let info = JsonDocument.Parse(infoBody).RootElement

        let claim name =
            match info.TryGetProperty(name: string) with
            | true, v -> [ Claim(name, v.GetString()) ]
            | _ -> []

        let identity = ClaimsIdentity(claim "sub" @ claim "email" @ claim "name" @ claim "preferred_username", cookieScheme)
        let props = AuthenticationProperties(IsPersistent = true)
        storeTokens props (jsonField tokenBody "id_token") (jsonField tokenBody "refresh_token")
        props.SetString(renewAtKey, renewAt renewEvery)

        do! ctx.SignInAsync(cookieScheme, ClaimsPrincipal identity, props)
        ctx.User <- ClaimsPrincipal identity
    }

/// The signed-in user, or 401.
let me: HttpHandler =
    fun next ctx ->
        match currentUser ctx with
        | Some user -> json (Codec.encodeUser user) next ctx
        | None -> requireUser next ctx

/// Only same-site paths are honoured as a post-login destination.
let private safeReturnTo (ctx: HttpContext) =
    match ctx.TryGetQueryStringValue "returnTo" with
    | Some p when p.StartsWith "/" && not (p.StartsWith "//") -> p
    | _ -> "/"

/// Browser navigation: sends the user through Keycloak (and, from there,
/// straight to Google) and back to `returnTo`. In local dev, signs the dev
/// user in on the spot instead.
let login (config: Config) : HttpHandler =
    fun _ ctx ->
        task {
            match config.DevAutoLogin with
            | Some(user, password) ->
                do! devAutoLogin config user password ctx
                ctx.Response.Redirect(safeReturnTo ctx)
            | None ->
                // IsPersistent is what puts an Expires on the cookie. Without
                // it the session dies with the browser - or, on a phone, with
                // the installed app.
                do! ctx.ChallengeAsync(oidcScheme, AuthenticationProperties(IsPersistent = true, RedirectUri = safeReturnTo ctx))

            return Some ctx
        }

/// Browser navigation: clears the cookie, ends the Keycloak session, lands on the app.
let logout (config: Config) : HttpHandler =
    fun _ ctx ->
        task {
            do! ctx.SignOutAsync cookieScheme
            do! ctx.SignOutAsync(oidcScheme, AuthenticationProperties(RedirectUri = config.AppUrl + "/"))
            return Some ctx
        }
