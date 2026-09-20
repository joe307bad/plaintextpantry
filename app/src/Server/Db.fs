/// Postgres access. Everything is scoped to one user: PowerSync uploads stamp
/// the caller's id on inserts and refuse to touch rows they don't own, and
/// the MCP tools' queries all carry `WHERE user_id = @user`.
module Server.Db

open System
open System.Threading.Tasks
open Npgsql
open Shared

/// Tables the client may write to, with the Postgres type of each writable
/// column. Anything not listed here is rejected. `id` is always a uuid and
/// `user_id` is never taken from the client.
let private tables =
    Map
        [ "recipes", [ "title", "text"; "body", "text"; "created_at", "timestamptz" ]
          "shopping_lists", [ "name", "text"; "created_at", "timestamptz" ]
          "shopping_items",
          [ "list_id", "uuid"
            "name", "text"
            "quantity", "text"
            "unit", "text"
            "done", "integer"
            "created_at", "timestamptz" ] ]

let private param (cmd: NpgsqlCommand) (name: string) (value: string option) =
    let v : obj =
        match value with
        | Some s -> box s
        | None -> box DBNull.Value
    cmd.Parameters.AddWithValue(name, v) |> ignore

// ---------------------------------------------------------------------------
// PowerSync upload queue
// ---------------------------------------------------------------------------

let private buildCommand (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: string) (op: CrudOp) =
    let columns =
        match Map.tryFind op.Table tables with
        | Some cols -> cols
        | None -> failwithf "Table '%s' is not writable" op.Table

    // Only columns present in the upload, in whitelist order.
    let present = columns |> List.filter (fun (c, _) -> op.Data.ContainsKey c)
    let cast (c, t) = $"@{c}::{t}"

    let sql =
        match op.Op with
        | "PUT" ->
            let names = "id" :: "user_id" :: List.map fst present |> String.concat ", "
            let values = "@id::uuid" :: "@user_id" :: List.map cast present |> String.concat ", "

            // The WHERE on the upsert keeps a colliding id from letting one
            // user overwrite another's row.
            let onConflict =
                match present with
                | [] -> "DO NOTHING"
                | _ ->
                    present
                    |> List.map (fun (c, _) -> $"{c} = EXCLUDED.{c}")
                    |> String.concat ", "
                    |> fun sets -> $"DO UPDATE SET {sets} WHERE {op.Table}.user_id = @user_id"

            $"INSERT INTO {op.Table} ({names}) VALUES ({values}) ON CONFLICT (id) {onConflict}"
        | "PATCH" ->
            match present with
            | [] -> "SELECT 1"
            | _ ->
                let sets = present |> List.map (fun (c, t) -> $"{c} = @{c}::{t}") |> String.concat ", "
                $"UPDATE {op.Table} SET {sets} WHERE id = @id::uuid AND user_id = @user_id"
        | "DELETE" -> $"DELETE FROM {op.Table} WHERE id = @id::uuid AND user_id = @user_id"
        | other -> failwithf "Unknown op '%s'" other

    let cmd = new NpgsqlCommand(sql, conn, tx)

    if op.Op <> "PATCH" || not present.IsEmpty then
        param cmd "id" (Some op.Id)
        param cmd "user_id" (Some userId)

    for (c, _) in present do
        param cmd c op.Data.[c]

    cmd

/// Applies a whole upload transaction atomically, so a failure leaves the
/// client's queue intact to retry.
let applyCrud (connectionString: string) (userId: string) (ops: CrudOp list) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        for op in ops do
            use cmd = buildCommand conn tx userId op
            let! _ = cmd.ExecuteNonQueryAsync()
            ()

        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Direct queries (MCP tools). Writes land in Postgres and PowerSync pushes
// them to the user's devices like any other change.
// ---------------------------------------------------------------------------

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
                    "INSERT INTO recipes (id, user_id, title, body) VALUES (@id, @u, @t, @b)"
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

let deleteRecipe cs (userId: string) (id: Guid) =
    withConn cs (fun conn ->
        task {
            use cmd = command conn "DELETE FROM recipes WHERE id = @id AND user_id = @u" [ "id", id; "u", userId ]
            let! n = cmd.ExecuteNonQueryAsync()
            return n = 1
        })

/// The user's newest list: the one adding goes into.
let latestShoppingList cs (userId: string) =
    withConn cs (fun conn ->
        task {
            let! rows =
                readAll
                    (command
                        conn
                        "SELECT id, name, created_at FROM shopping_lists WHERE user_id = @u ORDER BY created_at DESC, id LIMIT 1"
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
                    "INSERT INTO shopping_lists (id, user_id, name) VALUES (@id, @u, @n)"
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
                        "INSERT INTO shopping_items (id, user_id, list_id, name, quantity, unit) VALUES (@id, @u, @l, @n, @q, @unit)"
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
