/// Postgres access. Uploads are scoped to the pantry: they stamp the caller's
/// id on inserts and refuse to touch a row unless the caller is an approved
/// member of the pantry it belongs to (`decide`). The MCP tools are scoped
/// to the caller instead - their queries all carry `WHERE user_id = @user`,
/// and their inserts go into the caller's own pantry - so an assistant sees
/// what its user made, not everything a shared pantry holds.
module Server.Db

open System
open System.Threading.Tasks
open Npgsql
open Shared

/// Tables the client may write to, with the Postgres type of each writable
/// column. Anything not listed here is rejected. `id` is always a uuid and
/// `user_id` is never taken from the client: it is stamped with the caller's,
/// which for a pantry is its owner and for a member row is the member.
///
/// `pantry_id` is writable, because a client makes rows locally and says
/// which pantry they belong to, but every value it sends is checked against
/// the caller's memberships (`decide`) before anything is applied.
let private tables =
    Map
        [ "pantries", [ "name", "text"; "created_at", "timestamptz" ]
          "pantry_members",
          [ "pantry_id", "uuid"
            "email", "text"
            "name", "text"
            "status", "text"
            "created_at", "timestamptz" ]
          "recipes", [ "pantry_id", "uuid"; "title", "text"; "body", "text"; "created_at", "timestamptz" ]
          "shopping_lists",
          [ "pantry_id", "uuid"; "name", "text"; "created_at", "timestamptz"; "archived_at", "timestamptz" ]
          "shopping_items",
          [ "pantry_id", "uuid"
            "list_id", "uuid"
            "name", "text"
            "quantity", "text"
            "unit", "text"
            "done", "integer"
            "created_at", "timestamptz" ]
          "menus",
          [ "pantry_id", "uuid"; "name", "text"; "created_at", "timestamptz"; "archived_at", "timestamptz" ]
          "menu_recipes",
          [ "pantry_id", "uuid"; "menu_id", "uuid"; "recipe_id", "uuid"; "created_at", "timestamptz" ]
          "menu_sides",
          [ "pantry_id", "uuid"
            "menu_recipe_id", "uuid"
            "name", "text"
            "done", "integer"
            "created_at", "timestamptz" ]
          "tags", [ "pantry_id", "uuid"; "name", "text"; "created_at", "timestamptz" ]
          "recipe_tags",
          [ "pantry_id", "uuid"; "recipe_id", "uuid"; "tag_id", "uuid"; "created_at", "timestamptz" ] ]

let private param (cmd: NpgsqlCommand) (name: string) (value: string option) =
    let v : obj =
        match value with
        | Some s -> box s
        | None -> box DBNull.Value
    cmd.Parameters.AddWithValue(name, v) |> ignore

// ---------------------------------------------------------------------------
// PowerSync upload queue
// ---------------------------------------------------------------------------

/// What to do with one uploaded op. A violation is skipped rather than
/// refused: failing the transaction would leave it at the head of that
/// client's queue forever, retried on every sync. Skipping is also what the
/// old `WHERE user_id = @user_id` guards did - the statement simply matched
/// no rows - only now it is decided before the statement runs, and said out
/// loud in the log.
type private Decision =
    | Apply
    | Skip of reason: string

let private exists (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (sql: string) (parameters: (string * string) list) =
    task {
        use cmd = new NpgsqlCommand(sql, conn, tx)

        for (name, value) in parameters do
            param cmd name (Some value)

        let! found = cmd.ExecuteScalarAsync()
        return not (isNull found)
    }

let private ownsPantry conn tx (userId: string) (pantryId: string) =
    exists conn tx "SELECT 1 FROM pantries WHERE id = @p::uuid AND user_id = @u" [ "p", pantryId; "u", userId ]

let private approvedIn conn tx (userId: string) (pantryId: string) =
    exists
        conn
        tx
        "SELECT 1 FROM pantry_members WHERE pantry_id = @p::uuid AND user_id = @u AND status = @s"
        [ "p", pantryId; "u", userId; "s", Pantry.Approved ]

/// The pantry a row already in Postgres belongs to; `None` when there is no
/// such row (a delete the client queued for a row that never made it up).
let private pantryOfRow (conn: NpgsqlConnection) tx (table: string) (id: string) : Task<string option> =
    task {
        let column = if table = "pantries" then "id" else "pantry_id"
        use cmd = new NpgsqlCommand($"SELECT {column} FROM {table} WHERE id = @id::uuid", conn, tx)
        param cmd "id" (Some id)
        let! found = cmd.ExecuteScalarAsync()

        return
            match found with
            | null -> None
            | value -> Some(string value)
    }

/// Whether the caller may apply `op`, in the transaction that will apply it.
///
/// The ordinary tables are one rule: the pantry the row goes into (for a PUT)
/// or already belongs to (for a PATCH or DELETE) has to be one the caller is
/// an approved member of. Membership is the only permission there is - a
/// member may change and delete anything in the pantry, including rows
/// another member made.
///
/// The two pantry tables are their own rules, because they are how permission
/// itself is handed out:
///
///   - a pantry is made by whoever is signed in, and only its owner renames
///     or deletes it (`user_id` is stamped, so "owner" means the caller);
///   - anyone may ask to join one, which is a `pending` member row for
///     themselves, and anyone may withdraw or leave by deleting their own;
///   - only the owner approves, by patching `status` - patching your own row
///     to `approved` is exactly what this stops.
let private decide (conn: NpgsqlConnection) tx (userId: string) (op: CrudOp) : Task<Decision> =
    task {
        let incomingPantry = op.Data.TryFind "pantry_id" |> Option.flatten

        match op.Op, op.Table with
        // Made by, and so owned by, whoever is signed in. An id that is
        // already someone else's pantry is caught by the upsert's own guard.
        | "PUT", "pantries" -> return Apply
        | "PUT", "pantry_members" ->
            match incomingPantry with
            | None -> return Skip "a member row with no pantry"
            | Some pantry ->
                // A membership already here stays in the pantry it is in.
                // Without this, a PUT of your own approved row with another
                // pantry's id on it would carry the approval across, which is
                // every permission there is.
                let! existing = pantryOfRow conn tx "pantry_members" op.Id

                if existing |> Option.exists (fun p -> p <> pantry) then
                    return Skip "a membership cannot change pantry"
                else
                    let! owner = ownsPantry conn tx userId pantry
                    let status = op.Data.TryFind "status" |> Option.flatten

                    // The same code scanned on a second device: that device's
                    // row is a duplicate of a membership already asked for,
                    // and the unique index would refuse it.
                    let! alreadyAsked =
                        exists
                            conn
                            tx
                            "SELECT 1 FROM pantry_members WHERE pantry_id = @p::uuid AND user_id = @u AND id <> @id::uuid"
                            [ "p", pantry; "u", userId; "id", op.Id ]

                    let! self =
                        exists
                            conn
                            tx
                            "SELECT 1 FROM pantry_members WHERE id = @id::uuid AND user_id = @u"
                            [ "id", op.Id; "u", userId ]

                    if alreadyAsked then
                        return Skip "already a member of that pantry"
                    elif owner then
                        return Apply
                    // Asking to join: a new row, for yourself, that says out
                    // loud that it is pending. Leaving the status out is not
                    // the same thing - on an upsert it would keep whatever the
                    // row holds - so it is refused.
                    elif existing.IsNone then
                        return (if status = Some Pantry.Pending then Apply else Skip "a membership starts out pending")
                    // Your own row again, from another device: the details are
                    // yours to write, the status is the owner's to give.
                    elif self && status <> Some Pantry.Approved then
                        return Apply
                    else
                        return Skip "only an owner approves a member"
        | "PUT", _ ->
            match incomingPantry with
            | None -> return Skip "a row with no pantry"
            | Some pantry ->
                let! approved = approvedIn conn tx userId pantry
                return (if approved then Apply else Skip "not a member of that pantry")
        | ("PATCH" | "DELETE"), table ->
            let! pantry = pantryOfRow conn tx table op.Id

            match pantry with
            // Nothing there to change: let the statement match no rows, the
            // way it did before, rather than jam the queue.
            | None -> return Apply
            | Some pantry ->
                // Rows don't move between pantries; the client never asks.
                if table <> "pantries" && incomingPantry |> Option.exists (fun p -> p <> pantry) then
                    return Skip "a row cannot change pantry"
                else
                    let! owner = ownsPantry conn tx userId pantry

                    match table with
                    | "pantries" -> return (if owner then Apply else Skip "only an owner changes a pantry")
                    | "pantry_members" ->
                        let! self =
                            exists
                                conn
                                tx
                                "SELECT 1 FROM pantry_members WHERE id = @id::uuid AND user_id = @u"
                                [ "id", op.Id; "u", userId ]

                        let touchesStatus = op.Op = "PATCH" && op.Data.ContainsKey "status"

                        if owner then return Apply
                        elif touchesStatus then return Skip "only an owner approves a member"
                        elif self then return Apply
                        else return Skip "not your membership to change"
                    | _ ->
                        let! approved = approvedIn conn tx userId pantry
                        return (if approved then Apply else Skip "not a member of that pantry")
        | other, _ -> return failwithf "Unknown op '%s'" other
    }

let private buildCommand (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: string) (op: CrudOp) =
    let columns =
        match Map.tryFind op.Table tables with
        | Some cols -> cols
        | None -> failwithf "Table '%s' is not writable" op.Table

    // Only columns present in the upload, in whitelist order.
    let present = columns |> List.filter (fun (c, _) -> op.Data.ContainsKey c)
    let cast (c, t) = $"@{c}::{t}"

    /// Who may overwrite a row that is already there, for the upsert a PUT of
    /// an id that exists becomes. `decide` has already vouched for the pantry
    /// the row is going into; this is about the pantry it is in now.
    let mayOverwrite =
        match op.Table with
        | "pantries" -> $"{op.Table}.user_id = @user_id"
        | "pantry_members" ->
            $"(EXISTS (SELECT 1 FROM pantries p WHERE p.id = {op.Table}.pantry_id AND p.user_id = @user_id) OR {op.Table}.user_id = @user_id)"
        | _ ->
            $"EXISTS (SELECT 1 FROM pantry_members m WHERE m.pantry_id = {op.Table}.pantry_id AND m.user_id = @user_id AND m.status = '{Pantry.Approved}')"

    let sql =
        match op.Op with
        | "PUT" ->
            let names = "id" :: "user_id" :: List.map fst present |> String.concat ", "
            let values = "@id::uuid" :: "@user_id" :: List.map cast present |> String.concat ", "

            let onConflict =
                match present with
                | [] -> "DO NOTHING"
                | _ ->
                    present
                    |> List.map (fun (c, _) -> $"{c} = EXCLUDED.{c}")
                    |> String.concat ", "
                    |> fun sets -> $"DO UPDATE SET {sets} WHERE {mayOverwrite}"

            $"INSERT INTO {op.Table} ({names}) VALUES ({values}) ON CONFLICT (id) {onConflict}"
        | "PATCH" ->
            match present with
            | [] -> "SELECT 1"
            | _ ->
                let sets = present |> List.map (fun (c, t) -> $"{c} = @{c}::{t}") |> String.concat ", "
                $"UPDATE {op.Table} SET {sets} WHERE id = @id::uuid"
        | "DELETE" -> $"DELETE FROM {op.Table} WHERE id = @id::uuid"
        | other -> failwithf "Unknown op '%s'" other

    let cmd = new NpgsqlCommand(sql, conn, tx)

    if op.Op <> "PATCH" || not present.IsEmpty then
        param cmd "id" (Some op.Id)
        param cmd "user_id" (Some userId)

    for (c, _) in present do
        param cmd c op.Data.[c]

    cmd

/// Applies a whole upload transaction atomically, so a failure leaves the
/// client's queue intact to retry. Each op is vouched for by `decide` first,
/// in the same transaction it would be applied in.
/// Applies what the caller is allowed to apply and returns exactly that: the
/// ops that reached the database. The usage counters are drawn from the
/// return value rather than from the request, so a write someone was not
/// allowed to make is not counted as one that happened.
let applyCrud (connectionString: string) (userId: string) (ops: CrudOp list) : Task<CrudOp list> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let applied = ResizeArray<CrudOp>()

        for op in ops do
            // The table name goes into SQL by name - `decide` looks the row's
            // pantry up, `buildCommand` writes it - so the whitelist is checked
            // before either of them sees it.
            if not (Map.containsKey op.Table tables) then
                failwithf "Table '%s' is not writable" op.Table

            match! decide conn tx userId op with
            | Skip reason -> eprintfn "upload: skipped %s %s %s (%s)" op.Op op.Table op.Id reason
            | Apply ->
                use cmd = buildCommand conn tx userId op
                let! _ = cmd.ExecuteNonQueryAsync()
                applied.Add op

        do! tx.CommitAsync()
        return List.ofSeq applied
    }

// ---------------------------------------------------------------------------
// Pantries
// ---------------------------------------------------------------------------

/// The caller's own pantry: the oldest one they own, made here if they have
/// none. Called before a device is handed sync credentials, so a new user's
/// first sync already carries the pantry everything they make will go into,
/// and is where the MCP tools put what they create.
let ensureOwnPantry (connectionString: string) (userId: string) (email: string) (name: string) : Task<Guid> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        use find =
            new NpgsqlCommand("SELECT id FROM pantries WHERE user_id = @u ORDER BY created_at, id LIMIT 1", conn, tx)

        param find "u" (Some userId)
        let! existing = find.ExecuteScalarAsync()

        let! id =
            task {
                match existing with
                | null ->
                    let id = Guid.NewGuid()

                    use insert =
                        new NpgsqlCommand("INSERT INTO pantries (id, user_id, name) VALUES (@id::uuid, @u, @n)", conn, tx)

                    param insert "id" (Some(string id))
                    param insert "u" (Some userId)
                    param insert "n" (Some Pantry.DefaultName)
                    let! _ = insert.ExecuteNonQueryAsync()
                    return id
                | value -> return (value :?> Guid)
            }

        // The owner is a member of their own pantry, and their email and name
        // are kept fresh here so the members list can name them.
        use member' =
            new NpgsqlCommand(
                "INSERT INTO pantry_members (id, pantry_id, user_id, email, name, status) VALUES (@mid::uuid, @p::uuid, @u, @e, @n, @s) "
                + "ON CONFLICT (pantry_id, user_id) DO UPDATE SET email = EXCLUDED.email, name = EXCLUDED.name, status = EXCLUDED.status",
                conn,
                tx
            )

        param member' "mid" (Some(string (Guid.NewGuid())))
        param member' "p" (Some(string id))
        param member' "u" (Some userId)
        param member' "e" (Some email)
        param member' "n" (Some name)
        param member' "s" (Some Pantry.Approved)
        let! _ = member'.ExecuteNonQueryAsync()

        do! tx.CommitAsync()
        return id
    }

// ---------------------------------------------------------------------------
// Direct queries (MCP tools). Writes land in Postgres and PowerSync pushes
// them to the user's devices like any other change.
//
// These are scoped to the caller (`WHERE user_id = @u`), not to a pantry: an
// assistant works on what its user made. What they create goes into the
// caller's own pantry, except for a row that hangs off another - an item on a
// list, a side under a menu entry - which goes wherever its parent is, so the
// two never come apart.
// ---------------------------------------------------------------------------

/// The pantry an MCP write lands in: the caller's own. Callers make sure
/// there is one (`ensureOwnPantry`) before inserting, since an account that
/// has only ever been reached through MCP has never synced a device.
let private ownPantry = "(SELECT id FROM pantries WHERE user_id = @u ORDER BY created_at, id LIMIT 1)"

type RecipeRow =
    { Id: Guid
      Title: string
      Body: string
      CreatedAt: DateTimeOffset }

type ShoppingListRow =
    { Id: Guid
      Name: string
      CreatedAt: DateTimeOffset }

type ShoppingItemRow =
    { Id: Guid
      ListId: Guid
      Name: string
      Quantity: string
      Unit: string
      Done: bool
      CreatedAt: DateTimeOffset }

let private withConn (connectionString: string) (work: NpgsqlConnection -> Task<'a>) : Task<'a> =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        return! work conn
    }

let private command (conn: NpgsqlConnection) (sql: string) (parameters: (string * obj) list) =
    let cmd = new NpgsqlCommand(sql, conn)

    for (name, value) in parameters do
        cmd.Parameters.AddWithValue(name, value) |> ignore

    cmd

let private readAll (cmd: NpgsqlCommand) (read: Data.Common.DbDataReader -> 'a) : Task<'a list> =
    task {
        use! reader = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while! reader.ReadAsync() do
            rows.Add(read reader)

        return List.ofSeq rows
    }

let private recipeOf (r: Data.Common.DbDataReader) =
    { Id = r.GetGuid 0
      Title = r.GetString 1
      Body = r.GetString 2
      CreatedAt = r.GetFieldValue<DateTimeOffset> 3 }

let private listOf (r: Data.Common.DbDataReader) : ShoppingListRow =
    { Id = r.GetGuid 0
      Name = r.GetString 1
      CreatedAt = r.GetFieldValue<DateTimeOffset> 2 }

let private itemOf (r: Data.Common.DbDataReader) : ShoppingItemRow =
    { Id = r.GetGuid 0
      ListId = r.GetGuid 1
      Name = r.GetString 2
      Quantity = r.GetString 3
      Unit = r.GetString 4
      Done = r.GetInt32 5 <> 0
      CreatedAt = r.GetFieldValue<DateTimeOffset> 6 }

let listRecipes cs (userId: string) =
    withConn cs (fun conn ->
        readAll
            (command conn "SELECT id, title, body, created_at FROM recipes WHERE user_id = @u ORDER BY created_at DESC" [ "u", userId ])
            recipeOf)

let getRecipe cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            let! rows =
                readAll
                    (command conn "SELECT id, title, body, created_at FROM recipes WHERE user_id = @u AND id = @id" [ "u", userId; "id", id ])
                    recipeOf

            return List.tryHead rows
        })

let insertRecipe cs (userId: string) (title: string) (body: string) =
    withConn cs (fun conn ->
        task {
            let id = Guid.NewGuid()

            use cmd =
                command
                    conn
                    $"INSERT INTO recipes (id, user_id, pantry_id, title, body) VALUES (@id, @u, {ownPantry}, @t, @b)"
                    [ "id", id; "u", userId; "t", title; "b", body ]

            let! _ = cmd.ExecuteNonQueryAsync()
            return id
        })

/// Returns false when the recipe isn't the user's (or doesn't exist).
let updateRecipe cs (userId: string) (id: Guid) (title: string option) (body: string option) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "UPDATE recipes SET title = COALESCE(@t::text, title), body = COALESCE(@b::text, body) WHERE id = @id AND user_id = @u"
                    [ "id", id
                      "u", userId
                      "t", (match title with Some t -> box t | None -> box DBNull.Value)
                      "b", (match body with Some b -> box b | None -> box DBNull.Value) ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

/// Also drops the recipe, and the sides under it, from any menu it was on,
/// and takes its tags off it (the tags themselves stay).
let deleteRecipe cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd = command conn "DELETE FROM recipes WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()

            if n = 1 then
                use tags =
                    command conn "DELETE FROM recipe_tags WHERE recipe_id = @id AND user_id = @u" [ "id", id; "u", userId ]

                let! _ = tags.ExecuteNonQueryAsync()

                use sides =
                    command
                        conn
                        "DELETE FROM menu_sides WHERE user_id = @u AND menu_recipe_id IN (SELECT id FROM menu_recipes WHERE recipe_id = @id AND user_id = @u)"
                        [ "id", id; "u", userId ]

                let! _ = sides.ExecuteNonQueryAsync()

                use entries =
                    command conn "DELETE FROM menu_recipes WHERE recipe_id = @id AND user_id = @u" [ "id", id; "u", userId ]

                let! _ = entries.ExecuteNonQueryAsync()
                ()

            return n = 1
        })

/// The user's newest un-archived list: the one adding goes into.
let latestShoppingList cs (userId: string) =
    withConn cs (fun conn ->
        task {
            let! rows =
                readAll
                    (command
                        conn
                        "SELECT id, name, created_at FROM shopping_lists WHERE user_id = @u AND archived_at IS NULL ORDER BY created_at DESC, id LIMIT 1"
                        [ "u", userId ])
                    listOf

            return List.tryHead rows
        })

let insertShoppingList cs (userId: string) (name: string) =
    withConn cs (fun conn ->
        task {
            let id = Guid.NewGuid()

            use cmd =
                command
                    conn
                    $"INSERT INTO shopping_lists (id, user_id, pantry_id, name) VALUES (@id, @u, {ownPantry}, @n)"
                    [ "id", id; "u", userId; "n", name ]

            let! _ = cmd.ExecuteNonQueryAsync()
            return id
        })

let listShoppingItems cs (userId: string) (listId: Guid) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT id, list_id, name, quantity, unit, done, created_at FROM shopping_items WHERE user_id = @u AND list_id = @l ORDER BY done, created_at"
                [ "u", userId; "l", listId ])
            itemOf)

let insertShoppingItems cs (userId: string) (listId: Guid) (items: (string * string * string) list) =
    withConn cs (fun conn ->
        task {
            use! tx = conn.BeginTransactionAsync()
            let ids = ResizeArray()

            for (name, quantity, unit) in items do
                let id = Guid.NewGuid()

                use cmd =
                    command
                        conn
                        "INSERT INTO shopping_items (id, user_id, pantry_id, list_id, name, quantity, unit) VALUES (@id, @u, (SELECT pantry_id FROM shopping_lists WHERE id = @l), @l, @n, @q, @unit)"
                        [ "id", id; "u", userId; "l", listId; "n", name; "q", quantity; "unit", unit ]

                cmd.Transaction <- tx
                let! _ = cmd.ExecuteNonQueryAsync()
                ids.Add id

            do! tx.CommitAsync()
            return List.ofSeq ids
        })

let setShoppingItemDone cs (userId: string) (id: Guid) (isDone: bool) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "UPDATE shopping_items SET done = @d WHERE id = @id AND user_id = @u"
                    [ "id", id; "u", userId; "d", (if isDone then 1 else 0) ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

let deleteShoppingItem cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd = command conn "DELETE FROM shopping_items WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

/// Puts the list away. Returns false when it isn't the user's.
let archiveShoppingList cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "UPDATE shopping_lists SET archived_at = now() WHERE id = @id AND user_id = @u AND archived_at IS NULL"
                    [ "id", id; "u", userId ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

// ---------------------------------------------------------------------------
// Menus
// ---------------------------------------------------------------------------

type MenuRow = { Id: Guid; Name: string; CreatedAt: DateTimeOffset }

/// A menu entry joined to its recipe, so callers get the title in one go.
type MenuRecipeRow =
    { Id: Guid
      RecipeId: Guid
      Title: string
      Body: string
      CreatedAt: DateTimeOffset }

type MenuSideRow =
    { Id: Guid
      MenuRecipeId: Guid
      Name: string
      Done: bool
      CreatedAt: DateTimeOffset }

let private menuOf (r: Data.Common.DbDataReader) : MenuRow =
    { Id = r.GetGuid 0
      Name = r.GetString 1
      CreatedAt = r.GetFieldValue<DateTimeOffset> 2 }

let private menuRecipeOf (r: Data.Common.DbDataReader) : MenuRecipeRow =
    { Id = r.GetGuid 0
      RecipeId = r.GetGuid 1
      Title = r.GetString 2
      Body = r.GetString 3
      CreatedAt = r.GetFieldValue<DateTimeOffset> 4 }

let private sideOf (r: Data.Common.DbDataReader) : MenuSideRow =
    { Id = r.GetGuid 0
      MenuRecipeId = r.GetGuid 1
      Name = r.GetString 2
      Done = r.GetInt32 3 <> 0
      CreatedAt = r.GetFieldValue<DateTimeOffset> 4 }

/// The user's newest un-archived menu: the one adding goes into.
let latestMenu cs (userId: string) =
    withConn cs (fun conn ->
        task {
            let! rows =
                readAll
                    (command
                        conn
                        "SELECT id, name, created_at FROM menus WHERE user_id = @u AND archived_at IS NULL ORDER BY created_at DESC, id LIMIT 1"
                        [ "u", userId ])
                    menuOf

            return List.tryHead rows
        })

let insertMenu cs (userId: string) (name: string) =
    withConn cs (fun conn ->
        task {
            let id = Guid.NewGuid()

            use cmd =
                command conn $"INSERT INTO menus (id, user_id, pantry_id, name) VALUES (@id, @u, {ownPantry}, @n)" [ "id", id; "u", userId; "n", name ]

            let! _ = cmd.ExecuteNonQueryAsync()
            return id
        })

let archiveMenu cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "UPDATE menus SET archived_at = now() WHERE id = @id AND user_id = @u AND archived_at IS NULL"
                    [ "id", id; "u", userId ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

/// A menu's entries in the order added. Entries whose recipe is gone are
/// left out, as the UI does.
let listMenuRecipes cs (userId: string) (menuId: Guid) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT e.id, e.recipe_id, r.title, r.body, e.created_at FROM menu_recipes e JOIN recipes r ON r.id = e.recipe_id AND r.user_id = e.user_id WHERE e.user_id = @u AND e.menu_id = @m ORDER BY e.created_at, e.id"
                [ "u", userId; "m", menuId ])
            menuRecipeOf)

/// Adds the recipe to the menu; `None` when the recipe isn't the user's.
let insertMenuRecipe cs (userId: string) (menuId: Guid) (recipeId: Guid) =
    withConn cs (fun conn ->
        task {
            let id = Guid.NewGuid()

            use cmd =
                command
                    conn
                    "INSERT INTO menu_recipes (id, user_id, pantry_id, menu_id, recipe_id) SELECT @id, @u, m.pantry_id, @m, r.id FROM menus m JOIN recipes r ON r.pantry_id = m.pantry_id WHERE m.id = @m AND r.id = @r AND r.user_id = @u"
                    [ "id", id; "u", userId; "m", menuId; "r", recipeId ]

            let! n = cmd.ExecuteNonQueryAsync()
            return (if n = 1 then Some id else None)
        })

/// Takes the entry, and its sides, off the menu.
let deleteMenuRecipe cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use sides =
                command conn "DELETE FROM menu_sides WHERE menu_recipe_id = @id AND user_id = @u" [ "id", id; "u", userId ]

            let! _ = sides.ExecuteNonQueryAsync()
            use cmd = command conn "DELETE FROM menu_recipes WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

/// Every side on a menu, in the order added.
let listMenuSides cs (userId: string) (menuId: Guid) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT s.id, s.menu_recipe_id, s.name, s.done, s.created_at FROM menu_sides s JOIN menu_recipes e ON e.id = s.menu_recipe_id WHERE s.user_id = @u AND e.menu_id = @m ORDER BY s.created_at, s.id"
                [ "u", userId; "m", menuId ])
            sideOf)

/// Adds sides under a menu entry; `None` when the entry isn't the user's.
let insertMenuSides cs (userId: string) (menuRecipeId: Guid) (names: string list) =
    withConn cs (fun conn ->
        task {
            use owns =
                command conn "SELECT 1 FROM menu_recipes WHERE id = @id AND user_id = @u" [ "id", menuRecipeId; "u", userId ]

            let! found = owns.ExecuteScalarAsync()

            if isNull found then
                return None
            else
                use! tx = conn.BeginTransactionAsync()
                let ids = ResizeArray()

                for name in names do
                    let id = Guid.NewGuid()

                    use cmd =
                        command
                            conn
                            "INSERT INTO menu_sides (id, user_id, pantry_id, menu_recipe_id, name) VALUES (@id, @u, (SELECT pantry_id FROM menu_recipes WHERE id = @e), @e, @n)"
                            [ "id", id; "u", userId; "e", menuRecipeId; "n", name ]

                    cmd.Transaction <- tx
                    let! _ = cmd.ExecuteNonQueryAsync()
                    ids.Add id

                do! tx.CommitAsync()
                return Some(List.ofSeq ids)
        })

let setMenuSideDone cs (userId: string) (id: Guid) (isDone: bool) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "UPDATE menu_sides SET done = @d WHERE id = @id AND user_id = @u"
                    [ "id", id; "u", userId; "d", (if isDone then 1 else 0) ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

let deleteMenuSide cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd = command conn "DELETE FROM menu_sides WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

// ---------------------------------------------------------------------------
// Tags
// ---------------------------------------------------------------------------

/// A tag with how many recipes carry it.
type TagRow =
    { Id: Guid
      Name: string
      Recipes: int
      CreatedAt: DateTimeOffset }

/// What a rename did. `Taken` is how two tags are kept from going by one
/// name, which is all the app would treat them as (`Shared.Tag.key`).
type RenameResult =
    | Renamed
    | TagNotFound
    | NameTaken of string

let private tagOf (r: Data.Common.DbDataReader) : TagRow =
    { Id = r.GetGuid 0
      Name = r.GetString 1
      Recipes = r.GetInt32 2
      CreatedAt = r.GetFieldValue<DateTimeOffset> 3 }

/// Tags are matched by name the way the client matches them, ignoring case
/// and surrounding space, and shown in that same order.
let listTags cs (userId: string) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT t.id, t.name, (SELECT count(*) FROM recipe_tags rt WHERE rt.tag_id = t.id)::int, t.created_at FROM tags t WHERE t.user_id = @u ORDER BY lower(t.name), t.id"
                [ "u", userId ])
            tagOf)

/// Every tag on every recipe, as (recipe id, tag name).
let listRecipeTags cs (userId: string) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT rt.recipe_id, t.name FROM recipe_tags rt JOIN tags t ON t.id = rt.tag_id WHERE rt.user_id = @u ORDER BY lower(t.name), t.id"
                [ "u", userId ])
            (fun r -> r.GetGuid 0, r.GetString 1))

/// One recipe's tags, in the order they are shown.
let tagsOfRecipe cs (userId: string) (recipeId: Guid) =
    withConn cs (fun conn ->
        readAll
            (command
                conn
                "SELECT t.name FROM recipe_tags rt JOIN tags t ON t.id = rt.tag_id WHERE rt.user_id = @u AND rt.recipe_id = @r ORDER BY lower(t.name), t.id"
                [ "u", userId; "r", recipeId ])
            (fun r -> r.GetString 0))

/// Puts tags on a recipe, making a tag of any name that isn't one yet - the
/// same thing typing a name into the Tags tab does. A name the recipe
/// already carries is left alone. False when the recipe isn't the user's.
let insertRecipeTags cs (userId: string) (recipeId: Guid) (names: string list) =
    withConn cs (fun conn ->
        task {
            use owns = command conn "SELECT 1 FROM recipes WHERE id = @id AND user_id = @u" [ "id", recipeId; "u", userId ]
            let! found = owns.ExecuteScalarAsync()

            if isNull found then
                return false
            else
                use! tx = conn.BeginTransactionAsync()

                for name in names do
                    let! tagId =
                        task {
                            use find =
                                command
                                    conn
                                    // In the recipe's pantry, which is where
                                    // the link below goes: a tag from another
                                    // pantry would be a link to nothing the
                                    // page can show.
                                    "SELECT id FROM tags WHERE pantry_id = (SELECT pantry_id FROM recipes WHERE id = @r) AND lower(name) = lower(@n)"
                                    [ "u", userId; "n", name; "r", recipeId ]

                            find.Transaction <- tx
                            let! existing = find.ExecuteScalarAsync()

                            match existing with
                            | null ->
                                let id = Guid.NewGuid()

                                use insert =
                                    command
                                        conn
                                        "INSERT INTO tags (id, user_id, pantry_id, name) VALUES (@id, @u, (SELECT pantry_id FROM recipes WHERE id = @r), @n)"
                                        [ "id", id; "u", userId; "n", name; "r", recipeId ]

                                insert.Transaction <- tx
                                let! _ = insert.ExecuteNonQueryAsync()
                                return id
                            | id -> return (id :?> Guid)
                        }

                    use link =
                        command
                            conn
                            "INSERT INTO recipe_tags (id, user_id, pantry_id, recipe_id, tag_id) SELECT @id, @u, (SELECT pantry_id FROM recipes WHERE id = @r), @r, @t WHERE NOT EXISTS (SELECT 1 FROM recipe_tags WHERE recipe_id = @r AND tag_id = @t)"
                            [ "id", Guid.NewGuid(); "u", userId; "r", recipeId; "t", tagId ]

                    link.Transaction <- tx
                    let! _ = link.ExecuteNonQueryAsync()
                    ()

                do! tx.CommitAsync()
                return true
        })

/// Takes a tag off one recipe by name; the tag itself stays. False when the
/// recipe doesn't carry it.
let deleteRecipeTag cs (userId: string) (recipeId: Guid) (name: string) =
    withConn cs (fun conn ->
        task {
            use cmd =
                command
                    conn
                    "DELETE FROM recipe_tags rt USING tags t WHERE t.id = rt.tag_id AND rt.user_id = @u AND rt.recipe_id = @r AND lower(t.name) = lower(@n)"
                    [ "u", userId; "r", recipeId; "n", name ]

            let! n = cmd.ExecuteNonQueryAsync()
            return n > 0
        })

/// Renames the tag everywhere at once: recipes carry its id, not its name.
let renameTag cs (userId: string) (id: Guid) (name: string) =
    withConn cs (fun conn ->
        task {
            use clash =
                command
                    conn
                    "SELECT name FROM tags WHERE user_id = @u AND lower(name) = lower(@n) AND id <> @id"
                    [ "u", userId; "n", name; "id", id ]

            let! other = clash.ExecuteScalarAsync()

            match other with
            | null ->
                use cmd =
                    command conn "UPDATE tags SET name = @n WHERE id = @id AND user_id = @u" [ "n", name; "id", id; "u", userId ]

                let! n = cmd.ExecuteNonQueryAsync()
                return (if n = 1 then Renamed else TagNotFound)
            | taken -> return NameTaken(string taken)
        })

/// Deletes the tag and takes it off every recipe.
let deleteTag cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use links = command conn "DELETE FROM recipe_tags WHERE tag_id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! _ = links.ExecuteNonQueryAsync()
            use cmd = command conn "DELETE FROM tags WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })
