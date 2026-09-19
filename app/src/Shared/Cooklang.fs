/// Cooklang parser (https://cooklang.org), written against the canonical test
/// suite in github.com/cooklang/spec (tests/canonical.yaml). Compiled by Fable
/// for the client (editor highlighting, lint, shopping list) and by .NET for the
/// server, so it must stay free of platform-specific APIs.
///
/// All offsets are UTF-16 code units - the same thing JS string indices are -
/// so spans can be handed straight to the editor.
module Cooklang

open System
open System.Text
open System.Text.RegularExpressions

/// Half-open character range [Start, End) into the source text.
type Span = { Start: int; End: int }

[<RequireQualifiedAccess>]
type Quantity =
    | Number of float
    /// Anything that isn't a plain number, fraction or mixed number: "few", "7 k", "01/2".
    | Text of string

/// `@name{quantity%unit}(note)`. A missing quantity means "some".
type Ingredient =
    { Name: string
      Quantity: Quantity option
      Unit: string option
      Note: string option
      Span: Span }

/// `#name{quantity}(note)`. A missing quantity means one.
type Cookware =
    { Name: string
      Quantity: Quantity option
      Note: string option
      Span: Span }

/// `~name{quantity%unit}`; the name is optional.
type Timer =
    { Name: string option
      Quantity: Quantity option
      Unit: string option
      Span: Span }

[<RequireQualifiedAccess>]
type Item =
    | Text of string
    | Ingredient of Ingredient
    | Cookware of Cookware
    | Timer of Timer

[<RequireQualifiedAccess>]
type Block =
    /// One line of instructions.
    | Step of Item list
    /// A `> note` line.
    | Note of string

/// Blocks under a `= Section` heading; the first section has no name unless the
/// recipe starts with a heading.
type Section = { Name: string option; Blocks: Block list }

type Recipe =
    { /// Front matter (`--- key: value ---`) and legacy `>> key: value` lines, in order.
      Metadata: (string * string) list
      Sections: Section list }

/// Source ranges for syntax highlighting.
[<RequireQualifiedAccess>]
type TokenKind =
    /// `@name`, including the marker.
    | Ingredient
    /// `#name`, including the marker.
    | Cookware
    /// `~name` or the bare `~`.
    | Timer
    | Quantity
    | Unit
    /// `(note)` after a component, or the text of a `> note` line.
    | Note
    | Comment
    | MetadataKey
    | MetadataValue
    /// `---`, `>>`, `>`, `=`.
    | Delimiter
    | SectionName

type Token = { Kind: TokenKind; Span: Span }

[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning

type Diagnostic =
    { Severity: Severity
      Message: string
      Span: Span }

type ParseResult =
    { Recipe: Recipe
      Tokens: Token list
      Diagnostics: Diagnostic list }

let private isNewline c =
    c = '\n' || c = '\r' || c = '\u0085' || c = '\u2028' || c = '\u2029'

let private isSpace c = not (isNewline c) && Char.IsWhiteSpace c

let private isMarker c = c = '@' || c = '#' || c = '~'

/// A character that can be part of a one-word name.
let private isWordChar c =
    c <> '{' && not (isMarker c) && not (isNewline c) && not (isSpace c) && not (Char.IsPunctuation c)

let private numberRx = Regex @"^\d+(\.\d+)?$"
/// "1/2", "1 / 2", "1 1/2". Numerators with a leading zero ("01/2") are text.
let private fractionRx = Regex @"^(?:(\d+)\s+)?(0|[1-9]\d*)\s*/\s*([1-9]\d*)$"

let private parseQuantity (raw: string) =
    if numberRx.IsMatch raw then
        Quantity.Number(float raw)
    else
        let m = fractionRx.Match raw

        if m.Success then
            let whole = if m.Groups.[1].Success then float m.Groups.[1].Value else 0.0
            Quantity.Number(whole + float m.Groups.[2].Value / float m.Groups.[3].Value)
        else
            Quantity.Text raw

/// What follows a marker character.
type private Component =
    /// `@name` with no braces; the name ends at whitespace or punctuation.
    | Word of name: string * endIdx: int
    /// `@name{`; the name may contain spaces and punctuation. `braceIdx` points at `{`.
    | Braced of name: string * braceIdx: int

let parse (src: string) : ParseResult =
    let src = if isNull src then "" else src
    let len = src.Length
    let tokens = ResizeArray<Token>()
    let diags = ResizeArray<Diagnostic>()
    let metadata = ResizeArray<string * string>()
    let sections = ResizeArray<Section>()
    let blocks = ResizeArray<Block>()
    let mutable sectionName: string option = None
    let mutable pos = 0

    let at i = if i < len then src.[i] else '\000'
    let span s e = { Start = s; End = e }
    let token kind s e = tokens.Add { Kind = kind; Span = span s e }

    let diag severity message s e =
        diags.Add { Severity = severity; Message = message; Span = span s e }

    /// Index of the end of the line containing `i` (the newline itself is excluded).
    let lineEnd i =
        let mutable j = i
        while j < len && not (isNewline src.[j]) do j <- j + 1
        j

    let skipNewline i =
        if at i = '\r' && at (i + 1) = '\n' then i + 2
        elif i < len && isNewline src.[i] then i + 1
        else i

    let indexOfInLine (c: char) s e =
        let mutable j = s
        while j < e && src.[j] <> c do j <- j + 1
        if j < e then j else -1

    /// Shrinks [s, e) to exclude surrounding whitespace.
    let trimmed s e =
        let mutable s = s
        let mutable e = e
        while s < e && Char.IsWhiteSpace src.[s] do s <- s + 1
        while e > s && Char.IsWhiteSpace src.[e - 1] do e <- e - 1
        s, e

    let slice s e = src.Substring(s, e - s)

    let endSection () =
        if blocks.Count > 0 || sectionName.IsSome then
            sections.Add { Name = sectionName; Blocks = List.ofSeq blocks }
            blocks.Clear()

    /// `key: value` within [s, e).
    let metadataLine s e =
        match indexOfInLine ':' s e with
        | -1 -> diag Severity.Warning "Expected 'key: value'" s e
        | colon ->
            let ks, ke = trimmed s colon
            let vs, ve = trimmed (colon + 1) e
            let key = slice ks ke
            token TokenKind.MetadataKey ks ke
            token TokenKind.MetadataValue vs ve

            if key = "" then
                diag Severity.Warning "Metadata key is empty" s colon
            elif metadata |> Seq.exists (fun (k, _) -> k = key) then
                diag Severity.Warning $"Duplicate metadata key '{key}'" ks ke

            metadata.Add(key, slice vs ve)

    /// A `---` block at the very start of the document.
    let frontMatter () =
        let e = lineEnd 0

        if (slice 0 e).Trim() = "---" then
            let mutable i = skipNewline e
            let mutable close = -1

            while close < 0 && i < len do
                let le = lineEnd i
                if (slice i le).Trim() = "---" then close <- i
                else
                    if (slice i le).Trim() <> "" then metadataLine i le
                    i <- skipNewline le

            if close >= 0 then
                token TokenKind.Delimiter 0 e
                token TokenKind.Delimiter close (lineEnd close)
                pos <- skipNewline (lineEnd close)
            else
                // Not front matter after all - parse the whole thing as recipe text.
                tokens.Clear()
                diags.Clear()
                metadata.Clear()
                diag Severity.Warning "Unclosed front matter: expected a closing '---' line" 0 e

    /// Reads the name after the marker at `start`.
    let readComponent start =
        let after = start + 1

        if at after = '{' then
            Some(Braced("", after))
        elif after < len && isWordChar src.[after] then
            // A `{` before the end of the line or the next marker makes this a multi-word name.
            let mutable i = after
            while i < len && src.[i] <> '{' && not (isNewline src.[i]) && not (isMarker src.[i]) do i <- i + 1

            if at i = '{' then
                Some(Braced((slice after i).TrimEnd(), i))
            else
                let mutable j = after
                while j < len && isWordChar src.[j] do j <- j + 1
                Some(Word(slice after j, j))
        else
            None

    /// `{quantity%unit}` at `openIdx`; None if the brace isn't closed on this line.
    let readAmount openIdx =
        let e = lineEnd openIdx

        match indexOfInLine '}' (openIdx + 1) e with
        | -1 -> None
        | close ->
            let pct = indexOfInLine '%' (openIdx + 1) close
            let qs, qe = trimmed (openIdx + 1) (if pct >= 0 then pct else close)

            let quantity =
                if qe > qs then
                    token TokenKind.Quantity qs qe
                    Some(parseQuantity (slice qs qe))
                else
                    None

            let unit =
                if pct < 0 then
                    None
                else
                    let us, ue = trimmed (pct + 1) close

                    if ue > us then
                        token TokenKind.Unit us ue
                        Some(slice us ue)
                    else
                        diag Severity.Warning "Empty unit after '%'" pct (pct + 1)
                        None

            Some(quantity, unit, close + 1)

    /// `(note)` directly after a component's closing brace.
    let readNote i =
        if at i <> '(' then
            None, i
        else
            match indexOfInLine ')' (i + 1) (lineEnd i) with
            | -1 ->
                diag Severity.Warning "Unclosed '(' note" i (i + 1)
                None, i
            | close ->
                token TokenKind.Note i (close + 1)
                Some((slice (i + 1) close).Trim()), close + 1

    /// The marker at `start` and everything that belongs to it. Returns
    /// (name, quantity, unit, note, end) or None when the marker is just text.
    let readItem kind what start =
        let oneWord name e =
            token kind start e
            Some(name, None, None, None, e)

        match readComponent start with
        | None -> None
        | Some(Word(name, e)) -> oneWord name e
        | Some(Braced(name, brace)) ->
            match readAmount brace with
            | None when name = "" -> None
            | None ->
                // cooklang-rs falls back to a one-word name and treats the rest as text.
                diag Severity.Warning "Unclosed '{'" brace (brace + 1)
                let mutable j = start + 1
                while j < len && isWordChar src.[j] do j <- j + 1
                oneWord (slice (start + 1) j) j
            | Some(quantity, unit, afterBrace) ->
                token kind start brace
                if name = "" && kind <> TokenKind.Timer then
                    diag Severity.Error $"Missing {what} name" start (brace + 1)
                let note, e = readNote afterBrace
                Some(name, quantity, unit, note, e)

    let readIngredient start =
        readItem TokenKind.Ingredient "ingredient" start
        |> Option.map (fun (name, quantity, unit, note, e) ->
            Item.Ingredient
                { Name = name
                  Quantity = quantity
                  Unit = unit
                  Note = note
                  Span = span start e },
            e)

    let readCookware start =
        readItem TokenKind.Cookware "cookware" start
        |> Option.map (fun (name, quantity, _, note, e) ->
            Item.Cookware
                { Name = name
                  Quantity = quantity
                  Note = note
                  Span = span start e },
            e)

    let readTimer start =
        readItem TokenKind.Timer "timer" start
        |> Option.map (fun (name, quantity, unit, _, e) ->
            match quantity, unit with
            | Some(Quantity.Text _), _ -> diag Severity.Error "Timer duration must be a number" start e
            | Some _, None -> diag Severity.Warning "Timer has no unit (e.g. ~{10%minutes})" start e
            | None, _ when name = "" -> diag Severity.Error "Timer needs a duration, e.g. ~{10%minutes}" start e
            | _ -> ()

            Item.Timer
                { Name = (if name = "" then None else Some name)
                  Quantity = quantity
                  Unit = unit
                  Span = span start e },
            e)

    /// One step, starting at `pos`. Ends at the end of the line, except that a
    /// trailing `-- comment` swallows its newline so the step continues on the
    /// next line (as the canonical tests require).
    let readStep () =
        let items = ResizeArray<Item>()
        let text = StringBuilder()
        let hasContent () = items.Count > 0 || text.Length > 0

        let flush () =
            if text.Length > 0 then
                items.Add(Item.Text(text.ToString()))
                text.Clear() |> ignore

        let mutable finished = false

        while not finished do
            let c = at pos

            if pos >= len then
                finished <- true
            elif isNewline c then
                pos <- skipNewline pos
                finished <- true
            elif c = '[' && at (pos + 1) = '-' then
                let close = src.IndexOf("-]", pos + 2)
                let e = if close < 0 then len else close + 2
                if close < 0 then diag Severity.Error "Unclosed block comment" pos (pos + 2)
                token TokenKind.Comment pos e
                if hasContent () then text.Append ' ' |> ignore
                pos <- e
            elif c = '-' && at (pos + 1) = '-' && at (pos + 2) <> '-' then
                let e = lineEnd pos
                token TokenKind.Comment pos e
                pos <- skipNewline e
                if hasContent () then text.Append ' ' |> ignore else finished <- true
            else
                let item =
                    match c with
                    | '@' -> readIngredient pos
                    | '#' -> readCookware pos
                    | '~' -> readTimer pos
                    | _ -> None

                match item with
                | Some(item, e) ->
                    flush ()
                    items.Add item
                    pos <- e
                | None ->
                    text.Append c |> ignore
                    pos <- pos + 1

        flush ()
        List.ofSeq items

    let readLine () =
        let mutable i = pos
        while i < len && isSpace src.[i] do i <- i + 1
        let c = at i
        let e = lineEnd i

        if i >= len then
            pos <- len
        elif isNewline c then
            pos <- skipNewline i
        elif c = '=' then
            let mutable j = i
            while at j = '=' do j <- j + 1
            token TokenKind.Delimiter i j
            let ns, ne = trimmed j e
            let mutable ne = ne
            while ne > ns && src.[ne - 1] = '=' do ne <- ne - 1
            let ns, ne = trimmed ns ne
            if ne > ns then token TokenKind.SectionName ns ne
            endSection ()
            sectionName <- (if ne > ns then Some(slice ns ne) else None)
            pos <- skipNewline e
        elif c = '>' && at (i + 1) = '>' then
            token TokenKind.Delimiter i (i + 2)
            diag Severity.Warning "'>>' metadata is deprecated; put 'key: value' lines between '---' lines at the top instead" i (i + 2)
            metadataLine (i + 2) e
            pos <- skipNewline e
        elif c = '>' then
            token TokenKind.Delimiter i (i + 1)
            let ns, ne = trimmed (i + 1) e
            token TokenKind.Note ns ne
            blocks.Add(Block.Note(slice ns ne))
            pos <- skipNewline e
        else
            pos <- i

            match readStep () with
            | [] -> ()
            | items -> blocks.Add(Block.Step items)

    frontMatter ()
    while pos < len do readLine ()
    endSection ()

    { Recipe =
        { Metadata = List.ofSeq metadata
          Sections = List.ofSeq sections }
      Tokens = tokens |> Seq.sortBy (fun t -> t.Span.Start) |> List.ofSeq
      Diagnostics = List.ofSeq diags }

/// Every step in the recipe, in order, ignoring sections and notes.
let steps (recipe: Recipe) =
    [ for s in recipe.Sections do
          for b in s.Blocks do
              match b with
              | Block.Step items -> yield items
              | Block.Note _ -> () ]

/// Every ingredient mention, in order. The same ingredient used twice appears twice.
let ingredients (recipe: Recipe) =
    [ for items in steps recipe do
          for item in items do
              match item with
              | Item.Ingredient i -> yield i
              | _ -> () ]

/// Every cookware mention, in order.
let cookware (recipe: Recipe) =
    [ for items in steps recipe do
          for item in items do
              match item with
              | Item.Cookware c -> yield c
              | _ -> () ]
