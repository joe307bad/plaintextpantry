/// QR codes, both ways: the picture of a pantry id its owner shares, and the
/// camera that reads one off someone else's screen. Thin bindings over
/// `qrcode` (drawing) and `jsqr` (reading), in the spirit of PowerSync.fs -
/// only what the settings page uses.
module Qr

open Browser.Dom
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open Fetch

/// The browser's `navigator`, which Fable.Browser.Dom doesn't bind: the share
/// sheet and the camera both hang off it, and both are reached dynamically
/// because neither is on every device this runs on.
[<Emit("navigator")>]
let private navigator: obj = jsNative

[<Import("toDataURL", "qrcode")>]
let private toDataUrlRaw (text: string, options: obj) : JS.Promise<string> = jsNative

/// `text` as a PNG data URL. Big enough to scan off a phone screen and to
/// survive being sent as a picture; the thin margin is the "quiet zone" a
/// reader needs to find the code's edges.
let toDataUrl (text: string) : JS.Promise<string> =
    toDataUrlRaw (text, createObj [ "margin" ==> 2; "width" ==> 512; "errorCorrectionLevel" ==> "M" ])

[<Emit("new File([$0], $1, { type: 'image/png' })")>]
let private asFile (blob: obj) (name: string) : obj = jsNative

/// Hands the image to the phone's share sheet, where it can go to anyone the
/// owner messages. Nothing to share with (desktop, mostly) means saving the
/// PNG instead, which they can attach themselves. A share the user backs out
/// of is not a failure and says nothing.
let share (fileName: string) (title: string) (text: string) (dataUrl: string) : JS.Promise<unit> =
    promise {
        let nav: obj = navigator
        let! response = fetch dataUrl []
        let! blob = response.blob ()
        let payload = createObj [ "files" ==> [| asFile blob fileName |]; "title" ==> title; "text" ==> text ]

        let canShare =
            not (isNullOrUndefined nav?canShare) && unbox<bool> (nav?canShare payload)

        if canShare then
            do!
                unbox<JS.Promise<unit>> (nav?share payload)
                |> Promise.catch (fun err -> JS.console.debug ("share", err))
        else
            let link = document.createElement "a" :?> HTMLAnchorElement
            link.href <- dataUrl
            link?download <- fileName
            link.click ()
    }

[<ImportDefault("jsqr")>]
let private decode (data: obj, width: int, height: int, options: obj) : obj = jsNative

/// Turns on the back camera, draws it into `video`, and reads frames until one
/// holds a QR code - then calls `onFound` with its text and stops. `onError`
/// is for the two things that go wrong out loud: no camera, or permission
/// refused. Returns the way to stop, which the caller must call when the
/// scanner is closed; `onFound` has stopped already.
let scan (video: HTMLVideoElement) (onFound: string -> unit) (onError: string -> unit) : unit -> unit =
    let mutable stopped = false
    let mutable stream: obj = null
    let canvas = document.createElement "canvas" :?> HTMLCanvasElement

    let stop () =
        stopped <- true

        if not (isNullOrUndefined stream) then
            for track in unbox<obj[]> (stream?getTracks ()) do
                track?stop () |> ignore

            stream <- null

    // One frame: the camera's pixels through jsQR. `dontInvert` is the cheaper
    // pass, and a code on a screen is dark-on-light like a printed one.
    let read () =
        let width = int video.videoWidth
        let height = int video.videoHeight

        if width > 0 && height > 0 then
            canvas.width <- float width
            canvas.height <- float height
            let ctx: obj = !!canvas.getContext "2d"
            ctx?drawImage (video, 0, 0, width, height)
            let image: obj = ctx?getImageData (0, 0, width, height)
            let found = decode (image?data, width, height, createObj [ "inversionAttempts" ==> "dontInvert" ])

            if isNullOrUndefined found then None else Some(string found?data)
        else
            None

    let rec tick () =
        if not stopped then
            match read () with
            | Some text ->
                stop ()
                onFound text
            | None -> window.requestAnimationFrame (fun _ -> tick ()) |> ignore

    let media: obj = navigator?mediaDevices

    if isNullOrUndefined media then
        onError "This browser has no camera to scan with."
    else
        let constraints =
            createObj [ "video" ==> createObj [ "facingMode" ==> "environment" ]; "audio" ==> false ]

        unbox<JS.Promise<obj>> (media?getUserMedia constraints)
        |> Promise.map (fun granted ->
            // Closed again before the camera was ready: turn it straight off.
            if stopped then
                for track in unbox<obj[]> (granted?getTracks ()) do
                    track?stop () |> ignore
            else
                stream <- granted
                video?srcObject <- granted

                // `play` answers with a promise in every browser that has a
                // camera, and it rejects if the tab is hidden by the time the
                // stream arrives; that is not worth a message on screen.
                match video?play () with
                | played when not (isNullOrUndefined played) ->
                    unbox<JS.Promise<unit>> played
                    |> Promise.catch (fun err -> JS.console.debug ("camera", err))
                    |> Promise.start
                | _ -> ()

                tick ())
        |> Promise.catch (fun _ -> onError "No camera, or permission for it was refused.")
        |> Promise.start

    stop
