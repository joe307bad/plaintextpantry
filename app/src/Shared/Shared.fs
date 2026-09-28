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

/// The grey note beside a row, saying what there is to say about where it
/// came from: who put it there, which recipes a shopping-list item is called
/// for by, whether a menu entry's ingredients are on the list. One
/// parenthesis, however many of those there are to fill it - "(Added by Joe,
/// from Focaccia, Pizza)", "(Added by Joe, in shopping list)".
module Note =
    /// "Added by Joe", and nothing at all when there is nobody to name: a row
    /// made before rows remembered who made them, or one made by someone
    /// whose membership of the pantry has not synced yet.
    let addedBy (who: string option) =
        match who with
        | Some who when who.Trim() <> "" -> $"Added by {who.Trim()}"
        | _ -> ""

    /// "from Focaccia, Pizza", and nothing at all when no recipe calls for it.
    let from (recipes: string list) =
        match recipes with
        | [] -> ""
        | titles -> "from " + String.concat ", " titles

    /// Whichever of `parts` there are, in one parenthesis. The blank ones are
    /// dropped, so a caller lists everything a row could say and gets only
    /// what it does say; nothing at all when it says nothing.
    let text (parts: string list) =
        match parts |> List.filter (fun part -> part <> "") with
        | [] -> ""
        | parts -> "(" + String.concat ", " parts + ")"

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

    /// The recipes an item comes from: the ones whose Cooklang names an
    /// ingredient by that name, matched the way adding merges rows
    /// (case-insensitive, trimmed). `recipes` is each title with the
    /// ingredient names its body parses to. Nothing is stored, so taking
    /// `@flour{}` out of a recipe leaves the item on the list and only drops
    /// the recipe from it, and an item typed by hand picks a recipe up as
    /// soon as one calls for it.
    let sources (recipes: (string * string list) list) (name: string) =
        let key (s: string) = s.Trim().ToLowerInvariant()
        let name = key name

        if name = "" then
            []
        else
            recipes
            |> List.filter (fun (_, ingredients) -> ingredients |> List.exists (fun i -> key i = name))
            |> List.map fst
            |> List.distinct

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

/// A tag is a name a user gives recipes ("quick", "Kaitlyn's fav"), stored
/// once and joined to each recipe it is on. Names are free text, kept as
/// typed, but two that differ only in case or surrounding space are the same
/// tag: typing "Quick" on a second recipe puts it on the existing "quick".
module Tag =
    /// The name as it is stored: as typed, without the surrounding space.
    let clean (name: string) = name.Trim()

    /// What two names are compared by to decide they are the same tag.
    let key (name: string) = (clean name).ToLowerInvariant()

    /// The one of `tags` a typed name stands for, if any. `name` reads the
    /// name off a tag, so this works on whatever shape holds them.
    let find (name: 'tag -> string) (typed: string) (tags: 'tag list) =
        tags |> List.tryFind (fun t -> key (name t) = key typed)

    /// Tags in the order they are shown: by name, ignoring case. They are
    /// stored in the order they were made, which says nothing about how
    /// they read as a row of badges.
    let sorted (name: 'tag -> string) (tags: 'tag list) = tags |> List.sortBy (name >> key)

/// A pantry is the household everything belongs to: one person owns it, any
/// number of people are members of it, and a member may do anything in it.
/// Everyone gets one of their own ("My Pantry") the first time they sign in;
/// a second one is joined by scanning its owner's QR code, which asks for
/// membership and waits for the owner to approve it.
///
/// Names are free text and may repeat - two households both called "Home" is
/// nobody's mistake - so a pantry is told apart by its id, of which the first
/// section is shown beside the name.
module Pantry =
    /// A member who has been let in. The other status is `pending`.
    [<Literal>]
    let Approved = "approved"

    /// Asked to join, waiting on the owner.
    [<Literal>]
    let Pending = "pending"

    /// The first section of the id: "56dcd3ca" of
    /// "56dcd3ca-089a-48e6-94e8-f4fcf0acea3c". Enough to tell two pantries
    /// by the same name apart, short enough to read out.
    let shortId (id: string) =
        match id.Split('-') with
        | [||] -> id
        | parts -> parts.[0]

    /// How a pantry reads in the header dropdown: "My Pantry · 56dcd3ca".
    let label (name: string) (id: string) = name.Trim() + " · " + shortId id

    /// What a new user's own pantry is called.
    [<Literal>]
    let DefaultName = "My Pantry"

    /// The name as it is stored: as typed, without the surrounding space. A
    /// blank name would leave nothing to pick in the dropdown, so it falls
    /// back to what a new pantry is called.
    let cleanName (name: string) =
        match name.Trim() with
        | "" -> DefaultName
        | name -> name

    /// Who a member is, for the owner to recognise: the name they sign in
    /// with, their email if they have no name, and failing both the first
    /// section of their user id. Only their client knows the first two, so a
    /// member row that hasn't synced yet still says something.
    let memberName (name: string) (email: string) (userId: string) =
        match name.Trim(), email.Trim() with
        | "", "" -> shortId userId
        | "", email -> email
        | name, _ -> name

    /// Whether the id looks like a pantry id, which is all a scanned QR code is
    /// checked against before asking to join it: 8-4-4-4-12 hex.
    let isId (text: string) =
        let isHex c =
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

        let sections = text.Trim().Split('-')
        let lengths = [| 8; 4; 4; 4; 12 |]

        sections.Length = lengths.Length
        && Array.forall2 (fun (s: string) n -> s.Length = n && Seq.forall isHex s) sections lengths

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

/// The pages the usage dashboard counts visits to.
///
/// The browser is what knows a page was opened, but it is not what writes the
/// count: the observability server's key would have to be in the bundle for
/// that, and the key can write any table it likes. So the browser posts a
/// section name here and the server writes the row - and because the names
/// are this list and nothing else, the most a browser can cause is one more
/// visit against one of eight pages. Totals only; nobody is identified.
module Usage =
    /// One per page. `recipe` covers every single recipe's page, since a chart
    /// with a line per recipe would be a list of recipes, not of usage.
    let sections =
        [ "recipes"; "recipe"; "shopping-list"; "menu"; "settings"; "login"; "terms"; "privacy" ]

    let isSection (name: string) = List.contains name sections

/// HTTP routes served by the F# server.
module Route =
    let syncCredentials = "/api/sync/credentials"
    let upload = "/api/sync/upload"
    /// Browser navigations (not fetches): start the OIDC login / end the session.
    let login = "/api/auth/login"
    let logout = "/api/auth/logout"
    /// 200 + User when signed in, 401 otherwise.
    let me = "/api/auth/me"
    /// One page visit, from the browser. No session needed: the login page is
    /// a page too, and it is only ever seen by someone who has none.
    let pageview = "/api/usage/pageview"
