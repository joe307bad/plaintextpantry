/// Fable bindings for the slice of CodeMirror 6 the recipe editor uses.
/// Typed just far enough to keep CooklangEditor.fs honest; everything else is `obj`.
module CodeMirror

open System.Text.RegularExpressions
open Fable.Core

type Extension = interface end

type Text =
    abstract length: int
    abstract toString: unit -> string

type StateField<'T> = interface end

type EditorState =
    abstract doc: Text
    abstract field: StateField<'T> -> 'T

type Transaction =
    abstract docChanged: bool
    abstract state: EditorState

type ViewUpdate =
    abstract docChanged: bool
    abstract state: EditorState

type EditorView =
    abstract state: EditorState
    abstract dispatch: spec: obj -> unit
    abstract destroy: unit -> unit

type Facet<'Input> =
    abstract ``of``: value: 'Input -> Extension
    abstract from: field: StateField<'T> * get: ('T -> 'Input) -> Extension

type Decoration = interface end
type DecorationSet = interface end

type RangeSetBuilder<'T> =
    abstract add: from: int * ``to``: int * value: 'T -> unit
    abstract finish: unit -> DecorationSet

type StateFieldSpec<'T> =
    abstract create: state: EditorState -> 'T
    abstract update: value: 'T * tr: Transaction -> 'T
    abstract provide: field: StateField<'T> -> Extension

/// A `@codemirror/lint` diagnostic; `severity` is "error" | "warning" | "info" | "hint".
type Diagnostic =
    { from: int
      ``to``: int
      severity: string
      message: string }

[<AllowNullLiteral>]
type CompletionMatch =
    abstract from: int
    abstract ``to``: int
    abstract text: string

type CompletionContext =
    abstract pos: int
    abstract explicit: bool
    abstract state: EditorState
    /// null when the text before the cursor doesn't match.
    abstract matchBefore: expr: Regex -> CompletionMatch

type EditorViewStatic =
    [<Emit("new $0($1)")>]
    abstract Create: config: obj -> EditorView
    abstract updateListener: Facet<ViewUpdate -> unit>
    abstract decorations: Facet<DecorationSet>
    abstract lineWrapping: Extension

type EditorStateStatic =
    abstract create: config: obj -> EditorState

type StateFieldStatic =
    abstract define: spec: StateFieldSpec<'T> -> StateField<'T>

type DecorationStatic =
    abstract mark: spec: obj -> Decoration

type RangeSetBuilderStatic =
    [<Emit("new $0()")>]
    abstract Create: unit -> RangeSetBuilder<Decoration>

[<Import("EditorView", "@codemirror/view")>]
let editorView: EditorViewStatic = jsNative

[<Import("Decoration", "@codemirror/view")>]
let decoration: DecorationStatic = jsNative

[<Import("keymap", "@codemirror/view")>]
let keymap: Facet<obj[]> = jsNative

[<Import("placeholder", "@codemirror/view")>]
let placeholder: string -> Extension = jsNative

[<Import("EditorState", "@codemirror/state")>]
let editorState: EditorStateStatic = jsNative

[<Import("StateField", "@codemirror/state")>]
let stateField: StateFieldStatic = jsNative

[<Import("RangeSetBuilder", "@codemirror/state")>]
let rangeSetBuilder: RangeSetBuilderStatic = jsNative

[<Import("defaultKeymap", "@codemirror/commands")>]
let defaultKeymap: obj[] = jsNative

[<Import("historyKeymap", "@codemirror/commands")>]
let historyKeymap: obj[] = jsNative

[<Import("history", "@codemirror/commands")>]
let history: unit -> Extension = jsNative

[<Import("linter", "@codemirror/lint")>]
let linter (source: EditorView -> Diagnostic[]) (config: obj) : Extension = jsNative

[<Import("autocompletion", "@codemirror/autocomplete")>]
let autocompletion: config: obj -> Extension = jsNative

[<Import("completionKeymap", "@codemirror/autocomplete")>]
let completionKeymap: obj[] = jsNative

[<Import("closeBrackets", "@codemirror/autocomplete")>]
let closeBrackets: unit -> Extension = jsNative

[<Import("closeBracketsKeymap", "@codemirror/autocomplete")>]
let closeBracketsKeymap: obj[] = jsNative
