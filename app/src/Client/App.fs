module App

open System
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

    /// Like navigate, but doesn't leave a history entry (auth redirects).
    let replace segments =
        window.history.replaceState (null, "", format segments)

    let onUrlChanged (handler: string list -> unit) =
        window.addEventListener ("popstate", fun _ -> handler (currentUrl ()))

type Page =
    | RecipeList
    | RecipeDetail of id: string
    | ShoppingList
    | Menu
    | Login
    | Terms
    | Privacy
    | NotFound

/// Pages anyone can read, signed in or not.
let private isPublic page =
    match page with
    | Login
    | Terms
    | Privacy -> true
    | _ -> false

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
      /// Where to land after signing in: the page that hit the 401.
      ReturnTo: string list
      Recipes: Recipe list
      /// Newest first; the head is the list shown and added to.
      ShoppingLists: ShoppingList list
      /// Every list's items; `currentList` picks out the ones on screen.
      ShoppingItems: ShoppingItem list
      /// Newest first; the head is the menu shown and added to.
      Menus: Menu list
      /// Every menu's entries; `currentMenu` picks out the ones on screen.
      MenuRecipes: MenuRecipe list
      /// `Some` while the "New Recipe" modal is open.
      NewRecipe: RecipeForm option
      /// Detail-page form; `None` until the recipe being viewed has loaded.
      Edit: RecipeForm option
      /// Recipe awaiting delete confirmation on the list page.
      PendingDelete: string option
      /// Menu entry awaiting confirmation to be taken off the menu.
      PendingMenuRemove: string option
      /// Recipe whose ingredients are all on the list already, awaiting
      /// confirmation to add them again.
      PendingShoppingAdd: string option
      /// The blank row at the end of the shopping list.
      NewItem: string
      /// The mobile bottom-sheet menu, toggled by the logo button.
      MenuOpen: bool }

type Msg =
    | SessionChecked of Shared.User option
    | SignIn
    | SignOut
    | UrlChanged of string list
    | RecipesChanged of Recipe list
    | ShoppingListsChanged of ShoppingList list
    | ShoppingItemsChanged of ShoppingItem list
    | MenusChanged of Menu list
    | MenuRecipesChanged of MenuRecipe list
    | AddToMenu of recipeId: string
    | ConfirmMenuRemove of id: string
    | CancelMenuRemove
    | RemoveFromMenu of id: string
    | AddToShoppingList of recipeId: string
    /// Add even though every ingredient is already on the list.
    | AddToShoppingListAnyway of recipeId: string
    | CancelShoppingAdd
    | NewItemChanged of string
    | AddNewItem
    | SetShoppingItemDone of id: string * isDone: bool
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
    | ToggleMenu
    | Ignore

let private parseUrl (segments: string list) =
    match segments with
    | [] -> RecipeList
    | [ "recipe"; id ] -> RecipeDetail id
    | [ "shopping-list" ] -> ShoppingList
    | [ "menu" ] -> Menu
    | [ "login" ] -> Login
    | [ "terms" ] -> Terms
    | [ "privacy" ] -> Privacy
    | _ -> NotFound

let private fireAndForget (work: JS.Promise<'a>) =
    Cmd.OfPromise.either (fun () -> work) () (fun _ -> Ignore) (fun err ->
        Browser.Dom.console.error err
        Ignore)

let init () =
    { Page = parseUrl (Router.currentUrl ())
      Session = Checking
      ReturnTo = []
      Recipes = []
      ShoppingLists = []
      ShoppingItems = []
      Menus = []
      MenuRecipes = []
      NewRecipe = None
      Edit = None
      PendingDelete = None
      PendingMenuRemove = None
      PendingShoppingAdd = None
      NewItem = ""
      MenuOpen = false },
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Router.onUrlChanged (UrlChanged >> dispatch))
          Cmd.OfPromise.either Db.currentUser () SessionChecked (fun err ->
              console.error err
              SessionChecked None) ]

/// Once signed in: open the live queries and start syncing.
let private startSync () =
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Db.watchRecipes (RecipesChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchShoppingLists (ShoppingListsChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchShoppingItems (ShoppingItemsChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchMenus (MenusChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> Db.watchMenuRecipes (MenuRecipesChanged >> dispatch))
          Cmd.ofEffect (fun _ -> Db.connect () |> Promise.catch (fun e -> console.error e) |> ignore) ]

let private findRecipe id (recipes: Recipe list) =
    recipes |> List.tryFind (fun r -> r.id = id)

let private formOf (recipe: Recipe) = { Title = recipe.title; Body = recipe.body }

let private currentList (model: Model) = List.tryHead model.ShoppingLists

let private currentMenu (model: Model) = List.tryHead model.Menus

let private ingredientsOf (recipe: Recipe) =
    Cooklang.ingredients (Cooklang.parse recipe.body).Recipe

/// Puts a recipe's ingredients on the list, shown at once and persisted after.
let private addToShoppingList (recipe: Recipe) (model: Model) =
    let lists, items, plan =
        Db.planShoppingAdd model.ShoppingLists model.ShoppingItems (ingredientsOf recipe)

    { model with
        ShoppingLists = lists
        ShoppingItems = items
        PendingShoppingAdd = None },
    fireAndForget (Db.addToShoppingList plan)

let update msg model =
    match msg with
    | SessionChecked(Some user) ->
        // A signed-in user has no business on /login.
        let page =
            if model.Page = Login then
                Router.replace []
                RecipeList
            else
                model.Page

        { model with Session = SignedIn user; Page = page }, startSync ()
    | SessionChecked None ->
        let returnTo = if isPublic model.Page then [] else Router.currentUrl ()
        if not (isPublic model.Page) then Router.replace [ "login" ]

        let page = if isPublic model.Page then model.Page else Login
        { model with Session = SignedOut; Page = page; ReturnTo = returnTo }, Cmd.none
    | SignIn -> model, Cmd.ofEffect (fun _ -> Db.signIn (Router.format model.ReturnTo))
    | SignOut -> model, fireAndForget (Db.signOut ())
    | UrlChanged segments ->
        let page = parseUrl segments

        let edit =
            match page with
            | RecipeDetail id -> findRecipe id model.Recipes |> Option.map formOf
            | _ -> None

        { model with Page = page; Edit = edit; MenuOpen = false }, Cmd.none
    | RecipesChanged recipes ->
        // Populate the detail form once the recipe arrives; never clobber an in-progress edit.
        let edit =
            match model.Page, model.Edit with
            | RecipeDetail id, None -> findRecipe id recipes |> Option.map formOf
            | _ -> model.Edit

        { model with Recipes = recipes; Edit = edit }, Cmd.none
    | ShoppingListsChanged lists -> { model with ShoppingLists = lists }, Cmd.none
    | ShoppingItemsChanged items -> { model with ShoppingItems = items }, Cmd.none
    | MenusChanged menus -> { model with Menus = menus }, Cmd.none
    | MenuRecipesChanged entries -> { model with MenuRecipes = entries }, Cmd.none
    // Every write below is applied to the model first and persisted second,
    // so the UI moves with the tap; the watched queries confirm a beat later.
    // Lists are kept in the order the queries use, so that confirmation
    // changes nothing on screen.
    | AddToShoppingList id ->
        match findRecipe id model.Recipes with
        | Some recipe when Db.allIngredientsPresent model.ShoppingLists model.ShoppingItems (ingredientsOf recipe) ->
            // Probably a double tap; ask before doubling the quantities.
            { model with PendingShoppingAdd = Some id }, Cmd.none
        | Some recipe -> addToShoppingList recipe model
        | None -> model, Cmd.none
    | AddToShoppingListAnyway id ->
        match findRecipe id model.Recipes with
        | Some recipe -> addToShoppingList recipe model
        | None -> { model with PendingShoppingAdd = None }, Cmd.none
    | CancelShoppingAdd -> { model with PendingShoppingAdd = None }, Cmd.none
    | AddToMenu id ->
        let menus, entries, plan = Db.planMenuAdd model.Menus model.MenuRecipes id
        { model with Menus = menus; MenuRecipes = entries }, fireAndForget (Db.addToMenu plan)
    | ConfirmMenuRemove id -> { model with PendingMenuRemove = Some id }, Cmd.none
    | CancelMenuRemove -> { model with PendingMenuRemove = None }, Cmd.none
    | RemoveFromMenu id ->
        { model with
            MenuRecipes = model.MenuRecipes |> List.filter (fun e -> e.id <> id)
            PendingMenuRemove = None },
        fireAndForget (Db.removeMenuRecipe id)
    | NewItemChanged text -> { model with NewItem = text }, Cmd.none
    | AddNewItem ->
        if model.NewItem.Trim() = "" then
            model, Cmd.none
        else
            let lists, items, plan = Db.planFreeItemAdd model.ShoppingLists model.ShoppingItems model.NewItem

            { model with ShoppingLists = lists; ShoppingItems = items; NewItem = "" },
            fireAndForget (Db.addToShoppingList plan)
    | SetShoppingItemDone(id, isDone) ->
        let items =
            model.ShoppingItems
            |> List.map (fun i -> if i.id = id then { i with ``done`` = (if isDone then 1 else 0) } else i)
            |> List.sortBy (fun i -> i.``done``, i.created_at, i.id)

        { model with ShoppingItems = items }, fireAndForget (Db.setShoppingItemDone id isDone)
    | ClearDoneShoppingItems ->
        match currentList model with
        | Some list ->
            { model with
                ShoppingItems =
                    model.ShoppingItems
                    |> List.filter (fun i -> i.list_id <> list.id || i.``done`` = 0) },
            fireAndForget (Db.clearDoneShoppingItems list.id)
        | None -> model, Cmd.none
    | OpenNewRecipe -> { model with NewRecipe = Some { Title = ""; Body = "" } }, Cmd.none
    | CloseNewRecipe -> { model with NewRecipe = None }, Cmd.none
    | NewTitleChanged title ->
        { model with NewRecipe = model.NewRecipe |> Option.map (fun r -> { r with Title = title }) }, Cmd.none
    | NewBodyChanged body ->
        { model with NewRecipe = model.NewRecipe |> Option.map (fun r -> { r with Body = body }) }, Cmd.none
    | AddRecipe ->
        match model.NewRecipe with
        | Some r when r.Title.Trim() <> "" ->
            let recipe = Db.newRecipe (r.Title.Trim()) r.Body

            { model with
                NewRecipe = None
                Recipes = recipe :: model.Recipes },
            fireAndForget (Db.addRecipe recipe)
        | _ -> model, Cmd.none
    | EditTitleChanged title ->
        { model with Edit = model.Edit |> Option.map (fun r -> { r with Title = title }) }, Cmd.none
    | EditBodyChanged body ->
        { model with Edit = model.Edit |> Option.map (fun r -> { r with Body = body }) }, Cmd.none
    | SaveRecipe id ->
        match model.Edit with
        | Some r when r.Title.Trim() <> "" ->
            let title = r.Title.Trim()

            let recipes =
                model.Recipes
                |> List.map (fun x -> if x.id = id then { x with title = title; body = r.Body } else x)

            { model with Recipes = recipes }, fireAndForget (Db.updateRecipe id title r.Body)
        | _ -> model, Cmd.none
    | ConfirmDelete id -> { model with PendingDelete = Some id }, Cmd.none
    | CancelDelete -> { model with PendingDelete = None }, Cmd.none
    | DeleteRecipe id ->
        if model.Page <> RecipeList then Router.navigate []

        { model with
            Page = RecipeList
            Edit = None
            PendingDelete = None
            Recipes = model.Recipes |> List.filter (fun r -> r.id <> id)
            MenuRecipes = model.MenuRecipes |> List.filter (fun e -> e.recipe_id <> id) },
        fireAndForget (Db.deleteRecipe id)
    | ToggleMenu -> { model with MenuOpen = not model.MenuOpen }, Cmd.none
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

/// Which nav link is highlighted for a page.
let private onRecipes page =
    match page with
    | RecipeList
    | RecipeDetail _ -> true
    | _ -> false

/// Desktop: the bar across the top. Hidden on phones, where `mobileMenu` takes over.
let private navBar (page: Page) (user: Shared.User) dispatch =
    let navLink segments text isActive =
        linkWith
            (if isActive then "font-semibold text-gray-900" else "text-gray-500 hover:text-gray-900")
            dispatch
            segments
            text

    Html.nav
        [ prop.className "hidden items-center gap-6 border-b border-gray-200 px-4 py-3 md:flex"
          prop.children
              [ // Just the door, no wordmark, at the top left.
                Html.a
                    [ prop.href "/"
                      // Sits a little tighter to "Recipes" than the links do to each other.
                      prop.className "-mr-2"
                      prop.ariaLabel "Plaintext Pantry"
                      prop.onClick (fun e ->
                          e.preventDefault ()
                          Router.navigate []
                          dispatch (UrlChanged []))
                      prop.children [ Html.img [ prop.src "/brand/icon.png"; prop.alt ""; prop.className "h-5 w-5" ] ] ]
                navLink [] "Recipes" (onRecipes page)
                navLink [ "menu" ] "Menu" (page = Menu)
                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList)
                Html.span [ prop.className "ml-auto text-sm text-gray-500"; prop.text user.Email ]
                Html.button
                    [ prop.className "text-sm text-gray-500 hover:text-gray-900"
                      prop.text "Sign out"
                      prop.onClick (fun _ -> dispatch SignOut) ] ] ]

/// Phones: no top bar. The logo sits bottom-right (the one round button in the
/// app) and toggles a bottom sheet holding the same links. Navigating closes
/// it (see UrlChanged).
let private mobileMenu (page: Page) (user: Shared.User) (isOpen: bool) dispatch =
    let navLink segments text isActive =
        linkWith
            ("block py-3 text-lg " + if isActive then "font-semibold text-gray-900" else "text-gray-600")
            dispatch
            segments
            text

    Html.div
        [ prop.className "md:hidden"
          prop.children
              [ if isOpen then
                    Html.div
                        [ prop.className "fade-in fixed inset-0 z-30 bg-black/30"
                          prop.ariaHidden true
                          prop.onClick (fun _ -> dispatch ToggleMenu) ]
                    Html.nav
                        [ prop.className
                              "sheet-in fixed inset-x-0 bottom-0 z-40 border-t border-gray-200 bg-white px-6 pt-4 pb-16"
                          prop.children
                              [ navLink [] "Recipes" (onRecipes page)
                                navLink [ "menu" ] "Menu" (page = Menu)
                                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList)
                                Html.div
                                    [ prop.className "mt-3 flex items-center justify-between border-t border-gray-200 pt-4"
                                      prop.children
                                          [ Html.span [ prop.className "text-sm text-gray-500"; prop.text user.Email ]
                                            Html.button
                                                [ prop.className "text-sm text-gray-500"
                                                  prop.text "Sign out"
                                                  prop.onClick (fun _ -> dispatch SignOut) ] ] ] ] ]
                Html.button
                    [ prop.className
                          "fixed right-4 bottom-4 z-50 flex h-9 w-9 items-center justify-center rounded-full bg-white shadow-lg shadow-black/15"
                      prop.ariaLabel (if isOpen then "Close menu" else "Open menu")
                      prop.ariaExpanded isOpen
                      // Toggle on finger-down, not on the click that follows the
                      // finger lifting: the sheet is already moving by the time a
                      // click would have fired. Keyboard activation still comes
                      // through as a click, with no pointer (detail = 0).
                      prop.onPointerDown (fun _ -> dispatch ToggleMenu)
                      prop.onClick (fun e -> if e.detail = 0 then dispatch ToggleMenu)
                      prop.children [ Html.img [ prop.src "/brand/icon.png"; prop.alt ""; prop.className "h-5 w-5" ] ] ] ] ]

/// Terms · Privacy · joebad.com, under the pages that render outside the shell.
let private legalFooter dispatch =
    let item = linkWith "hover:text-gray-900 transition-colors" dispatch
    let dot = Html.span [ prop.ariaHidden true; prop.text "·" ]

    Html.footer
        [ prop.className "flex shrink-0 items-center justify-center gap-3 py-6 text-xs text-gray-500"
          prop.children
              [ item [ "terms" ] "Terms"
                dot
                item [ "privacy" ] "Privacy"
                dot
                Html.a
                    [ prop.href "https://joebad.com"
                      prop.target "_blank"
                      prop.rel "noopener noreferrer"
                      prop.className "hover:text-gray-900 transition-colors"
                      prop.text "joebad.com" ] ] ]

/// Google's "G", in a white circle so the brand colours read against the magenta button.
let private googleIcon =
    Html.span
        [ prop.className "flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-white"
          prop.children
              [ Svg.svg
                    [ svg.viewBox (0, 0, 24, 24)
                      svg.className "block h-3 w-3"
                      svg.custom ("aria-hidden", "true")
                      svg.children
                          [ Svg.path
                                [ svg.fill "#4285F4"
                                  svg.d
                                      "M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z" ]
                            Svg.path
                                [ svg.fill "#34A853"
                                  svg.d
                                      "M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" ]
                            Svg.path
                                [ svg.fill "#FBBC05"
                                  svg.d
                                      "M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" ]
                            Svg.path
                                [ svg.fill "#EA4335"
                                  svg.d
                                      "M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" ] ] ] ] ]

let private loginPage dispatch =
    Html.main
        [ prop.className "flex min-h-screen flex-col px-4"
          prop.children
              [ Html.div
                    [ prop.className "flex flex-1 flex-col items-center justify-center gap-8"
                      prop.children
                          [ Html.img
                                [ prop.src "/brand/logo.svg"; prop.alt "Plaintext Pantry"; prop.className "w-48 max-w-full" ]
                            Html.p
                                [ prop.className "max-w-sm text-center text-lg text-gray-600"
                                  prop.text "Local-first, open source, free, minimalist recipe and grocery list manager" ]
                            Html.button
                                [ prop.className
                                      "inline-flex items-center gap-2.5 bg-brand px-4 py-2.5 text-sm font-semibold leading-none text-white hover:shadow-md hover:shadow-brand/30"
                                  prop.onClick (fun _ -> dispatch SignIn)
                                  prop.children [ googleIcon; Html.span [ prop.className "pt-px"; prop.text "Login with Google" ] ] ] ] ]
                legalFooter dispatch ] ]

/// Terms and Privacy: readable signed out, so they sit outside the nav shell.
let private legalPage (title: string) (paragraphs: ReactElement list) dispatch =
    Html.main
        [ prop.className "flex min-h-screen flex-col px-4"
          prop.children
              [ Html.div
                    [ prop.className "mx-auto w-full max-w-2xl flex-1 py-6 text-sm leading-relaxed text-gray-800"
                      prop.children
                          [ Html.div
                                [ prop.className "mb-4 flex items-center gap-3"
                                  prop.children
                                      [ linkWith "text-gray-500 hover:text-gray-900" dispatch [] "← Back"
                                        Html.h1 [ prop.className "font-medium"; prop.text title ] ] ]
                            Html.div [ prop.className "space-y-4"; prop.children paragraphs ] ] ]
                legalFooter dispatch ] ]

let private termsPage dispatch =
    legalPage
        "Terms of Service"
        [ Html.p "Last updated: September 19, 2026"
          Html.p
              "By using Plaintext Pantry, you agree to these terms. Plaintext Pantry is a free, open source recipe and grocery list manager, provided as-is while it is being built."
          Html.p
              "You are responsible for your account and for anything done with it. Do not misuse the service, attempt to disrupt it, or access it through unauthorized means."
          Html.p
              "Your recipes are yours. Plaintext Pantry stores them only so they can sync between your devices and the assistants you connect; it claims no rights over them."
          Html.p
              "Plaintext Pantry may change, suspend, or discontinue features at any time, and does not guarantee availability or that your data will never be lost. Keep your own copies of anything you cannot afford to lose; the format is plain text so that is easy."
          Html.p
              "The service is provided \"as is\" without warranties of any kind. To the fullest extent permitted by law, Plaintext Pantry and its operators are not liable for damages arising from your use of it."
          Html.p
              "These terms may be updated from time to time. Continued use after changes take effect constitutes acceptance of the updated terms."
          Html.p
              [ prop.children
                    [ Html.text "Questions? Reach the author at "
                      Html.a
                          [ prop.href "https://joebad.com"
                            prop.target "_blank"
                            prop.rel "noopener noreferrer"
                            prop.className "text-brand underline"
                            prop.text "joebad.com" ]
                      Html.text "." ] ] ]
        dispatch

let private privacyPage dispatch =
    legalPage
        "Privacy Policy"
        [ Html.p "Last updated: September 19, 2026"
          Html.p
              "Plaintext Pantry respects your privacy. This policy describes what is collected and how it is used when you use the service."
          Html.p
              "When you sign in, Plaintext Pantry receives basic account information from your identity provider (such as your email address and name). It is used to authenticate you and to keep your data separate from everyone else's."
          Html.p
              "The recipes and shopping lists you create are stored on Plaintext Pantry's server so they can sync between your devices. They are also kept locally on each device you sign in from. Your content is not sent to third-party providers, and it is not used for anything other than serving it back to you."
          Html.p
              "If you connect an AI assistant through the MCP server, that assistant can read and change your recipes and lists on your behalf. What the assistant does with them is governed by its own provider's policy; you can revoke its access at any time."
          Html.p
              "Standard technical logs (request metadata and errors) are kept to keep the service secure and reliable. Your personal information is never sold."
          Html.p
              "Data is retained for as long as needed to operate the service and as required by law. You may request account deletion by contacting the author."
          Html.p
              "This policy may be updated from time to time. Material changes will be reflected by updating the date above."
          Html.p
              [ prop.children
                    [ Html.text "Contact the author at "
                      Html.a
                          [ prop.href "https://joebad.com"
                            prop.target "_blank"
                            prop.rel "noopener noreferrer"
                            prop.className "text-brand underline"
                            prop.text "joebad.com" ]
                      Html.text "." ] ] ]
        dispatch

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
                                              prop.className "px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.onClick (fun _ -> dispatch CloseNewRecipe) ]
                                        Html.button
                                            [ prop.type' "submit"
                                              prop.className "bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
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
                                              prop.className "px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.autoFocus true
                                              prop.onClick (fun _ -> dispatch CancelDelete) ]
                                        Html.button
                                            [ prop.type' "button"
                                              prop.className "bg-red-600 px-3 py-1 text-white hover:bg-red-700"
                                              prop.text "Delete"
                                              prop.onClick (fun _ -> dispatch (DeleteRecipe recipe.id)) ] ] ] ] ] ] ]

let private listPage (model: Model) (known: CooklangEditor.KnownNames) dispatch =
    Html.div
        [ Html.button
              [ prop.type' "button"
                prop.className "mb-3 bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
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
                                        [ // Same box as the number on the menu page, so the
                                          // + here and the × there share a column.
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "w-6 shrink-0 px-1 text-left text-gray-400 hover:text-red-600"
                                                prop.title "Delete recipe"
                                                prop.text "×"
                                                prop.onClick (fun _ -> dispatch (ConfirmDelete r.id)) ]
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "px-1 text-gray-400 hover:text-green-700"
                                                prop.title "Add to menu"
                                                prop.text "+"
                                                prop.onClick (fun _ -> dispatch (AddToMenu r.id)) ]
                                          linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipe"; r.id ] r.title ] ] ] ]
          match model.NewRecipe with
          | Some recipe -> newRecipeModal recipe known dispatch
          | None -> Html.none
          match model.PendingDelete |> Option.bind (fun id -> findRecipe id model.Recipes) with
          | Some recipe -> confirmDeleteModal recipe dispatch
          | None -> Html.none ]

/// Shown when every ingredient of the recipe is already on the current list.
let private confirmShoppingAddModal (recipe: Recipe) dispatch =
    Html.div
        [ prop.className "fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          prop.onClick (fun _ -> dispatch CancelShoppingAdd)
          prop.children
              [ Html.div
                    [ prop.className "w-full max-w-sm rounded bg-white p-4 flex flex-col gap-3"
                      prop.onClick (fun e -> e.stopPropagation ())
                      prop.children
                          [ Html.p
                                [ prop.text
                                      "All the ingredients for this recipe are already in the current shopping list. Add them again?" ]
                            Html.div
                                [ prop.className "flex justify-end gap-2"
                                  prop.children
                                      [ Html.button
                                            [ prop.type' "button"
                                              prop.className "px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.autoFocus true
                                              prop.onClick (fun _ -> dispatch CancelShoppingAdd) ]
                                        Html.button
                                            [ prop.type' "button"
                                              prop.className "bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                                              prop.text "Add again"
                                              prop.onClick (fun _ -> dispatch (AddToShoppingListAnyway recipe.id)) ] ] ] ] ] ] ]

let private detailPage (model: Model) (id: string) (known: CooklangEditor.KnownNames) dispatch =
    match findRecipe id model.Recipes, model.Edit with
    | Some _, Some edit ->
        let field = "w-full rounded border border-gray-300 px-2 py-1"

        // Phones: the form is the whole viewport, three rows - back + title,
        // the editor taking every remaining pixel (CodeMirror scrolls inside
        // it), and the actions. Desktop: the same rows in normal flow.
        Html.form
            [ prop.className "fixed inset-0 flex flex-col gap-3 p-4 md:static md:max-w-2xl md:p-0"
              prop.onSubmit (fun e ->
                  e.preventDefault ()
                  dispatch (SaveRecipe id))
              prop.children
                  [ Html.div
                        [ prop.className "flex shrink-0 items-center gap-3"
                          prop.children
                              [ linkWith "shrink-0 whitespace-nowrap text-sm text-blue-600 hover:text-blue-800" dispatch [] "← All recipes"
                                Html.input
                                    [ prop.className (field + " min-w-0 text-lg font-semibold")
                                      prop.type' "text"
                                      prop.placeholder "Title"
                                      prop.value edit.Title
                                      prop.onChange (EditTitleChanged >> dispatch) ] ] ]
                    Html.div
                        [ prop.className
                              "flex min-h-0 flex-1 flex-col [&>div]:min-h-0 [&>div]:flex-1 [&_.cm-editor]:h-full md:block"
                          prop.children
                              [ CooklangEditor.CooklangEditor(edit.Body, known, "Recipe", EditBodyChanged >> dispatch) ] ]
                    // Right padding on phones keeps the row clear of the menu button.
                    Html.div
                        [ prop.className "flex shrink-0 flex-wrap items-center gap-2 pr-14 md:pr-0"
                          prop.children
                              [ Html.button
                                    [ prop.type' "submit"
                                      prop.className "bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                                      prop.text "Save" ]
                                Html.button
                                    [ prop.type' "button"
                                      prop.className "border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50"
                                      prop.onClick (fun _ -> dispatch (AddToShoppingList id))
                                      // Shorter on phones so the row stays on one line.
                                      prop.children
                                          [ Html.span [ prop.className "md:hidden"; prop.text "Add to list" ]
                                            Html.span [ prop.className "hidden md:inline"; prop.text "Add to shopping list" ] ] ]
                                Html.button
                                    [ prop.type' "button"
                                      prop.className "ml-auto border border-red-300 px-3 py-1 text-red-600 hover:bg-red-50"
                                      prop.text "Delete"
                                      prop.onClick (fun _ -> dispatch (DeleteRecipe id)) ] ] ] ] ]
    | _ ->
        Html.div
            [ Html.p "Recipe not found (it may still be syncing, or it was deleted)."
              link dispatch [] "Back to recipes" ]

/// The blank row at the end of the list. Enter (the keyboard's "done" on
/// phones) adds the item and leaves the field focused for the next one;
/// tapping away adds it too. Either way the row is blank again afterwards.
let private newItemRow (text: string) dispatch =
    Html.li
        [ Html.form
              [ prop.className "flex items-center gap-2 py-1"
                prop.onSubmit (fun e ->
                    e.preventDefault ()
                    dispatch AddNewItem)
                prop.children
                    [ // Lines the text up with the checkboxes above without being one.
                      Html.input [ prop.type' "checkbox"; prop.disabled true; prop.ariaHidden true; prop.tabIndex -1 ]
                      Html.input
                          [ prop.className "min-w-0 flex-1 bg-transparent py-0.5 outline-none placeholder:text-gray-400"
                            prop.type' "text"
                            prop.placeholder "Add an item"
                            prop.ariaLabel "Add an item"
                            prop.autoComplete "off"
                            prop.custom ("enterKeyHint", "done")
                            prop.value text
                            prop.onChange (NewItemChanged >> dispatch)
                            prop.onBlur (fun _ -> dispatch AddNewItem) ] ] ] ]

/// "Created: 3 days ago", from a row's ISO timestamp.
let private createdAge (createdAt: string) =
    "Created: " + Shared.Created.age DateTime.Now ((DateTime.Parse createdAt).ToLocalTime())

let private shoppingListPage (model: Model) dispatch =
    let list = currentList model

    let items =
        match list with
        | Some list -> model.ShoppingItems |> List.filter (fun i -> i.list_id = list.id)
        | None -> []

    let anyDone = items |> List.exists (fun i -> i.``done`` <> 0)

    Html.div
        [ match list with
          | Some list ->
              // Bottom-left, on the same line as the menu button bottom-right.
              Html.p
                  [ prop.className "fixed bottom-4 left-4 z-50 flex h-9 items-center text-sm text-gray-500"
                    prop.text (createdAge list.created_at) ]
              Html.div
                  [ // Phones: name left, button at the right edge. Desktop: both on the left.
                    prop.className "mb-3 flex items-center justify-between gap-3 md:justify-start"
                    prop.children
                        [ Html.h2 [ prop.className "text-lg font-semibold"; prop.text list.name ]
                          Html.button
                              [ prop.type' "button"
                                prop.className "border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                                prop.text "Clear checked"
                                prop.disabled (not anyDone)
                                prop.onClick (fun _ -> dispatch ClearDoneShoppingItems) ] ] ]
          | None -> Html.none
          Html.ul
              [ prop.className "flex flex-col gap-1"
                prop.children
                    [ for item in items do
                          let isDone = item.``done`` <> 0

                          Html.li
                              [ // The whole row is the label, so tapping the text checks the box too.
                                Html.label
                                    [ prop.className "flex cursor-pointer select-none items-center gap-2 py-1"
                                      prop.children
                                          [ Html.input
                                                [ prop.type' "checkbox"
                                                  prop.isChecked isDone
                                                  prop.onChange (fun (isChecked: bool) ->
                                                      dispatch (SetShoppingItemDone(item.id, isChecked))) ]
                                            Html.span
                                                [ prop.className (if isDone then "text-gray-400 line-through" else "")
                                                  prop.text (
                                                      [ item.quantity; item.unit; item.name ]
                                                      |> List.filter ((<>) "")
                                                      |> String.concat " "
                                                  ) ] ] ] ]
                      newItemRow model.NewItem dispatch ] ] ]

let private confirmMenuRemoveModal (entry: MenuRecipe) (recipe: Recipe) dispatch =
    Html.div
        [ prop.className "fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          prop.onClick (fun _ -> dispatch CancelMenuRemove)
          prop.children
              [ Html.div
                    [ prop.className "w-full max-w-sm rounded bg-white p-4 flex flex-col gap-3"
                      prop.onClick (fun e -> e.stopPropagation ())
                      prop.children
                          [ Html.p [ prop.text $"Remove \"{recipe.title}\" from the menu?" ]
                            Html.div
                                [ prop.className "flex justify-end gap-2"
                                  prop.children
                                      [ Html.button
                                            [ prop.type' "button"
                                              prop.className "px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.autoFocus true
                                              prop.onClick (fun _ -> dispatch CancelMenuRemove) ]
                                        Html.button
                                            [ prop.type' "button"
                                              prop.className "bg-red-600 px-3 py-1 text-white hover:bg-red-700"
                                              prop.text "Remove"
                                              prop.onClick (fun _ -> dispatch (RemoveFromMenu entry.id)) ] ] ] ] ] ] ]

/// The current menu: its recipes in the order they were added, each with a
/// badge when the current shopping list already has all its ingredients.
let private menuPage (model: Model) dispatch =
    let menu = currentMenu model

    let entries =
        match menu with
        | Some menu -> model.MenuRecipes |> List.filter (fun e -> e.menu_id = menu.id)
        | None -> []

    Html.div
        [ match menu with
          | Some menu ->
              // Bottom-left, on the same line as the menu button bottom-right.
              Html.p
                  [ prop.className "fixed bottom-4 left-4 z-50 flex h-9 items-center text-sm text-gray-500"
                    prop.text (createdAge menu.created_at) ]
              // As tall as the recipe list's "New Recipe" button, so the two
              // lists start on the same line.
              Html.div
                  [ prop.className "mb-3 flex h-8 items-center"
                    prop.children [ Html.h2 [ prop.className "text-lg font-semibold"; prop.text menu.name ] ] ]
          | None -> Html.none
          match entries with
          | [] -> Html.p "No recipes on the menu yet. Use the + next to a recipe to add it."
          | entries ->
              Html.ul
                  [ prop.className "flex flex-col gap-1"
                    prop.children
                        [ // An entry whose recipe hasn't synced yet (or was
                          // deleted elsewhere) has nothing to show, and
                          // doesn't take a number.
                          let shown =
                              entries |> List.choose (fun e -> findRecipe e.recipe_id model.Recipes |> Option.map (fun r -> e, r))

                          for i, (e, r) in List.indexed shown do
                              let inShoppingList =
                                  Db.allIngredientsPresent model.ShoppingLists model.ShoppingItems (ingredientsOf r)

                              Html.li
                                  [ prop.className "flex items-center gap-2"
                                    prop.children
                                        [ // Same left edge and padding as the × on the
                                          // recipe list, so the two pages line up.
                                          Html.span
                                              [ prop.className "w-6 shrink-0 px-1 text-left text-sm text-gray-500 tabular-nums"
                                                prop.text $"{i + 1}." ]
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "px-1 text-gray-400 hover:text-red-600"
                                                prop.title "Remove from menu"
                                                prop.text "×"
                                                prop.onClick (fun _ -> dispatch (ConfirmMenuRemove e.id)) ]
                                          linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipe"; r.id ] r.title
                                          if inShoppingList then
                                              Html.span
                                                  [ prop.className "text-xs text-gray-500"
                                                    prop.text "(in shopping list)" ] ] ] ] ]
          let pending =
              model.PendingMenuRemove
              |> Option.bind (fun id -> model.MenuRecipes |> List.tryFind (fun e -> e.id = id))
              |> Option.bind (fun e -> findRecipe e.recipe_id model.Recipes |> Option.map (fun r -> e, r))

          match pending with
          | Some(entry, recipe) -> confirmMenuRemoveModal entry recipe dispatch
          | None -> Html.none ]

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

    match model.Session, model.Page with
    | Checking, _ -> Html.none
    | _, Terms -> termsPage dispatch
    | _, Privacy -> privacyPage dispatch
    | SignedOut, _ -> loginPage dispatch
    | SignedIn _, Login -> Html.none
    | SignedIn user, _ ->
        Html.div
            [ navBar model.Page user dispatch
              mobileMenu model.Page user model.MenuOpen dispatch
              Html.main
                  [ prop.className "px-4 py-3 pb-16 md:pb-3"
                    prop.children
                        [ match model.Page with
                          | RecipeList -> listPage model known dispatch
                          | RecipeDetail id -> detailPage model id known dispatch
                          | ShoppingList -> shoppingListPage model dispatch
                          | Menu -> menuPage model dispatch
                          | Login
                          | Terms
                          | Privacy -> Html.none
                          | NotFound -> Html.p "Page not found." ] ]
              match model.PendingShoppingAdd |> Option.bind (fun id -> findRecipe id model.Recipes) with
              | Some recipe -> confirmShoppingAddModal recipe dispatch
              | None -> Html.none ]

let root = ReactDOM.createRoot (document.getElementById "root")
root.render (View())
