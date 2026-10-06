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
      /// Where the session-cookie keys live. Unset (local dev) leaves them
      /// in the user profile; prod bind-mounts a directory on the data
      /// volume so a redeploy doesn't sign everyone out. See Auth.configure.
      DataProtectionKeysDir: string option
      /// A QuestDB that usage counters are written to, and the key that gets a
      /// write in. Either one empty - local dev, CI, a deployment that wants
      /// no counters - is the off switch: see Usage.fs.
      O11yEndpoint: string
      O11yApiKey: string
      /// The tag every counter row carries, so a developer clicking around is
      /// not counted as traffic. The dashboard filters on `prod`.
      O11yEnv: string
      /// Local dev only (set by dev.sh): "user:password" of a Keycloak
      /// account that the login button signs in directly, so the /login page
      /// can be worked on without ever seeing Keycloak's. Never set in prod.
      DevAutoLogin: (string * string) option }

/// The stand-ins that make `dotnet run` work with no setup. They are in a
/// public repository, so anything still holding one is not a secret - which
/// is fine on a laptop and is the whole game on a host. `check` below is what
/// stops a deployment running on them.
module private Dev =
    let jwtSecret = "plaintextpantry-local-dev-jwt-secret-change-me-before-anyone-cares"
    let clientSecret = "plaintextpantry-local-dev-client-secret"

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
      JwtSecret = env "POWERSYNC_JWT_SECRET" Dev.jwtSecret
      JwtAudience = "powersync-dev"
      AppUrl = (env "APP_URL" "http://localhost:5173").TrimEnd '/'
      KeycloakIssuer = issuer
      KeycloakMetadataUrl = metadataUrl
      KeycloakClientId = env "KEYCLOAK_CLIENT_ID" "plaintextpantry-web"
      KeycloakClientSecret = env "KEYCLOAK_CLIENT_SECRET" Dev.clientSecret
      McpResource = (env "MCP_RESOURCE" "http://localhost:5050/mcp").TrimEnd '/'
      AllowInsecureKeycloak = metadataUrl.StartsWith "http://"
      DataProtectionKeysDir =
        match env "DATA_PROTECTION_KEYS_DIR" "" with
        | "" -> None
        | dir -> Some dir
      O11yEndpoint = (env "O11Y_ENDPOINT" "").TrimEnd '/'
      O11yApiKey = env "O11Y_API_KEY" ""
      O11yEnv = env "O11Y_ENV" "dev"
      DevAutoLogin =
        match (env "DEV_AUTO_LOGIN" "").Split(':', 2) with
        | [| user; password |] -> Some(user, password)
        | _ -> None }

/// Refuses to start a deployment that is still wearing its development
/// clothes. Each of these is unremarkable on a laptop and a way in anywhere
/// else: the JWT secret mints a sync token for any user id there is, the
/// client secret is half of what Keycloak checks before handing out a
/// session, and the dev auto-login signs a visitor in as somebody without
/// asking. "Anywhere else" is read off APP_URL, which is the one setting a
/// deployment cannot avoid getting right - the browser has to reach it.
let check (config: Config) =
    let local =
        [ "http://localhost"; "https://localhost"; "http://127.0.0.1"; "https://127.0.0.1"; "http://[::1]" ]
        |> List.exists config.AppUrl.StartsWith

    let problems =
        [ if config.JwtSecret = Dev.jwtSecret then
              "POWERSYNC_JWT_SECRET is the development default (it is in the repository)"
          if config.KeycloakClientSecret = Dev.clientSecret then
              "KEYCLOAK_CLIENT_SECRET is the development default (it is in the repository)"
          if config.DevAutoLogin.IsSome then
              "DEV_AUTO_LOGIN is set, which signs every visitor in as that account" ]

    if not local && not problems.IsEmpty then
        failwithf
            "Refusing to start with APP_URL=%s:\n  - %s\nSet these from somewhere private, or run on localhost."
            config.AppUrl
            (String.Join("\n  - ", problems))
