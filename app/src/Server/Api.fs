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
/// rules filter on (auth.user_id()), so each device only ever downloads its
/// owner's rows.
let private syncCredentials (config: Config) : HttpHandler =
    fun next ctx ->
        let user = (Auth.currentUser ctx).Value

        let token =
            Jwt.create config.JwtSecret "powersync-dev" config.JwtAudience user.Id (TimeSpan.FromHours 1.)

        json (Codec.encodeCredentials { Endpoint = config.PowerSyncUrl; Token = token }) next ctx

let private upload (config: Config) : HttpHandler =
    fun next ctx ->
        task {
            let user = (Auth.currentUser ctx).Value
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            match Decode.fromString Codec.decodeCrudOps body with
            | Error err -> return! RequestErrors.BAD_REQUEST err next ctx
            | Ok ops ->
                do! Db.applyCrud config.ConnectionString user.Id ops
                return! Successful.NO_CONTENT next ctx
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
          subRoute "/api" (RequestErrors.NOT_FOUND "Not found") ]
