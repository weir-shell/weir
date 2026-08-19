module Weir.Version

open System.Reflection

// The ONE build stamp [D:version-stamp][D:masking-mechanized] — the WeirStamp target bakes
// `<tag>+<hash>` into this assembly's InformationalVersion (a real tag on a
// release, the honest `0.0.0-dev` marker otherwise; the sha always rides).
// Read from THIS assembly (Weir.dll), not GetEntryAssembly, so the value is
// the same whether the host is the weir executable or an in-proc test runner.
// `--version` and the LSP `serverInfo.version` both read `current`, so the
// human string and the editor's cannot diverge — one source, two consumers.
let current: string =
    match Assembly.GetExecutingAssembly().GetCustomAttributes(typeof<AssemblyInformationalVersionAttribute>, false) with
    | [| :? AssemblyInformationalVersionAttribute as a |] -> a.InformationalVersion
    | _ -> "0.0.0-dev+unknown"
