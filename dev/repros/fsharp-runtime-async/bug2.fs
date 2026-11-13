open System.Threading.Tasks
open System.Runtime.CompilerServices
open Microsoft.FSharp.Core.CompilerServices

let inline twice ([<InlineIfLambda>] h: int -> int) (x: int) : int = h (h x)

let g (x: int) : Task<int> = Task.FromResult(x + 1)

let f (x: int) : Task<int> =
    StateMachineHelpers.__runtimeAsyncReturn (twice (fun i -> AsyncHelpers.Await(g i)) x)

[<EntryPoint>]
let main _ =
    System.Console.WriteLine((f 3).Result)
    0
