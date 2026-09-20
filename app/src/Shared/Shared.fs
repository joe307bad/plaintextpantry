namespace Shared

open System
open Thoth.Json.Core

/// What the PowerSync client needs to open a sync connection.
type SyncCredentials = { Endpoint: string; Token: string }

/// The signed-in user, as /api/auth/me reports it. `Id` is the Keycloak
/// subject and is what every row's `user_id` holds.
type User = { Id: string; Email: string; Name: string }

/// A shopping list is only a name for now: the client and the MCP tools
/// both add to the newest one, and create one named after today when the
/// user has none. Names are free text and may repeat.
module ShoppingList =
    let defaultName (date: DateTime) = sprintf "SL-%02d%02d" date.Month date.Day

    /// How long ago a list was made, in calendar days: "today", "yesterday",
    /// "3 days ago". Both dates are compared as local dates.
    let age (today: DateTime) (created: DateTime) =
        match (today.Date - created.Date).Days with
        | d when d <= 0 -> "today"
        | 1 -> "yesterday"
        | d -> $"{d} days ago"

/// One entry from the PowerSync client upload queue, in the shape the
/// server applies to Postgres. Values are strings (or null); Postgres
/// casts them to the real column types.
type CrudOp =
    { Op: string // PUT | PATCH | DELETE
      Table: string
      Id: string
      Data: Map<string, string option> }

/// Wire format, written once here and used by both client and server.
/// Explicit coders: no reflection, so nothing to break across Fable versions.
module Codec =
    let encodeCredentials (c: SyncCredentials) =
        Encode.object [ "endpoint", Encode.string c.Endpoint; "token", Encode.string c.Token ]

    let decodeCredentials: Decoder<SyncCredentials> =
        Decode.object (fun get ->
            { Endpoint = get.Required.Field "endpoint" Decode.string
              Token = get.Required.Field "token" Decode.string })

    let encodeUser (u: User) =
        Encode.object [ "id", Encode.string u.Id; "email", Encode.string u.Email; "name", Encode.string u.Name ]

    let decodeUser: Decoder<User> =
        Decode.object (fun get ->
            { Id = get.Required.Field "id" Decode.string
              Email = get.Required.Field "email" Decode.string
              Name = get.Required.Field "name" Decode.string })

    let encodeCrudOp (op: CrudOp) =
        Encode.object
            [ "op", Encode.string op.Op
              "table", Encode.string op.Table
              "id", Encode.string op.Id
              "data",
              op.Data
              |> Map.toList
              |> List.map (fun (k, v) -> k, (match v with Some s -> Encode.string s | None -> Encode.nil))
              |> Encode.object ]

    let decodeCrudOp: Decoder<CrudOp> =
        Decode.object (fun get ->
            { Op = get.Required.Field "op" Decode.string
              Table = get.Required.Field "table" Decode.string
              Id = get.Required.Field "id" Decode.string
              Data =
                get.Required.Field "data" (Decode.dict (Decode.lossyOption Decode.string)) })

    let encodeCrudOps (ops: CrudOp list) = Encode.list (List.map encodeCrudOp ops)
    let decodeCrudOps: Decoder<CrudOp list> = Decode.list decodeCrudOp

/// HTTP routes served by the F# server.
module Route =
    let syncCredentials = "/api/sync/credentials"
    let upload = "/api/sync/upload"
    /// Browser navigations (not fetches): start the OIDC login / end the session.
    let login = "/api/auth/login"
    let logout = "/api/auth/logout"
    /// 200 + User when signed in, 401 otherwise.
    let me = "/api/auth/me"
