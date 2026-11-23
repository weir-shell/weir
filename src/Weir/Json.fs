/// Writing and reading JSON by hand, tersely [D:json-builder]: a builder over `Utf8JsonWriter` and a few typed getters over `JsonElement`. No serializer [D:lsp-v1].
module Weir.Json

open System.Text.Json

/// a piece of JSON, written when it is applied to a writer
type Node = Utf8JsonWriter -> unit

let inline str (s: string) : Node = fun w -> w.WriteStringValue s
let inline num (n: int) : Node = fun w -> w.WriteNumberValue n
let inline bool (b: bool) : Node = fun w -> w.WriteBooleanValue b
let nil: Node = fun w -> w.WriteNullValue()

/// an array of any nodes; a `seq` is written as it is enumerated
let inline arr (items: seq<Node>) : Node =
    fun w ->
        w.WriteStartArray()

        for i in items do
            i w

        w.WriteEndArray()

/// a field left out when it has no value (bare, a `string option` writes null)
[<Struct>]
type OrOmit = OrOmit of string option

let inline orOmit (v: string option) : OrOmit = OrOmit v

/// `jobj { "key", value; … }`: a field is a bare pair, in source order.
/// The members inline, so a block compiles to the writer calls it names
type ObjBuilder() =
    member inline _.Yield((k: string, v: Node)) : Node =
        fun w ->
            w.WritePropertyName k
            v w

    member inline _.Yield((k: string, v: string)) : Node = fun w -> w.WriteString(k, v)
    member inline _.Yield((k: string, v: int)) : Node = fun w -> w.WriteNumber(k, v)
    member inline _.Yield((k: string, v: int64)) : Node = fun w -> w.WriteNumber(k, v)
    member inline _.Yield((k: string, v: bool)) : Node = fun w -> w.WriteBoolean(k, v)

    member inline _.Yield((k: string, v: string option)) : Node =
        fun w ->
            match v with
            | Some s -> w.WriteString(k, s)
            | None -> w.WriteNull k

    member inline _.Yield((k: string, OrOmit v)) : Node =
        fun w ->
            match v with
            | Some s -> w.WriteString(k, s)
            | None -> ()

    member inline _.Yield((k: string, items: Node list)) : Node =
        fun w ->
            w.WriteStartArray k

            for i in items do
                i w

            w.WriteEndArray()

    member inline _.Yield((k: string, items: string list)) : Node =
        fun w ->
            w.WriteStartArray k

            for i in items do
                w.WriteStringValue i

            w.WriteEndArray()

    member inline _.YieldFrom([<InlineIfLambda>] fields: Node) : Node = fields
    member inline _.Zero() : Node = fun _ -> ()

    member inline _.Combine([<InlineIfLambda>] a: Node, [<InlineIfLambda>] b: Node) : Node =
        fun w ->
            a w
            b w

    member inline _.Delay([<InlineIfLambda>] f: unit -> Node) : Node = fun w -> (f ()) w

    member inline _.For(xs: seq<'a>, [<InlineIfLambda>] f: 'a -> Node) : Node =
        fun w ->
            for x in xs do
                (f x) w

    member inline _.Run([<InlineIfLambda>] body: Node) : Node =
        fun w ->
            w.WriteStartObject()
            body w
            w.WriteEndObject()

/// `jfields { … }`: fields with no braces of their own, for `yield!` into an object
type FieldsBuilder() =
    inherit ObjBuilder()
    member inline _.Run([<InlineIfLambda>] body: Node) : Node = body

let jobj = ObjBuilder()
let jfields = FieldsBuilder()

// ---- reading ---------------------------------------------------------------

let tryProp (name: string) (el: JsonElement) : JsonElement option =
    match el.TryGetProperty name with
    | true, v -> Some v
    | _ -> None

/// the property as a string, or `d` when it is absent or no string
let strOr (d: string) (name: string) (el: JsonElement) : string =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> d

/// the property as an int, or `d` when it is absent or no number
let intOr (d: int) (name: string) (el: JsonElement) : int =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
    | _ -> d

/// the property as a bool, or `d` when it is absent or no bool
let boolOr (d: bool) (name: string) (el: JsonElement) : bool =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.True -> true
    | true, v when v.ValueKind = JsonValueKind.False -> false
    | _ -> d

/// the property's string elements, or `d` when it is absent or no array
let strListOr (d: string list) (name: string) (el: JsonElement) : string list =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [
        for e in v.EnumerateArray() do
            if e.ValueKind = JsonValueKind.String then
                e.GetString()
      ]
    | _ -> d
