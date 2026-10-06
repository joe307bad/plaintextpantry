module RecipeTimeTests

open Xunit
open Cooklang

let private minutesOf (src: string) = totalMinutes (parse src).Recipe

[<Fact>]
let ``a duration reads the same however it is written`` () =
    Assert.Equal(Some 90.0, Duration.minutes "90")
    Assert.Equal(Some 90.0, Duration.minutes "90 minutes")
    Assert.Equal(Some 90.0, Duration.minutes "1 hour 30 minutes")
    Assert.Equal(Some 90.0, Duration.minutes "1h30m")
    Assert.Equal(Some 90.0, Duration.minutes "about 1.5 hrs")

[<Fact>]
let ``a duration with no number in it is no duration`` () =
    Assert.Equal(None, Duration.minutes "overnight")
    Assert.Equal(None, Duration.minutes "")
    // A number with a unit that isn't time: nothing to add up.
    Assert.Equal(None, Duration.minutes "4 servings")

[<Fact>]
let ``what the recipe says is what it takes`` () =
    Assert.Equal(Some 45, minutesOf "---\ntime required: 45 minutes\n---\nBoil the @water{}.")
    Assert.Equal(Some 45, minutesOf "---\ntime: 45 min\n---\nBoil the @water{}.")
    Assert.Equal(Some 90, minutesOf "---\nduration: 1 hr 30 min\n---\nBoil the @water{}.")

[<Fact>]
let ``the key is the same key however it is spelled`` () =
    Assert.Equal(Some 45, minutesOf "---\nTime Required: 45 minutes\n---\nBoil the @water{}.")
    Assert.Equal(Some 45, minutesOf "---\ntime_required: 45 minutes\n---\nBoil the @water{}.")

[<Fact>]
let ``prep and cook time are added together`` () =
    Assert.Equal(Some 50, minutesOf "---\nprep time: 20 minutes\ncook time: 30 minutes\n---\nBoil the @water{}.")
    // Either one on its own still answers.
    Assert.Equal(Some 20, minutesOf "---\nprep time: 20 minutes\n---\nBoil the @water{}.")

[<Fact>]
let ``what the recipe states beats the timers in it`` () =
    let src = "---\ntime: 25 minutes\n---\nSimmer for ~{3%hours}."
    Assert.Equal(Some 25, minutesOf src)

[<Fact>]
let ``with nothing stated, the timers are added up`` () =
    Assert.Equal(Some 25, minutesOf "Rest for ~{10%minutes}, then bake for ~{15%minutes}.")
    Assert.Equal(Some 90, minutesOf "Simmer for ~dinner{1.5%hours}.")
    // A timer with no unit is minutes, as the parser's warning says.
    Assert.Equal(Some 10, minutesOf "Rest for ~{10}.")

[<Fact>]
let ``a recipe with no time in it gives no figure`` () =
    Assert.Equal(None, minutesOf "Boil the @water{} and add @pasta{200%g}.")
    Assert.Equal(None, minutesOf "---\ntime: overnight\n---\nLeave the @dough{} out.")
    Assert.Equal(None, minutesOf "")

[<Fact>]
let ``minutes are whole, and rounded up`` () =
    Assert.Equal(Some 1, minutesOf "Blanch for ~{30%seconds}.")
    Assert.Equal(Some 2, minutesOf "Stir for ~{90%seconds}.")
