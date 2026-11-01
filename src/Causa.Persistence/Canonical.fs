// Owner: persistence. T19: canonical JSON (RFC 8785 subset) and digests for causa-storage v1.0.0 file sets.
namespace Causa.Persistence

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography

/// The JSON values the storage files use. Integers only (the contract forbids floating point).
type Json =
    | JString of string
    | JInt of int64
    | JBool of bool
    | JNull
    | JArray of Json list
    | JObject of (string * Json) list

[<RequireQualifiedAccess>]
module Canonical =
    let private escape (s: string) =
        let sb = StringBuilder(s.Length + 2)
        sb.Append '"' |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore

        sb.Append('"').ToString()

    /// RFC 8785 form for this value set: keys sorted by UTF-16 code units, no whitespace, minimal escaping.
    let rec render (value: Json) : string =
        match value with
        | JString s -> escape s
        | JInt n -> string n
        | JBool true -> "true"
        | JBool false -> "false"
        | JNull -> "null"
        | JArray items -> "[" + (items |> List.map render |> String.concat ",") + "]"
        | JObject fields ->
            let sorted = fields |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            "{" + (sorted |> List.map (fun (k, v) -> escape k + ":" + render v) |> String.concat ",") + "}"

    let bytes (value: Json) : byte[] = Encoding.UTF8.GetBytes(render value)

    let sha256Hex (data: byte[]) : string = Convert.ToHexString(SHA256.HashData data).ToLowerInvariant()

    let rec private ofElement (e: JsonElement) : Result<Json, string> =
        match e.ValueKind with
        | JsonValueKind.String -> Ok(JString(e.GetString() |> Option.ofObj |> Option.defaultValue ""))
        | JsonValueKind.Number ->
            match e.TryGetInt64() with
            | true, n -> Ok(JInt n)
            | _ -> Error "a number that is not an integer"
        | JsonValueKind.True -> Ok(JBool true)
        | JsonValueKind.False -> Ok(JBool false)
        | JsonValueKind.Null -> Ok JNull
        | JsonValueKind.Array ->
            e.EnumerateArray()
            |> Seq.fold (fun acc item -> Result.bind (fun xs -> ofElement item |> Result.map (fun x -> x :: xs)) acc) (Ok [])
            |> Result.map (List.rev >> JArray)
        | JsonValueKind.Object ->
            e.EnumerateObject()
            |> Seq.fold (fun acc p -> Result.bind (fun xs -> ofElement p.Value |> Result.map (fun x -> (p.Name, x) :: xs)) acc) (Ok [])
            |> Result.map (List.rev >> JObject)
        | _ -> Error "an undefined JSON value"

    /// Parses UTF-8 JSON (no BOM) into a Json value.
    let parse (data: byte[]) : Result<Json, string> =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory data)
            ofElement doc.RootElement
        with :? JsonException as ex ->
            Error ex.Message

    let field (name: string) (value: Json) : Json option =
        match value with
        | JObject fields -> fields |> List.tryFind (fst >> (=) name) |> Option.map snd
        | _ -> None

    /// The paths (dotted) at which two values differ, for SnapshotDiffersFromReplay.
    let rec differences (prefix: string) (a: Json) (b: Json) : string list =
        match a, b with
        | JObject xs, JObject ys ->
            let keys = (List.map fst xs @ List.map fst ys) |> List.distinct |> List.sort
            let path k = if prefix = "" then k else prefix + "." + k

            keys
            |> List.collect (fun k ->
                match field k a, field k b with
                | Some x, Some y -> differences (path k) x y
                | _ -> [ path k ])
        | x, y when x = y -> []
        | _ -> [ (if prefix = "" then "$" else prefix) ]
