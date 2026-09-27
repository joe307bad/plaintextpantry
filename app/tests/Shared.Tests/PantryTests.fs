module PantryTests

open Xunit
open Shared

[<Fact>]
let ``a pantry reads as its name and the first section of its id`` () =
    Assert.Equal("My Pantry · 56dcd3ca", Pantry.label "My Pantry" "56dcd3ca-089a-48e6-94e8-f4fcf0acea3c")
    Assert.Equal("56dcd3ca", Pantry.shortId "56dcd3ca-089a-48e6-94e8-f4fcf0acea3c")

[<Fact>]
let ``a name is kept as typed, and a blank one falls back`` () =
    Assert.Equal("Bada Family", Pantry.cleanName "  Bada Family ")
    Assert.Equal(Pantry.DefaultName, Pantry.cleanName "   ")

[<Fact>]
let ``a member is named by whatever is known of them`` () =
    Assert.Equal("Joe", Pantry.memberName "Joe" "joe@example.com" "90dfb0e9-5f6b-4251-92bf-db61fabcda52")
    // Their client writes the name and email, so a row that hasn't synced yet
    // still has the user id to go by.
    Assert.Equal("joe@example.com", Pantry.memberName "" "joe@example.com" "90dfb0e9-5f6b-4251-92bf-db61fabcda52")
    Assert.Equal("90dfb0e9", Pantry.memberName "" "" "90dfb0e9-5f6b-4251-92bf-db61fabcda52")

[<Fact>]
let ``only a pantry id is taken off a scanned code`` () =
    Assert.True(Pantry.isId "56dcd3ca-089a-48e6-94e8-f4fcf0acea3c")
    Assert.True(Pantry.isId "  56DCD3CA-089A-48E6-94E8-F4FCF0ACEA3C  ")
    Assert.False(Pantry.isId "https://example.com")
    Assert.False(Pantry.isId "56dcd3ca-089a-48e6-94e8")
    Assert.False(Pantry.isId "56dcd3ca-089a-48e6-94e8-f4fcf0acea3")
    Assert.False(Pantry.isId "zzzzzzzz-089a-48e6-94e8-f4fcf0acea3c")
