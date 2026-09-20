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
