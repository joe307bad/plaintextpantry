namespace Shared

open System
open Thoth.Json.Core

/// What the PowerSync client needs to open a sync connection.
type SyncCredentials = { Endpoint: string; Token: string }

/// The signed-in user, as /api/auth/me reports it. `Id` is the Keycloak
/// subject and is what every row's `user_id` holds.
type User = { Id: string; Email: string; Name: string }

/// How long ago something was made, in calendar days: "today", "yesterday",
/// "3 days ago". Both dates are compared as local dates.
module Created =
    let age (today: DateTime) (created: DateTime) =
        match (today.Date - created.Date).Days with
        | d when d <= 0 -> "today"
        | 1 -> "yesterday"
        | d -> $"{d} days ago"

/// A shopping list is only a name for now: the client and the MCP tools
/// both add to the newest one, and create one named after today when the
/// user has none. Names are free text and may repeat.
module ShoppingList =
    let defaultName (date: DateTime) = sprintf "SL-%02d%02d" date.Month date.Day

/// A shopping-list row is shown as one line, "2 cups flour", but stored as
/// quantity, unit and name so a recipe's ingredients can be matched by name.
module ShoppingItem =
    let text (quantity: string) (unit: string) (name: string) =
        [ quantity; unit; name ] |> List.filter ((<>) "") |> String.concat " "

    /// The parts an edit of that line stands for. When the "2 cups " is left
    /// alone the quantity and unit stay and only the name changes; when it
    /// is touched the whole line becomes the name, like an item typed in.
    let edit (quantity: string) (unit: string) (edited: string) =
        let edited = edited.Trim()
        let prefix = text quantity unit ""

        if prefix <> "" && edited.StartsWith(prefix + " ") then
            quantity, unit, edited.Substring(prefix.Length).Trim()
        else
            "", "", edited

/// A menu is a shopping list's twin for recipes: a name, and the recipes
/// added to it. Adding goes into the newest one, created on first use and
/// named after today plus two random words ("M-0920-silly-fiddle") so two
/// menus from the same day can be told apart; names may still repeat.
module Menu =
    let adjectives =
        [| "silly"; "big"; "wobbly"; "sleepy"; "fancy"; "grumpy"; "tiny"; "jolly"; "snazzy"; "bouncy"
           "crispy"; "soggy"; "spicy"; "zesty"; "chunky"; "fluffy"; "toasty"; "peppy"; "dizzy"; "cheeky"
           "mellow"; "nifty"; "plucky"; "quirky"; "rusty"; "shiny"; "smoky"; "sunny"; "tangy"; "wonky" |]

    let nouns =
        [| "fiddle"; "bongo"; "pickle"; "waffle"; "noodle"; "biscuit"; "teapot"; "walrus"; "muffin"; "kazoo"
           "turnip"; "dumpling"; "pretzel"; "gumbo"; "radish"; "goblin"; "banjo"; "yeti"; "otter"; "llama"
           "pancake"; "taco"; "crumpet"; "nugget"; "spatula"; "ladle"; "kettle"; "pudding"; "scone"; "tuba" |]

    let defaultName (date: DateTime) (random: Random) =
        let pick (words: string[]) = words.[random.Next words.Length]
        sprintf "M-%02d%02d-%s-%s" date.Month date.Day (pick adjectives) (pick nouns)

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
