# Corpus comparison report (78 snippets)

- agree-accept: 4
- agree-reject: 50
- weir-accepts-fsharp-rejects (GOLD): 0
- fsharp-accepts-weir-rejects: 24

## GOLD: weir accepts, F# rejects

## F# accepts, weir rejects
--- 2c140db18d7e.snippet
let myVal : nativeptr<voidptr> =  Unchecked.defaultof<_>
--- 1a41bd9f4d21.snippet
// Ω
let _ = "\937"B
--- 31c8cb1f8702.snippet
let _ = "a\097"B
--- 14723552eac5.snippet
// ú
let _ = "\250"B
--- 30249ca404bf.snippet
// Ω
let _ = "\837"B
--- 44dcb5d03e45.snippet
let f x =
    match x with
    | 0 -> 0
--- d748ad70a977.snippet
let inline f (x: int) =
    x + x

let i = f 5
--- e8686993e92c.snippet
let x: int list = []
--- daf877149687.snippet
let myFunc1 param = param + 1
let myFunc2 param = param + 2
--- b4fc41d28d6b.snippet
let f param = param + 1
--- 67e63cfaa076.snippet
let (|IsA|) x = x = "A"
let (IsA r) = "A"
--- 3f9d31131224.snippet
let result = query { join a in ["x"] on ("x" = a); join b in ["y"] on ("y" = b); select a }
--- a0b2bd73d2c6.snippet
let f x = x + 1
let s = $"{f:N2}"
--- 2c96b3d2ce29.snippet
let f x = x + 1
let g x = x * 2
let s = $"{f} and {g}"
--- 30827aee43ec.snippet
let f x = x + 1
let s = $"{f 42}"
--- 2647c7ca01ba.snippet
let myFunc (x: int) = string x
let s = $"result: {myFunc}"
--- 1cbc84e93705.snippet
let add x y = x + y
let s = $"{add 1}"
--- f7165aed4635.snippet
let f = fun x -> x + 1
let s = $"{f}"
--- 8b62bdf759d2.snippet
/// <summary> Return <paramref/> </summary>
/// <param> the parameter </param>
let f a = a
--- 7210abda8650.snippet
/// <summary> Return <paramref name="a" /> </summary>
/// <param name="a"> the parameter </param>
/// <param name="a"> the parameter </param>
let f a = a
--- 8f7ab01121ef.snippet
/// <summary> Return <paramref name="b" /> </summary>
/// <param name="b"> the parameter </param>
let f a = a
--- 1a6c0790accc.snippet
/// <summary> F </summary>
/// <param name="x"> the parameter </param>
let f a = a
--- 735e69ffda78.snippet
/// <summary> Return <paramref name="b" /> </summary>
/// <param name="a"> the parameter </param>
let f a = a
--- ee5f30008c85.snippet
let internal original_submission = "From the first submission";;
