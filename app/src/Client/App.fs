module App

open Browser.Dom
open Elmish
open Fable.Core
open Feliz
open Feliz.UseElmish
open Db

/// Tiny path router over the History API: "/recipe/<id>" <-> ["recipe"; "<id>"].
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

/// Editable recipe fields, used by both the "New Recipe" modal and the detail page.
type RecipeForm = { Title: string; Body: string }

/// Who is signed in. Data only starts syncing once we know.
type Session =
    | Checking
    | SignedOut
    | SignedIn of Shared.User

type Model =
    { Page: Page
      Session: Session
      Recipes: Recipe list
      ShoppingItems: ShoppingItem list
      /// `Some` while the "New Recipe" modal is open.
      NewRecipe: RecipeForm option
      /// Detail-page form; `None` until the recipe being viewed has loaded.
      Edit: RecipeForm option
      /// Recipe awaiting delete confirmation on the list page.
      PendingDelete: string option }

type Msg =
    | SessionChecked of Shared.User option
    | SignIn
    | SignOut
    | UrlChanged of string list
    | RecipesChanged of Recipe list
    | ShoppingItemsChanged of ShoppingItem list
    | AddToShoppingList of recipeId: string
    | SetShoppingItemDone of id: string * isDone: bool
    | DeleteShoppingItem of id: string
    | ClearDoneShoppingItems
    | OpenNewRecipe
    | CloseNewRecipe
    | NewTitleChanged of string
    | NewBodyChanged of string
    | AddRecipe
    | EditTitleChanged of string
    | EditBodyChanged of string
    | SaveRecipe of id: string
    | ConfirmDelete of id: string
    | CancelDelete
    | DeleteRecipe of id: string
    | Ignore

let private parseUrl (segments: string list) =
    match segments with
    | [] -> RecipeList
    | [ "recipe"; id ] -> RecipeDetail id
    | [ "shopping-list" ] -> ShoppingList
    | _ -> NotFound

let private fireAndForget (work: JS.Promise<'a>) =
    Cmd.OfPromise.either (fun () -> work) () (fun _ -> Ignore) (fun err ->
        Browser.Dom.console.error err
        Ignore)

let init () =
    { Page = parseUrl (Router.currentUrl ())
      Session = Checking
      Recipes = []
      ShoppingItems = []
      NewRecipe = None
      Edit = None
      PendingDelete = None },
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Router.onUrlChanged (UrlChanged >> dispatch))
          Cmd.OfPromise.either Db.currentUser () SessionChecked (fun err ->
              console.error err
              SessionChecked None) ]

/// Once signed in: open the live queries and start syncing.
let private startSync () =
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Db.watchRecipes (RecipesChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchShoppingItems (ShoppingItemsChanged >> dispatch))
          Cmd.ofEffect (fun _ -> Db.connect () |> Promise.catch (fun e -> console.error e) |> ignore) ]

let private findRecipe id (recipes: Recipe list) =
    recipes |> List.tryFind (fun r -> r.id = id)

let private formOf (recipe: Recipe) = { Title = recipe.title; Body = recipe.body }

let update msg model =
    match msg with
    | SessionChecked(Some user) -> { model with Session = SignedIn user }, startSync ()
    | SessionChecked None -> { model with Session = SignedOut }, Cmd.none
    | SignIn -> model, Cmd.ofEffect (fun _ -> Db.signIn ())
    | SignOut -> model, fireAndForget (Db.signOut ())
    | UrlChanged segments ->
        let page = parseUrl segments

        let edit =
            match page with
            | RecipeDetail id -> findRecipe id model.Recipes |> Option.map formOf
            | _ -> None

        { model with Page = page; Edit = edit }, Cmd.none
    | RecipesChanged recipes ->
        // Populate the detail form once the recipe arrives; never clobber an in-progress edit.
        let edit =
            match model.Page, model.Edit with
            | RecipeDetail id, None -> findRecipe id recipes |> Option.map formOf
            | _ -> model.Edit

        { model with Recipes = recipes; Edit = edit }, Cmd.none
    | ShoppingItemsChanged items -> { model with ShoppingItems = items }, Cmd.none
    | AddToShoppingList id ->
        match findRecipe id model.Recipes with
        | Some recipe ->
            let ingredients = Cooklang.ingredients (Cooklang.parse recipe.body).Recipe
            model, fireAndForget (Db.addToShoppingList ingredients)
        | None -> model, Cmd.none
    | SetShoppingItemDone(id, isDone) -> model, fireAndForget (Db.setShoppingItemDone id isDone)
    | DeleteShoppingItem id -> model, fireAndForget (Db.deleteShoppingItem id)
    | ClearDoneShoppingItems -> model, fireAndForget (Db.clearDoneShoppingItems ())
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
    | EditTitleChanged title ->
        { model with Edit = model.Edit |> Option.map (fun r -> { r with Title = title }) }, Cmd.none
    | EditBodyChanged body ->
        { model with Edit = model.Edit |> Option.map (fun r -> { r with Body = body }) }, Cmd.none
    | SaveRecipe id ->
        match model.Edit with
        | Some r when r.Title.Trim() <> "" -> model, fireAndForget (Db.updateRecipe id (r.Title.Trim()) r.Body)
        | _ -> model, Cmd.none
    | ConfirmDelete id -> { model with PendingDelete = Some id }, Cmd.none
    | CancelDelete -> { model with PendingDelete = None }, Cmd.none
    | DeleteRecipe id ->
        if model.Page <> RecipeList then Router.navigate []

        { model with
            Page = RecipeList
            Edit = None
            PendingDelete = None },
        fireAndForget (Db.deleteRecipe id)
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

let private navBar (page: Page) (user: Shared.User) dispatch =
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
        [ prop.className "flex items-center gap-6 border-b border-gray-200 px-4 py-3"
          prop.children
              [ navLink [] "Recipes" onRecipes
                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList)
                Html.span [ prop.className "ml-auto text-sm text-gray-500"; prop.text user.Email ]
                Html.button
                    [ prop.className "text-sm text-gray-500 hover:text-gray-900"
                      prop.text "Sign out"
                      prop.onClick (fun _ -> dispatch SignOut) ] ] ]

let private loginPage dispatch =
    Html.main
        [ prop.className "flex min-h-screen flex-col items-center justify-center gap-4 px-4"
          prop.children
              [ Html.h1 [ prop.className "text-2xl font-semibold"; prop.text "Plaintext Pantry" ]
                Html.p [ prop.className "text-gray-500"; prop.text "Recipes in plain text, on every device." ]
                Html.button
                    [ prop.className "rounded bg-gray-900 px-4 py-2 text-white hover:bg-gray-700"
                      prop.text "Sign in"
                      prop.onClick (fun _ -> dispatch SignIn) ] ] ]

let private newRecipeModal (recipe: RecipeForm) (known: CooklangEditor.KnownNames) dispatch =
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
                            CooklangEditor.CooklangEditor(recipe.Body, known, "Recipe", NewBodyChanged >> dispatch)
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

let private confirmDeleteModal (recipe: Recipe) dispatch =
    Html.div
        [ prop.className "fixed inset-0 flex items-center justify-center bg-black/50"
          prop.onClick (fun _ -> dispatch CancelDelete)
          prop.children
              [ Html.div
                    [ prop.className "w-full max-w-sm rounded bg-white p-4 flex flex-col gap-3"
                      prop.onClick (fun e -> e.stopPropagation ())
                      prop.children
                          [ Html.p [ prop.text $"Delete \"{recipe.title}\"?" ]
                            Html.div
                                [ prop.className "flex justify-end gap-2"
                                  prop.children
                                      [ Html.button
                                            [ prop.type' "button"
                                              prop.className "rounded px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.autoFocus true
                                              prop.onClick (fun _ -> dispatch CancelDelete) ]
                                        Html.button
                                            [ prop.type' "button"
                                              prop.className "rounded bg-red-600 px-3 py-1 text-white hover:bg-red-700"
                                              prop.text "Delete"
                                              prop.onClick (fun _ -> dispatch (DeleteRecipe recipe.id)) ] ] ] ] ] ] ]

let private listPage (model: Model) (known: CooklangEditor.KnownNames) dispatch =
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
                  [ prop.className "flex flex-col gap-1"
                    prop.children
                        [ for r in recipes ->
                              Html.li
                                  [ prop.className "flex items-center gap-2"
                                    prop.children
                                        [ Html.button
                                              [ prop.type' "button"
                                                prop.className "px-1 text-gray-400 hover:text-red-600"
                                                prop.title "Delete recipe"
                                                prop.text "×"
                                                prop.onClick (fun _ -> dispatch (ConfirmDelete r.id)) ]
                                          linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipe"; r.id ] r.title ] ] ] ]
          match model.NewRecipe with
          | Some recipe -> newRecipeModal recipe known dispatch
          | None -> Html.none
          match model.PendingDelete |> Option.bind (fun id -> findRecipe id model.Recipes) with
          | Some recipe -> confirmDeleteModal recipe dispatch
          | None -> Html.none ]

let private detailPage (model: Model) (id: string) (known: CooklangEditor.KnownNames) dispatch =
    match findRecipe id model.Recipes, model.Edit with
    | Some _, Some edit ->
        let field = "w-full rounded border border-gray-300 px-2 py-1"

        Html.div
            [ Html.p [ prop.className "mb-3"; prop.children [ link dispatch [] "← All recipes" ] ]
              Html.form
                  [ prop.className "max-w-2xl flex flex-col gap-3"
                    prop.onSubmit (fun e ->
                        e.preventDefault ()
                        dispatch (SaveRecipe id))
                    prop.children
                        [ Html.input
                              [ prop.className (field + " text-lg font-semibold")
                                prop.type' "text"
                                prop.placeholder "Title"
                                prop.value edit.Title
                                prop.onChange (EditTitleChanged >> dispatch) ]
                          CooklangEditor.CooklangEditor(edit.Body, known, "Recipe", EditBodyChanged >> dispatch)
                          Html.div
                              [ prop.className "flex justify-between"
                                prop.children
                                    [ Html.div
                                          [ prop.className "flex gap-2"
                                            prop.children
                                                [ Html.button
                                                      [ prop.type' "submit"
                                                        prop.className "rounded bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                                                        prop.text "Save" ]
                                                  Html.button
                                                      [ prop.type' "button"
                                                        prop.className "rounded border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50"
                                                        prop.text "Add to shopping list"
                                                        prop.onClick (fun _ -> dispatch (AddToShoppingList id)) ] ] ]
                                      Html.button
                                          [ prop.type' "button"
                                            prop.className "rounded px-3 py-1 text-red-600 hover:text-red-800"
                                            prop.text "Delete"
                                            prop.onClick (fun _ -> dispatch (DeleteRecipe id)) ] ] ] ] ] ]
    | _ ->
        Html.div
            [ Html.p "Recipe not found (it may still be syncing, or it was deleted)."
              link dispatch [] "Back to recipes" ]

let private shoppingListPage (model: Model) dispatch =
    match model.ShoppingItems with
    | [] -> Html.p "Nothing to buy."
    | items ->
        let anyDone = items |> List.exists (fun i -> i.``done`` <> 0)

        Html.div
            [ Html.button
                  [ prop.type' "button"
                    prop.className "mb-3 rounded border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                    prop.text "Clear checked"
                    prop.disabled (not anyDone)
                    prop.onClick (fun _ -> dispatch ClearDoneShoppingItems) ]
              Html.ul
                  [ prop.className "flex flex-col gap-1"
                    prop.children
                        [ for item in items ->
                              let isDone = item.``done`` <> 0

                              Html.li
                                  [ prop.className "flex items-center gap-2"
                                    prop.children
                                        [ Html.input
                                              [ prop.type' "checkbox"
                                                prop.isChecked isDone
                                                prop.onChange (fun (checked: bool) ->
                                                    dispatch (SetShoppingItemDone(item.id, checked))) ]
                                          Html.span
                                              [ prop.className (if isDone then "text-gray-400 line-through" else "")
                                                prop.text (
                                                    [ item.quantity; item.unit; item.name ]
                                                    |> List.filter ((<>) "")
                                                    |> String.concat " "
                                                ) ]
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "ml-auto px-2 text-gray-400 hover:text-red-600"
                                                prop.title "Remove"
                                                prop.text "×"
                                                prop.onClick (fun _ -> dispatch (DeleteShoppingItem item.id)) ] ] ] ] ] ]

[<ReactComponent>]
let View () =
    let model, dispatch = React.useElmish (init, update)

    // Names from every recipe, for the editor's completions. Recomputed only when the synced list changes.
    let known =
        React.useMemo (
            (fun () ->
                let recipes = model.Recipes |> List.map (fun r -> (Cooklang.parse r.body).Recipe)

                { CooklangEditor.KnownNames.Ingredients =
                    recipes |> List.collect Cooklang.ingredients |> List.map (fun i -> i.Name) |> List.distinct
                  CooklangEditor.KnownNames.Cookware =
                    recipes |> List.collect Cooklang.cookware |> List.map (fun c -> c.Name) |> List.distinct }),
            [| box model.Recipes |]
        )

    match model.Session with
    | Checking -> Html.none
    | SignedOut -> loginPage dispatch
    | SignedIn user ->
        Html.div
            [ navBar model.Page user dispatch
              Html.main
                  [ prop.className "px-4 py-3"
                    prop.children
                        [ match model.Page with
                          | RecipeList -> listPage model known dispatch
                          | RecipeDetail id -> detailPage model id known dispatch
                          | ShoppingList -> shoppingListPage model dispatch
                          | NotFound -> Html.p "Page not found." ] ] ]

let root = ReactDOM.createRoot (document.getElementById "root")
root.render (View())
