# Plaintext Pantry

A deliberately tiny, unstyled, local-first recipe app. Full-stack F#,
[PowerSync](https://www.powersync.com) for sync, no hand-written JavaScript.

## Run it

Requires Docker, .NET 10 SDK and Node.

```
./dev.sh
```

Then open <http://localhost:5173>. `./dev.sh down` stops the containers,
`./dev.sh reset` also deletes the Postgres volume.

## Layout

```
infra/docker/          docker compose: Postgres (logical replication) + PowerSync service
  postgres/init/       creates the schema, the storage DB and the `powersync` publication
  powersync/           service.yaml (HS256 dev auth) and sync-config.yaml (sync streams)
app/src/Shared/        types + Thoth.Json coders shared by client and server (the wire format)
app/src/Server/        Giraffe: issues PowerSync JWTs, applies CRUD uploads to Postgres
app/src/Client/        Fable + Feliz + Elmish; PowerSync.fs holds the @powersync/web bindings
```

## How data flows

1. The client writes to its local SQLite (via `@powersync/web`) - the UI is
   driven by a `watch` query, so it updates immediately, online or not.
2. PowerSync queues each write; the connector in `Db.fs` POSTs the batch to the
   F# server (`/api/sync/upload`) using the coders from `Shared.fs`.
3. The server applies it to Postgres inside one transaction.
4. The PowerSync service replicates the change back down to every client.

The status line at the top shows the live `SyncStatus`, including upload and
download errors - the SDK only logs those at debug level, so the UI is where
you'll see a failing upload.

## Why not Fable.Remoting?

Its serializer (Fable.SimpleJson) mis-classifies `list`/`option` under Fable 5
and throws before sending (Fable.SimpleJson#111, Fable.Remoting#394). Explicit
Thoth.Json coders in `Shared.fs` avoid reflection entirely.
