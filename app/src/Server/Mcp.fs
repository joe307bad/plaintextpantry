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

/// The newest list with its items. `Id` and `Name` are null when the user
/// has no list yet; add_shopping_items makes one.
type ShoppingList =
    { Id: string
      Name: string
      Items: ShoppingItem list }

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
      Description("The current shopping list (the newest one) and its items, unchecked first. id and name are null until something has been added.")>]
    member _.GetShoppingList() : Task<ShoppingList> =
        task {
            require "shopping:read"
            let! list = Db.latestShoppingList cs user.Id

            match list with
            | None -> return { Id = null; Name = null; Items = [] }
            | Some l ->
                let! rows = Db.listShoppingItems cs user.Id l.Id
                return { Id = string l.Id; Name = l.Name; Items = List.map item rows }
        }

    [<McpServerTool(Name = "add_shopping_items");
      Description("Add items to the current shopping list, creating one named SL-MMDD (today) if the user has none.")>]
    member _.AddShoppingItems([<Description("Items to add")>] items: NewShoppingItem list) : Task<ShoppingItem list> =
        task {
            require "shopping:write"
            let! list = Db.latestShoppingList cs user.Id

            let! listId =
                match list with
                | Some l -> Task.FromResult l.Id
                | None -> Db.insertShoppingList cs user.Id (Shared.ShoppingList.defaultName DateTime.Now)

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
