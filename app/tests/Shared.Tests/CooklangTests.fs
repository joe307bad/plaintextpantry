module CooklangTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open YamlDotNet.Serialization
open Cooklang

/// Both the expected YAML and our parse result are rendered to the same plain
/// text shape, so a failure shows exactly which item differs.
module Render =
    let item (kind: string) (name: string) (quantity: string) (unit: string) =
        $"{kind}(name={name}; quantity={quantity}; unit={unit})"

    let text (s: string) = $"text({s})"

    let steps (steps: string list list) =
        steps
        |> List.mapi (fun i items -> $"step {i + 1}:\n" + (items |> List.map (fun s -> "  " + s) |> String.concat "\n"))
        |> String.concat "\n"

    let metadata (pairs: (string * string) list) =
        pairs |> List.map (fun (k, v) -> $"{k} = {v}") |> String.concat "\n"

    let recipe (stepList: string list list) (pairs: (string * string) list) =
        "steps:\n" + steps stepList + "\nmetadata:\n" + metadata pairs

module Actual =
    let quantity (q: Quantity option) (fallback: string) =
        match q with
        | Some(Quantity.Number n) -> if Math.Floor n = n then string (int n) else string n
        | Some(Quantity.Text t) -> t
        | None -> fallback

    let item =
        function
        | Item.Text t -> Render.text t
        | Item.Ingredient i -> Render.item "ingredient" i.Name (quantity i.Quantity "some") (defaultArg i.Unit "")
        | Item.Cookware c -> Render.item "cookware" c.Name (quantity c.Quantity "1") ""
        | Item.Timer t -> Render.item "timer" (defaultArg t.Name "") (quantity t.Quantity "") (defaultArg t.Unit "")

    let recipe (r: Recipe) =
        Render.recipe (steps r |> List.map (List.map item)) r.Metadata

module Expected =
    let private str (o: obj) =
        match o with
        | null -> ""
        | :? string as s -> s
        | o -> string o

    let item (m: Dictionary<obj, obj>) =
        let get k = if m.ContainsKey(box k) then str m.[box k] else ""

        match get "type" with
        | "text" -> Render.text (get "value")
        | kind -> Render.item kind (get "name") (get "quantity") (get "units")

    let recipe (result: Dictionary<obj, obj>) =
        let steps =
            match result.["steps"] with
            | :? List<obj> as steps ->
                [ for step in steps ->
                      [ for i in (step :?> List<obj>) -> item (i :?> Dictionary<obj, obj>) ] ]
            | _ -> []

        let metadata =
            match result.["metadata"] with
            | :? Dictionary<obj, obj> as m -> [ for kv in m -> str kv.Key, str kv.Value ]
            | _ -> []

        Render.recipe steps metadata

/// Canonical tests whose expectation we knowingly don't meet.
let private knownDeviations =
    Map
        [ "testMetadataBreak",
          "expects three lines to join into one step, contradicting testMultiLineDirections; we keep one step per line" ]

let private canonical: Lazy<Dictionary<string, Dictionary<obj, obj>>> =
    lazy
        (let yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "canonical.yaml"))
         let root = Deserializer().Deserialize<Dictionary<obj, obj>>(yaml)
         let tests = root.["tests"] :?> Dictionary<obj, obj>
         let result = Dictionary<string, Dictionary<obj, obj>>()
         for kv in tests do result.[string kv.Key] <- kv.Value :?> Dictionary<obj, obj>
         result)

let canonicalNames () : IEnumerable<obj[]> =
    canonical.Value.Keys |> Seq.map (fun name -> [| box name |])

[<Theory; MemberData(nameof canonicalNames)>]
let ``canonical suite`` (name: string) =
    let test = canonical.Value.[name]
    let source = string test.["source"]
    let expected = Expected.recipe (test.["result"] :?> Dictionary<obj, obj>)
    let actual = Actual.recipe (parse source).Recipe

    match Map.tryFind name knownDeviations with
    | Some reason -> Assert.True((expected <> actual), $"{name} now passes - remove it from knownDeviations ({reason})")
    | None -> Assert.Equal(expected, actual)

[<Fact>]
let ``diagnostics for common mistakes`` () =
    let messages src =
        (parse src).Diagnostics |> List.map (fun d -> d.Severity, d.Message)

    Assert.Contains((Severity.Warning, "Unclosed '{'"), messages "Add @flour{200")
    Assert.Contains((Severity.Error, "Missing ingredient name"), messages "Add @{2%cups}")
    Assert.Contains((Severity.Error, "Timer duration must be a number"), messages "Wait ~{abc%minutes}")
    Assert.Contains((Severity.Warning, "Timer has no unit (e.g. ~{10%minutes})"), messages "Wait ~{5}")
    Assert.Contains((Severity.Error, "Unclosed block comment"), messages "Mix [- oops")
    Assert.Contains((Severity.Warning, "Empty unit after '%'"), messages "@salt{1%}")
    Assert.Empty(messages "Mix @flour{200%g} in a #bowl{} for ~{5%minutes}.")

[<Fact>]
let ``tokens cover components`` () =
    let src = "Mix @flour{200%g}(sifted) in #bowl{} for ~{5%minutes}. -- done"
    let tokens = (parse src).Tokens |> List.map (fun t -> t.Kind, src.Substring(t.Span.Start, t.Span.End - t.Span.Start))

    Assert.Equal<(TokenKind * string) list>(
        [ TokenKind.Ingredient, "@flour"
          TokenKind.Quantity, "200"
          TokenKind.Unit, "g"
          TokenKind.Note, "(sifted)"
          TokenKind.Cookware, "#bowl"
          TokenKind.Timer, "~"
          TokenKind.Quantity, "5"
          TokenKind.Unit, "minutes"
          TokenKind.Comment, "-- done" ],
        tokens
    )

[<Fact>]
let ``sections, notes and front matter`` () =
    let src = "---\ntitle: Bread\nservings: 4\n---\n= Dough =\nMix @flour{500%g}.\n> Rest it.\n= Bake\nBake for ~{30%minutes}."
    let r = (parse src).Recipe

    Assert.Equal<(string * string) list>([ "title", "Bread"; "servings", "4" ], r.Metadata)
    Assert.Equal<string option list>([ Some "Dough"; Some "Bake" ], r.Sections |> List.map (fun s -> s.Name))
    Assert.Equal(2, (r.Sections |> List.head).Blocks.Length)
    Assert.Contains(Block.Note "Rest it.", (r.Sections |> List.head).Blocks)
    Assert.Equal<string list>([ "flour" ], ingredients r |> List.map (fun i -> i.Name))
