module Server.Config

open System

type Config =
    { /// Address Kestrel binds to. Loopback for local dev; 0.0.0.0 in the container.
      ListenUrl: string
      ConnectionString: string
      PowerSyncUrl: string
      JwtSecret: string
      JwtAudience: string
      /// Where the browser reaches the app (post-logout landing page).
      AppUrl: string
      /// Keycloak realm as the browser sees it; also the `iss` every token must carry.
      KeycloakIssuer: string
      /// Where THIS process fetches discovery + JWKS from. Same as the issuer
      /// locally; in prod the internal `http://keycloak:8080/...` so the box
      /// doesn't loop out through its own public IP.
      KeycloakMetadataUrl: string
      KeycloakClientId: string
      KeycloakClientSecret: string
      /// Public URL of the MCP endpoint. Doubles as the OAuth resource
      /// identifier: Keycloak stamps it into access tokens as `aud` and the
      /// server only accepts MCP tokens that carry it.
      McpResource: string
      /// Discovery/JWKS may be fetched over plain http: local dev, or prod's
      /// internal `http://keycloak:8080`. Token issuer/audience checks are
      /// unaffected by this.
      AllowInsecureKeycloak: bool
      /// Local dev only (set by dev.sh): "user:password" of a Keycloak
      /// account that the login button signs in directly, so the /login page
      /// can be worked on without ever seeing Keycloak's. Never set in prod.
      DevAutoLogin: (string * string) option }

let private env name fallback =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> fallback
    | v -> v

/// Defaults line up with infra/docker/.env so `dotnet run` works without
/// any extra setup; dev.sh exports the same values explicitly.
let load () =
    let pgHost = env "PG_HOST" "localhost"
    let pgUser = env "PG_USER" "postgres"
    let pgPassword = env "PG_PASSWORD" "postgres"
    let pgPort = env "PG_PORT" "5432"
    let pgDb = env "PG_APP_DB" "pantry"
    let psPort = env "PS_PORT" "8080"
    let issuer = (env "KEYCLOAK_ISSUER" "http://localhost:8180/realms/plaintextpantry").TrimEnd '/'
    let metadataUrl = (env "KEYCLOAK_METADATA_URL" issuer).TrimEnd '/'

    { ListenUrl = env "SERVER_URL" "http://localhost:5050"
      ConnectionString = $"Host={pgHost};Port={pgPort};Username={pgUser};Password={pgPassword};Database={pgDb}"
      PowerSyncUrl = env "POWERSYNC_URL" $"http://localhost:{psPort}"
      JwtSecret = env "POWERSYNC_JWT_SECRET" "plaintextpantry-local-dev-jwt-secret-change-me-before-anyone-cares"
      JwtAudience = "powersync-dev"
      AppUrl = (env "APP_URL" "http://localhost:5173").TrimEnd '/'
      KeycloakIssuer = issuer
      KeycloakMetadataUrl = metadataUrl
      KeycloakClientId = env "KEYCLOAK_CLIENT_ID" "plaintextpantry-web"
      KeycloakClientSecret = env "KEYCLOAK_CLIENT_SECRET" "plaintextpantry-local-dev-client-secret"
      McpResource = (env "MCP_RESOURCE" "http://localhost:5050/mcp").TrimEnd '/'
      AllowInsecureKeycloak = metadataUrl.StartsWith "http://"
      DevAutoLogin =
        match (env "DEV_AUTO_LOGIN" "").Split(':', 2) with
        | [| user; password |] -> Some(user, password)
        | _ -> None }
