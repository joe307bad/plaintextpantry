/// Printing the menu: the list is drawn onto a canvas 3in wide at 300 dpi,
/// as tall as it needs to be, encoded as a JPEG and handed to the browser's
/// print dialog inside a throwaway iframe whose page is sized to the image.
/// A raster rather than printed HTML so the output is the same on every
/// browser and receipt printer.
module Print

open Browser.Dom
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop

/// One entry: the recipe title and the sides noted under it.
type Entry = { Title: string; Sides: string list }

let private dpi = 300.0
let private widthIn = 3.0
let private marginIn = 0.2
/// Page margin when printing: the image is inset by this on every side, so it
/// lands inside the printer's printable area without touching the settings.
let private pageMarginIn = 0.2

/// Points to pixels at the print resolution.
let private pt (n: float) = n * dpi / 72.0

/// CSS pixels (96/in) to pixels at the print resolution.
let private px (n: float) = n * dpi / 96.0

let private borderWidth = px 5.0
let private brand = "#ff33d2"

type private Font = { Css: string; Size: float; Color: string }

let private heading = { Css = $"bold {pt 13.0}px Helvetica, Arial, sans-serif"; Size = pt 13.0; Color = "#212121" }
let private recipe = { Css = $"{pt 14.0}px Helvetica, Arial, sans-serif"; Size = pt 14.0; Color = "#212121" }

/// The logo beside the menu name: a square this big, then this much gap.
let private logoSize = pt 18.0
let private logoGap = pt 5.0

/// "Tacos w/ rice", "… w/ rice and beans", "… w/ rice, beans, and slaw".
let private withSides (e: Entry) =
    match e.Sides with
    | [] -> e.Title
    | [ a ] -> $"{e.Title} w/ {a}"
    | [ a; b ] -> $"{e.Title} w/ {a} and {b}"
    | sides ->
        let init, last = List.take (sides.Length - 1) sides, List.last sides
        $"""{e.Title} w/ {String.concat ", " init}, and {last}"""

/// A line of text at (x, baseline y), plus the space it takes.
type private Line = { Text: string; Font: Font; X: float; Y: float }

/// Breaks `text` into lines no wider than `maxWidth`, measured with `font`.
let private wrap (ctx: obj) (font: Font) (maxWidth: float) (text: string) =
    ctx?font <- font.Css

    let width (s: string) : float = ctx?measureText(s)?width

    text.Split(' ')
    |> Array.fold
        (fun (lines: string list, current: string) word ->
            let candidate = if current = "" then word else current + " " + word

            if current <> "" && width candidate > maxWidth then
                current :: lines, word
            else
                lines, candidate)
        ([], "")
    |> fun (lines, last) -> List.rev (last :: lines)

/// Lays the menu out top to bottom: the logo and name, then each entry as
/// "n. title w/ sides". Returns the lines, where the logo goes (its top-left
/// corner) and the total height.
let private layout (ctx: obj) (name: string) (entries: Entry list) =
    let margin = marginIn * dpi + borderWidth
    let contentWidth = widthIn * dpi - 2.0 * margin
    let leading (font: Font) = font.Size * 1.4

    // Start above the margin by the heading's internal slack (half-leading
    // plus ascent above the cap height), so the caps sit on the margin.
    let mutable y = margin - heading.Size * 0.48
    let lines = ResizeArray<Line>()

    let write (font: Font) (x: float) (text: string) =
        for l in wrap ctx font (contentWidth - (x - margin)) text do
            y <- y + leading font
            lines.Add { Text = l; Font = font; X = x; Y = y - (leading font - font.Size) / 2.0 }

    // The name starts to the right of the logo, which is centred on the
    // name's first line (the cap height of the heading font is about 0.72em).
    let nameX = margin + logoSize + logoGap
    write heading nameX name
    let firstBaseline = lines[0].Y
    let logoAt = margin, firstBaseline - heading.Size * 0.36 - logoSize / 2.0
    // A one-line name is shorter than the logo; carry on below whichever is lower.
    y <- max y (snd logoAt + logoSize)
    y <- y + pt 4.0

    entries
    |> List.iteri (fun i e ->
        y <- y + pt 8.0
        // The number sits in its own column so wrapped titles align.
        let numberWidth = pt 20.0
        let numberY = y
        write recipe (margin + numberWidth) (withSides e)
        lines.Add { Text = $"{i + 1}."; Font = recipe; X = margin; Y = numberY + leading recipe - (leading recipe - recipe.Size) / 2.0 })

    List.ofSeq lines, logoAt, y + margin

/// Loads the logo; `None` if it cannot be, so the menu still prints.
let private loadLogo () : JS.Promise<HTMLImageElement option> =
    Promise.create (fun resolve _ ->
        let img = document.createElement "img" :?> HTMLImageElement
        img.onload <- fun _ -> resolve (Some img)
        img.onerror <- fun _ -> resolve None
        img.src <- "/brand/icon.png")

/// Draws the menu to a canvas and returns it as a JPEG data URL.
let private render (logo: HTMLImageElement option) (name: string) (entries: Entry list) =
    let canvas = document.createElement "canvas" :?> HTMLCanvasElement
    let ctx: obj = canvas?getContext("2d")

    // Measure first so the canvas can be exactly as tall as the content;
    // sizing a canvas resets its context, so the fonts are set again below.
    let lines, (logoX, logoY), height = layout ctx name entries
    canvas.width <- int (widthIn * dpi)
    canvas.height <- int (ceil height)

    ctx?fillStyle <- "#ffffff"
    ctx?fillRect (0, 0, canvas.width, canvas.height)
    // The border, stroked along the inside of the edge.
    ctx?strokeStyle <- brand
    ctx?lineWidth <- borderWidth
    ctx?strokeRect (borderWidth / 2.0, borderWidth / 2.0, float canvas.width - borderWidth, float canvas.height - borderWidth)
    ctx?textBaseline <- "alphabetic"

    match logo with
    | Some img -> ctx?drawImage (img, logoX, logoY, logoSize, logoSize)
    | None -> ()

    for l in lines do
        ctx?font <- l.Font.Css
        ctx?fillStyle <- l.Font.Color
        ctx?fillText (l.Text, l.X, l.Y)

    canvas.toDataURL ("image/jpeg", 0.92)

/// Opens the print dialog on the rendered image. The iframe's page is 3in
/// wide, with the image inset by the page margin, so it prints at size on
/// a receipt printer; the iframe is removed once the dialog closes.
let private show (name: string) (dataUrl: string) =
    let iframe = document.createElement "iframe" :?> HTMLIFrameElement
    iframe.setAttribute ("aria-hidden", "true")
    iframe.setAttribute ("style", "position: fixed; right: 0; bottom: 0; width: 0; height: 0; border: 0")
    document.body.appendChild iframe |> ignore

    // A same-origin about:blank document is ready at once; built with DOM
    // calls rather than document.write so `onload` is attached before `src`.
    let doc = iframe.contentDocument
    let style = doc.createElement "style"

    style.textContent <-
        $"@page {{ size: {widthIn}in auto; margin: {pageMarginIn}in }} html, body {{ margin: 0 }} img {{ display: block; width: {widthIn - 2.0 * pageMarginIn}in }}"

    doc.head.appendChild style |> ignore
    doc.title <- name

    let win = iframe.contentWindow
    win?onafterprint <- fun () -> document.body.removeChild iframe |> ignore

    let img = doc.createElement "img" :?> HTMLImageElement

    img.onload <-
        fun _ ->
            win.focus ()
            win.print ()

    img.src <- dataUrl
    doc.body.appendChild img |> ignore

/// Renders the menu and opens the print dialog on it.
let menu (name: string) (entries: Entry list) =
    loadLogo ()
    |> Promise.iter (fun logo -> show name (render logo name entries))
