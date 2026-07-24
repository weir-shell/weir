module Weir.Diagnose

let private isIdentStart c = System.Char.IsLetter c || c = '_'

let private isIdentCont c =
    System.Char.IsLetterOrDigit c || c = '_'

let hint
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

        // Command-callable heads (cd) parse in COMMAND mode even
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
