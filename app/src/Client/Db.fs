/// Local-first data layer: the PowerSync SQLite database, the connector that
/// talks to the F# server, and the queries the UI uses.
module Db

open System
open Fable.Core
open Fable.Core.JsInterop
open Fetch
open Thoth.Json.Core
open Thoth.Json.JavaScript
open Shared
open PowerSync

/// Row shape of the local `recipes` table. Field names match SQLite columns.
type Recipe = { id: string; title: string; body: string; created_at: string }

/// Calls to the F# server, using the coders shared with it.
module private Api =
    let private ensureOk (response: Response) =
        promise {
            if response.Ok then
                return response
            else
                let! body = response.text ()
                return failwithf "%s %d: %s" response.Url response.Status body
        }

    let getSyncCredentials () =
        promise {
            let! response = fetch Route.syncCredentials [] |> Promise.bind ensureOk
            let! body = response.text ()

            match Decode.fromString Codec.decodeCredentials body with
            | Ok creds -> return creds
            | Error err -> return failwithf "Bad credentials response: %s" err
        }

    let uploadCrud (ops: CrudOp list) =
        fetch
            Route.upload
            [ Method HttpMethod.POST
              requestHeaders [ ContentType "application/json" ]
              Body(BodyInit.Case3(Encode.toString 0 (Codec.encodeCrudOps ops))) ]
        |> Promise.bind ensureOk
        |> Promise.map ignore

[<Emit("new Date().toISOString()")>]
let private nowIso () : string = jsNative

let db =
    database "plaintextpantry.sqlite" [ "recipes", [ "title", column.text; "body", column.text; "created_at", column.text ] ]

let private toCrudOp (entry: CrudEntry) : CrudOp =
    let data =
        match entry.opData with
        | None -> Map.empty
        | Some d ->
            JS.Constructors.Object.keys d
            |> Seq.map (fun key ->
                let value: obj = d?(key)
                key, (if isNullOrUndefined value then None else Some(string value)))
            |> Map.ofSeq

    { Op = entry.op; Table = entry.table; Id = entry.id; Data = data }

let private connector =
    { new BackendConnector with
        member _.fetchCredentials() =
            promise {
                let! creds = Api.getSyncCredentials ()
                return { endpoint = creds.Endpoint; token = creds.Token }
            }

        /// Drains the queue one transaction at a time. A failed upload rejects
        /// the promise, which leaves the transaction queued so PowerSync retries.
        member _.uploadData(database) =
            let rec drain () =
                promise {
                    let! tx = database.getNextCrudTransaction ()

                    match tx with
                    | None -> ()
                    | Some tx ->
                        let ops = tx.crud |> Array.map toCrudOp |> List.ofArray
                        do! Api.uploadCrud ops
                        do! tx.complete ()
                        do! drain ()
                }

            drain () }

let connect () = db.connect connector

/// Live list of recipes; `onChange` fires with the full set on every change.
let watchRecipes (onChange: Recipe list -> unit) =
    let query =
        db.query<Recipe> {| sql = "SELECT id, title, body, created_at FROM recipes ORDER BY created_at DESC"; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Recipe> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch recipes", err) }
    |> ignore

let watchStatus (onChange: SyncStatus -> unit) =
    onChange db.currentStatus
    db.registerListener (createObj [ "statusChanged" ==> onChange ]) |> ignore

let addRecipe (title: string) (body: string) =
    db.execute (
        "INSERT INTO recipes (id, title, body, created_at) VALUES (?, ?, ?, ?)",
        [| string (Guid.NewGuid()); title; body; nowIso () |]
    )

let renameRecipe (id: string) (title: string) =
    db.execute ("UPDATE recipes SET title = ? WHERE id = ?", [| title; id |])

let deleteRecipe (id: string) =
    db.execute ("DELETE FROM recipes WHERE id = ?", [| id |])
