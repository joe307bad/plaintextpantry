/// Who is asking. Two ways in, both issued by Keycloak:
///
///   - The browser: OIDC authorization-code login (ASP.NET Core's handler does
///     PKCE, state/nonce, the token exchange and id_token validation) ending
///     in an httpOnly session cookie. Keycloak is only in the login path.
///   - MCP clients: a bearer access token minted by Keycloak for the
///     `McpResource` audience. Verified against the realm's published keys;
///     a web-session token is refused here because it lacks that audience.
///
/// Keycloak's `sub` is the user id everywhere: the session, the PowerSync
/// JWT, and every row's `user_id`.
module Server.Auth

open System
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
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
let mcpScopes = [ "mcp"; "recipes:read"; "recipes:write"; "shopping:read"; "shopping:write" ]

let configure (services: IServiceCollection) (config: Config) =
    let metadata = $"{config.KeycloakMetadataUrl}/.well-known/openid-configuration"

    services
        .AddAuthentication(fun o ->
            o.DefaultScheme <- cookieScheme
            o.DefaultChallengeScheme <- oidcScheme)
        .AddCookie(fun o ->
            o.Cookie.Name <- "ptp_session"
            o.Cookie.HttpOnly <- true
            o.Cookie.SameSite <- SameSiteMode.Lax
            o.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
            o.ExpireTimeSpan <- TimeSpan.FromDays 30.
            o.SlidingExpiration <- true
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
            // Keep only the id_token in the session (not the access/refresh
            // tokens, which would triple the cookie's size): logout passes it
            // as id_token_hint so Keycloak ends the session without asking.
            o.SaveTokens <- false

            o.Events.OnTokenValidated <-
                fun ctx ->
                    ctx.Properties.StoreTokens [ AuthenticationToken(Name = "id_token", Value = ctx.TokenEndpointResponse.IdToken) ]
                    Task.CompletedTask

            o.Scope.Add "email"
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

/// Browser navigation: sends the user through Keycloak and back to `returnTo`.
let login: HttpHandler =
    fun _ ctx ->
        task {
            do! ctx.ChallengeAsync(oidcScheme, AuthenticationProperties(RedirectUri = safeReturnTo ctx))
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
