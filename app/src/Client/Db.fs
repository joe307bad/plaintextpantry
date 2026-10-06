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

/// Row shape of the local `pantries` table: the household everything below
/// belongs to. `user_id` is its owner.
type Pantry =
    { id: string
      user_id: string
      name: string
      created_at: string }

/// Row shape of the local `pantry_members` table: one row per person in a
/// pantry, the owner included. `status` is `Shared.Pantry.Pending` until the
/// owner approves it. `email` and `name` are written by that person's own
/// client, since the server only ever knows their user id.
type PantryMember =
    { id: string
      pantry_id: string
      user_id: string
      email: string
      name: string
      status: string
      created_at: string }

/// Row shape of the local `recipes` table. Field names match SQLite columns.
/// `pantry_id`, here and on every row below, is the pantry it belongs to:
/// what the pages filter by, and what decides who else can see it.
/// `user_id` is who made it, which the pages show as "(Added by Joe)"; it is
/// stamped by the server on upload, so a row that predates it is ''.
type Recipe =
    { id: string
      pantry_id: string
      user_id: string
      title: string
      body: string
      created_at: string }

/// Row shape of the local `shopping_lists` table. Archived lists are kept
/// but never shown; the watch query leaves them out, so a row here is live.
type ShoppingList =
    { id: string
      pantry_id: string
      name: string
      created_at: string }

/// Row shape of the local `shopping_items` table. `quantity` is text so "some" survives; `done` is 0/1.
type ShoppingItem =
    { id: string
      pantry_id: string
      user_id: string
      list_id: string
      name: string
      quantity: string
      unit: string
      ``done``: int
      created_at: string }

/// Row shape of the local `menus` table. A shopping list's twin: only a name.
type Menu =
    { id: string
      pantry_id: string
      name: string
      created_at: string }

/// Row shape of the local `menu_recipes` table: one row per time a recipe
/// was added to a menu.
type MenuRecipe =
    { id: string
      pantry_id: string
      user_id: string
      menu_id: string
      recipe_id: string
      created_at: string }

/// Row shape of the local `menu_sides` table: a side dish under a menu
/// entry. `done` is 0/1, checked off from the shopping list.
type MenuSide =
    { id: string
      pantry_id: string
      menu_recipe_id: string
      name: string
      ``done``: int
      created_at: string }

/// Row shape of the local `tags` table: one row per tag name.
type Tag =
    { id: string
      pantry_id: string
      name: string
      created_at: string }

/// Row shape of the local `recipe_tags` table: one row per tag on a recipe.
type RecipeTag =
    { id: string
      pantry_id: string
      recipe_id: string
      tag_id: string
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
    /// `fetchUnsafe`: Fable.Fetch's `fetch` rejects on any non-2xx, which
    /// would make a 401 look like being offline and keep a dead session alive.
    let getMe () =
        promise {
            let! response =
                fetchUnsafe Route.me [] |> Promise.map Some |> Promise.catch (fun _ -> None)

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

    /// Who was signed in last time, straight out of localStorage: no network,
    /// no await. What the app opens on, before /me has had its say.
    let rememberedUser () = LastUser.get ()

    let forgetMe () = LastUser.clear ()

    let getSyncCredentials () =
        promise {
            let! response = fetch Route.syncCredentials [] |> Promise.bind ensureOk
            let! body = response.text ()

            match Decode.fromString Codec.decodeCredentials body with
            | Ok creds -> return creds
            | Error err -> return failwithf "Bad credentials response: %s" err
        }

    /// One page visit, counted for the usage dashboard. Fire-and-forget and
    /// silent: a counter is not worth a failed navigation, a retry or a line
    /// in the console on a flaky connection, and the server holds the key
    /// that the write actually needs (see Server/Usage.fs).
    let countPageview (section: string) =
        fetchUnsafe
            Route.pageview
            [ Method HttpMethod.POST
              requestHeaders [ ContentType "text/plain" ]
              Body(BodyInit.Case3 section) ]
        |> Promise.map ignore
        |> Promise.catch ignore
        |> ignore

    let uploadCrud (ops: CrudOp list) =
        fetch
            Route.upload
            [ Method HttpMethod.POST
              requestHeaders [ ContentType "application/json" ]
              Body(BodyInit.Case3(Encode.toString 0 (Codec.encodeCrudOps ops))) ]
        |> Promise.bind ensureOk
        |> Promise.map ignore

/// Which pantry the header was last left on. A habit of the device, not
/// something to sync: two people in one pantry may well be looking at
/// different ones. Cleared on sign-out with everything else.
module PickedPantry =
    let private key = "ptp.pantry"

    let get () =
        match Browser.WebStorage.localStorage.getItem key with
        | null -> None
        | id -> Some id

    let set (id: string) =
        Browser.WebStorage.localStorage.setItem (key, id)

    let clear () =
        Browser.WebStorage.localStorage.removeItem key

[<Emit("new Date().toISOString()")>]
let private nowIso () : string = jsNative

let db =
    database
        "plaintextpantry.sqlite"
        [ "pantries", [ "user_id", column.text; "name", column.text; "created_at", column.text ]
          "pantry_members",
          [ "pantry_id", column.text
            "user_id", column.text
            "email", column.text
            "name", column.text
            "status", column.text
            "created_at", column.text ]
          "recipes",
          [ "pantry_id", column.text
            "user_id", column.text
            "title", column.text
            "body", column.text
            "created_at", column.text ]
          "shopping_lists",
          [ "pantry_id", column.text; "name", column.text; "created_at", column.text; "archived_at", column.text ]
          "shopping_items",
          [ "pantry_id", column.text
            "user_id", column.text
            "list_id", column.text
            "name", column.text
            "quantity", column.text
            "unit", column.text
            "done", column.integer
            "created_at", column.text ]
          "menus",
          [ "pantry_id", column.text; "name", column.text; "created_at", column.text; "archived_at", column.text ]
          "menu_recipes",
          [ "pantry_id", column.text
            "user_id", column.text
            "menu_id", column.text
            "recipe_id", column.text
            "created_at", column.text ]
          "menu_sides",
          [ "pantry_id", column.text
            "menu_recipe_id", column.text
            "name", column.text
            "done", column.integer
            "created_at", column.text ]
          "tags", [ "pantry_id", column.text; "name", column.text; "created_at", column.text ]
          "recipe_tags",
          [ "pantry_id", column.text; "recipe_id", column.text; "tag_id", column.text; "created_at", column.text ] ]

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

/// How the app syncs. PowerSync's defaults suit a desktop tab left open all
/// day; this is a phone in a kitchen.
///
/// `retryDelayMs` is the wait after a failed stream before trying again (5s by
/// default) - a phone waking up wants the next attempt straight away.
/// `crudUploadThrottleMs` is how long a local write sits before being pushed
/// (1s by default); the round trip back down is what the other devices in the
/// pantry are waiting on, so it is worth shortening.
let private syncOptions =
    createObj [ "retryDelayMs" ==> 1000; "crudUploadThrottleMs" ==> 200 ]

/// When the stream was last opened, so `syncNow` can tell a stream that was
/// just built from one that has been sitting there since the app was last
/// looked at.
let mutable private openedAt = 0.0

let connect () =
    openedAt <- JS.Constructors.Date.now ()
    db.connect (connector, syncOptions)

/// Tears the sync stream down and opens a fresh one - `connect` again, which
/// aborts whatever is there first.
///
/// PowerSync retries a stream that *errors*, but not one that simply stops
/// arriving - which is what a phone does to it. iOS freezes an installed app
/// mid-stream, and on waking the connection is dead while the SDK still
/// believes it is connected, so nothing written elsewhere (another device, the
/// MCP tools) ever lands. Reconnecting is the only way to find out, so the app
/// does it whenever it comes back to the foreground.
let resync () = connect ()

/// Closes the stream and leaves the local database where it is: the account
/// the app opened on turned out to be signed out, so there is nothing to sync
/// and no point retrying the credentials call every second behind the login
/// page.
let stopSync () =
    openedAt <- 0.0
    db.disconnect ()

[<Emit("document.visibilityState === 'visible'")>]
let private isVisible () : bool = jsNative

/// How long a stream counts as freshly opened. Two taps in quick succession
/// want one reconnect between them, not one each: tearing the stream down
/// mid-checkpoint and starting over is the one thing that would make the app
/// slower to catch up, which is the opposite of the point.
let private quietMs = 1500.0

/// Opens a fresh stream unless one was opened a moment ago. This is the "pull
/// down whatever is new, now" the app reaches for whenever it has a reason to
/// think it has fallen behind: coming back to the foreground, the network
/// returning, or simply moving to another screen.
let syncNow () =
    let now = JS.Constructors.Date.now ()

    if isVisible () && now - openedAt > quietMs then
        resync () |> Promise.catch (fun err -> JS.console.error ("resync", err)) |> ignore

/// Reconnects whenever the app is looked at again or the network comes back,
/// so a change made elsewhere while it was away is there by the time the first
/// screen is drawn - no tapping between pages to shake it loose.
///
/// `pageshow` is for Safari, which restores a page from its back/forward cache
/// without firing `visibilitychange`. Coming back to the app tends to fire
/// several of these at once (becoming visible is also regaining focus), and
/// `syncNow`'s quiet window is what keeps that to one reconnect.
let keepSynced () =
    Browser.Dom.document.addEventListener ("visibilitychange", fun _ -> syncNow ())
    Browser.Dom.window.addEventListener ("pageshow", fun _ -> syncNow ())
    Browser.Dom.window.addEventListener ("focus", fun _ -> syncNow ())
    Browser.Dom.window.addEventListener ("online", fun _ -> syncNow ())

let currentUser () = Api.getMe ()

/// Who was signed in when the app was last closed, from localStorage. No
/// network and no waiting: it is what the app opens on, with `currentUser`
/// confirming it or taking it away a moment later.
let rememberedUser () = Api.rememberedUser ()

/// One page visit, counted for the usage dashboard. Totals, not people: the
/// section name is all that is sent, and the server is what writes the row.
let countPageview (section: string) = Api.countPageview section

/// Ends the session: wipes local data (another account may sign in on this
/// browser next), then hands the browser to the server, which clears the
/// cookie and ends the Keycloak session.
let signOut () =
    promise {
        Api.forgetMe ()
        PickedPantry.clear ()

        do! db.disconnectAndClear ()
        Browser.Dom.window.location.href <- Route.logout
    }

let signIn (returnTo: string) =
    Browser.Dom.window.location.href <- Route.login + "?returnTo=" + JS.encodeURIComponent returnTo

/// Live list of recipes; `onChange` fires with the full set on every change.
let watchRecipes (onChange: Recipe list -> unit) =
    // Rows synced before `body` or `user_id` existed have NULL there; the UI
    // wants a string.
    let sql =
        "SELECT id, pantry_id, COALESCE(user_id, '') AS user_id, title, COALESCE(body, '') AS body, created_at "
        + "FROM recipes ORDER BY created_at DESC"

    let query = db.query<Recipe> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Recipe> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch recipes", err) }
    |> ignore

/// Live un-archived shopping lists, newest first. The head is the one
/// adding goes into.
let watchShoppingLists (onChange: ShoppingList list -> unit) =
    let sql =
        "SELECT id, pantry_id, name, created_at FROM shopping_lists WHERE archived_at IS NULL ORDER BY created_at DESC, id"
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
        "SELECT id, pantry_id, COALESCE(user_id, '') AS user_id, list_id, name, quantity, unit, done, created_at "
        + "FROM shopping_items ORDER BY done, created_at, id"

    let query = db.query<ShoppingItem> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<ShoppingItem> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch shopping items", err) }
    |> ignore

/// Live un-archived menus, newest first. The head is the one adding goes into.
let watchMenus (onChange: Menu list -> unit) =
    let sql = "SELECT id, pantry_id, name, created_at FROM menus WHERE archived_at IS NULL ORDER BY created_at DESC, id"
    let query = db.query<Menu> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Menu> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menus", err) }
    |> ignore

/// Live menu entries across every menu, in the order they were added. The
/// UI picks out the menu it is showing.
let watchMenuRecipes (onChange: MenuRecipe list -> unit) =
    let sql =
        "SELECT id, pantry_id, COALESCE(user_id, '') AS user_id, menu_id, recipe_id, created_at "
        + "FROM menu_recipes ORDER BY created_at, id"
    let query = db.query<MenuRecipe> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<MenuRecipe> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menu recipes", err) }
    |> ignore

/// Live sync health: true while downloads are failing, which is what having no
/// network - or a server that isn't answering - looks like from in here.
/// Connection errors count as download errors, so an app that cannot get a
/// stream up at all reports true too. Called once straight away and then on
/// every change.
let watchSyncFailing (onChange: bool -> unit) =
    let report (status: SyncStatus) = onChange status.downloadError.IsSome

    report db.currentStatus
    db.registerListener (createObj [ "statusChanged" ==> report ]) |> ignore

/// The row a new recipe becomes. Built by the caller so the UI can show it
/// before the insert lands. `userId` is written here as well as stamped by
/// the server on upload, so "(Added by Joe)" is there from the first render
/// rather than appearing when sync comes back.
let newRecipe (pantryId: string) (userId: string) (title: string) (body: string) : Recipe =
    { id = string (Guid.NewGuid())
      pantry_id = pantryId
      user_id = userId
      title = title
      body = body
      created_at = nowIso () }

let addRecipe (recipe: Recipe) =
    db.execute (
        "INSERT INTO recipes (id, pantry_id, user_id, title, body, created_at) VALUES (?, ?, ?, ?, ?, ?)",
        [| recipe.id; recipe.pantry_id; recipe.user_id; recipe.title; recipe.body; recipe.created_at |]
    )

let updateRecipe (id: string) (title: string) (body: string) =
    db.execute ("UPDATE recipes SET title = ?, body = ? WHERE id = ?", [| title; body; id |])

/// Also takes the recipe, and the sides under it, off every menu it was on,
/// and takes its tags off it (the tags themselves stay).
let deleteRecipe (id: string) =
    promise {
        let! _ = db.execute ("DELETE FROM recipes WHERE id = ?", [| id |])
        let! _ = db.execute ("DELETE FROM recipe_tags WHERE recipe_id = ?", [| id |])

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

/// The list a pantry with none gets the first time something is added to it.
let private newShoppingList (pantryId: string) : ShoppingList =
    { id = string (Guid.NewGuid())
      pantry_id = pantryId
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
let private planAdd
    (pantryId: string)
    (userId: string)
    (lists: ShoppingList list)
    (items: ShoppingItem list)
    (entries: (string * Quantity option * string) list)
    =
    let target, newList =
        match lists with
        | l :: _ -> l, None
        | [] ->
            let l = newShoppingList pantryId
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
                  pantry_id = pantryId
                  user_id = userId
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
let planShoppingAdd pantryId userId lists items (ingredients: Cooklang.Ingredient list) =
    planAdd pantryId userId lists items (ingredients |> List.map (fun i -> i.Name, i.Quantity, defaultArg i.Unit ""))

/// One item typed on the list page, taken as-is for the name.
let planFreeItemAdd pantryId userId lists items (text: string) =
    planAdd pantryId userId lists items [ text.Trim(), None, "" ]

let addToShoppingList (plan: ShoppingPlan) =
    promise {
        match plan.NewList with
        | Some list ->
            let! _ =
                db.execute (
                    "INSERT INTO shopping_lists (id, pantry_id, name, created_at) VALUES (?, ?, ?, ?)",
                    [| list.id; list.pantry_id; list.name; list.created_at |]
                )

            ()
        | None -> ()

        for row in plan.Updated do
            let! _ = db.execute ("UPDATE shopping_items SET quantity = ? WHERE id = ?", [| row.quantity; row.id |])
            ()

        for row in plan.Inserted do
            let! _ =
                db.execute (
                    "INSERT INTO shopping_items (id, pantry_id, user_id, list_id, name, quantity, unit, done, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, 0, ?)",
                    [| row.id; row.pantry_id; row.user_id; row.list_id; row.name; row.quantity; row.unit; row.created_at |]
                )

            ()
    }

let setShoppingItemDone (id: string) (isDone: bool) =
    db.execute ("UPDATE shopping_items SET done = ? WHERE id = ?", [| (if isDone then 1 else 0); id |])

let updateShoppingItem (id: string) (name: string) (quantity: string) (unit: string) =
    db.execute ("UPDATE shopping_items SET name = ?, quantity = ?, unit = ? WHERE id = ?", [| name; quantity; unit; id |])

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
let planMenuAdd (pantryId: string) (userId: string) (menus: Menu list) (entries: MenuRecipe list) (recipeId: string) =
    let target, newMenu =
        match menus with
        | m :: _ -> m, None
        | [] ->
            let m: Menu =
                { id = string (Guid.NewGuid())
                  pantry_id = pantryId
                  name = Shared.Menu.defaultName DateTime.Now (Random())
                  created_at = nowIso () }

            m, Some m

    let entry =
        { id = string (Guid.NewGuid())
          pantry_id = pantryId
          user_id = userId
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
                    "INSERT INTO menus (id, pantry_id, name, created_at) VALUES (?, ?, ?, ?)",
                    [| menu.id; menu.pantry_id; menu.name; menu.created_at |]
                )

            ()
        | None -> ()

        let e = plan.Inserted

        let! _ =
            db.execute (
                "INSERT INTO menu_recipes (id, pantry_id, user_id, menu_id, recipe_id, created_at) VALUES (?, ?, ?, ?, ?, ?)",
                [| e.id; e.pantry_id; e.user_id; e.menu_id; e.recipe_id; e.created_at |]
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
    let sql = "SELECT id, pantry_id, menu_recipe_id, name, done, created_at FROM menu_sides ORDER BY created_at, id"
    let query = db.query<MenuSide> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<MenuSide> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch menu sides", err) }
    |> ignore

/// The row a new side becomes; built by the caller so the UI can show it
/// before the insert lands.
let newMenuSide (pantryId: string) (menuRecipeId: string) (name: string) : MenuSide =
    { id = string (Guid.NewGuid())
      pantry_id = pantryId
      menu_recipe_id = menuRecipeId
      name = name.Trim()
      ``done`` = 0
      created_at = nowIso () }

let addMenuSide (side: MenuSide) =
    db.execute (
        "INSERT INTO menu_sides (id, pantry_id, menu_recipe_id, name, done, created_at) VALUES (?, ?, ?, ?, 0, ?)",
        [| side.id; side.pantry_id; side.menu_recipe_id; side.name; side.created_at |]
    )

let setMenuSideDone (id: string) (isDone: bool) =
    db.execute ("UPDATE menu_sides SET done = ? WHERE id = ?", [| (if isDone then 1 else 0); id |])

let removeMenuSide (id: string) =
    db.execute ("DELETE FROM menu_sides WHERE id = ?", [| id |])

/// Puts the menu away; its entries and sides stay with it.
let archiveMenu (id: string) =
    db.execute ("UPDATE menus SET archived_at = ? WHERE id = ?", [| nowIso (); id |])


// ---------------------------------------------------------------------------
// Tags
// ---------------------------------------------------------------------------

/// Live tags, in the order they were made. The UI sorts them by name to show
/// them (`Shared.Tag.sorted`), so a rename doesn't move rows about here.
let watchTags (onChange: Tag list -> unit) =
    let sql = "SELECT id, pantry_id, name, created_at FROM tags ORDER BY created_at, id"
    let query = db.query<Tag> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Tag> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch tags", err) }
    |> ignore

/// Live tag-to-recipe links across every recipe. The UI picks out the ones
/// for the recipe it is showing.
let watchRecipeTags (onChange: RecipeTag list -> unit) =
    let sql = "SELECT id, pantry_id, recipe_id, tag_id, created_at FROM recipe_tags ORDER BY created_at, id"
    let query = db.query<RecipeTag> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<RecipeTag> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch recipe tags", err) }
    |> ignore

/// The row that puts a tag on a recipe. Built by the caller, like
/// `newMenuSide`, so the UI can show it before the insert lands.
let newRecipeTag (pantryId: string) (recipeId: string) (tagId: string) : RecipeTag =
    { id = string (Guid.NewGuid())
      pantry_id = pantryId
      recipe_id = recipeId
      tag_id = tagId
      created_at = nowIso () }

let addRecipeTag (link: RecipeTag) =
    db.execute (
        "INSERT INTO recipe_tags (id, pantry_id, recipe_id, tag_id, created_at) VALUES (?, ?, ?, ?, ?)",
        [| link.id; link.pantry_id; link.recipe_id; link.tag_id; link.created_at |]
    )

let removeRecipeTag (id: string) =
    db.execute ("DELETE FROM recipe_tags WHERE id = ?", [| id |])

/// What typing a name into a recipe's Tags tab does: the tag to create when
/// that name is new, and the row that puts it on the recipe. Both are `None`
/// when the name is blank or the recipe has that tag already. Pure, like
/// `planShoppingAdd`: returns the tags and links as the UI should now show
/// them, plus the plan.
type TagPlan = { NewTag: Tag option; Linked: RecipeTag option }

let planTagAdd (pantryId: string) (tags: Tag list) (links: RecipeTag list) (recipeId: string) (name: string) =
    let name = Shared.Tag.clean name
    let nothing = { NewTag = None; Linked = None }

    if name = "" then
        tags, links, nothing
    else
        // Typing a name that is already a tag puts that tag on, rather than
        // making a second one by the same name.
        let tag, newTag =
            match Shared.Tag.find (fun (t: Tag) -> t.name) name tags with
            | Some t -> t, None
            | None ->
                let t =
                    { id = string (Guid.NewGuid())
                      pantry_id = pantryId
                      name = name
                      created_at = nowIso () }

                t, Some t

        if links |> List.exists (fun l -> l.recipe_id = recipeId && l.tag_id = tag.id) then
            tags, links, nothing
        else
            let link = newRecipeTag pantryId recipeId tag.id

            (match newTag with
             | Some t -> tags @ [ t ]
             | None -> tags),
            links @ [ link ],
            { NewTag = newTag; Linked = Some link }

let addTag (plan: TagPlan) =
    promise {
        match plan.NewTag with
        | Some tag ->
            let! _ =
                db.execute (
                    "INSERT INTO tags (id, pantry_id, name, created_at) VALUES (?, ?, ?, ?)",
                    [| tag.id; tag.pantry_id; tag.name; tag.created_at |]
                )

            ()
        | None -> ()

        match plan.Linked with
        | Some link ->
            let! _ = addRecipeTag link
            ()
        | None -> ()
    }

/// Renames the tag everywhere at once: recipes carry its id, not its name.
let renameTag (id: string) (name: string) =
    db.execute ("UPDATE tags SET name = ? WHERE id = ?", [| Shared.Tag.clean name; id |])

/// Drops the tag and takes it off every recipe.
let deleteTag (id: string) =
    promise {
        let! _ = db.execute ("DELETE FROM recipe_tags WHERE tag_id = ?", [| id |])
        let! _ = db.execute ("DELETE FROM tags WHERE id = ?", [| id |])
        ()
    }


// ---------------------------------------------------------------------------
// Pantries
// ---------------------------------------------------------------------------

/// Live pantries: the ones this user belongs to, including any still waiting
/// on approval (the sync rules send the pantry itself straight away, so a
/// device can name what it is waiting for). Oldest first, which puts the
/// user's own - made for them at their first sync - at the head.
let watchPantries (onChange: Pantry list -> unit) =
    let sql = "SELECT id, user_id, name, created_at FROM pantries ORDER BY created_at, id"
    let query = db.query<Pantry> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<Pantry> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch pantries", err) }
    |> ignore

/// Live memberships of every pantry the user belongs to: their own, and - for
/// a pantry they own - everyone who is in it or has asked to be. In the order
/// they were asked for, so the queue an owner works through is the order the
/// requests came in.
let watchPantryMembers (onChange: PantryMember list -> unit) =
    let sql =
        "SELECT id, pantry_id, user_id, email, name, status, created_at FROM pantry_members ORDER BY created_at, id"

    let query = db.query<PantryMember> {| sql = sql; parameters = [||] |}

    query.watch().registerListener
        { new WatchedQueryListener<PantryMember> with
            member _.onData(rows) = onChange (List.ofArray rows)
            member _.onError(err) = JS.console.error ("watch pantry members", err) }
    |> ignore

/// Renames the pantry. Only its owner may, which the server enforces; the UI
/// only offers it to them.
let renamePantry (id: string) (name: string) =
    db.execute ("UPDATE pantries SET name = ? WHERE id = ?", [| Shared.Pantry.cleanName name; id |])

/// Asks to join the pantry a QR code named: a membership for this user,
/// pending until its owner approves it. The row carries the email and name to
/// be recognised by, since the server only knows the user id.
///
/// The pantry itself isn't here yet - it arrives on the next sync, along with
/// whatever the owner has done with the request - so this writes the one row
/// and lets sync answer.
let joinPantry (pantryId: string) (user: User) =
    db.execute (
        "INSERT INTO pantry_members (id, pantry_id, user_id, email, name, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
        [| string (Guid.NewGuid())
           pantryId
           user.Id
           user.Email
           user.Name
           Shared.Pantry.Pending
           nowIso () |]
    )

/// Lets a member in. The owner's to make, like `removeMember`.
let approveMember (id: string) =
    db.execute ("UPDATE pantry_members SET status = ? WHERE id = ?", [| Shared.Pantry.Approved; id |])

/// Takes a member out of the pantry, or turns down a request to join. What
/// they made in the pantry stays with it; only their way in goes.
let removeMember (id: string) =
    db.execute ("DELETE FROM pantry_members WHERE id = ?", [| id |])
