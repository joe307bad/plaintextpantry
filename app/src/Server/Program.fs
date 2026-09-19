module Server.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe

[<EntryPoint>]
let main args =
    let config = Config.load ()

    let api = Api.handler config

    let builder = WebApplication.CreateBuilder(args)
    builder.Services.AddGiraffe() |> ignore

    let app = builder.Build()
    app.Urls.Add "http://localhost:5050"
    app.UseGiraffeErrorHandler(fun ex logger ->
        logger.LogError(ex, "Unhandled request error")
        ServerErrors.INTERNAL_ERROR ex.Message)
    |> ignore

    app.UseGiraffe(choose [ api; RequestErrors.NOT_FOUND "Not found" ])
    app.Run()
    0
