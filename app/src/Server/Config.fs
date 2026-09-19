module Server.Config

open System

type Config =
    { /// Address Kestrel binds to. Loopback for local dev; 0.0.0.0 in the container.
      ListenUrl: string
      ConnectionString: string
      PowerSyncUrl: string
      JwtSecret: string
      JwtAudience: string
      /// Single fixed user until real auth exists.
      UserId: string }

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

    { ListenUrl = env "SERVER_URL" "http://localhost:5050"
      ConnectionString = $"Host={pgHost};Port={pgPort};Username={pgUser};Password={pgPassword};Database={pgDb}"
      PowerSyncUrl = env "POWERSYNC_URL" $"http://localhost:{psPort}"
      JwtSecret = env "POWERSYNC_JWT_SECRET" "plaintextpantry-local-dev-jwt-secret-change-me-before-anyone-cares"
      JwtAudience = "powersync-dev"
      UserId = "local-dev-user" }
