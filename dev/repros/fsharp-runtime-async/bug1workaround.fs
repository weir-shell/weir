open System.Threading.Tasks
open System.Runtime.CompilerServices
open Microsoft.FSharp.Core.CompilerServices

[<MethodImpl(MethodImplOptions.NoInlining)>]
let rec f (x: int) : Task<int> =
    StateMachineHelpers.__runtimeAsyncReturn (if x = 0 then 0 else 1 + AsyncHelpers.Await(f (x - 1)))

[<EntryPoint>]
let main _ =
    System.Console.WriteLine((f 3).Result)
    0
