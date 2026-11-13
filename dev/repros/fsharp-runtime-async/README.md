# F# runtime async: two compiler bugs

Environment: .NET SDK 11.0.100 (stock `fsc`), runtime 11.0.0, FSharp.Core 11.0.101,
`net11.0`, `<LangVersion>preview</LangVersion>`, linux-x64.

Build one file: `dotnet build -c Release --disable-build-servers -p:Repro=<name>`

| File | Result |
|---|---|
| `bug1` | FS1118, then `fsc` never exits |
| `bug1workaround` | builds, prints `3` |
| `bug2` | FS0971 |
| `bug2ok` | builds, prints `8` |
| `bug2control` | builds, prints `5` |

## Bug 1: a small recursive runtime-async function fails to compile and hangs fsc

`bug1.fs`: a directly recursive function whose body is `__runtimeAsyncReturn (... AsyncHelpers.Await(f (x - 1)))`.

- Release: `error FS1118: Failed to inline the value 'f' marked 'inline', perhaps because a recursive value was marked 'inline'`.
  `f` is not marked `inline`.
- After reporting the error `fsc` does not terminate: about 470% CPU and 4.2 GB RSS after 23 s, still growing. Killed by timeout.
- Debug, or Release with `-p:Optimize=false`: builds and prints `3`.
- `[<MethodImpl(MethodImplOptions.NoInlining)>]` on `f` (`bug1workaround.fs`): builds and prints `3`.
- A larger recursive body (a multi-case `match`) compiles in Release without the attribute, so it looks tied to the optimizer's size-based inlining.

## Bug 2: an inlined lambda containing `Await`, applied to its own result

`bug2.fs`: `let inline twice h x = h (h x)` called with `fun i -> AsyncHelpers.Await(g i)` inside a runtime-async method. No recursion.

- Release and Debug: `error FS0971: Undefined value 'h'`, reported at the call site and again at the caller in `main`.
- Same with `[<InlineIfLambda>]` removed from `h`.
- Same if the body is `let v = h x in h v`.
- `h x + h x` (`bug2ok.fs`): builds and prints `8`.
- The same shape without runtime async, `fun i -> (g i).Result` (`bug2control.fs`): builds and prints `5`.
