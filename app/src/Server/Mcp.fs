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

/// What the tools return for a recipe. Camel-cased by the SDK's serializer.
type Recipe =
    { Id: string
      Title: string
      /// Cooklang source: `@ingredient{qty%unit}`, `#cookware{}`, `~{time}`.
      Body: string
      CreatedAt: DateTimeOffset }

type ShoppingItem =
    { Id: string
      Name: string
      Quantity: string
      Unit: string
      Done: bool }

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

let private recipe (r: Db.RecipeRow) =
    { Id = string r.Id
      Title = r.Title
      Body = r.Body
      CreatedAt = r.CreatedAt }

let private item (i: Db.ShoppingItemRow) =
    { Id = string i.Id
      Name = i.Name
      Quantity = i.Quantity
      Unit = i.Unit
      Done = i.Done }

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

    let require scope =
        if not (Set.contains scope scopes) then
            raise (noScope scope)

    /// The current list's id, creating today's list when there is none.
    let currentListId () =
        task {
            let! list = Db.latestShoppingList cs user.Id

            match list with
            | Some l -> return l.Id
            | None -> return! Db.insertShoppingList cs user.Id (Shared.ShoppingList.defaultName DateTime.Now)
        }

    /// The current menu's id, creating today's menu when there is none.
    let currentMenuId () =
        task {
            let! menu = Db.latestMenu cs user.Id

            match menu with
            | Some m -> return m.Id
            | None -> return! Db.insertMenu cs user.Id (Shared.Menu.defaultName DateTime.Now (Random()))
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

    [<McpServerTool(Name = "list_recipes"); Description("All of the user's recipes: id, title and full Cooklang body.")>]
    member _.ListRecipes() : Task<Recipe list> =
        task {
            require "recipes:read"
            let! rows = Db.listRecipes cs user.Id
            return List.map recipe rows
        }

    [<McpServerTool(Name = "get_recipe"); Description("One recipe by id.")>]
    member _.GetRecipe([<Description("Recipe id, from list_recipes")>] id: string) : Task<Recipe> =
        task {
            require "recipes:read"
            let! row = Db.getRecipe cs user.Id (parseId id)

            match row with
            | Some r -> return recipe r
            | None -> return raise (McpException "No such recipe")
        }

    [<McpServerTool(Name = "create_recipe");
      Description("Create a recipe. The body is Cooklang: ingredients as @name{quantity%unit}, cookware as #name{}, timers as ~{10%minutes}, one step per paragraph. Multi-word names end with {}: @olive oil{2%tbsp}.")>]
    member _.CreateRecipe
        ([<Description("Recipe title")>] title: string, [<Description("Cooklang source")>] body: string)
        : Task<Recipe> =
        task {
            require "recipes:write"
            let! id = Db.insertRecipe cs user.Id title body
            let! row = Db.getRecipe cs user.Id id
            return recipe row.Value
        }

    [<McpServerTool(Name = "update_recipe"); Description("Change a recipe's title and/or body. Omit a field to leave it unchanged.")>]
    member _.UpdateRecipe
        (
            [<Description("Recipe id")>] id: string,
            [<Description("New title; omit to keep"); Optional; DefaultParameterValue(null: string)>] title: string,
            [<Description("New Cooklang body; omit to keep"); Optional; DefaultParameterValue(null: string)>] body: string
        ) : Task<Recipe> =
        task {
            require "recipes:write"
            let gid = parseId id
            let! ok = Db.updateRecipe cs user.Id gid (Option.ofObj title) (Option.ofObj body)

            if not ok then
                raise (McpException "No such recipe")

            let! row = Db.getRecipe cs user.Id gid
            return recipe row.Value
        }

    [<McpServerTool(Name = "delete_recipe"); Description("Delete a recipe permanently.")>]
    member _.DeleteRecipe([<Description("Recipe id")>] id: string) : Task<string> =
        task {
            require "recipes:write"
            let! ok = Db.deleteRecipe cs user.Id (parseId id)
            return (if ok then "Deleted." else "No such recipe.")
        }

    [<McpServerTool(Name = "get_shopping_list");
      Description("The current shopping list (the newest un-archived one) and its items, unchecked first, plus the current menu's sides grouped by name. id and name are null until something has been added.")>]
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

                return
                    { Id = string l.Id
                      Name = l.Name
                      Items = List.map item rows
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
            return rows |> List.filter (fun r -> List.contains r.Id ids) |> List.map item
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
