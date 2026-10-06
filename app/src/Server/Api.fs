module Server.Api

open System
open System.IO
open Giraffe
open Thoth.Json.Core
open Thoth.Json.System.Text.Json
open Shared
open Server.Config

let private json (value: IEncodable) : HttpHandler =
    fun _ ctx ->
        ctx.SetContentType "application/json; charset=utf-8"
        ctx.WriteStringAsync(Encode.toString 0 value)

/// A PowerSync token for the signed-in user. Its `sub` is what the sync
/// rules filter on (auth.user_id()), so each device only ever downloads the
/// pantries that user belongs to.
///
/// Also the one place that makes sure they have a pantry of their own: a
/// device about to sync needs something to put what it makes into, and this
/// runs before every connection, so an account that somehow has none (a
/// first sign-in, a pantry deleted) gets one back.
let private syncCredentials (config: Config) : HttpHandler =
    fun next ctx ->
        task {
            let user = (Auth.currentUser ctx).Value
            let! _ = Db.ensureOwnPantry config.ConnectionString user.Id user.Email user.Name

            let token =
                Jwt.create config.JwtSecret "powersync-dev" config.JwtAudience user.Id (TimeSpan.FromHours 1.)

            return! json (Codec.encodeCredentials { Endpoint = config.PowerSyncUrl; Token = token }) next ctx
        }

/// What an upload changed, for the usage counters: one entry per table and
/// kind of change, with how many rows there were of it. Counted from what the
/// database took, so a recipe saved twice is two writes and a write that was
/// refused is none.
let private counted (ops: CrudOp list) =
    let action (op: CrudOp) =
        match op.Op with
        | "PUT" -> "create"
        // The one update worth telling from the rest: approving a member is
        // the moment a pantry is actually shared with somebody, as against
        // the `create` above, which is only somebody asking.
        | "PATCH" when op.Table = "pantry_members" && op.Data.TryFind "status" = Some(Some Pantry.Approved) -> "approve"
        | "PATCH" -> "update"
        | _ -> "delete"

    ops
    |> List.countBy (fun op -> op.Table, action op)
    |> List.map (fun ((table, action), count) -> table, action, count)

let private upload (config: Config) : HttpHandler =
    fun next ctx ->
        task {
            let user = (Auth.currentUser ctx).Value
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            match Decode.fromString Codec.decodeCrudOps body with
            | Error err -> return! RequestErrors.BAD_REQUEST err next ctx
            | Ok ops ->
                let! applied = Db.applyCrud config.ConnectionString user ops
                Server.Usage.changes config "app" (counted applied)
                return! Successful.NO_CONTENT next ctx
        }

/// One page visit, from the browser that opened it.
///
/// No session required, and it cannot have one: the login page is the page
/// nobody signed in ever sees. The body is a section name, checked against
/// the fixed list in `Shared.Usage`, so the most anyone can do by posting here
/// all day is inflate one of eight counters - not write new rows, new columns
/// or new tables into a database this app shares with two others.
let private pageview (config: Config) : HttpHandler =
    fun next ctx ->
        task {
            // The longest section name is well under this. Reading the body
            // to its end would let an unauthenticated request decide how much
            // memory to spend here; reading a mouthful cannot.
            let buffer = Array.zeroCreate<byte> 64
            let! read = ctx.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream = false)
            let section = Text.Encoding.UTF8.GetString(buffer, 0, read).Trim()

            if Shared.Usage.isSection section then
                Server.Usage.pageview config section
                return! Successful.NO_CONTENT next ctx
            else
                return! RequestErrors.BAD_REQUEST "Unknown section" next ctx
        }

/// Falls through (None) for anything it doesn't handle, so endpoint routing
/// (the MCP endpoint) gets a turn.
let handler (config: Config) : HttpHandler =
    choose
        [ GET >=> route Route.login >=> Auth.login config
          GET >=> route Route.logout >=> Auth.logout config
          GET >=> route Route.me >=> Auth.me
          GET >=> route Route.syncCredentials >=> Auth.requireUser >=> syncCredentials config
          POST >=> route Route.upload >=> Auth.requireUser >=> upload config
          POST >=> route Route.pageview >=> pageview config
          subRoute "/api" (RequestErrors.NOT_FOUND "Not found") ]
