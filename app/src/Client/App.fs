module App

open System
open Browser.Dom
open Elmish
open Fable.Core
open Fable.Core.JsInterop
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

/// The two views of a recipe on its page: the steps as written up, or the
/// Cooklang they come from.
type DetailTab =
    | RecipeTab
    | CooklangTab

/// A short message at the bottom of the screen. `Seq` lets a hide scheduled
/// for an earlier toast leave a newer one alone; `Leaving` keeps it mounted
/// while the exit animation plays. `Undo` is the shopping item a tap on the
/// toast un-checks, and is what makes the toast an "Undo" to tap at all.
type Toast = { Seq: int; Text: string; Leaving: bool; Undo: string option }

/// What "Archive" puts away: the current shopping list or the current menu.
/// Archiving is how a new one starts; the next add creates it.
type Archivable =
    | CurrentShoppingList
    | CurrentMenu

/// Registers the service worker (pwa.js). `onNeedRefresh` is called when a
/// new version has installed and is waiting, with the function that
/// switches to it and reloads.
[<Import("register", "./pwa.js")>]
let private registerPwa (onNeedRefresh: (unit -> unit) -> unit) : unit = jsNative

/// iOS keeps a native undo stack of everything typed into the page, and a
/// shake offers to undo it ("Undo Typing?") - long after the field was
/// committed and blanked, and even from being carried round a shop. No web
/// API clears that stack, but WebKit clears the whole page's whenever any
/// frame closes its document (FrameLoader::closeURL ->
/// Editor::clearUndoRedoOperations -> WebPageProxy::clearAllEditCommands ->
/// NSUndoManager removeAllActions), and detaching an iframe does just that.
/// So: a throwaway iframe. Harmless elsewhere; run once typing is committed.
let private forgetTyping =
    Cmd.ofEffect (fun _ ->
        let frame = document.createElement "iframe"
        frame.setAttribute ("hidden", "")
        frame.setAttribute ("aria-hidden", "true")
        document.body.appendChild frame |> ignore
        frame.remove ())

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
      /// Every entry's sides.
      MenuSides: MenuSide list
      /// The blank "Add a side" row under each menu entry, by entry id.
      NewSides: Map<string, string>
      /// `Some` while the "New Recipe" modal is open.
      NewRecipe: RecipeForm option
      /// Detail-page form; `None` until the recipe being viewed has loaded.
      Edit: RecipeForm option
      DetailTab: DetailTab
      /// Recipe awaiting delete confirmation on the list page.
      PendingDelete: string option
      /// Menu entry awaiting confirmation to be taken off the menu.
      PendingMenuRemove: string option
      /// Awaiting confirmation to archive.
      PendingArchive: Archivable option
      /// Recipe whose ingredients are all on the list already, awaiting
      /// confirmation to add them again.
      PendingShoppingAdd: string option
      /// The blank row at the end of the shopping list.
      NewItem: string
      /// The shopping-list row being edited in place (after a long press),
      /// and its text as typed so far.
      EditingItem: (string * string) option
      /// The mobile bottom-sheet menu, toggled by the logo button.
      MenuOpen: bool
      /// Set once a new version is installed and waiting; calling it reloads
      /// into it. Shown as a bar that stays until tapped.
      Update: (unit -> unit) option
      Toast: Toast option }

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
    | MenuSidesChanged of MenuSide list
    | NewSideChanged of entryId: string * text: string
    | AddSide of entryId: string
    | RemoveSide of id: string
    /// One shopping-list row can stand for several sides (see `sideGroups`).
    | SetSidesDone of ids: string list * isDone: bool
    | PrintMenu
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
    | StartEditItem of id: string
    | EditItemChanged of string
    | SaveEditItem
    | CancelEditItem
    | ConfirmArchive of Archivable
    | CancelArchive
    | Archive of Archivable
    | OpenNewRecipe
    | CloseNewRecipe
    | NewTitleChanged of string
    | NewBodyChanged of string
    | AddRecipe
    | EditTitleChanged of string
    | EditBodyChanged of string
    | SelectDetailTab of DetailTab
    | SaveRecipe of id: string
    | ConfirmDelete of id: string
    | CancelDelete
    | DeleteRecipe of id: string
    | ToggleMenu
    | UpdateAvailable of reload: (unit -> unit)
    /// Starts the exit animation...
    | HideToast of seq: int
    /// ...and, once it has played, unmounts.
    | RemoveToast of seq: int
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

let private after (ms: int) (msg: Msg) =
    Cmd.ofEffect (fun dispatch -> window.setTimeout ((fun () -> dispatch msg), ms) |> ignore)

/// How long the toast's enter/exit animations run (index.css).
let private toastAnimationMs = 150

/// Shows a toast for a couple of seconds - twice that when there is an Undo
/// on it, since a tap target has to be reached before it goes.
let private toastWith (undo: string option) (text: string) (model: Model) =
    let seq = (model.Toast |> Option.map (fun t -> t.Seq) |> Option.defaultValue 0) + 1

    { model with Toast = Some { Seq = seq; Text = text; Leaving = false; Undo = undo } },
    after (if undo.IsSome then 4000 else 2000) (HideToast seq)

/// Shows `text` for a couple of seconds.
let private toast (text: string) (model: Model) = toastWith None text model

/// The toast a checked-off item raises: nothing but "Undo".
let private undoToast (itemId: string) (model: Model) = toastWith (Some itemId) "" model

/// Starts the toast's exit, if there is one; otherwise nothing to do.
let private dismissToast (model: Model) =
    match model.Toast with
    | Some t when not t.Leaving -> { model with Toast = Some { t with Leaving = true } }, after toastAnimationMs (RemoveToast t.Seq)
    | _ -> model, Cmd.none

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
      MenuSides = []
      NewSides = Map.empty
      NewRecipe = None
      Edit = None
      DetailTab = RecipeTab
      PendingDelete = None
      PendingMenuRemove = None
      PendingArchive = None
      PendingShoppingAdd = None
      EditingItem = None
      NewItem = ""
      MenuOpen = false
      Update = None
      Toast = None },
    Cmd.batch
        [ Cmd.ofEffect (fun dispatch -> Router.onUrlChanged (UrlChanged >> dispatch))
          Cmd.ofEffect (fun dispatch -> registerPwa (UpdateAvailable >> dispatch))
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
          Cmd.ofEffect (fun dispatch -> Db.watchMenuSides (MenuSidesChanged >> dispatch))
          Cmd.ofEffect (fun _ -> Db.connect () |> Promise.catch (fun e -> console.error e) |> ignore) ]

let private findRecipe id (recipes: Recipe list) =
    recipes |> List.tryFind (fun r -> r.id = id)

let private formOf (recipe: Recipe) = { Title = recipe.title; Body = recipe.body }

let private currentList (model: Model) = List.tryHead model.ShoppingLists

let private currentMenu (model: Model) = List.tryHead model.Menus

/// The current menu's entries with their recipes, in menu order. An entry
/// whose recipe hasn't synced yet (or was deleted elsewhere) is left out.
let private currentMenuEntries (model: Model) =
    match currentMenu model with
    | None -> []
    | Some menu ->
        model.MenuRecipes
        |> List.filter (fun e -> e.menu_id = menu.id)
        |> List.choose (fun e -> findRecipe e.recipe_id model.Recipes |> Option.map (fun r -> e, r))

/// Sides of the current menu, grouped under their entries in menu order.
let private currentMenuSides (model: Model) =
    match currentMenu model with
    | None -> []
    | Some menu ->
        model.MenuRecipes
        |> List.filter (fun e -> e.menu_id = menu.id)
        |> List.collect (fun e -> model.MenuSides |> List.filter (fun s -> s.menu_recipe_id = e.id))

/// One row per distinct side name for the shopping list: sides on the menu
/// have to be "green beans" and "green beans" (exact text) to count as one
/// thing, since only the same spelling is surely the same dish. Each group
/// keeps the order of its first appearance, and is done when every side in
/// it is.
type private SideGroup = { Name: string; Ids: string list; Done: bool }

let private sideGroups (sides: MenuSide list) =
    sides
    |> List.groupBy (fun s -> s.name)
    |> List.map (fun (name, group) ->
        { Name = name
          Ids = group |> List.map (fun s -> s.id)
          Done = group |> List.forall (fun s -> s.``done`` <> 0) })

type private Quantity = Cooklang.Quantity

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

/// `addToShoppingList`, then a toast naming the list it went on.
let private addToShoppingListWithToast (recipe: Recipe) (model: Model) =
    if (ingredientsOf recipe).IsEmpty then
        toast $"\"{recipe.title}\" has no ingredients to add" model
    else
        let model, cmd = addToShoppingList recipe model
        let model, showToast = toast $"\"{recipe.title}\" added to {(List.head model.ShoppingLists).name}" model
        model, Cmd.batch [ cmd; showToast ]

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

        { model with Page = page; Edit = edit; DetailTab = RecipeTab; MenuOpen = false }, Cmd.none
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
    | MenuSidesChanged sides -> { model with MenuSides = sides }, Cmd.none
    | NewSideChanged(entryId, text) -> { model with NewSides = model.NewSides.Add(entryId, text) }, Cmd.none
    | AddSide entryId ->
        match model.NewSides.TryFind entryId with
        | Some text when text.Trim() <> "" ->
            let side = Db.newMenuSide entryId text

            { model with
                MenuSides = model.MenuSides @ [ side ]
                NewSides = model.NewSides.Remove entryId },
            Cmd.batch [ fireAndForget (Db.addMenuSide side); forgetTyping ]
        | _ -> model, forgetTyping
    | RemoveSide id ->
        { model with MenuSides = model.MenuSides |> List.filter (fun s -> s.id <> id) },
        fireAndForget (Db.removeMenuSide id)
    | SetSidesDone(ids, isDone) ->
        let sides =
            model.MenuSides
            |> List.map (fun s -> if List.contains s.id ids then { s with ``done`` = (if isDone then 1 else 0) } else s)

        { model with MenuSides = sides }, Cmd.batch [ for id in ids -> fireAndForget (Db.setMenuSideDone id isDone) ]
    // Every write below is applied to the model first and persisted second,
    // so the UI moves with the tap; the watched queries confirm a beat later.
    // Lists are kept in the order the queries use, so that confirmation
    // changes nothing on screen.
    | AddToShoppingList id ->
        match findRecipe id model.Recipes with
        | Some recipe when Db.allIngredientsPresent model.ShoppingLists model.ShoppingItems (ingredientsOf recipe) ->
            // Probably a double tap; ask before doubling the quantities.
            { model with PendingShoppingAdd = Some id }, Cmd.none
        | Some recipe -> addToShoppingListWithToast recipe model
        | None -> model, Cmd.none
    | AddToShoppingListAnyway id ->
        match findRecipe id model.Recipes with
        | Some recipe -> addToShoppingListWithToast recipe model
        | None -> { model with PendingShoppingAdd = None }, Cmd.none
    | CancelShoppingAdd -> { model with PendingShoppingAdd = None }, Cmd.none
    | AddToMenu id ->
        match findRecipe id model.Recipes with
        | None -> model, Cmd.none
        | Some recipe ->
            let menus, entries, plan = Db.planMenuAdd model.Menus model.MenuRecipes id
            let menu = List.head menus
            let model = { model with Menus = menus; MenuRecipes = entries }

            // The ingredients go on the shopping list too, unless they are all
            // there already (adding again would double the quantities).
            let ingredients = ingredientsOf recipe

            let model, shoppingCmd, where =
                if ingredients.IsEmpty then
                    model, Cmd.none, menu.name
                elif Db.allIngredientsPresent model.ShoppingLists model.ShoppingItems ingredients then
                    model, Cmd.none, $"{menu.name} (ingredients already on the list)"
                else
                    let model, cmd = addToShoppingList recipe model
                    model, cmd, $"{menu.name} and {(List.head model.ShoppingLists).name}"

            let model, showToast = toast $"\"{recipe.title}\" added to {where}" model
            model, Cmd.batch [ fireAndForget (Db.addToMenu plan); shoppingCmd; showToast ]
    | PrintMenu ->
        match currentMenu model with
        | Some menu ->
            let entries =
                currentMenuEntries model
                |> List.map (fun (e, r) ->
                    { Print.Title = r.title
                      Print.Sides = model.MenuSides |> List.filter (fun s -> s.menu_recipe_id = e.id) |> List.map (fun s -> s.name) })

            model, Cmd.ofEffect (fun _ -> Print.menu menu.name entries)
        | None -> model, Cmd.none
    | ConfirmMenuRemove id -> { model with PendingMenuRemove = Some id }, Cmd.none
    | CancelMenuRemove -> { model with PendingMenuRemove = None }, Cmd.none
    | RemoveFromMenu id ->
        { model with
            MenuRecipes = model.MenuRecipes |> List.filter (fun e -> e.id <> id)
            MenuSides = model.MenuSides |> List.filter (fun s -> s.menu_recipe_id <> id)
            PendingMenuRemove = None },
        fireAndForget (Db.removeMenuRecipe id)
    | NewItemChanged text -> { model with NewItem = text }, Cmd.none
    | AddNewItem ->
        if model.NewItem.Trim() = "" then
            model, forgetTyping
        else
            let lists, items, plan = Db.planFreeItemAdd model.ShoppingLists model.ShoppingItems model.NewItem

            { model with ShoppingLists = lists; ShoppingItems = items; NewItem = "" },
            Cmd.batch [ fireAndForget (Db.addToShoppingList plan); forgetTyping ]
    | SetShoppingItemDone(id, isDone) ->
        let items =
            model.ShoppingItems
            |> List.map (fun i -> if i.id = id then { i with ``done`` = (if isDone then 1 else 0) } else i)
            |> List.sortBy (fun i -> i.``done``, i.created_at, i.id)

        let model = { model with ShoppingItems = items }
        let save = fireAndForget (Db.setShoppingItemDone id isDone)

        if isDone then
            // Checking something off can be taken back for a moment.
            let model, showToast = undoToast id model
            model, Cmd.batch [ save; showToast ]
        else
            // Un-checking it - by the Undo itself, or by tapping the row again -
            // leaves that toast with nothing to offer.
            let model, hide =
                match model.Toast with
                | Some t when t.Undo = Some id -> dismissToast model
                | _ -> model, Cmd.none

            model, Cmd.batch [ save; hide ]
    | StartEditItem id ->
        match model.ShoppingItems |> List.tryFind (fun i -> i.id = id) with
        | Some item -> { model with EditingItem = Some(id, Shared.ShoppingItem.text item.quantity item.unit item.name) }, Cmd.none
        | None -> model, Cmd.none
    | EditItemChanged text ->
        match model.EditingItem with
        | Some(id, _) -> { model with EditingItem = Some(id, text) }, Cmd.none
        | None -> model, Cmd.none
    // A blank edit is a cancel; there is no way to delete from here.
    | SaveEditItem ->
        match model.EditingItem with
        | Some(id, text) when text.Trim() <> "" ->
            match model.ShoppingItems |> List.tryFind (fun i -> i.id = id) with
            | Some item when text.Trim() <> Shared.ShoppingItem.text item.quantity item.unit item.name ->
                let quantity, unit, name = Shared.ShoppingItem.edit item.quantity item.unit text

                let items =
                    model.ShoppingItems
                    |> List.map (fun i -> if i.id = id then { i with name = name; quantity = quantity; unit = unit } else i)

                { model with ShoppingItems = items; EditingItem = None },
                Cmd.batch [ fireAndForget (Db.updateShoppingItem id name quantity unit); forgetTyping ]
            | _ -> { model with EditingItem = None }, forgetTyping
        | Some _ -> { model with EditingItem = None }, forgetTyping
        | None -> model, Cmd.none
    | CancelEditItem -> { model with EditingItem = None }, forgetTyping
    | ConfirmArchive what -> { model with PendingArchive = Some what }, Cmd.none
    | CancelArchive -> { model with PendingArchive = None }, Cmd.none
    // The model only holds live lists and menus, so dropping the current one
    // is what the watched query will show once the archive lands.
    | Archive CurrentShoppingList ->
        match currentList model with
        | Some list ->
            { model with
                ShoppingLists = model.ShoppingLists |> List.filter (fun l -> l.id <> list.id)
                NewItem = ""
                EditingItem = None
                PendingArchive = None },
            fireAndForget (Db.archiveShoppingList list.id)
        | None -> { model with PendingArchive = None }, Cmd.none
    | Archive CurrentMenu ->
        match currentMenu model with
        | Some menu ->
            { model with
                Menus = model.Menus |> List.filter (fun m -> m.id <> menu.id)
                NewSides = Map.empty
                PendingArchive = None },
            fireAndForget (Db.archiveMenu menu.id)
        | None -> { model with PendingArchive = None }, Cmd.none
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
    | SelectDetailTab tab -> { model with DetailTab = tab }, Cmd.none
    | SaveRecipe id ->
        match model.Edit with
        | Some r when r.Title.Trim() <> "" ->
            let title = r.Title.Trim()

            let recipes =
                model.Recipes
                |> List.map (fun x -> if x.id = id then { x with title = title; body = r.Body } else x)

            let model, showToast = toast $"\"{title}\" saved" { model with Recipes = recipes }
            model, Cmd.batch [ fireAndForget (Db.updateRecipe id title r.Body); showToast ]
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
            MenuRecipes = model.MenuRecipes |> List.filter (fun e -> e.recipe_id <> id)
            MenuSides =
                let gone =
                    model.MenuRecipes
                    |> List.filter (fun e -> e.recipe_id = id)
                    |> List.map (fun e -> e.id)
                    |> Set.ofList

                model.MenuSides |> List.filter (fun s -> not (gone.Contains s.menu_recipe_id)) },
        fireAndForget (Db.deleteRecipe id)
    | ToggleMenu -> { model with MenuOpen = not model.MenuOpen }, Cmd.none
    | UpdateAvailable reload -> { model with Update = Some reload }, Cmd.none
    | HideToast seq ->
        match model.Toast with
        | Some t when t.Seq = seq -> { model with Toast = Some { t with Leaving = true } }, after toastAnimationMs (RemoveToast seq)
        | _ -> model, Cmd.none
    | RemoveToast seq ->
        match model.Toast with
        | Some t when t.Seq = seq -> { model with Toast = None }, Cmd.none
        | _ -> model, Cmd.none
    | Ignore -> model, Cmd.none

/// Props for an anchor that navigates in-app (real href, so open-in-new-tab still works).
let private linkProps (className: string) dispatch (segments: string list) =
    [ prop.href (Router.format segments)
      prop.className className
      prop.onClick (fun e ->
          e.preventDefault ()
          Router.navigate segments
          dispatch (UrlChanged segments)) ]

let private linkWith (className: string) dispatch (segments: string list) (text: string) =
    Html.a (linkProps className dispatch segments @ [ prop.text text ])

let private link dispatch = linkWith "" dispatch

/// Which nav link is highlighted for a page.
let private onRecipes page =
    match page with
    | RecipeList
    | RecipeDetail _ -> true
    | _ -> false

/// Desktop: the bar across the top. Hidden on phones, where `mobileMenu` takes over.
let private navBar (page: Page) (user: Shared.User) dispatch =
    // `nav-link` (index.css) reserves the bold width from a hidden copy of the
    // text in `data-text`, so the active link going bold shifts nothing.
    let navLink segments (text: string) isActive =
        Html.a (
            linkProps
                ("nav-link " + if isActive then "font-semibold text-gray-900" else "text-gray-500 hover:text-gray-900")
                dispatch
                segments
            @ [ prop.text text; prop.custom ("data-text", text) ]
        )

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
                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList)
                navLink [ "menu" ] "Menu" (page = Menu)
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
                              "sheet-in fixed inset-x-0 bottom-0 z-40 border-t border-gray-200 bg-white px-6 pt-4 pb-safe-16"
                          prop.children
                              [ navLink [] "Recipes" (onRecipes page)
                                navLink [ "shopping-list" ] "Shopping list" (page = ShoppingList)
                                navLink [ "menu" ] "Menu" (page = Menu)
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
                          "fixed right-4 bottom-safe-4 z-50 flex h-9 w-9 items-center justify-center rounded-full bg-white shadow-lg shadow-black/15"
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
                    [ prop.className "mx-auto w-full max-w-2xl flex-1 pt-safe-6 pb-6 text-sm leading-relaxed text-gray-800"
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

/// The confirm button's look: destructive, or an ordinary go-ahead.
type private Confirm =
    | Danger
    | Primary

/// A question with Cancel and one action. Clicking the backdrop cancels.
let private confirmModal (question: string) (action: string) (kind: Confirm) (onCancel: Msg) (onConfirm: Msg) dispatch =
    Html.div
        [ prop.className "fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          prop.onClick (fun _ -> dispatch onCancel)
          prop.children
              [ Html.div
                    [ prop.className "w-full max-w-sm rounded bg-white p-4 flex flex-col gap-3"
                      prop.onClick (fun e -> e.stopPropagation ())
                      prop.children
                          [ Html.p [ prop.text question ]
                            Html.div
                                [ prop.className "flex justify-end gap-2"
                                  prop.children
                                      [ Html.button
                                            [ prop.type' "button"
                                              prop.className "px-3 py-1 text-gray-600 hover:text-gray-900"
                                              prop.text "Cancel"
                                              prop.autoFocus true
                                              prop.onClick (fun _ -> dispatch onCancel) ]
                                        Html.button
                                            [ prop.type' "button"
                                              prop.className (
                                                  match kind with
                                                  | Danger -> "bg-red-600 px-3 py-1 text-white hover:bg-red-700"
                                                  | Primary -> "bg-blue-600 px-3 py-1 text-white hover:bg-blue-700"
                                              )
                                              prop.text action
                                              prop.onClick (fun _ -> dispatch onConfirm) ] ] ] ] ] ] ]

let private confirmDeleteModal (recipe: Recipe) dispatch =
    confirmModal $"Delete \"{recipe.title}\"?" "Delete" Danger CancelDelete (DeleteRecipe recipe.id) dispatch

let private listPage (model: Model) (known: CooklangEditor.KnownNames) dispatch =
    Html.div
        [ Html.button
              [ prop.type' "button"
                prop.className "mb-3 bg-brand px-3 py-1 font-semibold text-white hover:shadow-md hover:shadow-brand/30"
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
                                  [ prop.key r.id
                                    prop.className "flex items-center gap-2"
                                    prop.children
                                        [ // Same box as the number on the menu page, so the
                                          // + here and the × there share a column.
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "icon-btn w-6 shrink-0 px-1 text-left text-gray-400 hover:text-red-600"
                                                prop.title "Delete recipe"
                                                prop.text "×"
                                                prop.onClick (fun _ -> dispatch (ConfirmDelete r.id)) ]
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "icon-btn -ml-2 px-1 text-gray-400 hover:text-green-700"
                                                prop.title "Add to menu"
                                                prop.text "+"
                                                prop.onClick (fun _ -> dispatch (AddToMenu r.id)) ]
                                          linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipe"; r.id ] r.title ] ] ] ]
          match model.NewRecipe with
          | Some recipe -> newRecipeModal recipe known dispatch
          | None -> Html.none ]

/// Shown when every ingredient of the recipe is already on the current list.
let private confirmShoppingAddModal (recipe: Recipe) dispatch =
    confirmModal
        "All the ingredients for this recipe are already in the current shopping list. Add them again?"
        "Add again"
        Primary
        CancelShoppingAdd
        (AddToShoppingListAnyway recipe.id)
        dispatch

let private quantityText (q: Cooklang.Quantity option) (unit: string option) =
    let amount =
        match q with
        | Some(Quantity.Number n) -> string n
        | Some(Quantity.Text t) -> t
        | None -> ""

    match amount, unit with
    | "", None -> ""
    | "", Some u -> u
    | a, None -> a
    | a, Some u -> $"{a} {u}"

/// One step's items as running text: ingredient and cookware names in bold,
/// with the amount after in grey; timers as their duration.
let private stepText (items: Cooklang.Item list) =
    [ for item in items do
          match item with
          | Cooklang.Item.Text t -> Html.text t
          | Cooklang.Item.Ingredient i ->
              Html.span [ prop.className "font-semibold"; prop.text i.Name ]

              match quantityText i.Quantity i.Unit with
              | "" -> ()
              | q -> Html.span [ prop.className "text-gray-500"; prop.text $" ({q})" ]
          | Cooklang.Item.Cookware c -> Html.span [ prop.className "font-semibold"; prop.text c.Name ]
          | Cooklang.Item.Timer t ->
              let duration = quantityText t.Quantity t.Unit
              Html.text (if duration = "" then defaultArg t.Name "" else duration) ]

/// The recipe as a numbered list of steps, with section headings and notes
/// where they fall. Numbering runs through the whole recipe.
let private stepsView (body: string) =
    let recipe = (Cooklang.parse body).Recipe
    let mutable n = 0

    Html.div
        [ prop.className "flex flex-col gap-3"
          prop.children
              [ if Cooklang.steps recipe |> List.isEmpty then
                    Html.p [ prop.className "text-sm text-gray-500"; prop.text "No steps yet. Write some on the Cooklang tab." ]
                for section in recipe.Sections do
                    match section.Name with
                    | Some name -> Html.h3 [ prop.className "font-semibold"; prop.text name ]
                    | None -> ()

                    for block in section.Blocks do
                        match block with
                        | Cooklang.Block.Step items ->
                            n <- n + 1

                            Html.div
                                [ prop.className "flex gap-2"
                                  prop.children
                                      [ Html.span [ prop.className "w-6 shrink-0 text-right text-gray-500 tabular-nums"; prop.text $"{n}." ]
                                        Html.p [ prop.className "min-w-0"; prop.children (stepText items) ] ] ]
                        | Cooklang.Block.Note note ->
                            Html.p [ prop.className "ml-8 text-sm text-gray-500 italic"; prop.text note ] ] ]

let private detailTabs (current: DetailTab) dispatch =
    Html.div
        [ prop.className "flex shrink-0 gap-4 border-b border-gray-200"
          prop.role "tablist"
          prop.children
              [ for tab, label in [ RecipeTab, "Recipe"; CooklangTab, "Cooklang" ] ->
                    let selected = (tab = current)

                    Html.button
                        [ prop.type' "button"
                          prop.role "tab"
                          prop.ariaSelected selected
                          prop.className (
                              "-mb-px border-b-2 px-1 py-1 text-sm "
                              + if selected then "border-brand font-semibold text-ink" else "border-transparent text-gray-500 hover:text-ink"
                          )
                          prop.text label
                          prop.onClick (fun _ -> dispatch (SelectDetailTab tab)) ] ] ]

let private detailPage (model: Model) (id: string) (known: CooklangEditor.KnownNames) dispatch =
    match findRecipe id model.Recipes, model.Edit with
    | Some _, Some edit ->
        let field = "w-full rounded border border-gray-300 px-2 py-1"

        // Phones: the form is the whole viewport, three rows - back + title,
        // the editor taking every remaining pixel (CodeMirror scrolls inside
        // it), and the actions. Desktop: the same rows in normal flow.
        Html.form
            [ prop.className "fixed inset-0 flex flex-col gap-3 p-4 pt-safe-4 pb-safe-4 md:static md:max-w-2xl md:p-0"
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
                        [ prop.className "flex shrink-0 flex-wrap items-center gap-2"
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
                                      prop.onClick (fun _ -> dispatch (ConfirmDelete id)) ] ] ]
                    detailTabs model.DetailTab dispatch
                    // Both tabs stay mounted (the editor keeps its cursor and
                    // undo history); the one not selected is hidden.
                    Html.div
                        [ prop.className (
                              "min-h-0 flex-1 overflow-y-auto"
                              + if model.DetailTab = RecipeTab then "" else " hidden"
                          )
                          prop.children [ stepsView edit.Body ] ]
                    // On desktop the form is in normal flow, so flex-1 is inert
                    // and the editor falls back to its own min-height.
                    Html.div
                        [ prop.className (
                              "min-h-0 flex-1 flex-col [&>div]:min-h-0 [&>div]:flex-1 [&_.cm-editor]:h-full"
                              + if model.DetailTab = CooklangTab then " flex" else " hidden"
                          )
                          prop.children
                              [ CooklangEditor.CooklangEditor(edit.Body, known, "Recipe", EditBodyChanged >> dispatch) ] ] ] ]
    | _ ->
        Html.div
            [ Html.p "Recipe not found (it may still be syncing, or it was deleted)."
              link dispatch [] "Back to recipes" ]

/// The blank row at the end of the list. Enter (the keyboard's "done" on
/// phones) adds the item and leaves the field focused for the next one;
/// tapping away adds it too. Either way the row is blank again afterwards.
let private newItemRow (text: string) dispatch =
    Html.li
        [ // Keyed, like the item rows, so adding keeps this input mounted and focused.
          prop.key "new"
          prop.children
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
                                  prop.onBlur (fun _ -> dispatch AddNewItem) ] ] ] ] ]

/// "Created: 3 days ago", from a row's ISO timestamp.
let private createdAge (createdAt: string) =
    "Created: " + Shared.Created.age DateTime.Now ((DateTime.Parse createdAt).ToLocalTime())

/// Tap or long press on a list row. One press is in flight at a time: from
/// pointer-down it is `Pending`; if the finger stays put for `holdMs` it
/// becomes `Held` and the row goes into editing; lifting before that is a
/// tap; moving (a scroll) or the browser cancelling it drops it.
module private Press =
    type State =
        | Idle
        | Pending of timer: float * x: float * y: float
        | Held

    /// How a press ended: lifted in time, lifted after the hold, or there
    /// was no press on (a lift after a scroll cancelled it, or in a field).
    type Ended =
        | Tap
        | LongPress
        | NoPress

    let private holdMs = 500
    let private slop = 10.0
    let mutable private state = Idle

    let cancel () =
        match state with
        | Pending(timer, _, _) -> window.clearTimeout timer
        | _ -> ()

        state <- Idle

    let start (e: Browser.Types.PointerEvent) (onHeld: unit -> unit) =
        cancel ()

        let timer =
            window.setTimeout (
                (fun () ->
                    state <- Held
                    onHeld ()),
                holdMs
            )

        state <- Pending(timer, e.clientX, e.clientY)

    let move (e: Browser.Types.PointerEvent) =
        match state with
        | Pending(_, x, y) when abs (e.clientX - x) > slop || abs (e.clientY - y) > slop -> cancel ()
        | _ -> ()

    /// What the press that just ended was, and puts things back to rest.
    let finish () =
        let ended = state
        cancel ()

        match ended with
        | Pending _ -> Tap
        | Held -> LongPress
        | Idle -> NoPress

/// A shopping-list row: a tap anywhere on it (text or box) checks it, a
/// long press turns the text into a field for editing it. The box is only
/// drawn for the pointer - the row handles the gesture - but still takes
/// the keyboard. `editing` is the text typed so far while editing. `source`
/// is the recipes the item comes from, "(Focaccia)", shown beside the text
/// and left out of it: editing changes the item, never where it came from.
/// On a phone it drops to a second line of its own instead.
let private itemRow (id: string) (text: string) (source: string) (isDone: bool) (editing: string option) (onDone: bool -> unit) dispatch =
    let field = $"edit-{id}"

    Html.li
        [ prop.key id
          prop.children
              [ Html.div
                    [ prop.className "press-row flex select-none items-center gap-2 py-1"
                      prop.onPointerDown (fun e ->
                          if editing.IsNone && e.button = 0 then
                              // Capture, so the rest of the press reaches this row
                              // even once the span under the finger has become the field.
                              e.currentTarget?setPointerCapture (e.pointerId)
                              Press.start e (fun () -> dispatch (StartEditItem id)))
                      prop.onPointerMove Press.move
                      prop.onPointerCancel (fun _ -> Press.cancel ())
                      prop.onPointerUp (fun _ ->
                          match Press.finish () with
                          | Press.Tap -> onDone (not isDone)
                          // The field is on screen by now. Focusing it here, inside
                          // the pointer event, is what makes iOS raise the keyboard;
                          // from the timer it would not.
                          | Press.LongPress ->
                              match document.getElementById field with
                              | null -> ()
                              | el -> el.focus ()
                          | Press.NoPress -> ())
                      // Android's long-press menu, and the right-click one on desktop.
                      prop.onContextMenu (fun e -> e.preventDefault ())
                      prop.children
                          [ Html.input
                                [ prop.type' "checkbox"
                                  prop.className "pointer-events-none"
                                  prop.isChecked isDone
                                  prop.onChange (fun (isChecked: bool) -> onDone isChecked) ]
                            match editing with
                            | Some draft ->
                                Html.form
                                    [ prop.className "flex min-w-0 flex-1"
                                      prop.onSubmit (fun e ->
                                          e.preventDefault ()
                                          dispatch SaveEditItem)
                                      prop.children
                                          [ Html.input
                                                [ prop.id field
                                                  prop.className "min-w-0 flex-1 select-text bg-transparent py-0.5 outline-none"
                                                  prop.type' "text"
                                                  prop.ariaLabel "Edit item"
                                                  prop.autoComplete "off"
                                                  prop.custom ("enterKeyHint", "done")
                                                  prop.value draft
                                                  prop.onChange (EditItemChanged >> dispatch)
                                                  prop.onKeyDown (fun e -> if e.key = "Escape" then dispatch CancelEditItem)
                                                  prop.onBlur (fun _ -> dispatch SaveEditItem) ] ] ]
                            | None ->
                                // Phones: the recipe goes under the item, indented a
                                // little, so a long one doesn't push the item itself
                                // into wrapping. Desktop has room for one line.
                                Html.div
                                    [ prop.className "flex min-w-0 flex-1 flex-col md:flex-row md:items-center md:gap-2"
                                      prop.children
                                          [ Html.span
                                                [ prop.className (if isDone then "text-gray-400 line-through" else "")
                                                  prop.text text ]

                                            if source <> "" then
                                                Html.span
                                                    [ prop.className (
                                                          "min-w-0 truncate pl-3 text-sm md:pl-0 "
                                                          + (if isDone then "text-gray-300" else "text-gray-400")
                                                      )
                                                      prop.text source ] ] ] ] ] ] ]

let private shoppingListPage (model: Model) dispatch =
    let list = currentList model

    let items =
        match list with
        | Some list -> model.ShoppingItems |> List.filter (fun i -> i.list_id = list.id)
        | None -> []

    // Unchecked items, then the blank row, then the menu's sides, then what's
    // already checked: everything still to buy stays above the ticked-off tail.
    let todo, ``done`` = items |> List.partition (fun i -> i.``done`` = 0)

    // Every recipe with the ingredients its Cooklang names, newest first: what
    // an item's "(Focaccia)" is read off, freshly on every render, so editing a
    // recipe moves the labels with it.
    let recipeIngredients =
        model.Recipes
        |> List.map (fun r -> r.title, ingredientsOf r |> List.map (fun i -> i.Name))

    let row (item: ShoppingItem) =
        let editing =
            match model.EditingItem with
            | Some(id, draft) when id = item.id -> Some draft
            | _ -> None

        itemRow
            item.id
            (Shared.ShoppingItem.text item.quantity item.unit item.name)
            (Shared.ShoppingItem.sources recipeIngredients item.name |> Shared.ShoppingItem.sourceText)
            (item.``done`` <> 0)
            editing
            (fun isDone -> dispatch (SetShoppingItemDone(item.id, isDone)))
            dispatch

    Html.div
        [ match list with
          | Some list ->
              // Bottom-left, on the same line as the menu button bottom-right;
              // the same white pill, since it floats over the list.
              Html.p
                  [ prop.className "fixed bottom-safe-4 left-4 z-50 flex h-9 items-center rounded-full bg-white px-3 text-sm text-gray-500 shadow-lg shadow-black/15"
                    prop.text (createdAge list.created_at) ]
              Html.div
                  [ // Phones: name left, button at the right edge. Desktop: both on the left.
                    prop.className "mb-3 flex items-center justify-between gap-3 md:justify-start"
                    prop.children
                        [ Html.h2 [ prop.className "min-w-0 truncate text-lg font-semibold"; prop.title list.name; prop.text list.name ]
                          Html.button
                              [ prop.type' "button"
                                prop.className "border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50"
                                prop.text "Archive"
                                prop.onClick (fun _ -> dispatch (ConfirmArchive CurrentShoppingList)) ] ] ]
          | None ->
              Html.p
                  [ prop.className "mb-3 text-gray-500"
                    prop.text "Nothing on the list yet. Add ingredients from a recipe page, or type an item below." ]
          Html.ul
              [ prop.className "flex flex-col gap-1"
                prop.children
                    [ yield! List.map row todo
                      newItemRow model.NewItem dispatch ] ]
          // Sides noted on the current menu, checkable here like the items
          // above. They live with the menu and are archived with it.
          match sideGroups (currentMenuSides model) with
          | [] -> Html.none
          | groups ->
              Html.h3 [ prop.className "mt-4 mb-1 text-sm font-semibold text-gray-500"; prop.text "Menu sides" ]
              Html.ul
                  [ prop.className "flex flex-col gap-1"
                    prop.children
                        [ for g in groups do
                              Html.li
                                  [ Html.label
                                        [ prop.className "flex cursor-pointer select-none items-center gap-2 py-1"
                                          prop.children
                                              [ Html.input
                                                    [ prop.type' "checkbox"
                                                      prop.isChecked g.Done
                                                      prop.onChange (fun (isChecked: bool) ->
                                                          dispatch (SetSidesDone(g.Ids, isChecked))) ]
                                                Html.span
                                                    [ prop.className (if g.Done then "text-gray-400 line-through" else "")
                                                      prop.text (
                                                          match g.Ids.Length with
                                                          | 1 -> g.Name
                                                          | n -> $"{n} × {g.Name}"
                                                      ) ] ] ] ] ] ]
          // The ticked-off tail, last. `mt-1` stands in for the gap it used to
          // sit in when it was part of the list above.
          match ``done`` with
          | [] -> Html.none
          | items -> Html.ul [ prop.className "mt-1 flex flex-col gap-1"; prop.children (List.map row items) ] ]

/// The blank row under a menu entry. Same manners as the shopping list's
/// (Enter adds and keeps focus; tapping away adds too), without a checkbox.
let private newSideRow (entryId: string) (text: string) dispatch =
    Html.li
        [ // Keyed, like the rows above it, so adding a side keeps this input
          // mounted and focused for the next one.
          prop.key "new"
          prop.children
              [ Html.form
                    [ prop.onSubmit (fun e ->
                          e.preventDefault ()
                          dispatch (AddSide entryId))
                      prop.children
                          [ Html.input
                                [ prop.className "w-full bg-transparent py-0.5 text-sm outline-none placeholder:text-gray-400"
                                  prop.type' "text"
                                  prop.placeholder "Add a side"
                                  prop.ariaLabel "Add a side"
                                  prop.autoComplete "off"
                                  prop.custom ("enterKeyHint", "done")
                                  prop.value text
                                  prop.onChange (fun (t: string) -> dispatch (NewSideChanged(entryId, t)))
                                  prop.onBlur (fun _ -> dispatch (AddSide entryId)) ] ] ] ] ]

/// The sides under one menu entry, hung off a guide line beneath its title.
let private sidesTree (entryId: string) (sides: MenuSide list) (draft: string) dispatch =
    Html.ul
        [ // Indented past the number and × so the line starts under the title.
          prop.className "ml-6 flex flex-col border-l border-gray-200 pl-3 md:ml-10"
          prop.children
              [ for side in sides do
                    Html.li
                        [ prop.key side.id
                          prop.className "flex items-center gap-2 py-0.5 text-sm"
                          prop.children
                              [ Html.span [ prop.text side.name ]
                                Html.button
                                    [ prop.type' "button"
                                      prop.className "icon-btn px-1 text-gray-400 hover:text-red-600"
                                      prop.title "Remove side"
                                      prop.text "×"
                                      prop.onClick (fun _ -> dispatch (RemoveSide side.id)) ] ] ]
                newSideRow entryId draft dispatch ] ]

let private confirmMenuRemoveModal (entry: MenuRecipe) (recipe: Recipe) dispatch =
    confirmModal $"Remove \"{recipe.title}\" from the menu?" "Remove" Danger CancelMenuRemove (RemoveFromMenu entry.id) dispatch

/// Archiving clears the page; a new list or menu starts on the next add.
let private confirmArchiveModal (what: Archivable) (name: string) dispatch =
    let thing =
        match what with
        | CurrentShoppingList -> "shopping list"
        | CurrentMenu -> "menu"

    confirmModal $"Archive {thing} \"{name}\"? A new one starts the next time you add something." "Archive" Primary CancelArchive (Archive what) dispatch

/// The current menu: its recipes in the order they were added, each with a
/// badge when the current shopping list already has all its ingredients.
let private menuPage (model: Model) dispatch =
    let menu = currentMenu model
    let shown = currentMenuEntries model

    Html.div
        [ match menu with
          | Some menu ->
              // Bottom-left, on the same line as the menu button bottom-right;
              // the same white pill, since it floats over the list.
              Html.p
                  [ prop.className "fixed bottom-safe-4 left-4 z-50 flex h-9 items-center rounded-full bg-white px-3 text-sm text-gray-500 shadow-lg shadow-black/15"
                    prop.text (createdAge menu.created_at) ]
              // As tall as the recipe list's "New Recipe" button, so the two
              // lists start on the same line. Phones: name left, button at the
              // right edge. Desktop: both on the left.
              Html.div
                  [ prop.className "mb-3 flex h-8 items-center justify-between gap-3 md:justify-start"
                    prop.children
                        [ // One line, cut with an ellipsis: the name has two random
                          // words on the end and the buttons keep their room.
                          Html.h2 [ prop.className "min-w-0 truncate text-lg font-semibold"; prop.title menu.name; prop.text menu.name ]
                          Html.div
                              [ prop.className "flex shrink-0 items-center gap-2"
                                prop.children
                                    [ Html.button
                                          [ prop.type' "button"
                                            prop.className "border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                                            prop.text "Print"
                                            prop.disabled shown.IsEmpty
                                            prop.onClick (fun _ -> dispatch PrintMenu) ]
                                      Html.button
                                          [ prop.type' "button"
                                            prop.className "border border-gray-300 px-3 py-1 text-gray-700 hover:bg-gray-50"
                                            prop.text "Archive"
                                            prop.onClick (fun _ -> dispatch (ConfirmArchive CurrentMenu)) ] ] ] ] ]
          | None -> Html.none
          match shown with
          | [] -> Html.p "No recipes on the menu yet. Use the + next to a recipe to add it."
          | shown ->
              Html.ul
                  [ prop.className "flex flex-col gap-1"
                    prop.children
                        [ for i, (e, r) in List.indexed shown do
                              let inShoppingList =
                                  Db.allIngredientsPresent model.ShoppingLists model.ShoppingItems (ingredientsOf r)

                              Html.li
                                  [ prop.key e.id
                                    prop.children
                                        [ Html.div
                                              [ prop.className "flex items-center gap-2"
                                                prop.children
                                                    [ // Same left edge and padding as the × on the
                                                      // recipe list, so the two pages line up.
                                                      Html.span
                                                          [ prop.className "w-6 shrink-0 px-1 text-left text-sm text-gray-500 tabular-nums"
                                                            prop.text $"{i + 1}." ]
                                                      Html.button
                                                          [ prop.type' "button"
                                                            prop.className "icon-btn -ml-2 px-1 text-gray-400 hover:text-red-600"
                                                            prop.title "Remove from menu"
                                                            prop.text "×"
                                                            prop.onClick (fun _ -> dispatch (ConfirmMenuRemove e.id)) ]
                                                      linkWith "text-blue-600 underline hover:text-blue-800" dispatch [ "recipe"; r.id ] r.title
                                                      if inShoppingList then
                                                          Html.span
                                                              [ prop.className "text-xs text-gray-500"
                                                                prop.text "(in shopping list)" ] ] ]
                                          sidesTree
                                              e.id
                                              (model.MenuSides |> List.filter (fun s -> s.menu_recipe_id = e.id))
                                              (model.NewSides.TryFind e.id |> Option.defaultValue "")
                                              dispatch ] ] ] ]
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
                  [ // Enough bottom padding that the last row scrolls clear of the
                    // fixed "Created" pill and menu button.
                    prop.className "px-4 pt-safe-3 pb-safe-24 md:py-3"
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
              | None -> Html.none
              match model.PendingDelete |> Option.bind (fun id -> findRecipe id model.Recipes) with
              | Some recipe -> confirmDeleteModal recipe dispatch
              | None -> Html.none
              match model.PendingArchive with
              | Some CurrentShoppingList ->
                  match currentList model with
                  | Some list -> confirmArchiveModal CurrentShoppingList list.name dispatch
                  | None -> Html.none
              | Some CurrentMenu ->
                  match currentMenu model with
                  | Some menu -> confirmArchiveModal CurrentMenu menu.name dispatch
                  | None -> Html.none
              | None -> Html.none
              // Bottom centre, above the phone's bottom row (menu button,
              // "Created"): the update bar, if any, over the passing toast.
              Html.div
                  [ prop.className
                        "pointer-events-none fixed bottom-safe-16 left-1/2 z-50 flex -translate-x-1/2 flex-col items-center gap-2 md:bottom-6"
                    prop.children
                        [ match model.Update with
                          | Some reload ->
                              Html.div
                                  [ prop.className
                                        "toast-in pointer-events-auto flex items-center gap-3 bg-ink px-3 py-2 text-sm text-white shadow-lg"
                                    prop.role "status"
                                    prop.children
                                        [ Html.span [ prop.text "Update available" ]
                                          Html.button
                                              [ prop.type' "button"
                                                prop.className "font-semibold text-brand"
                                                prop.text "Reload"
                                                prop.onClick (fun _ -> reload ()) ] ] ]
                          | None -> Html.none
                          match model.Toast with
                          | Some t ->
                              // Keyed by sequence so a new toast re-runs the animation.
                              Html.div
                                  [ prop.key t.Seq
                                    prop.className (
                                        (if t.Leaving then "toast-out" else "toast-in")
                                        + " flex items-center gap-3 bg-ink px-3 py-2 text-sm text-white shadow-lg"
                                        // Only a toast with an Undo on it takes taps;
                                        // the rest let them through to the page.
                                        + (if t.Undo.IsSome then " pointer-events-auto" else "")
                                    )
                                    prop.role "status"
                                    prop.children
                                        [ if t.Text <> "" then
                                              Html.span [ prop.text t.Text ]

                                          match t.Undo with
                                          | Some itemId ->
                                              Html.button
                                                  [ prop.type' "button"
                                                    prop.className "font-semibold text-brand"
                                                    prop.text "Undo"
                                                    prop.onClick (fun _ -> dispatch (SetShoppingItemDone(itemId, false))) ]
                                          | None -> Html.none ] ]
                          | None -> Html.none ] ] ]

let root = ReactDOM.createRoot (document.getElementById "root")
root.render (View())
