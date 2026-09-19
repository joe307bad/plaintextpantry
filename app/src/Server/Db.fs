/// Applies PowerSync CRUD upload entries to Postgres.
module Server.Db

open System
open System.Threading.Tasks
open Npgsql
open Shared

/// Tables the client may write to, with the Postgres type of each writable
/// column. Anything not listed here is rejected. `id` is always a uuid.
let private tables =
    Map [ "recipes", [ "title", "text"; "created_at", "timestamptz" ] ]

let private param (cmd: NpgsqlCommand) (name: string) (value: string option) =
    let v : obj =
        match value with
        | Some s -> box s
        | None -> box DBNull.Value
    cmd.Parameters.AddWithValue(name, v) |> ignore

let private buildCommand (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (op: CrudOp) =
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
            let names = "id" :: List.map fst present |> String.concat ", "
            let values = "@id::uuid" :: List.map cast present |> String.concat ", "

            let onConflict =
                match present with
                | [] -> "DO NOTHING"
                | _ ->
                    present
                    |> List.map (fun (c, _) -> $"{c} = EXCLUDED.{c}")
                    |> String.concat ", "
                    |> sprintf "DO UPDATE SET %s"

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

    for (c, _) in present do
        param cmd c op.Data.[c]

    cmd

/// Applies a whole upload transaction atomically, so a failure leaves the
/// client's queue intact to retry.
let applyCrud (connectionString: string) (ops: CrudOp list) : Task =
    task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        for op in ops do
            use cmd = buildCommand conn tx op
            let! _ = cmd.ExecuteNonQueryAsync()
            ()

        do! tx.CommitAsync()
    }
