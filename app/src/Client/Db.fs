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

/// Row shape of the local `shopping_items` table. `quantity` is text so "some" survives; `done` is 0/1.
type ShoppingItem =
    { id: string
      name: string
      quantity: string
      unit: string
      ``done``: int
      created_at: string }

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

    /// `Some user` when the session cookie is valid, `None` on 401.
    let getMe () =
        promise {
            let! response = fetch Route.me []

            if response.Status = 401 then
                return None
            else
                let! response = ensureOk response
                let! body = response.text ()

                match Decode.fromString Codec.decodeUser body with
                | Ok user -> return Some user
                | Error err -> return failwithf "Bad /me response: %s" err
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
    database
        "plaintextpantry.sqlite"
        [ "recipes", [ "title", column.text; "body", column.text; "created_at", column.text ]
          "shopping_items",
          [ "name", column.text
            "quantity", column.text
            "unit", column.text
            "done", column.integer
            "created_at", column.text ] ]

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

let currentUser () = Api.getMe ()

/// Ends the session: wipes local data (another account may sign in on this
/// browser next), then hands the browser to the server, which clears the
/// cookie and ends the Keycloak session.
let signOut () =
    promise {
        do! db.disconnectAndClear ()
        Browser.Dom.window.location.href <- Route.logout
    }

let signIn (returnTo: string) =
    Browser.Dom.window.location.href <- Route.login + "?returnTo=" + JS.encodeURIComponent returnTo

/// Live list of recipes; `onChange` fires with the full set on every change.
let watchRecipes (onChange: Recipe list -> unit) =
    // Rows synced before `body` existed have NULL there; the UI wants a string.
    let sql =
        "SELECT id, title, COALESCE(body, '') AS body, created_at FROM recipes ORDER BY created_at DESC"

    let query = db.query<Recipe> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Recipe> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch recipes", err) }
    |> ignore

/// Live shopping list: unchecked items first, oldest first within each group.
let watchShoppingItems (onChange: ShoppingItem list -> unit) =
    let sql =
        "SELECT id, name, quantity, unit, done, created_at FROM shopping_items ORDER BY done, created_at, id"

    let query = db.query<ShoppingItem> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<ShoppingItem> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch shopping items", err) }
    |> ignore

let watchStatus (onChange: SyncStatus -> unit) =
    onChange db.currentStatus
    db.registerListener (createObj [ "statusChanged" ==> onChange ]) |> ignore

/// The row a new recipe becomes. Built by the caller so the UI can show it
/// before the insert lands.
let newRecipe (title: string) (body: string) : Recipe =
    { id = string (Guid.NewGuid())
      title = title
      body = body
      created_at = nowIso () }

let addRecipe (recipe: Recipe) =
    db.execute (
        "INSERT INTO recipes (id, title, body, created_at) VALUES (?, ?, ?, ?)",
        [| recipe.id; recipe.title; recipe.body; recipe.created_at |]
    )

let updateRecipe (id: string) (title: string) (body: string) =
    db.execute ("UPDATE recipes SET title = ?, body = ? WHERE id = ?", [| title; body; id |])

let deleteRecipe (id: string) =
    db.execute ("DELETE FROM recipes WHERE id = ?", [| id |])

type private Quantity = Cooklang.Quantity

let private quantityText (quantity: Quantity option) =
    match quantity with
    | Some(Quantity.Number n) -> string n
    | Some(Quantity.Text t) -> t
    | None -> ""

/// What adding ingredients does to the list: rows whose quantity was topped
/// up, and rows that are new. The caller shows the resulting list at once and
/// hands the plan to `addToShoppingList` to persist.
type ShoppingPlan = { Updated: ShoppingItem list; Inserted: ShoppingItem list }

/// One row per ingredient. An unchecked row with the same name and unit is
/// topped up instead when both quantities are numbers, so adding two recipes
/// that need flour gives one line, not two. Pure: works off the list the UI
/// already holds (a mirror of the table), so nothing is read from SQLite.
let planShoppingAdd (items: ShoppingItem list) (ingredients: Cooklang.Ingredient list) =
    let step (items: ShoppingItem list, updated: Set<string>, inserted: Set<string>) (i: Cooklang.Ingredient) =
        let unit = defaultArg i.Unit ""

        let existing =
            items
            |> List.tryFind (fun r ->
                r.name.ToLowerInvariant() = i.Name.ToLowerInvariant() && r.unit = unit && r.``done`` = 0)

        match existing, i.Quantity with
        | Some row, Some(Quantity.Number n) when fst (Double.TryParse row.quantity) ->
            let topped = { row with quantity = string (float row.quantity + n) }
            items |> List.map (fun r -> if r.id = row.id then topped else r), Set.add row.id updated, inserted
        | _ ->
            let row =
                { id = string (Guid.NewGuid())
                  name = i.Name
                  quantity = quantityText i.Quantity
                  unit = unit
                  ``done`` = 0
                  created_at = nowIso () }

            items @ [ row ], updated, Set.add row.id inserted

    let items, updated, inserted = ingredients |> List.fold step (items, Set.empty, Set.empty)
    // A row inserted then topped up by a later ingredient is still one insert.
    let updated = Set.difference updated inserted
    let items = items |> List.sortBy (fun i -> i.``done``, i.created_at, i.id)

    items,
    { Updated = items |> List.filter (fun i -> updated.Contains i.id)
      Inserted = items |> List.filter (fun i -> inserted.Contains i.id) }

let addToShoppingList (plan: ShoppingPlan) =
    promise {
        for row in plan.Updated do
            let! _ = db.execute ("UPDATE shopping_items SET quantity = ? WHERE id = ?", [| row.quantity; row.id |])
            ()

        for row in plan.Inserted do
            let! _ =
                db.execute (
                    "INSERT INTO shopping_items (id, name, quantity, unit, done, created_at) VALUES (?, ?, ?, ?, 0, ?)",
                    [| row.id; row.name; row.quantity; row.unit; row.created_at |]
                )

            ()
    }

let setShoppingItemDone (id: string) (isDone: bool) =
    db.execute ("UPDATE shopping_items SET done = ? WHERE id = ?", [| (if isDone then 1 else 0); id |])

let deleteShoppingItem (id: string) =
    db.execute ("DELETE FROM shopping_items WHERE id = ?", [| id |])

let clearDoneShoppingItems () =
    db.execute ("DELETE FROM shopping_items WHERE done <> 0", [||])
