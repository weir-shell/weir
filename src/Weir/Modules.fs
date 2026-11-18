/// The module table [D:module-table]: one row per builtin module — its
/// name, the line `#help` shows for it, and what its members do unless a
/// member is classified on its own. The effect partition, the purity
/// message's wording and the blurbs all read it, so a module is described
/// once. It stands before all of them in the build; Builtins holds the
/// members, and a test holds the two lists to each other.
module Weir.Modules

/// what a module's members do unless one is named on its own
type ModuleEffect =
    | NoEffect
    /// ambient: an input the run does not own, in the message's words
    | Reads of what: string
    /// a mutation, in the message's words
    | Writes of what: string

type ModuleRow = {
    Name: string
    Blurb: string
    Effect: ModuleEffect
    /// every member carries the effect; false where only the members
    /// named below do and the effect words the rest (Http)
    Whole: bool
    /// not worth naming as the effect a statement runs before the CLI
    /// parse: a diagnostic line is not the script's work
    Quiet: bool
}

let private row (name: string) (blurb: string) (effect: ModuleEffect) : ModuleRow = {
    Name = name
    Blurb = blurb
    Effect = effect
    Whole = true
    Quiet = false
}

let rows: ModuleRow list = [
    row "Args" "typed argv — flags, subcommands, defaults from a record" (Reads "reads the environment")
    row "Bytes" "the non-text value: base64, sha256, UTF-8 bridges" (NoEffect)
    row
        "Color"
        "terminal colour: red/green/…, bold/dim/underline, sgr — auto-off when piped"
        (Reads "reads the terminal")
    row "Debug" "debugging aids: peek at a value, break into a prompt" (Writes "writes to the terminal")
    |> fun r -> { r with Quiet = true }
    row "Dir" "directory operations: create, list, move, delete, stat" (Reads "reads the filesystem")
    row "Duration" "spans of time: literals, arithmetic, parse/show, sleep" (NoEffect)
    row "Env" "environment variables: one, a typed record, a dotenv file" (Reads "reads the environment")
    row "File" "file operations: read, write, copy, hash, secrets" (Reads "reads the filesystem")
    row "Float" "finite floats: convert, round, near-compare, parse" (NoEffect)
    row "Frontier" "the bounded worklist fold Graph and Tree derive from" (NoEffect)
    row "Graph" "reachability over a neighbor function, cycle-safe" (NoEffect)
    row "Http" "HTTP requests: one record, one runner, typed auth" (Reads "talks to the network")
    |> fun r -> { r with Whole = false }
    row "Instant" "points on the UTC timeline: now, parse, compare" (NoEffect)
    row "Json" "JSON helpers: inferShape drafts type decls from a sample" (NoEffect)
    row "Log" "levelled diagnostics to stderr (WEIR_LOG selects)" (Writes "writes logs")
    |> fun r -> { r with Quiet = true }
    row "Map" "immutable maps keyed by any equatable type: get, add, pairs" (NoEffect)
    row "Net" "network probes: portOpen" (Reads "talks to the network")
    row "Option" "presence and absence: map, bind, defaults" (NoEffect)
    row "Path" "path text and discovery: join, confine, glob, temp dirs" (NoEffect)
    row "Plan" "plan/apply: preview a plan's ops before applying" (NoEffect)
    row "Poll" "poll's options record: defaults" (NoEffect)
    row "Proc" "scoped process handles: pid, running, tail, wait, stop" (Writes "touches a process")
    row "Reply" "a handler's responses: empty, text, html, json, rawJson, stream, redirect" (NoEffect)
    row "Result" "the value or the error message `|> try result` gives: map, defaults, toOption" (NoEffect)
    row "Retry" "retry's options record: defaults" (NoEffect)
    row "Secret" "rendering-masked values: of, map, reveal" (NoEffect)
    row "Self" "the process's own facts: args, stdin, pid, paths — and prompt" (NoEffect)
    row "Seq" "lazy sequence pipeline ops: map, where, fold, pmap" (NoEffect)
    row
        "Server"
        "scoped HTTP listener handles: port, running; defaults, the default settings (see within serve)"
        (NoEffect)
    row "Size" "byte sizes: binary-unit literals, arithmetic, parse" (NoEffect)
    row "Str" "string ops: trim, split, match, encode, hash" (NoEffect)
    row "Table" "aligned-table helpers: inferShape drafts a row type from a sample" (NoEffect)
    row
        "Term"
        "the terminal: width (columns; 80 when there is no terminal), title (set the window title)"
        (Reads "reads the terminal")
    row "Tree" "parent-first effect walks over discovered children" (NoEffect)
    row "Uuid" "128-bit identifiers: v4, v5, time-ordered v7, parse, inspect" (NoEffect)
    row "Yaml" "YAML nodes: parse, merge (strategic patch), inferShape" (NoEffect)
]

let private rowOf: Map<string, ModuleRow> =
    rows |> List.map (fun r -> r.Name, r) |> Map.ofList

/// a module's row, by a member's qualified name
let rowFor (qualified: string) : ModuleRow option =
    match qualified.IndexOf '.' with
    | i when i > 0 -> Map.tryFind (qualified.Substring(0, i)) rowOf
    | _ -> None
