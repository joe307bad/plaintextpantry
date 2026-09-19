module App

open Browser.Dom
open Elmish
open Fable.Core
open Feliz
open Feliz.UseElmish
open Db

/// Tiny path router over the History API: "/recipes/<id>" <-> ["recipes"; "<id>"].
/// Back/forward fire `popstate`; in-app navigation pushes state and the
/// caller dispatches `UrlChanged` itself, so there is one source of truth.
module Router =
    let currentUrl () =
        window.location.pathname.Split('/')
        |> Array.filter ((<>) "")
        |> Array.map JS.decodeURIComponent
        |> List.ofArray

    let format (segments: string list) =
        "/" + (segments |> List.map JS.encodeURIComponent |> String.concat "/")

    let navigate segments =
        window.history.pushState (null, "", format segments)

    let onUrlChanged (handler: string list -> unit) =
        window.addEventListener ("popstate", fun _ -> handler (currentUrl ()))

type Page =
    | RecipeList
    | RecipeDetail of id: string
    | NotFound

type Model =
    { Page: Page
      Recipes: Recipe list
      NewTitle: string
      EditTitle: string
      Status: PowerSync.SyncStatus option }

type Msg =
    | UrlChanged of string list
    | RecipesChanged of Recipe list
    | StatusChanged of PowerSync.SyncStatus
    | NewTitleChanged of string
    | AddRecipe
    | EditTitleChanged of string
    | SaveTitle of id: string
    | DeleteRecipe of id: string
    | Ignore

let private parseUrl (segments: string list) =
    match segments with
    | [] -> RecipeList
    | [ "recipes"; id ] -> RecipeDetail id
    | _ -> NotFound

let private fireAndForget (work: JS.Promise<obj>) =
    Cmd.OfPromise.either (fun () -> work) () (fun _ -> Ignore) (fun err ->
        Browser.Dom.console.error err
        Ignore)

let init () =
    { Page = parseUrl (Router.currentUrl ())
      Recipes = []
      NewTitle = ""
      EditTitle = ""
      Status = None },
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Db.watchRecipes (RecipesChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchStatus (StatusChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Router.onUrlChanged (UrlChanged >> dispatch))
          Cmd.ofEffect (fun _ -> Db.connect () |> Promise.catch (fun e -> console.error e) |> ignore) ]

let private findRecipe id (recipes: Recipe list) =
    recipes |> List.tryFind (fun r -> r.id = id)

let update msg model =
    match msg with
    | UrlChanged segments ->
        let page = parseUrl segments

        let editTitle =
            match page with
            | RecipeDetail id -> findRecipe id model.Recipes |> Option.map (fun r -> r.title) |> Option.defaultValue ""
            | _ -> ""

        { model with Page = page; EditTitle = editTitle }, Cmd.none
    | RecipesChanged recipes ->
        // Keep the edit box in step with the synced title unless the user is mid-edit.
        let editTitle =
            match model.Page with
            | RecipeDetail id when model.EditTitle = "" ->
                findRecipe id recipes |> Option.map (fun r -> r.title) |> Option.defaultValue ""
            | _ -> model.EditTitle

        { model with Recipes = recipes; EditTitle = editTitle }, Cmd.none
    | StatusChanged status -> { model with Status = Some status }, Cmd.none
    | NewTitleChanged title -> { model with NewTitle = title }, Cmd.none
    | AddRecipe ->
        match model.NewTitle.Trim() with
        | "" -> model, Cmd.none
        | title -> { model with NewTitle = "" }, fireAndForget (Db.addRecipe title)
    | EditTitleChanged title -> { model with EditTitle = title }, Cmd.none
    | SaveTitle id ->
        match model.EditTitle.Trim() with
        | "" -> model, Cmd.none
        | title -> model, fireAndForget (Db.renameRecipe id title)
    | DeleteRecipe id ->
        Router.navigate []
        { model with Page = RecipeList; EditTitle = "" }, fireAndForget (Db.deleteRecipe id)
    | Ignore -> model, Cmd.none

/// Anchor that navigates in-app (real href, so open-in-new-tab still works).
let private link dispatch (segments: string list) (text: string) =
    Html.a
        [ prop.href (Router.format segments)
          prop.text text
          prop.onClick (fun e ->
              e.preventDefault ()
              Router.navigate segments
              dispatch (UrlChanged segments)) ]

let private statusLine (status: PowerSync.SyncStatus option) =
    let flags =
        match status with
        | None -> "starting"
        | Some s ->
            [ (if s.connected then "connected" elif s.connecting then "connecting" else "offline")
              (if s.uploading then "uploading" else "")
              (if s.downloading then "downloading" else "")
              (if s.hasSynced = Some true then "synced" else "not yet synced") ]
            |> List.filter ((<>) "")
            |> String.concat ", "

    let errors =
        match status with
        | None -> []
        | Some s ->
            [ s.uploadError |> Option.map (fun e -> "upload failed: " + e.Message)
              s.downloadError |> Option.map (fun e -> "download failed: " + e.Message) ]
            |> List.choose id

    Html.div
        [ yield Html.p [ Html.small [ prop.text ("sync: " + flags) ] ]
          for err in errors -> Html.p [ Html.small [ prop.text err ] ] ]

let private listPage (model: Model) dispatch =
    Html.div
        [ Html.form
              [ prop.onSubmit (fun e ->
                    e.preventDefault ()
                    dispatch AddRecipe)
                prop.children
                    [ Html.label
                          [ prop.htmlFor "recipe-title"
                            prop.text "Recipe Title " ]
                      Html.input
                          [ prop.id "recipe-title"
                            prop.type' "text"
                            prop.value model.NewTitle
                            prop.onChange (NewTitleChanged >> dispatch) ]
                      Html.text " "
                      Html.button [ prop.type' "submit"; prop.text "Add Recipe" ] ] ]
          Html.h2 "Recipes"
          match model.Recipes with
          | [] -> Html.p "No recipes yet."
          | recipes ->
              Html.ul
                  [ for r in recipes ->
                        Html.li [ link dispatch [ "recipes"; r.id ] r.title ] ] ]

let private detailPage (model: Model) (id: string) dispatch =
    match findRecipe id model.Recipes with
    | None ->
        Html.div
            [ Html.p "Recipe not found (it may still be syncing, or it was deleted)."
              link dispatch [] "Back to recipes" ]
    | Some recipe ->
        Html.div
            [ Html.p [ link dispatch [] "← All recipes" ]
              Html.h2 recipe.title
              Html.dl
                  [ Html.dt "Created"
                    Html.dd recipe.created_at
                    Html.dt "Id"
                    Html.dd [ Html.code recipe.id ] ]
              Html.form
                  [ prop.onSubmit (fun e ->
                        e.preventDefault ()
                        dispatch (SaveTitle recipe.id))
                    prop.children
                        [ Html.label [ prop.htmlFor "edit-title"; prop.text "Rename " ]
                          Html.input
                              [ prop.id "edit-title"
                                prop.type' "text"
                                prop.value model.EditTitle
                                prop.onChange (EditTitleChanged >> dispatch) ]
                          Html.text " "
                          Html.button [ prop.type' "submit"; prop.text "Save" ] ] ]
              Html.p
                  [ Html.button
                        [ prop.type' "button"
                          prop.text "Delete recipe"
                          prop.onClick (fun _ -> dispatch (DeleteRecipe recipe.id)) ] ] ]

[<ReactComponent>]
let View () =
    let model, dispatch = React.useElmish (init, update)

    Html.div
        [ Html.h1 [ link dispatch [] "Plaintext Pantry" ]
          statusLine model.Status
          Html.hr []
          match model.Page with
          | RecipeList -> listPage model dispatch
          | RecipeDetail id -> detailPage model id dispatch
          | NotFound -> Html.p "Page not found." ]

let root = ReactDOM.createRoot (document.getElementById "root")
root.render (View())
