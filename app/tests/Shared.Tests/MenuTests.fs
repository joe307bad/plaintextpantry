module MenuTests

open System
open Xunit
open Shared

[<Fact>]
let ``default name is M- plus zero-padded month and day, then two words`` () =
    let name = Menu.defaultName (DateTime(2026, 3, 5)) (Random 1)

    match name.Split '-' with
    | [| "M"; "0305"; adjective; noun |] ->
        Assert.Contains(adjective, Menu.adjectives)
        Assert.Contains(noun, Menu.nouns)
    | parts -> failwithf "unexpected shape: %A" parts

[<Fact>]
let ``the words vary`` () =
    let random = Random 42
    let names = [ for _ in 1..20 -> Menu.defaultName (DateTime(2026, 12, 31)) random ] |> List.distinct
    Assert.True(names.Length > 1)
