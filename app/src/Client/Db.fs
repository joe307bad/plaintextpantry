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

/// Row shape of the local `shopping_lists` table. Archived lists are kept
/// but never shown; the watch query leaves them out, so a row here is live.
type ShoppingList = { id: string; name: string; created_at: string }

/// Row shape of the local `shopping_items` table. `quantity` is text so "some" survives; `done` is 0/1.
type ShoppingItem =
    { id: string
      list_id: string
      name: string
      quantity: string
      unit: string
      ``done``: int
      created_at: string }

/// Row shape of the local `menus` table. A shopping list's twin: only a name.
type Menu = { id: string; name: string; created_at: string }

/// Row shape of the local `menu_recipes` table: one row per time a recipe
/// was added to a menu.
type MenuRecipe =
    { id: string
      menu_id: string
      recipe_id: string
      created_at: string }

/// Row shape of the local `menu_sides` table: a side dish under a menu
/// entry. `done` is 0/1, checked off from the shopping list.
type MenuSide =
    { id: string
      menu_recipe_id: string
      name: string
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

    /// The last user /me confirmed, kept so the app can open with no
    /// network. Cleared on 401 and on sign-out.
    module private LastUser =
        let private key = "ptp.user"

        let get () =
            match Browser.WebStorage.localStorage.getItem key with
            | null -> None
            | json ->
                match Decode.fromString Codec.decodeUser json with
                | Ok user -> Some user
                | Error _ -> None

        let set (user: User) =
            Browser.WebStorage.localStorage.setItem (key, Encode.toString 0 (Codec.encodeUser user))

        let clear () =
            Browser.WebStorage.localStorage.removeItem key

    /// `Some user` when the session cookie is valid, `None` on 401. When the
    /// request can't be made at all (offline), the last confirmed user: the
    /// cookie is still there, and local data is what the app runs on anyway.
    let getMe () =
        promise {
            let! response =
                fetch Route.me [] |> Promise.map Some |> Promise.catch (fun _ -> None)

            match response with
            | None -> return LastUser.get ()
            | Some response when response.Status = 401 ->
                LastUser.clear ()
                return None
            | Some response ->
                let! response = ensureOk response
                let! body = response.text ()

                match Decode.fromString Codec.decodeUser body with
                | Ok user ->
                    LastUser.set user
                    return Some user
                | Error err -> return failwithf "Bad /me response: %s" err
        }

    let forgetMe () = LastUser.clear ()

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
          "shopping_lists", [ "name", column.text; "created_at", column.text; "archived_at", column.text ]
          "shopping_items",
          [ "list_id", column.text
            "name", column.text
            "quantity", column.text
            "unit", column.text
            "done", column.integer
            "created_at", column.text ]
          "menus", [ "name", column.text; "created_at", column.text; "archived_at", column.text ]
          "menu_recipes", [ "menu_id", column.text; "recipe_id", column.text; "created_at", column.text ]
          "menu_sides",
          [ "menu_recipe_id", column.text
            "name", column.text
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
        Api.forgetMe ()
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

/// Live un-archived shopping lists, newest first. The head is the one
/// adding goes into.
let watchShoppingLists (onChange: ShoppingList list -> unit) =
    let sql = "SELECT id, name, created_at FROM shopping_lists WHERE archived_at IS NULL ORDER BY created_at DESC, id"
    let query = db.query<ShoppingList> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<ShoppingList> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch shopping lists", err) }
    |> ignore

/// Live shopping items across every list: unchecked items first, oldest
/// first within each group. The UI picks out the list it is showing.
let watchShoppingItems (onChange: ShoppingItem list -> unit) =
    let sql =
        "SELECT id, list_id, name, quantity, unit, done, created_at FROM shopping_items ORDER BY done, created_at, id"

    let query = db.query<ShoppingItem> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<ShoppingItem> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch shopping items", err) }
    |> ignore

/// Live un-archived menus, newest first. The head is the one adding goes into.
let watchMenus (onChange: Menu list -> unit) =
    let sql = "SELECT id, name, created_at FROM menus WHERE archived_at IS NULL ORDER BY created_at DESC, id"
    let query = db.query<Menu> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Menu> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menus", err) }
    |> ignore

/// Live menu entries across every menu, in the order they were added. The
/// UI picks out the menu it is showing.
let watchMenuRecipes (onChange: MenuRecipe list -> unit) =
    let sql = "SELECT id, menu_id, recipe_id, created_at FROM menu_recipes ORDER BY created_at, id"
    let query = db.query<MenuRecipe> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<MenuRecipe> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menu recipes", err) }
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

/// Also takes the recipe, and the sides under it, off every menu it was on.
let deleteRecipe (id: string) =
    promise {
        let! _ = db.execute ("DELETE FROM recipes WHERE id = ?", [| id |])

        let! _ =
            db.execute (
                "DELETE FROM menu_sides WHERE menu_recipe_id IN (SELECT id FROM menu_recipes WHERE recipe_id = ?)",
                [| id |]
            )

        let! _ = db.execute ("DELETE FROM menu_recipes WHERE recipe_id = ?", [| id |])
        ()
    }

type private Quantity = Cooklang.Quantity

let private quantityText (quantity: Quantity option) =
    match quantity with
    | Some(Quantity.Number n) -> string n
    | Some(Quantity.Text t) -> t
    | None -> ""

/// The list a user with none gets the first time they add something.
let private newShoppingList () : ShoppingList =
    { id = string (Guid.NewGuid())
      name = Shared.ShoppingList.defaultName DateTime.Now
      created_at = nowIso () }

/// True when every ingredient already has a row in the newest list, matched
/// by name the way `planShoppingAdd` merges (case-insensitive, checked or
/// not). Pure and in-memory: the model mirrors the tables, so this costs one
/// pass over the list's rows and no SQLite round trip. A recipe with no
/// ingredients, or a user with no list, has nothing to warn about.
let allIngredientsPresent (lists: ShoppingList list) (items: ShoppingItem list) (ingredients: Cooklang.Ingredient list) =
    match lists, ingredients with
    | [], _
    | _, [] -> false
    | list :: _, _ ->
        let names =
            items
            |> List.filter (fun i -> i.list_id = list.id)
            |> List.map (fun i -> i.name.ToLowerInvariant())
            |> Set.ofList

        ingredients |> List.forall (fun i -> names.Contains(i.Name.ToLowerInvariant()))

/// What adding ingredients does: the list to create when the user had none,
/// rows whose quantity was topped up, and rows that are new. The caller
/// shows the result at once and hands the plan to `addToShoppingList`.
type ShoppingPlan =
    { NewList: ShoppingList option
      Updated: ShoppingItem list
      Inserted: ShoppingItem list }

/// One row per entry (name, quantity, unit), in the newest of `lists`
/// (created here when there is none). An unchecked row there with the same
/// name and unit is topped up instead when both quantities are numbers, so
/// adding two recipes that need flour gives one line, not two. Pure: works
/// off the lists the UI already holds (a mirror of the tables), so nothing
/// is read from SQLite. Returns the lists and items as the UI should now
/// show them, plus the plan.
let private planAdd (lists: ShoppingList list) (items: ShoppingItem list) (entries: (string * Quantity option * string) list) =
    let target, newList =
        match lists with
        | l :: _ -> l, None
        | [] ->
            let l = newShoppingList ()
            l, Some l

    let listId = target.id

    let step (items: ShoppingItem list, updated: Set<string>, inserted: Set<string>) (name: string, quantity, unit) =
        let existing =
            items
            |> List.tryFind (fun r ->
                r.list_id = listId
                && r.name.ToLowerInvariant() = name.ToLowerInvariant()
                && r.unit = unit
                && r.``done`` = 0)

        match existing, quantity with
        | Some row, Some(Quantity.Number n) when fst (Double.TryParse row.quantity) ->
            let topped = { row with quantity = string (float row.quantity + n) }
            items |> List.map (fun r -> if r.id = row.id then topped else r), Set.add row.id updated, inserted
        | _ ->
            let row =
                { id = string (Guid.NewGuid())
                  list_id = listId
                  name = name
                  quantity = quantityText quantity
                  unit = unit
                  ``done`` = 0
                  created_at = nowIso () }

            items @ [ row ], updated, Set.add row.id inserted

    let items, updated, inserted = entries |> List.fold step (items, Set.empty, Set.empty)
    // A row inserted then topped up by a later ingredient is still one insert.
    let updated = Set.difference updated inserted
    let items = items |> List.sortBy (fun i -> i.``done``, i.created_at, i.id)

    (match newList with
     | Some l -> l :: lists
     | None -> lists),
    items,
    { NewList = newList
      Updated = items |> List.filter (fun i -> updated.Contains i.id)
      Inserted = items |> List.filter (fun i -> inserted.Contains i.id) }

/// A recipe's ingredients.
let planShoppingAdd lists items (ingredients: Cooklang.Ingredient list) =
    planAdd lists items (ingredients |> List.map (fun i -> i.Name, i.Quantity, defaultArg i.Unit ""))

/// One item typed on the list page, taken as-is for the name.
let planFreeItemAdd lists items (text: string) =
    planAdd lists items [ text.Trim(), None, "" ]

let addToShoppingList (plan: ShoppingPlan) =
    promise {
        match plan.NewList with
        | Some list ->
            let! _ =
                db.execute (
                    "INSERT INTO shopping_lists (id, name, created_at) VALUES (?, ?, ?)",
                    [| list.id; list.name; list.created_at |]
                )

            ()
        | None -> ()

        for row in plan.Updated do
            let! _ = db.execute ("UPDATE shopping_items SET quantity = ? WHERE id = ?", [| row.quantity; row.id |])
            ()

        for row in plan.Inserted do
            let! _ =
                db.execute (
                    "INSERT INTO shopping_items (id, list_id, name, quantity, unit, done, created_at) VALUES (?, ?, ?, ?, ?, 0, ?)",
                    [| row.id; row.list_id; row.name; row.quantity; row.unit; row.created_at |]
                )

            ()
    }

let setShoppingItemDone (id: string) (isDone: bool) =
    db.execute ("UPDATE shopping_items SET done = ? WHERE id = ?", [| (if isDone then 1 else 0); id |])

/// Puts the list away; its items stay with it. The next add starts a new one.
let archiveShoppingList (id: string) =
    db.execute ("UPDATE shopping_lists SET archived_at = ? WHERE id = ?", [| nowIso (); id |])

// ---------------------------------------------------------------------------
// Menus
// ---------------------------------------------------------------------------

/// What adding a recipe to a menu does: the menu to create when the user had
/// none, and the new entry. Shown at once, then handed to `addToMenu`.
type MenuPlan = { NewMenu: Menu option; Inserted: MenuRecipe }

/// One entry in the newest of `menus` (created here when there is none).
/// Every add is a new row, so a recipe can be on a menu twice. Pure, like
/// `planShoppingAdd`: returns the menus and entries as the UI should now
/// show them, plus the plan.
let planMenuAdd (menus: Menu list) (entries: MenuRecipe list) (recipeId: string) =
    let target, newMenu =
        match menus with
        | m :: _ -> m, None
        | [] ->
            let m: Menu =
                { id = string (Guid.NewGuid())
                  name = Shared.Menu.defaultName DateTime.Now (Random())
                  created_at = nowIso () }

            m, Some m

    let entry =
        { id = string (Guid.NewGuid())
          menu_id = target.id
          recipe_id = recipeId
          created_at = nowIso () }

    (match newMenu with
     | Some m -> m :: menus
     | None -> menus),
    entries @ [ entry ],
    { NewMenu = newMenu; Inserted = entry }

let addToMenu (plan: MenuPlan) =
    promise {
        match plan.NewMenu with
        | Some menu ->
            let! _ =
                db.execute (
                    "INSERT INTO menus (id, name, created_at) VALUES (?, ?, ?)",
                    [| menu.id; menu.name; menu.created_at |]
                )

            ()
        | None -> ()

        let e = plan.Inserted

        let! _ =
            db.execute (
                "INSERT INTO menu_recipes (id, menu_id, recipe_id, created_at) VALUES (?, ?, ?, ?)",
                [| e.id; e.menu_id; e.recipe_id; e.created_at |]
            )

        ()
    }

/// Takes the entry and its sides off the menu.
let removeMenuRecipe (id: string) =
    promise {
        let! _ = db.execute ("DELETE FROM menu_sides WHERE menu_recipe_id = ?", [| id |])
        let! _ = db.execute ("DELETE FROM menu_recipes WHERE id = ?", [| id |])
        ()
    }

/// Live sides across every menu, in the order they were added.
let watchMenuSides (onChange: MenuSide list -> unit) =
    let sql = "SELECT id, menu_recipe_id, name, done, created_at FROM menu_sides ORDER BY created_at, id"
    let query = db.query<MenuSide> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<MenuSide> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menu sides", err) }
    |> ignore

/// The row a new side becomes; built by the caller so the UI can show it
/// before the insert lands.
let newMenuSide (menuRecipeId: string) (name: string) : MenuSide =
    { id = string (Guid.NewGuid())
      menu_recipe_id = menuRecipeId
      name = name.Trim()
      ``done`` = 0
      created_at = nowIso () }

let addMenuSide (side: MenuSide) =
    db.execute (
        "INSERT INTO menu_sides (id, menu_recipe_id, name, done, created_at) VALUES (?, ?, ?, 0, ?)",
        [| side.id; side.menu_recipe_id; side.name; side.created_at |]
    )

let setMenuSideDone (id: string) (isDone: bool) =
    db.execute ("UPDATE menu_sides SET done = ? WHERE id = ?", [| (if isDone then 1 else 0); id |])

let removeMenuSide (id: string) =
    db.execute ("DELETE FROM menu_sides WHERE id = ?", [| id |])

/// Puts the menu away; its entries and sides stay with it.
let archiveMenu (id: string) =
    db.execute ("UPDATE menus SET archived_at = ? WHERE id = ?", [| nowIso (); id |])
