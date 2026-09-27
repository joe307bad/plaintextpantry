module TagTests

open Xunit
open Shared

[<Fact>]
let ``a name is stored as typed, without the surrounding space`` () =
    Assert.Equal("Kaitlyn's fav", Tag.clean "  Kaitlyn's fav ")

[<Fact>]
let ``case and surrounding space don't make a second tag`` () =
    Assert.Equal(Tag.key "quick", Tag.key " Quick ")
    Assert.NotEqual<string>(Tag.key "quick", Tag.key "quicker")

[<Fact>]
let ``a typed name finds the tag it stands for`` () =
    let tags = [ "quick"; "Kaitlyn's fav" ]
    Assert.Equal(Some "quick", Tag.find id " QUICK " tags)
    Assert.Equal(Some "Kaitlyn's fav", Tag.find id "kaitlyn's fav" tags)
    Assert.Equal(None, Tag.find id "weeknight" tags)

[<Fact>]
let ``tags are shown by name, ignoring case`` () =
    let tags = [ "weeknight"; "Kaitlyn's fav"; "quick" ]
    Assert.Equal<string list>([ "Kaitlyn's fav"; "quick"; "weeknight" ], Tag.sorted id tags)
