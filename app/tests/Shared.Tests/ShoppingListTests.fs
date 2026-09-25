module ShoppingListTests

open System
open Xunit
open Shared

[<Fact>]
let ``default name is SL- plus zero-padded month and day`` () =
    Assert.Equal("SL-0305", ShoppingList.defaultName (DateTime(2026, 3, 5)))
    Assert.Equal("SL-1231", ShoppingList.defaultName (DateTime(2026, 12, 31)))

[<Theory>]
[<InlineData(0, "today")>]
[<InlineData(1, "yesterday")>]
[<InlineData(2, "2 days ago")>]
[<InlineData(40, "40 days ago")>]
let ``age counts calendar days`` (daysAgo: int, expected: string) =
    let today = DateTime(2026, 9, 19, 8, 0, 0)
    // Late the evening `daysAgo` days back: still that many calendar days.
    let created = today.AddDays(float -daysAgo).Date.AddHours 23.0
    Assert.Equal(expected, Created.age today created)

[<Fact>]
let ``a clock skewed into the future still reads today`` () =
    let today = DateTime(2026, 9, 19)
    Assert.Equal("today", Created.age today (today.AddDays 1.0))

[<Fact>]
let ``editing past the quantity and unit keeps them`` () =
    Assert.Equal(("2", "cups", "bread flour"), ShoppingItem.edit "2" "cups" "2 cups bread flour")
    Assert.Equal(("2", "", "eggs"), ShoppingItem.edit "2" "" " 2 eggs ")

[<Fact>]
let ``editing the quantity or unit takes the whole line as the name`` () =
    Assert.Equal(("", "", "3 cups flour"), ShoppingItem.edit "2" "cups" "3 cups flour")
    Assert.Equal(("", "", "2 cups"), ShoppingItem.edit "2" "cups" "2 cups")

[<Fact>]
let ``editing a plain item is just a rename`` () =
    Assert.Equal(("", "", "oat milk"), ShoppingItem.edit "" "" "oat milk")

/// The recipes behind a line, read off their Cooklang the way the shopping
/// list page derives them.
let private ingredientsOf (title: string, body: string) =
    title, Cooklang.ingredients (Cooklang.parse body).Recipe |> List.map (fun i -> i.Name)

let private focaccia = "Focaccia", "Mix @flour{500%g} with @water{350%g} and @salt{}."
let private pizza = "Pizza", "Knead @Flour{300%g} into a base."

[<Fact>]
let ``an ingredient names the recipes calling for it, whatever the case`` () =
    let recipes = [ focaccia; pizza ] |> List.map ingredientsOf
    Assert.Equal<string list>([ "Focaccia"; "Pizza" ], ShoppingItem.sources recipes "flour")
    Assert.Equal<string list>([ "Focaccia" ], ShoppingItem.sources recipes " Water ")
    Assert.Equal<string list>([], ShoppingItem.sources recipes "oat milk")

[<Fact>]
let ``taking the ingredient out of the recipe drops the recipe`` () =
    let without = [ "Focaccia", "Mix @water{350%g} and @salt{}." ] |> List.map ingredientsOf
    Assert.Equal<string list>([], ShoppingItem.sources without "flour")

[<Fact>]
let ``two recipes of the same name count once`` () =
    let recipes = [ focaccia; focaccia ] |> List.map ingredientsOf
    Assert.Equal<string list>([ "Focaccia" ], ShoppingItem.sources recipes "flour")

[<Fact>]
let ``a nameless line comes from nowhere`` () =
    Assert.Equal<string list>([], ShoppingItem.sources ([ focaccia ] |> List.map ingredientsOf) "  ")

[<Fact>]
let ``the recipes read as a parenthesis after the line`` () =
    Assert.Equal("", ShoppingItem.sourceText [])
    Assert.Equal("(Focaccia)", ShoppingItem.sourceText [ "Focaccia" ])
    Assert.Equal("(Focaccia, Pizza)", ShoppingItem.sourceText [ "Focaccia"; "Pizza" ])
