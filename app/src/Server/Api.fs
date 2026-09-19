module Server.Api

open System
open System.IO
open Microsoft.AspNetCore.Http
open Giraffe
open Thoth.Json.Core
open Thoth.Json.System.Text.Json
open Shared
open Server.Config

let private json (value: IEncodable) : HttpHandler =
    fun next ctx ->
        ctx.SetContentType "application/json; charset=utf-8"
        ctx.WriteStringAsync(Encode.toString 0 value)

let private syncCredentials (config: Config) : HttpHandler =
    fun next ctx ->
        let token =
            Jwt.create config.JwtSecret "powersync-dev" config.JwtAudience config.UserId (TimeSpan.FromHours 1.)

        json (Codec.encodeCredentials { Endpoint = config.PowerSyncUrl; Token = token }) next ctx

let private upload (config: Config) : HttpHandler =
    fun next ctx ->
        task {
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            match Decode.fromString Codec.decodeCrudOps body with
            | Error err -> return! RequestErrors.BAD_REQUEST err next ctx
            | Ok ops ->
                do! Db.applyCrud config.ConnectionString ops
                return! Successful.NO_CONTENT next ctx
        }

let handler (config: Config) : HttpHandler =
    choose
        [ GET >=> route Route.syncCredentials >=> syncCredentials config
          POST >=> route Route.upload >=> upload config ]
