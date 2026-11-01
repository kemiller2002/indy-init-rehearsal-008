// Owner: integration. The engine's wire edge (Limen protocol 1). Pure decoding, a pure step function, pure encoding;
// the one mutable cell below is the effect edge the host shim calls through.
namespace Causa.App

open System.IO
open System.Text
open System.Text.Json
open Causa.Domain

/// The Limen messages the engine answers (BrowserToEngineMessage), decoded to data.
[<RequireQualifiedAccess>]
type Incoming =
    | Initialize of path: string * hash: string
    | Event of name: string * value: string option
    | LocationChanged of path: string * hash: string
    | EffectResult
    | CapabilityFact
    | Unreadable of reason: string

/// The answer to one message (EngineToBrowserMessage). Effects are requested, never performed here.
type Outgoing = { View: ShellView; Effects: unit list; Cancellations: string list }

/// WI-R8-T17: a typed command from the page through the domain kernel, decided by Update.submit (ui, WI-R8-T22;
/// CR-T17-01): Kernel.decideOn against the tab's history, an allowed decision's events stamped and appended; a refusal
/// changes nothing but the answer the page shows.
[<RequireQualifiedAccess>]
module Submit =
    let command (workspace: Workspace) (command: Command) : Workspace =
        let envelope =
            Update.envelope (Workspace.commandId (workspace.Submissions + 1)) Workspace.investigation workspace.History

        let history, outcome = Update.submit Workspace.policy Workspace.actor workspace.History envelope command

        { History = history
          Submissions = workspace.Submissions + 1
          Last = Some outcome }

module Program =
    let private tryProp (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private tryString name e =
        tryProp name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map (fun v -> v.GetString() |> Option.ofObj |> Option.defaultValue "")

    let private pathOf (location: JsonElement option) =
        location |> Option.bind (tryString "path") |> Option.defaultValue "/"

    let private hashOf (location: JsonElement option) =
        location |> Option.bind (tryString "hash") |> Option.defaultValue ""

    /// Pure: one JSON message to Incoming. Never throws.
    let decode (json: string) : Incoming =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            match tryString "kind" root with
            | Some "Initialize" ->
                let location = tryProp "location" root
                Incoming.Initialize(pathOf location, hashOf location)
            | Some "LocationChanged" ->
                let location = tryProp "location" root
                Incoming.LocationChanged(pathOf location, hashOf location)
            | Some "Event" ->
                let ev = tryProp "event" root
                let name = ev |> Option.bind (tryString "name") |> Option.defaultValue ""
                Incoming.Event(name, ev |> Option.bind (tryString "value"))
            | Some "EffectResult" -> Incoming.EffectResult
            | Some "CapabilityFact" -> Incoming.CapabilityFact
            | Some other -> Incoming.Unreadable $"unknown message kind {other}"
            | None -> Incoming.Unreadable "message has no kind"
        with :? JsonException as ex ->
            Incoming.Unreadable ex.Message

    /// Pure: the next shell state and the answer. Unknown or unreadable input leaves the state unchanged.
    let step (state: ShellState) (message: Incoming) : ShellState * Outgoing =
        let next =
            match message with
            | Incoming.Initialize(path, hash)
            | Incoming.LocationChanged(path, hash) ->
                { state with
                    Booted = true
                    Path = path
                    Hash = if Routes.isRouteHash hash then hash else state.Hash }
            | Incoming.Event(name, _) ->
                match Workspace.commandOfEvent name with
                | Some command -> { state with Workspace = Submit.command state.Workspace command }
                | None -> state
            | Incoming.EffectResult
            | Incoming.CapabilityFact
            | Incoming.Unreadable _ -> state
        next, { View = View.render next; Effects = []; Cancellations = [] }

    let private writeValue (w: Utf8JsonWriter) (name: string) (value: ViewValue) =
        match value with
        | Text s -> w.WriteString(name, s)
        | Number n -> w.WriteNumber(name, n)
        | Flag b -> w.WriteBoolean(name, b)
        | Rows rows ->
            w.WriteStartArray name
            rows
            |> List.iter (fun fields ->
                w.WriteStartObject()
                fields |> List.iter (fun (field, text) -> w.WriteString(field, text))
                w.WriteEndObject())
            w.WriteEndArray()

    /// Pure: the answer as Limen JSON.
    let encode (answer: Outgoing) : string =
        use stream = new MemoryStream()
        (
            use w = new Utf8JsonWriter(stream)
            w.WriteStartObject()
            w.WriteStartObject "view"
            answer.View |> List.iter (fun (name, value) -> writeValue w name value)
            w.WriteEndObject()
            w.WriteStartArray "effects"
            w.WriteEndArray()
            w.WriteStartArray "cancellations"
            answer.Cancellations |> List.iter w.WriteStringValue
            w.WriteEndArray()
            w.WriteEndObject()
        )
        Encoding.UTF8.GetString(stream.ToArray())

    // The effect edge: the only mutable value in the engine's wire layer. Limen dispatches one message at a time.
    let mutable private current = View.initial

    /// Called by Causa.Host.HostShim.Dispatch for every Limen message.
    let dispatch (json: string) : string =
        let next, answer = step current (decode json)
        current <- next
        encode answer
