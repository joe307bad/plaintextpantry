module MenuTests

open System
open Xunit
open Shared

[<Fact>]
let ``default name is M- plus zero-padded month and day`` () =
    Assert.Equal("M-0305", Menu.defaultName (DateTime(2026, 3, 5)))
    Assert.Equal("M-1231", Menu.defaultName (DateTime(2026, 12, 31)))
