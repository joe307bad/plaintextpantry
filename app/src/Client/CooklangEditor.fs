/// CodeMirror editor for Cooklang: highlighting, lint squiggles and completions,
/// all driven by the shared parser in Cooklang.fs.
module CooklangEditor

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open CodeMirror

/// Names to offer in completions on top of those already in the document.
type KnownNames =
    { Ingredients: string list
      Cookware: string list }

let private cssClass kind =
    match kind with
    | Cooklang.TokenKind.Ingredient -> "ck-ingredient"
    | Cooklang.TokenKind.Cookware -> "ck-cookware"
    | Cooklang.TokenKind.Timer -> "ck-timer"
    | Cooklang.TokenKind.Quantity -> "ck-quantity"
    | Cooklang.TokenKind.Unit -> "ck-unit"
    | Cooklang.TokenKind.Note -> "ck-note"
    | Cooklang.TokenKind.Comment -> "ck-comment"
    | Cooklang.TokenKind.MetadataKey -> "ck-metadata-key"
    | Cooklang.TokenKind.MetadataValue -> "ck-metadata-value"
    | Cooklang.TokenKind.Delimiter -> "ck-delimiter"
    | Cooklang.TokenKind.SectionName -> "ck-section"

let private marks =
    [ Cooklang.TokenKind.Ingredient
      Cooklang.TokenKind.Cookware
      Cooklang.TokenKind.Timer
      Cooklang.TokenKind.Quantity
      Cooklang.TokenKind.Unit
      Cooklang.TokenKind.Note
      Cooklang.TokenKind.Comment
      Cooklang.TokenKind.MetadataKey
      Cooklang.TokenKind.MetadataValue
      Cooklang.TokenKind.Delimiter
      Cooklang.TokenKind.SectionName ]
    |> List.map (fun kind -> kind, decoration.mark (createObj [ "class" ==> cssClass kind ]))
    |> Map.ofList

type private Parsed =
    { Result: Cooklang.ParseResult
      Decorations: DecorationSet }

let private parseDoc (text: string) =
    let result = Cooklang.parse text
    let builder = rangeSetBuilder.Create()

    for t in result.Tokens do
        if t.Span.End > t.Span.Start then
            builder.add (t.Span.Start, t.Span.End, marks.[t.Kind])

    { Result = result
      Decorations = builder.finish () }

/// Re-parses the whole document on every change; recipes are small enough.
let private parsed: StateField<Parsed> =
    stateField.define
        { new StateFieldSpec<Parsed> with
            member _.create state = parseDoc (state.doc.toString ())

            member _.update(value, tr) =
                if tr.docChanged then parseDoc (tr.state.doc.toString ()) else value

            member _.provide field =
                editorView.decorations.from (field, (fun p -> p.Decorations)) }

let private lint (view: EditorView) : Diagnostic[] =
    (view.state.field parsed).Result.Diagnostics
    |> List.map (fun d ->
        { from = d.Span.Start
          ``to`` = d.Span.End
          severity =
            match d.Severity with
            | Cooklang.Severity.Error -> "error"
            | Cooklang.Severity.Warning -> "warning"
          message = d.Message })
    |> Array.ofList

let private units =
    [ "g"; "kg"; "ml"; "l"; "tsp"; "tbsp"; "cup"; "cups"; "oz"; "lb"; "pinch"; "clove"; "cloves"
      "slice"; "slices"; "piece"; "pieces"; "can"; "bunch"; "sprig"; "sprigs"
      "seconds"; "minutes"; "hours" ]

/// Names with spaces or punctuation need `{}` to mark where they end.
let private applyText (name: string) =
    if name |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsPunctuation c) then
        name + "{}"
    else
        name

let private option (label: string) (apply: string) =
    createObj [ "label" ==> label; "apply" ==> apply ]

let private markerRx = JS.Constructors.RegExp.Create @"[@#~][^\s{}@#~]*"
let private unitRx = JS.Constructors.RegExp.Create @"%\s*[^\s{}%]*"
let private wordRx = JS.Constructors.RegExp.Create @"^[^\s{}@#~%]*$"

/// Completion source: ingredient / cookware names after `@` / `#`, units after `%`.
let private complete (known: unit -> KnownNames) (ctx: CompletionContext) : obj =
    let result (from: int) (options: obj list) =
        createObj [ "from" ==> from; "options" ==> Array.ofList options; "validFor" ==> wordRx ]

    match ctx.matchBefore markerRx with
    | null ->
        match ctx.matchBefore unitRx with
        | null -> null
        | m ->
            // Skip the '%' and any spaces after it.
            let typed = m.text.Substring 1
            let from = m.from + 1 + (typed.Length - typed.TrimStart().Length)
            result from [ for u in units -> option u u ]
    | m ->
        let recipe = (ctx.state.field parsed).Result.Recipe
        let known = known ()

        // Don't offer back the half-typed name the cursor is sitting in.
        let notAtCursor (span: Cooklang.Span) = ctx.pos < span.Start || ctx.pos > span.End

        let names =
            match m.text.[0] with
            | '@' ->
                (Cooklang.ingredients recipe |> List.filter (fun i -> notAtCursor i.Span) |> List.map (fun i -> i.Name))
                @ known.Ingredients
            | '#' ->
                (Cooklang.cookware recipe |> List.filter (fun c -> notAtCursor c.Span) |> List.map (fun c -> c.Name))
                @ known.Cookware
            | _ -> []

        match names |> List.filter ((<>) "") |> List.distinct with
        | [] -> null
        | names -> result (m.from + 1) [ for n in names -> option n (applyText n) ]

[<ReactComponent>]
let CooklangEditor (value: string, known: KnownNames, placeholderText: string, onChange: string -> unit) =
    let container = React.useElementRef ()
    let view = React.useRef<EditorView option> None
    // Refs so the extensions (created once, on mount) always see the latest props.
    let onChangeRef = React.useRef onChange
    onChangeRef.current <- onChange
    let knownRef = React.useRef known
    knownRef.current <- known

    let mount () : unit -> unit =
        let extensions =
            [| parsed :> obj
               history ()
               closeBrackets ()
               autocompletion (createObj [ "override" ==> [| complete (fun () -> knownRef.current) |] ])
               linter lint (createObj [ "delay" ==> 300 ])
               editorView.lineWrapping
               placeholder placeholderText
               keymap.``of`` (Array.concat [ closeBracketsKeymap; defaultKeymap; historyKeymap; completionKeymap ])
               editorView.updateListener.``of`` (fun u ->
                   if u.docChanged then
                       onChangeRef.current (u.state.doc.toString ())) |]

        let v =
            editorView.Create(
                createObj
                    [ "parent" ==> container.current
                      "state" ==> editorState.create (createObj [ "doc" ==> value; "extensions" ==> extensions ]) ]
            )

        view.current <- Some v

        fun () ->
            v.destroy ()
            view.current <- None

    React.useEffect (mount, [||])

    // Push external changes (e.g. a synced edit) into the editor without disturbing local typing.
    React.useEffect (
        (fun () ->
            match view.current with
            | Some v when v.state.doc.toString () <> value ->
                v.dispatch (
                    createObj
                        [ "changes" ==> createObj [ "from" ==> 0; "to" ==> v.state.doc.length; "insert" ==> value ] ]
                )
            | _ -> ()),
        [| box value |]
    )

    Html.div [ prop.ref container; prop.className "rounded border border-gray-300 text-sm" ]
