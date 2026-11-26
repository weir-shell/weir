module Weir.Diagnose

let private isIdentStart c = System.Char.IsLetter c || c = '_'

let private isIdentCont c =
    System.Char.IsLetterOrDigit c || c = '_'

/// a `| <binding>` stage [D:stage-binding-hint]: after a single `|` a
/// name the session binds is a value, never a program — the REPL's bare
/// `head`, `sort`, `last` shadow the tools of the same name there, and
/// the pipe-glyph error alone does not say so. Fires when the stage
/// reads as a command: a flag follows, or a program of that name exists
let private stageHint
    (isKnown: string -> bool)
    (isCommandCallable: string -> bool)
    (isExternal: string -> bool)
    (line: string)
    : string option =
    let n = line.Length
    let mutable found = None
    let mutable inStr = false
    let mutable i = 0

    while found.IsNone && i < n do
        let c = line[i]

        if inStr then
            if c = '\\' then
                i <- i + 1
            elif c = '"' then
                inStr <- false
        elif c = '"' then
            inStr <- true
        elif
            c = '|'
            && (i = 0 || line[i - 1] <> '|')
            && (i + 1 >= n || (line[i + 1] <> '|' && line[i + 1] <> '>' && line[i + 1] <> '}'))
            && (i = 0 || line[i - 1] <> '{')
        then
            let rest = line.Substring(i + 1).TrimStart()
            let word = rest |> Seq.takeWhile isIdentCont |> System.String.Concat
            let tail = rest.Substring(word.Length).TrimStart()

            let flagLike =
                tail.StartsWith "--"
                || (tail.StartsWith "-"
                    && tail.Length > 1
                    && (System.Char.IsLetter tail[1] || System.Char.IsDigit tail[1]))

            if
                word <> ""
                && isIdentStart word[0]
                && isKnown word
                && not (isCommandCallable word)
                // a match arm's binder is no stage
                && not (tail.StartsWith "->")
                && not (tail.StartsWith "when ")
                && (flagLike || isExternal word)
            then
                found <-
                    Some
                        $"'{word}' after '|' is a weir binding, so that stage is a value, and '|' only feeds programs. For the external command write '| ^{word}'; to hand the output to the binding write '|> {word}'."

        i <- i + 1

    found

let private headHint
    (isKnown: string -> bool)
    (isCommandCallable: string -> bool)
    (isExternal: string -> bool)
    (line: string)
    : string option =
    let trimmed = line.TrimStart()

    if trimmed = "" || not (isIdentStart trimmed[0]) then
        None
    else
        let head = trimmed |> Seq.takeWhile isIdentCont |> System.String.Concat
        let tail = trimmed.Substring(head.Length).TrimStart()

        // Command-callable heads (cd) parse in command mode even
        // though they are bindings — the hint must not claim
        // "expression mode" for them.
        if head = "" || tail = "" || not (isKnown head) || isCommandCallable head then
            None
        else
            let flagLike =
                tail.StartsWith "--"
                || (tail.StartsWith "-" && tail.Length > 1 && System.Char.IsLetter tail[1])

            let pathLike =
                tail.StartsWith "/"
                || tail = "."
                || tail.StartsWith ". "
                || tail.StartsWith "./"
                || tail.StartsWith ".."
                || tail.StartsWith "~"

            let barewordLike =
                tail.Length > 0 && System.Char.IsLetter tail[0] && isExternal head

            if flagLike || pathLike || barewordLike then
                Some
                    $"'{head}' is a weir binding, so this line is expression mode. For the external command use '^{trimmed}'; to use the binding, pipe it ('{head} |> ...') or quote string arguments."
            else
                None

let hint
    (isKnown: string -> bool)
    (isCommandCallable: string -> bool)
    (isExternal: string -> bool)
    (line: string)
    : string option =
    headHint isKnown isCommandCallable isExternal line
    |> Option.orElseWith (fun () -> stageHint isKnown isCommandCallable isExternal line)
