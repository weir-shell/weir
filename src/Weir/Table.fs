module Weir.Table

// the aligned-table boundary's text model [D:from-table]: kubectl/docker-
// style output — one header row, aligned data rows — sliced by header
// offsets, never whitespace runs, so a cell value with spaces ("Up 2
// hours", a free-text message column) survives intact. Both tools pad
// columns with 3+ spaces (Go's text/tabwriter, padding 3) while a
// two-word header ("CONTAINER ID", "NOMINATED NODE") keeps its single
// space — which is why a header boundary is a run of 2+ spaces and a
// single space stays inside one header. This file owns the text shapes
// only (columns, cells, match keys); the typed read lives in Eval and
// the drafting arm in Infer — the json/yaml layering.

open System
open Weir.Types

/// one column: the header's raw text and its start offset (0-based, in
/// code points [D:str-code-points] — Go's tabwriter pads by rune count)
type Column = { Header: string; Start: int }

/// split a header line into columns [D:from-table]: a column boundary is
/// a run of 2+ spaces; a single interior space rides inside one header
let columns (header: string) : Column list =
    let cols = ResizeArray<Column>()
    let mutable i = 0
    // the last column's start in both units: each start converts only the
    // text since the previous one, so the header costs one pass
    let mutable prevUtf16 = 0
    let mutable prevCp = 0

    while i < header.Length do
        if header[i] = ' ' then
            i <- i + 1
        else
            let start = i
            let mutable fin = false

            while not fin do
                if i >= header.Length then
                    fin <- true
                elif header[i] <> ' ' then
                    i <- i + 1
                elif i + 1 < header.Length && header[i + 1] <> ' ' then
                    // a single space inside the header token — ride on
                    i <- i + 1
                else
                    fin <- true

            prevCp <- prevCp + CodePoints.length (header.Substring(prevUtf16, start - prevUtf16))
            prevUtf16 <- start

            cols.Add
                { Header = header.Substring(start, i - start)
                  Start = prevCp }

    List.ofSeq cols

/// slice one data line by the header's columns [D:from-table]: column i
/// spans [start_i, start_{i+1}), the last column runs to end of line; a
/// line shorter than a column's start yields the empty cell. Cells trim.
let cells (cols: Column list) (line: string) : string list =
    let arr = List.toArray cols
    // column starts increase, so one resumed walk converts them all —
    // linear in the line, not one scan per column
    let offs = Array.zeroCreate arr.Length
    let mutable u = 0
    let mutable c = 0

    for i in 0 .. arr.Length - 1 do
        u <- CodePoints.offsetFrom line u c arr[i].Start
        c <- arr[i].Start
        offs[i] <- u

    arr
    |> Array.mapi (fun i _ ->
        let e = if i + 1 < arr.Length then offs[i + 1] else line.Length
        line.Substring(offs[i], max 0 (e - offs[i])).Trim())
    |> Array.toList

/// the read-side match key: keep the alphanumerics, lowercased — header
/// `POD-TEMPLATE-HASH` and field `podTemplateHash` both key
/// `podtemplatehash`, so matching is case-insensitive on the normalized
/// form (a declared [<Wire "…">] matches the raw header verbatim instead)
let matchKey (s: string) : string =
    String(
        s
        |> Seq.filter Char.IsLetterOrDigit
        |> Seq.map Char.ToLowerInvariant
        |> Seq.toArray
    )

/// the absent-cell idiom [D:from-table]: an empty cell, or kubectl's
/// `<none>` — under Option<T> both read as None
let isAbsent (cell: string) : bool = cell = "" || cell = "<none>"

/// az `-o table` (and other tabulate-style tools) draw a rule line of
/// dashes under the header — `--------  ----------  -----` [D:from-table-az].
/// A row that is only dashes and spaces (with at least one dash) is that
/// separator, never data: every cell would be dashes, which no real row is.
let isSeparatorRow (line: string) : bool =
    line.Trim() <> ""
    && line |> Seq.forall (fun c -> c = '-' || c = ' ')
    && line |> Seq.exists ((=) '-')

/// parse numbered input lines into the header's columns and the sliced
/// data rows (1-based line numbers ride each row; blank lines skip).
/// Errors are bare text — the caller prefixes its own name
/// ("from table: …" / "#infer: …").
let parse (lines: (int * string) list) : Result<Column list * (int * string list) list, string> =
    match lines |> List.filter (fun (_, l) -> l.Trim() <> "") with
    | [] -> Error "empty input — expected a header row (and aligned data rows under it)"
    | (hn, header) :: rows ->
        let cols = columns header

        // az `-o table` puts a dashes separator row between the header and
        // the data [D:from-table-az]; drop it when it is the first data row.
        // kubectl/docker have none, so a real first data row is untouched —
        // the skip is conditional on that row actually being a separator.
        let rows =
            match rows with
            | (_, sep) :: rest when isSeparatorRow sep -> rest
            | _ -> rows

        let dup =
            cols
            |> List.groupBy (fun c -> matchKey c.Header)
            |> List.tryPick (fun (_, g) ->
                match g with
                | a :: b :: _ -> Some(a, b)
                | _ -> None)

        match dup with
        | Some(a, b) ->
            Error $"line {hn}: columns '{a.Header}' and '{b.Header}' match the same name — headers must stay distinct"
        | None -> Ok(cols, rows |> List.map (fun (n, l) -> n, cells cols l))
