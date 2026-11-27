module Weir.Csv

// ---- the CSV boundary's text model [D:csv-adapter] ------------------------
// RFC 4180, comma only: records of comma-separated cells; a cell may be
// quoted, and inside the quotes a comma is data, `""` is one quote, and a
// line break is part of the cell — so one record can span several of the
// lines weir hands an adapter. Nothing is guessed: a quote in a bare cell,
// text after a closing quote and a quote never closed are errors naming
// the line.

/// the cells of the records in `lines` (1-based line numbers, as read):
/// each record with the line it starts on. Blank lines between records
/// are skipped; a UTF-8 byte-order mark before the first cell and a CR
/// at a line's end are dropped
let records (lines: (int * string) list) : Result<(int * string list) list, string> =
    let out = ResizeArray<int * string list>()
    let cells = ResizeArray<string>()
    let cell = System.Text.StringBuilder()
    // where the record under way began; 0 between records
    let mutable start = 0
    let mutable quoted = false
    // the cell was closed by its quote: only a comma or the line's end may follow
    let mutable closed = false
    let mutable error = None
    let mutable first = true

    let endCell () =
        cells.Add(cell.ToString())
        cell.Clear() |> ignore
        closed <- false

    for n, raw in lines do
        if error.IsNone then
            let line =
                if first && raw.StartsWith "\uFEFF" then
                    raw.Substring 1
                else
                    raw

            first <- false

            if quoted then
                // a line break inside a quoted cell is the cell's own
                cell.Append '\n' |> ignore
            elif line.Trim() = "" then
                ()
            else
                start <- n

            if quoted || start = n then
                let mutable i = 0

                while error.IsNone && i < line.Length do
                    let c = line[i]

                    if quoted then
                        if c = '"' then
                            if i + 1 < line.Length && line[i + 1] = '"' then
                                cell.Append '"' |> ignore
                                i <- i + 1
                            else
                                quoted <- false
                                closed <- true
                        else
                            cell.Append c |> ignore
                    // a line's own CR; inside a quoted cell it is the cell's
                    elif c = '\r' && i = line.Length - 1 then
                        ()
                    elif c = ',' then
                        endCell ()
                    elif closed then
                        error <-
                            Some
                                $"line {n}, col {i + 1}: text after a closing quote — a quoted cell ends at its quote; a quote inside one is written \"\""
                    elif c = '"' then
                        if cell.Length = 0 then
                            quoted <- true
                        else
                            error <-
                                Some
                                    $"line {n}, col {i + 1}: a quote inside an unquoted cell — quote the whole cell and write the quote as \"\""
                    else
                        cell.Append c |> ignore

                    i <- i + 1

                if error.IsNone && not quoted then
                    endCell ()
                    out.Add((start, List.ofSeq cells))
                    cells.Clear()
                    start <- 0

    match error with
    | Some e -> Error e
    | None when quoted ->
        Error $"line {start}: a quoted cell is never closed — the record that starts here runs to the end of the input"
    | None -> Ok(List.ofSeq out)

/// the header and the data records under it: every record must carry
/// the header's number of cells, and a header names each column once
let parse (lines: (int * string) list) : Result<string list * (int * string list) list, string> =
    match records lines with
    | Error e -> Error e
    | Ok [] -> Error "empty input — expected a header row naming the columns"
    | Ok((hline, header) :: rows) ->
        let dup =
            header
            |> List.countBy id
            |> List.tryFind (fun (_, k) -> k > 1)
            |> Option.map fst

        match header |> List.tryFindIndex (fun h -> h.Trim() = ""), dup with
        | Some i, _ ->
            Error
                $"line {hline}: the header's column {i + 1} has no name — a CSV read needs a header row naming every column"
        | _, Some d -> Error $"line {hline}: the header names '{Types.excerpt d}' twice — columns are matched by name"
        | None, None ->
            match rows |> List.tryFind (fun (_, cs) -> cs.Length <> header.Length) with
            | Some(n, cs) ->
                Error
                    $"line {n}: {cs.Length} cell(s), the header has {header.Length} — a comma or a quote inside a cell needs the cell quoted"
            | None -> Ok(header, rows)

/// one cell as written: quoted when it holds a comma, a quote, a line
/// break, or space at either end (which a reader may otherwise trim)
let cellText (s: string) : string =
    if
        s.IndexOfAny [| ','; '"'; '\n'; '\r' |] >= 0
        || (s <> ""
            && (System.Char.IsWhiteSpace s[0] || System.Char.IsWhiteSpace s[s.Length - 1]))
    then
        "\"" + s.Replace("\"", "\"\"") + "\""
    else
        s

/// one record as written
let line (cells: string list) : string =
    match cells with
    // a lone empty cell is quoted: a blank line is no record to a reader
    | [ "" ] -> "\"\""
    | _ -> cells |> List.map cellText |> String.concat ","
