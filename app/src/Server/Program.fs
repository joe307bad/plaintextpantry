module Server.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe
open ModelContextProtocol.Protocol

[<EntryPoint>]
let main args =
    let config = Config.load ()

    let builder = WebApplication.CreateBuilder(args)
    builder.Services.AddSingleton config |> ignore
    builder.Services.AddGiraffe() |> ignore
    builder.Services.AddHttpContextAccessor() |> ignore
    Auth.configure builder.Services config

    builder.Services
        .AddMcpServer(fun o -> o.ServerInfo <- Implementation(Name = "plaintextpantry", Version = "1.0.0"))
        .WithHttpTransport(fun o -> o.Stateless <- true)
        .WithTools<Mcp.PantryTools>()
    |> ignore

    let app = builder.Build()
    app.Urls.Add config.ListenUrl

    app.UseGiraffeErrorHandler(fun ex logger ->
        logger.LogError(ex, "Unhandled request error")
        ServerErrors.INTERNAL_ERROR ex.Message)
    |> ignore

    app.UseAuthentication() |> ignore
    app.UseAuthorization() |> ignore

    // Endpoint-routed; Giraffe below passes unmatched paths through to it.
    app.MapMcp("/mcp").RequireAuthorization(Auth.mcpScheme) |> ignore

    app.UseGiraffe(Api.handler config)
    app.Run()
    0
