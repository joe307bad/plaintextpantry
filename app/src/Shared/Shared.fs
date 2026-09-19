namespace Shared

open Thoth.Json.Core

/// What the PowerSync client needs to open a sync connection.
type SyncCredentials = { Endpoint: string; Token: string }

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
