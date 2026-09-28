/// The MCP server: Plaintext Pantry as tools an assistant can call.
///
/// Every request arrives with a Keycloak access token for the `McpResource`
/// audience (see Auth.fs); the tools act as that token's subject and never
/// see anyone else's rows. Scopes gate writes so a user can grant read-only
/// access at the consent screen.
///
/// The transport is stateless streamable HTTP: a server is built per request
/// and the tool class is constructed per call from DI, so the current
/// HttpContext (and its principal) is always the right one.
module Server.Mcp

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ModelContextProtocol
open ModelContextProtocol.Server
open Server.Config

let private noScope (scope: string) =
    McpException $"This token lacks the '{scope}' scope. Reconnect and grant it at the consent screen."

let private parseId (id: string) =
    match Guid.TryParse id with
    | true, g -> g
    | _ -> raise (McpException $"'{id}' is not a valid id")

/// The Cooklang this app parses, spelled out for whoever is writing a body.
/// Both the `body` parameter and the server's instructions carry it: an
/// assistant that has only ever seen recipes without metadata guesses at the
/// top of the file - YAML front matter with no `---`, a Markdown heading, the
/// old `>>` lines - and guesses the same way again next time.
[<Literal>]
let private cooklangDoc =
    """Metadata, if there is any, comes first: `key: value` lines between two `---` lines, at the very top of the body and nowhere else.

---
servings: 4
source: https://example.com
---

`>> key: value` lines still parse, but they are the old syntax and the app marks them as such; write the `---` block instead.

Then the steps, one per line, with a blank line between them:

  Simmer the @tomatoes{400%g} and @garlic in a #pan{} for ~{20%minutes}.

@name is an ingredient, #name cookware, ~name a timer; {quantity%unit} follows the name. A name of more than one word needs the braces to say where it ends: @olive oil{2%tbsp}, or @spring onion{} with no quantity.

`= Sauce` starts a section. `> ...` is a note to the cook. `--` comments out the rest of a line, `[- ... -]` comments inline."""

/// The same, as the `body` parameter's own description.
[<Literal>]
let bodyDoc = "Cooklang source.\n\n" + cooklangDoc

/// Handed to the client when it connects, so the dialect is known before the
/// first recipe is written rather than after it comes back wrong.
let instructions =
    $"""Plaintext Pantry: one household's recipes, shopping list and weekly menu. Recipes are written in Cooklang, and the shopping list and menu are built out of them - an ingredient on the list knows which recipes still call for it.

A recipe body looks like this:

{cooklangDoc}"""

/// What the tools return for a recipe. Camel-cased by the SDK's serializer.
type Recipe =
    { Id: string
      Title: string
      /// Cooklang source: `@ingredient{qty%unit}`, `#cookware{}`, `~{time}`.
      Body: string
      /// The names of the tags on it, as the badges beside it read.
      Tags: string list
      CreatedAt: DateTimeOffset }

/// What a write gives back: the recipe as it is now stored, and whatever the
/// Cooklang parser made of the body - a `>>` metadata line, an unclosed `{`,
/// a timer with no duration. The body is saved either way (the app shows the
/// same notes as squiggles in its own editor); they come back here because an
/// assistant that has just written the metadata the old way has no other way
/// of finding out, and would write it that way again next time.
type SavedRecipe = { Recipe: Recipe; Notes: string list }

/// A tag, with how many recipes carry it.
type Tag =
    { Id: string
      Name: string
      Recipes: int }

type ShoppingItem =
    { Id: string
      Name: string
      Quantity: string
      Unit: string
      Done: bool
      /// The recipes this ingredient comes from, as the page shows after the
      /// line: the titles of the recipes whose Cooklang still names it.
      /// Derived, never stored, so it empties when the last recipe calling
      /// for it stops doing so, and the item stays on the list.
      Recipes: string list }

/// Sides on the current menu with the same name, as one line: "2 × green
/// beans". Checking it checks every side in the group.
type SideGroup =
    { Name: string
      Count: int
      Done: bool
      SideIds: string list }

/// The newest list with its items. `Id` and `Name` are null when the user
/// has no list yet; add_shopping_items makes one. `MenuSides` are the
/// current menu's sides, shown under the items on the shopping list page.
type ShoppingList =
    { Id: string
      Name: string
      Items: ShoppingItem list
      MenuSides: SideGroup list }

type MenuSide =
    { Id: string
      Name: string
      Done: bool }

/// A recipe on the menu. `InShoppingList` is true when the current shopping
/// list already has every one of its ingredients, as the page's badge shows.
type MenuEntry =
    { Id: string
      RecipeId: string
      Title: string
      Sides: MenuSide list
      InShoppingList: bool }

/// The newest menu with its entries in the order added. `Id` and `Name` are
/// null when the user has no menu yet; add_to_menu makes one.
type Menu =
    { Id: string
      Name: string
      Entries: MenuEntry list }

type NewShoppingItem =
    { Name: string
      /// Free text; "2", "1.5" or "some" are all fine.
      Quantity: string
      Unit: string }

let private recipe (tags: string list) (r: Db.RecipeRow) =
    { Id = string r.Id
      Title = r.Title
      Body = r.Body
      Tags = tags
      CreatedAt = r.CreatedAt }

let private tag (t: Db.TagRow) =
    { Id = string t.Id
      Name = t.Name
      Recipes = t.Recipes }

/// `recipeIngredients` is each recipe's title with the ingredient names its
/// body parses to, which is what `Recipes` is read off.
let private item recipeIngredients (i: Db.ShoppingItemRow) =
    { Id = string i.Id
      Name = i.Name
      Quantity = i.Quantity
      Unit = i.Unit
      Done = i.Done
      Recipes = Shared.ShoppingItem.sources recipeIngredients i.Name }

let private side (s: Db.MenuSideRow) : MenuSide =
    { Id = string s.Id
      Name = s.Name
      Done = s.Done }

/// One group per distinct side name (exact text), in order of first
/// appearance, the way the shopping list page shows them.
let private sideGroups (sides: Db.MenuSideRow list) =
    sides
    |> List.groupBy (fun s -> s.Name)
    |> List.map (fun (name, group) ->
        { Name = name
          Count = group.Length
          Done = group |> List.forall (fun s -> s.Done)
          SideIds = group |> List.map (fun s -> string s.Id) })

/// The parser's complaints about a body, each against the line it is on.
let private cooklangNotes (body: string) =
    let body = if isNull body then "" else body

    [ for d in (Cooklang.parse body).Diagnostics do
          let upTo = body.Substring(0, min d.Span.Start body.Length)
          let line = 1 + (upTo |> Seq.filter ((=) '\n') |> Seq.length)

          let severity =
              match d.Severity with
              | Cooklang.Severity.Error -> "error"
              | Cooklang.Severity.Warning -> "warning"

          $"line {line}, {severity}: {d.Message}" ]

let private ingredientsOf (body: string) =
    Cooklang.ingredients (Cooklang.parse body).Recipe

/// True when every ingredient of the recipe has an item of the same name
/// (case-insensitive) on the list. A recipe with no ingredients never is.
let private allIngredientsPresent (items: Db.ShoppingItemRow list) (body: string) =
    let names = items |> List.map (fun i -> i.Name.ToLowerInvariant()) |> Set.ofList
    let ingredients = ingredientsOf body
    not ingredients.IsEmpty && ingredients |> List.forall (fun i -> names.Contains(i.Name.ToLowerInvariant()))

type private Quantity = Cooklang.Quantity

let private quantityText (q: Quantity option) =
    match q with
    | Some(Quantity.Number n) -> string n
    | Some(Quantity.Text t) -> t
    | None -> ""

[<McpServerToolType>]
type PantryTools(config: Config, http: IHttpContextAccessor) =
    let ctx = http.HttpContext

    let user =
        match Auth.currentUser ctx with
        | Some u -> u
        | None -> raise (McpException "No authenticated user")

    let scopes = Auth.scopes ctx
    let cs = config.ConnectionString

    /// The scope gate every tool passes through first - and, for a `:write`
    /// scope, where a mutation is counted for the usage dashboard. Counting
    /// here rather than in the tools means a tool that changes something
    /// cannot be left out: the check is compulsory, and the scope already
    /// names the area changed (`recipes:write` -> recipes), so there is no
    /// second list of tools to keep in step with the first.
    let require (scope: string) =
        if not (Set.contains scope scopes) then
            raise (noScope scope)

        if scope.EndsWith ":write" then
            Usage.mcp config (scope.Substring(0, scope.Length - ":write".Length))

    /// The pantry an assistant's writes go into: the caller's own. A device's
    /// first sync ordinarily makes it, so this is for the account that has
    /// only ever been reached through MCP and has no pantry yet. Called before
    /// anything that starts a recipe, list or menu; rows that hang off one of
    /// those take their pantry from it.
    let ensurePantry () =
        Db.ensureOwnPantry cs user.Id user.Email user.Name

    /// The current list's id, creating today's list when there is none.
    let currentListId () =
        task {
            let! list = Db.latestShoppingList cs user.Id

            match list with
            | Some l -> return l.Id
            | None ->
                let! _ = ensurePantry ()
                return! Db.insertShoppingList cs user.Id (Shared.ShoppingList.defaultName DateTime.Now)
        }

    /// The current menu's id, creating today's menu when there is none.
    let currentMenuId () =
        task {
            let! menu = Db.latestMenu cs user.Id

            match menu with
            | Some m -> return m.Id
            | None ->
                let! _ = ensurePantry ()
                return! Db.insertMenu cs user.Id (Shared.Menu.defaultName DateTime.Now (Random()))
        }

    /// Every recipe's title with the ingredient names its Cooklang parses to:
    /// what an item's `Recipes` is read off, the way the page derives it.
    /// Empty without `recipes:read`, since the titles are recipe data; the
    /// items themselves still come back.
    let recipeIngredients () =
        task {
            if not (Set.contains "recipes:read" scopes) then
                return []
            else
                let! rows = Db.listRecipes cs user.Id
                return rows |> List.map (fun r -> r.Title, ingredientsOf r.Body |> List.map (fun i -> i.Name))
        }

    /// The current menu's sides, for the shopping list. Empty with no menu.
    let currentMenuSides () =
        task {
            let! menu = Db.latestMenu cs user.Id

            match menu with
            | Some m -> return! Db.listMenuSides cs user.Id m.Id
            | None -> return []
        }

    [<McpServerTool(Name = "whoami"); Description("The account these tools act as, and the scopes this token was granted.")>]
    member _.WhoAmI() =
        {| Email = user.Email
           Name = user.Name
           Scopes = Set.toList scopes |}

    [<McpServerTool(Name = "list_recipes"); Description("All of the user's recipes: id, title, full Cooklang body and the tags on each.")>]
    member _.ListRecipes() : Task<Recipe list> =
        task {
            require "recipes:read"
            let! rows = Db.listRecipes cs user.Id
            let! links = Db.listRecipeTags cs user.Id
            let byRecipe = links |> List.groupBy fst |> Map.ofList

            return
                rows
                |> List.map (fun r ->
                    let tags = byRecipe |> Map.tryFind r.Id |> Option.defaultValue [] |> List.map snd
                    recipe tags r)
        }

    [<McpServerTool(Name = "get_recipe"); Description("One recipe by id.")>]
    member _.GetRecipe([<Description("Recipe id, from list_recipes")>] id: string) : Task<Recipe> =
        task {
            require "recipes:read"
            let gid = parseId id
            let! row = Db.getRecipe cs user.Id gid

            match row with
            | Some r ->
                let! tags = Db.tagsOfRecipe cs user.Id gid
                return recipe tags r
            | None -> return raise (McpException "No such recipe")
        }

    [<McpServerTool(Name = "create_recipe");
      Description("Create a recipe. Returns the recipe and `notes`: what the Cooklang parser made of the body, which is empty when the syntax is right.")>]
    member _.CreateRecipe
        ([<Description("Recipe title")>] title: string, [<Description(bodyDoc)>] body: string)
        : Task<SavedRecipe> =
        task {
            require "recipes:write"
            let! _ = ensurePantry ()
            let! id = Db.insertRecipe cs user.Id title body
            let! row = Db.getRecipe cs user.Id id
            return { Recipe = recipe [] row.Value; Notes = cooklangNotes row.Value.Body }
        }

    [<McpServerTool(Name = "update_recipe");
      Description("Change a recipe's title and/or body. Omit a field to leave it unchanged. Returns the recipe and `notes`, as create_recipe does.")>]
    member _.UpdateRecipe
        (
            [<Description("Recipe id")>] id: string,
            [<Description("New title; omit to keep"); Optional; DefaultParameterValue(null: string)>] title: string,
            [<Description("New body, same Cooklang as create_recipe; omit to keep");
              Optional;
              DefaultParameterValue(null: string)>] body: string
        ) : Task<SavedRecipe> =
        task {
            require "recipes:write"
            let gid = parseId id
            let! ok = Db.updateRecipe cs user.Id gid (Option.ofObj title) (Option.ofObj body)

            if not ok then
                raise (McpException "No such recipe")

            let! row = Db.getRecipe cs user.Id gid
            let! tags = Db.tagsOfRecipe cs user.Id gid
            return { Recipe = recipe tags row.Value; Notes = cooklangNotes row.Value.Body }
        }

    [<McpServerTool(Name = "delete_recipe"); Description("Delete a recipe permanently. Its tags come off it; the tags themselves stay.")>]
    member _.DeleteRecipe([<Description("Recipe id")>] id: string) : Task<string> =
        task {
            require "recipes:write"
            let! ok = Db.deleteRecipe cs user.Id (parseId id)
            return (if ok then "Deleted." else "No such recipe.")
        }

    // --- tags ----------------------------------------------------------------

    [<McpServerTool(Name = "list_tags");
      Description("Every tag the user has, with how many recipes carry each. Tags are free text and belong to the user, not to one recipe; two names differing only in case or surrounding space are the same tag.")>]
    member _.ListTags() : Task<Tag list> =
        task {
            require "recipes:read"
            let! rows = Db.listTags cs user.Id
            return List.map tag rows
        }

    [<McpServerTool(Name = "add_recipe_tags");
      Description("Put tags on a recipe, making a tag of any name that isn't one yet. A name the recipe already carries, in any casing, is left alone. Returns the recipe's tags afterwards.")>]
    member _.AddRecipeTags
        ([<Description("Recipe id, from list_recipes")>] recipeId: string, [<Description("Tag names")>] names: string list)
        : Task<string list> =
        task {
            require "recipes:write"
            let rid = parseId recipeId
            let names = names |> List.map Shared.Tag.clean |> List.filter ((<>) "")
            let! ok = Db.insertRecipeTags cs user.Id rid names

            if not ok then
                raise (McpException "No such recipe")

            return! Db.tagsOfRecipe cs user.Id rid
        }

    [<McpServerTool(Name = "remove_recipe_tag");
      Description("Take one tag off a recipe. The tag itself stays, on whatever other recipes carry it; delete_tag is what gets rid of it.")>]
    member _.RemoveRecipeTag
        ([<Description("Recipe id")>] recipeId: string, [<Description("Tag name")>] name: string)
        : Task<string> =
        task {
            require "recipes:write"
            let! ok = Db.deleteRecipeTag cs user.Id (parseId recipeId) (Shared.Tag.clean name)
            return (if ok then "Removed." else "That recipe doesn't have that tag.")
        }

    [<McpServerTool(Name = "rename_tag");
      Description("Rename a tag everywhere at once - recipes carry its id, not its name. Refused when another tag already goes by that name, since the app would treat the two as one tag.")>]
    member _.RenameTag
        ([<Description("Tag id, from list_tags")>] id: string, [<Description("New name")>] name: string)
        : Task<string> =
        task {
            require "recipes:write"
            let name = Shared.Tag.clean name

            if name = "" then
                raise (McpException "A tag needs a name")

            let! result = Db.renameTag cs user.Id (parseId id) name

            match result with
            | Db.Renamed -> return $"Renamed to \"{name}\"."
            | Db.TagNotFound -> return "No such tag."
            | Db.NameTaken other -> return raise (McpException $"There is already a tag called \"{other}\"")
        }

    [<McpServerTool(Name = "delete_tag"); Description("Delete a tag for good, taking it off every recipe that carries it. The recipes themselves are untouched.")>]
    member _.DeleteTag([<Description("Tag id, from list_tags")>] id: string) : Task<string> =
        task {
            require "recipes:write"
            let! ok = Db.deleteTag cs user.Id (parseId id)
            return (if ok then "Deleted." else "No such tag.")
        }

    [<McpServerTool(Name = "get_shopping_list");
      Description("The current shopping list (the newest un-archived one) and its items, unchecked first, plus the current menu's sides grouped by name. Each item's recipes are the recipes whose Cooklang still names that ingredient. id and name are null until something has been added.")>]
    member _.GetShoppingList() : Task<ShoppingList> =
        task {
            require "shopping:read"
            let! list = Db.latestShoppingList cs user.Id
            let! sides = currentMenuSides ()

            match list with
            | None ->
                return
                    { Id = null
                      Name = null
                      Items = []
                      MenuSides = sideGroups sides }
            | Some l ->
                let! rows = Db.listShoppingItems cs user.Id l.Id
                let! recipes = recipeIngredients ()

                return
                    { Id = string l.Id
                      Name = l.Name
                      Items = rows |> List.map (item recipes)
                      MenuSides = sideGroups sides }
        }

    [<McpServerTool(Name = "add_shopping_items");
      Description("Add items to the current shopping list, creating one named SL-MMDD (today) if the user has none.")>]
    member _.AddShoppingItems([<Description("Items to add")>] items: NewShoppingItem list) : Task<ShoppingItem list> =
        task {
            require "shopping:write"
            let! listId = currentListId ()

            let! ids =
                Db.insertShoppingItems
                    cs
                    user.Id
                    listId
                    (items |> List.map (fun i -> i.Name, (i.Quantity |> Option.ofObj |> Option.defaultValue ""), (i.Unit |> Option.ofObj |> Option.defaultValue "")))

            let! rows = Db.listShoppingItems cs user.Id listId
            let! recipes = recipeIngredients ()
            return rows |> List.filter (fun r -> List.contains r.Id ids) |> List.map (item recipes)
        }

    [<McpServerTool(Name = "set_shopping_item_done"); Description("Check an item off (or un-check it).")>]
    member _.SetShoppingItemDone
        ([<Description("Item id")>] id: string, [<Description("true = checked off")>] isDone: bool)
        : Task<string> =
        task {
            require "shopping:write"
            let! ok = Db.setShoppingItemDone cs user.Id (parseId id) isDone
            return (if ok then "Updated." else "No such item.")
        }

    [<McpServerTool(Name = "remove_shopping_item"); Description("Remove an item from the shopping list.")>]
    member _.RemoveShoppingItem([<Description("Item id")>] id: string) : Task<string> =
        task {
            require "shopping:write"
            let! ok = Db.deleteShoppingItem cs user.Id (parseId id)
            return (if ok then "Removed." else "No such item.")
        }

    [<McpServerTool(Name = "archive_shopping_list");
      Description("Put the current shopping list away, items and all. This is how a new list starts: the next add creates one named SL-MMDD.")>]
    member _.ArchiveShoppingList() : Task<string> =
        task {
            require "shopping:write"
            let! list = Db.latestShoppingList cs user.Id

            match list with
            | None -> return "No shopping list to archive."
            | Some l ->
                let! _ = Db.archiveShoppingList cs user.Id l.Id
                return $"Archived {l.Name}."
        }

    // --- menus ---------------------------------------------------------------

    [<McpServerTool(Name = "get_menu");
      Description("The current menu (the newest un-archived one): its recipes in the order added, each with its sides and whether the shopping list already has all its ingredients. id and name are null until something has been added.")>]
    member _.GetMenu() : Task<Menu> =
        task {
            require "menus:read"
            let! menu = Db.latestMenu cs user.Id

            match menu with
            | None -> return { Id = null; Name = null; Entries = [] }
            | Some m ->
                let! entries = Db.listMenuRecipes cs user.Id m.Id
                let! sides = Db.listMenuSides cs user.Id m.Id
                let! list = Db.latestShoppingList cs user.Id

                let! items =
                    match list with
                    | Some l -> Db.listShoppingItems cs user.Id l.Id
                    | None -> Task.FromResult []

                return
                    { Id = string m.Id
                      Name = m.Name
                      Entries =
                        entries
                        |> List.map (fun e ->
                            { Id = string e.Id
                              RecipeId = string e.RecipeId
                              Title = e.Title
                              Sides = sides |> List.filter (fun s -> s.MenuRecipeId = e.Id) |> List.map side
                              InShoppingList = allIngredientsPresent items e.Body }) }
        }

    [<McpServerTool(Name = "add_to_menu");
      Description("Add a recipe to the current menu, creating one named M-MMDD-word-word (today, plus two random words) if the user has none, and put its ingredients on the shopping list (skipped when they are all there already). The same recipe can be added more than once.")>]
    member _.AddToMenu([<Description("Recipe id")>] recipeId: string) : Task<MenuEntry> =
        task {
            require "menus:write"
            let rid = parseId recipeId
            let! recipe = Db.getRecipe cs user.Id rid

            match recipe with
            | None -> return raise (McpException "No such recipe")
            | Some r ->
                let! menuId = currentMenuId ()
                let! entryId = Db.insertMenuRecipe cs user.Id menuId rid

                // Ingredients go on the list too, as the + button does.
                let! listId = currentListId ()
                let! items = Db.listShoppingItems cs user.Id listId
                let ingredients = ingredientsOf r.Body

                if not ingredients.IsEmpty && not (allIngredientsPresent items r.Body) then
                    let! _ =
                        Db.insertShoppingItems
                            cs
                            user.Id
                            listId
                            (ingredients |> List.map (fun i -> i.Name, quantityText i.Quantity, defaultArg i.Unit ""))

                    ()

                let! items = Db.listShoppingItems cs user.Id listId

                return
                    { Id = string entryId.Value
                      RecipeId = string r.Id
                      Title = r.Title
                      Sides = []
                      InShoppingList = allIngredientsPresent items r.Body }
        }

    [<McpServerTool(Name = "remove_from_menu"); Description("Take a recipe off the menu, with any sides under it. The shopping list is left alone.")>]
    member _.RemoveFromMenu([<Description("Menu entry id, from get_menu")>] id: string) : Task<string> =
        task {
            require "menus:write"
            let! ok = Db.deleteMenuRecipe cs user.Id (parseId id)
            return (if ok then "Removed." else "No such menu entry.")
        }

    [<McpServerTool(Name = "add_menu_sides");
      Description("Note side dishes under a recipe on the menu (\"rice\", \"green beans\"). They show on the shopping list too, where sides with the same name across recipes count up as one line.")>]
    member _.AddMenuSides
        ([<Description("Menu entry id, from get_menu")>] entryId: string, [<Description("Side names")>] names: string list)
        : Task<MenuSide list> =
        task {
            require "menus:write"
            let eid = parseId entryId
            let names = names |> List.map (fun n -> n.Trim()) |> List.filter ((<>) "")
            let! ids = Db.insertMenuSides cs user.Id eid names

            match ids with
            | None -> return raise (McpException "No such menu entry")
            | Some ids ->
                let! menu = Db.latestMenu cs user.Id
                let! sides = Db.listMenuSides cs user.Id menu.Value.Id
                return sides |> List.filter (fun s -> List.contains s.Id ids) |> List.map side
        }

    [<McpServerTool(Name = "set_menu_side_done"); Description("Check a side off on the shopping list (or un-check it).")>]
    member _.SetMenuSideDone
        ([<Description("Side id")>] id: string, [<Description("true = checked off")>] isDone: bool)
        : Task<string> =
        task {
            require "menus:write"
            let! ok = Db.setMenuSideDone cs user.Id (parseId id) isDone
            return (if ok then "Updated." else "No such side.")
        }

    [<McpServerTool(Name = "remove_menu_side"); Description("Remove a side from the menu.")>]
    member _.RemoveMenuSide([<Description("Side id")>] id: string) : Task<string> =
        task {
            require "menus:write"
            let! ok = Db.deleteMenuSide cs user.Id (parseId id)
            return (if ok then "Removed." else "No such side.")
        }

    [<McpServerTool(Name = "archive_menu");
      Description("Put the current menu away, recipes and sides included. This is how a new menu starts: the next add_to_menu creates one named M-MMDD-word-word.")>]
    member _.ArchiveMenu() : Task<string> =
        task {
            require "menus:write"
            let! menu = Db.latestMenu cs user.Id

            match menu with
            | None -> return "No menu to archive."
            | Some m ->
                let! _ = Db.archiveMenu cs user.Id m.Id
                return $"Archived {m.Name}."
        }
