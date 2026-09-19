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
    | ShoppingList
    | NotFound

/// Contents of the "New Recipe" modal while it is open.
type NewRecipe = { Title: string; Body: string }

type Model =
    { Page: Page
      Recipes: Recipe list
      NewRecipe: NewRecipe option
      EditTitle: string }

type Msg =
    | UrlChanged of string list
    | RecipesChanged of Recipe list
    | OpenNewRecipe
    | CloseNewRecipe
    | NewTitleChanged of string
    | NewBodyChanged of string
    | AddRecipe
    | EditTitleChanged of string
    | SaveTitle of id: string
    | DeleteRecipe of id: string
    | Ignore

let private parseUrl (segments: string list) =
    match segments with
    | [] -> RecipeList
    | [ "recipes"; id ] -> RecipeDetail id
    | [ "shopping-list" ] -> ShoppingList
    | _ -> NotFound

let private fireAndForget (work: JS.Promise<obj>) =
    Cmd.OfPromise.either (fun () -> work) () (fun _ -> Ignore) (fun err ->
        Browser.Dom.console.error err
        Ignore)

let init () =
    { Page = parseUrl (Router.currentUrl ())
      Recipes = []
      NewRecipe = None
      EditTitle = "" },
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Db.watchRecipes (RecipesChanged >> dispatch))
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
    | OpenNewRecipe -> { model with NewRecipe = Some { Title = ""; Body = "" } }, Cmd.none
    | CloseNewRecipe -> { model with NewRecipe = None }, Cmd.none
    | NewTitleChanged title ->
        { model with NewRecipe = model.NewRecipe |> Option.map (fun r -> { r with Title = title }) }, Cmd.none
    | NewBodyChanged body ->
        { model with NewRecipe = model.NewRecipe |> Option.map (fun r -> { r with Body = body }) }, Cmd.none
    | AddRecipe ->
        match model.NewRecipe with
        | Some r when r.Title.Trim() <> "" ->
            { model with NewRecipe = None }, fireAndForget (Db.addRecipe (r.Title.Trim()) r.Body)
        | _ -> model, Cmd.none
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
let private linkWith (className: string) dispatch (segments: string list) (text: string) =
    Html.a
        [ prop.href (Router.format segments)
          prop.className className
          prop.text text
          prop.onClick (fun e ->
              e.preventDefault ()
              Router.navigate segments
              dispatch (UrlChanged segments)) ]

let private link dispatch = linkWith "" dispatch

let private navBar (page: Page) dispatch =
    let navLink segments text isActive =
        linkWith
            (if isActive then "font-semibold text-gray-900" else "text-gray-500 hover:text-gray-900")
            dispatch
            segments
            text

    let onRecipes =
        match page with
        | RecipeList
        | RecipeDetail _ -> true
        | _ -> false

    Html.nav
        [ prop.className "flex gap-6 border-b border-gray-200 px-4 py-3"
          prop.children
              [ navLink [] "Recipes" onRecipes
                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList) ] ]

let private newRecipeModal (recipe: NewRecipe) dispatch =
    let field = "w-full rounded border border-gray-300 px-2 py-1"

    Html.div
        [ prop.className "fixed inset-0 flex items-center justify-center bg-black/50"
          prop.onClick (fun _ -> dispatch CloseNewRecipe)
          prop.children
              [ Html.form
                    [ prop.className "w-full max-w-md rounded bg-white p-4 flex flex-col gap-3"
                      prop.onClick (fun e -> e.stopPropagation ())
                      prop.onSubmit (fun e ->
                          e.preventDefault ()
                          dispatch AddRecipe)
                      prop.children
                          [ Html.h2 [ prop.className "text-lg font-semibold"; prop.text "New Recipe" ]
                            Html.input
                                [ prop.className field
                                  prop.type' "text"
                                  prop.placeholder "Title"
                                  prop.autoFocus true
                                  prop.value recipe.Title
                                  prop.onChange (NewTitleChanged >> dispatch) ]
                            Html.textarea
                                [ prop.className field
                                  prop.rows 8
                                  prop.placeholder "Recipe"
                                  prop.value recipe.Body
                                  prop.onChange (NewBodyChanged >> dispatch) ]
                            Html.div
                                [ prop.className "flex justify-end gap-2"
                                  prop.children
                                      [ Html.button
                                            [ prop.type' "button"
                                              prop.className "rounded px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.onClick (fun _ -> dispatch CloseNewRecipe) ]
                                        Html.button
                                            [ prop.type' "submit"
                                              prop.className "rounded bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                                              prop.text "Save" ] ] ] ] ] ] ]

let private listPage (model: Model) dispatch =
    Html.div
        [ Html.button
              [ prop.type' "button"
                prop.className "mb-3 rounded bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                prop.text "New Recipe"
                prop.onClick (fun _ -> dispatch OpenNewRecipe) ]
          match model.Recipes with
          | [] -> Html.p "No recipes yet."
          | recipes ->
              Html.ul
                  [ for r in recipes ->
                        Html.li [ linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipes"; r.id ] r.title ] ]
          match model.NewRecipe with
          | Some recipe -> newRecipeModal recipe dispatch
          | None -> Html.none ]

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
              Html.pre [ prop.className "whitespace-pre-wrap font-sans"; prop.text recipe.body ]
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
        [ navBar model.Page dispatch
          Html.main
              [ prop.className "px-4 py-3"
                prop.children
                    [ match model.Page with
                      | RecipeList -> listPage model dispatch
                      | RecipeDetail id -> detailPage model id dispatch
                      | ShoppingList -> Html.p "Hello world"
                      | NotFound -> Html.p "Page not found." ] ] ]

let root = ReactDOM.createRoot (document.getElementById "root")
root.render (View())
