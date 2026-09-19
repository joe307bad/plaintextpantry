/// Hand-written Fable bindings for the parts of @powersync/web this app uses.
/// Kept deliberately small; extend as needed.
module PowerSync

open Fable.Core
open Fable.Core.JsInterop

type ColumnType = interface end

[<Import("column", "@powersync/web")>]
let column: {| text: ColumnType; integer: ColumnType; real: ColumnType |} = jsNative

[<Import("Table", "@powersync/web")>]
type Table(columns: obj) =
    class end

[<Import("Schema", "@powersync/web")>]
type Schema(tables: obj) =
    class end

/// One local write waiting in the upload queue.
type CrudEntry =
    abstract id: string
    /// "PUT" | "PATCH" | "DELETE"
    abstract op: string
    abstract table: string
    abstract opData: obj option

type CrudTransaction =
    abstract crud: CrudEntry[]
    abstract complete: unit -> JS.Promise<unit>

/// A live query: emits the full row set whenever its underlying tables change.
type WatchedQuery<'Row> =
    /// Returns an unsubscribe function.
    abstract registerListener: WatchedQueryListener<'Row> -> (unit -> unit)
    abstract close: unit -> JS.Promise<unit>

and WatchedQueryListener<'Row> =
    abstract onData: 'Row[] -> unit
    abstract onError: exn -> unit

type Query<'Row> =
    abstract watch: unit -> WatchedQuery<'Row>

type SyncStatus =
    abstract connected: bool
    abstract connecting: bool
    abstract uploading: bool
    abstract downloading: bool
    abstract hasSynced: bool option
    /// Last failure from the connector's uploadData; the SDK only logs these at debug level.
    abstract uploadError: exn option
    abstract downloadError: exn option

type Credentials = { endpoint: string; token: string }

/// Implemented by the app to hand PowerSync a token and to push local writes.
type BackendConnector =
    abstract fetchCredentials: unit -> JS.Promise<Credentials>
    abstract uploadData: PowerSyncDatabase -> JS.Promise<unit>

and [<Import("PowerSyncDatabase", "@powersync/web")>] PowerSyncDatabase(options: obj) =
    member _.connect(connector: BackendConnector) : JS.Promise<unit> = jsNative
    /// Stops syncing and wipes the local database (sign-out).
    member _.disconnectAndClear() : JS.Promise<unit> = jsNative
    member _.execute(sql: string, parameters: obj[]) : JS.Promise<obj> = jsNative
    member _.getAll<'Row>(sql: string, parameters: obj[]) : JS.Promise<'Row[]> = jsNative
    member _.getOptional<'Row>(sql: string, parameters: obj[]) : JS.Promise<'Row option> = jsNative
    member _.getNextCrudTransaction() : JS.Promise<CrudTransaction option> = jsNative
    member _.query<'Row>(definition: {| sql: string; parameters: obj[] |}) : Query<'Row> = jsNative
    /// Returns an unsubscribe function.
    member _.registerListener(listener: obj) : unit -> unit = jsNative
    member _.currentStatus: SyncStatus = jsNative

/// Build a database from a table name -> columns map.
let database (dbFilename: string) (tables: (string * (string * ColumnType) list) list) =
    let schema =
        tables
        |> List.map (fun (name, cols) -> name ==> Table(createObj (cols |> List.map (fun (c, t) -> c ==> t))))
        |> createObj
        |> Schema

    PowerSyncDatabase(createObj [ "schema" ==> schema; "database" ==> createObj [ "dbFilename" ==> dbFilename ] ])
