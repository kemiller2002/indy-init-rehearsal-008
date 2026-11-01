// Owner: integration. WI-R8-T17: the engine's wire edge as the host shim calls it (Limen JSON in, Limen JSON out).
// A typed command crosses decode -> step -> encode, the domain kernel decides it, and the answer is one complete
// ViewState from one projection; a refusal carries the kernel's reason and changes nothing else.
module Causa.Contract.Tests.App.ConsumerIntegration

open System.Text.Json
open Xunit
open Causa.Domain
open Causa.App

let private initialize = """{"kind":"Initialize","location":{"path":"/dist/index.html","hash":"#/investigations/new"}}"""
let private event (name: string) = """{"kind":"Event","event":{"name":""" + "\"" + name + "\"}}"

/// Runs messages through the pure wire edge, returning the final state and every encoded answer.
let private run (messages: string list) =
    messages
    |> List.fold
        (fun (state, answers) message ->
            let next, answer = Program.step state (Program.decode message)
            next, answers @ [ Program.encode answer ])
        (View.initial, [])

let private view (json: string) = JsonDocument.Parse(json).RootElement.GetProperty "view"
let private text (json: string) (key: string) = (view json).GetProperty(key).GetString()
/// A count or version key: since CR-T22-1 (Views/Investigation.fs) these are Limen strings holding the integer.
let private number (json: string) (key: string) = (view json).GetProperty(key).GetString() |> float

[<Fact>]
let ShimRoundTrip () =
    let state, answers =
        run [ initialize; event "submit.StartInvestigation"; event "submit.ActivateInvestigation"; event "submit.RecordObservation" ]

    let started, refused, observed = answers[1], answers[2], answers[3]

    // Allowed: one event stamped at the genesis version and evolved into the projection.
    Assert.Equal("Start investigation: done.", text started "outcomeText")
    Assert.Equal(1.0, number started "projectionVersion")
    Assert.Equal("Draft", text started "investigationLifecycle")
    Assert.Contains("Record at least one observation before activating", text started "capabilityActivateInvestigation")

    // Refused by the kernel's policy gate, with its reason; the projection is unchanged.
    Assert.Equal("refused", text refused "outcomeState")
    Assert.StartsWith("Activate was not done: Record at least one observation", text refused "outcomeText")
    Assert.Equal("activation-policy", text refused "outcomeCodes")
    Assert.Equal(1.0, number refused "projectionVersion")
    Assert.Equal(1.0, number refused "timelineCount")

    // The next legal command continues from the same history.
    Assert.Equal(2.0, number observed "projectionVersion")
    Assert.Equal("ObservationRecorded", text observed "timelineLatest")

    // The tab's history replays through the kernel to the version every view showed.
    let history = state.Workspace.History
    Assert.Equal(Ok(AggregateVersion 2L), Kernel.replay history |> Result.map _.Version)
    Assert.All(history, fun e -> Assert.Equal(Workspace.investigation, e.Investigation))

    // Every answer is a complete Limen message with the same keys (a ViewState is never a patch).
    let keys (json: string) =
        (view json).EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    Assert.All(answers, fun a -> Assert.Equal<Set<string>>(keys answers[0], keys a))

[<Fact>]
let ``unknown events and unreadable messages leave the state unchanged`` () =
    let state, _ = run [ initialize; event "submit.StartInvestigation" ]

    for message in [ event "submit.Nothing"; event "increment"; "not json"; """{"kind":"Mystery"}""" ] do
        let next, answer = Program.step state (Program.decode message)
        Assert.Equal(state, next)
        Assert.Equal(1.0, number (Program.encode answer) "projectionVersion")

[<Fact>]
let ``every offered command is a Limen event the engine decodes back to the same command`` () =
    for command in Workspace.offered do
        Assert.Equal(Some command, Workspace.commandOfEvent (Workspace.eventName command))

// WI-R8-CAPS: the handshake answer to Initialize selects Limen's schedule and files capabilities when the host offers
// exactly the identities this engine was built against, and no handshake (Limen legacy mode) for any other host.
let private offer (capabilities: LimenIdentity list) : HostOffer =
    { Major = 1
      Minor = 4
      Contract = LimenHandshake.core
      Capabilities = capabilities }

[<Fact>]
let ``the handshake selects the offered schedule and files capabilities`` () =
    let answer = LimenHandshake.answer (Some(offer [ LimenHandshake.files; LimenHandshake.schedule ]))
    Assert.Equal(Some [ LimenHandshake.schedule; LimenHandshake.files ], answer |> Option.map _.Selected)
    Assert.Equal(Some 4, answer |> Option.map _.Minor)

[<Fact>]
let ``a host with another contract, an unknown capability version or no offer gets no capability`` () =
    Assert.Equal(None, LimenHandshake.answer None)
    Assert.Equal(None, LimenHandshake.answer (Some { offer [ LimenHandshake.schedule ] with Contract = { LimenHandshake.core with Version = 2 } }))
    let changed = LimenHandshake.answer (Some(offer [ { LimenHandshake.schedule with Fingerprint = "sha256:other" } ]))
    Assert.Equal(Some [], changed |> Option.map _.Selected)

[<Fact>]
let ``Initialize with a host offer is answered with an Accepted handshake on the wire`` () =
    let message =
        """{"kind":"Initialize","protocolVersion":1,"capabilities":[],"location":{"path":"/","query":"","hash":""},"handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":[{"id":"limen.schedule","version":1,"fingerprint":"sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0"}]}}"""

    let _, answer = Program.step View.initial (Program.decode message)
    let handshake = JsonDocument.Parse(Program.encode answer).RootElement.GetProperty "handshake"
    Assert.Equal("Accepted", handshake.GetProperty("kind").GetString())
    Assert.Equal("limen.schedule", handshake.GetProperty("capabilities").[0].GetProperty("id").GetString())
    Assert.Equal(1, handshake.GetProperty("capabilities").GetArrayLength())

[<Fact>]
let ``a reconcile timer becomes a limen.schedule timeout and its Fired answer the engine's TimerFired`` () =
    let tab = { Program.initialTab with Capabilities = set [ LimenHandshake.schedule.Id ] }
    let key = ReconcileTimer(InvestigationId(System.Guid.NewGuid()))
    let next, wire = Program.toWire tab [ Timer(key, 2000); SetLeaveGuard true ]
    Assert.Equal<WireEffect list>([ WireEffect.Timeout("schedule-1", 2000) ], wire)
    Assert.Equal(Some key, Map.tryFind "schedule-1" next.Timers)

    let fired =
        """{"kind":"EffectResult","result":{"kind":"CapabilityResult","correlationId":"schedule-1","capability":"limen.schedule","version":1,"outcome":{"kind":"Completed","result":{"kind":"Fired","elapsedMs":2001}}}}"""

    Assert.Equal(Incoming.CapabilityResult("schedule-1", "limen.schedule", CapabilityAnswer.Fired), Program.decode fired)
    let after, _ = Program.stepTab System.Guid.NewGuid next (Program.decode fired)
    Assert.True(after.Timers.IsEmpty)

[<Fact>]
let ``without the capability a timer is dropped and a download says it cannot be offered`` () =
    let next, wire = Program.toWire Program.initialTab [ Timer(ReconcileTimer(InvestigationId(System.Guid.NewGuid())), 2000); Download("f.json", "{}") ]
    Assert.Empty wire
    Assert.Equal("This browser cannot download the export.", next.Notice)

// WI-R8-T32W (P0 from WI-R8-UTESTS): on a wired investigation every domain button was decided as Kernel.bareCommand
// (RecordObservation), because the frozen CommandEnvelope carries no body (DEV-R8-09). The tab now keeps the body by
// the command's identity and the engine decides it in full (AppEngine.decideAllWith / Config.CommandBody).
module private Wired =
    let investigation = InvestigationId(System.Guid "00000000-0000-4000-8000-0000000032a1")
    let tabId = System.Guid "00000000-0000-4000-8000-0000000032ab"
    let config = AppEngine.configFor tabId
    let commandId n = System.Guid(sprintf "00000000-0000-4000-8000-%012d" (32000 + n))

    let envelope n : CommandEnvelope = { CommandId = commandId n; Investigation = investigation; ExpectedVersion = None }

    let opened: Causa.Persistence.VerifiedInvestigation =
        let history = Create.firstHistory config investigation (commandId 0)

        match Kernel.replay history with
        | Ok summary ->
            { Investigation = summary
              History = history
              ReadAt = Causa.Persistence.CommitOid(System.String('c', 40))
              Bytes = { Manifest = [||]; Events = [||]; Snapshot = [||] } }
        | Error error -> failwithf "the first history should replay: %A" error

    /// A connected tab with the investigation opened (verified) in the engine and active on the page.
    let tab: Tab =
        let connected: Model =
            { Connection = Connected true
              Context =
                Some
                    { Repository = { Owner = "kemiller2002"; Name = "causa-write-test"; Branch = "main" }
                      Credential = Causa.Persistence.Credential.create "ghp_T32WWiredSessionTestToken" }
              NextRequestId = Causa.Persistence.RequestId 1L
              Network = NetworkState.Online
              Route = Route "#/"
              Loads = Map.ofList [ investigation, Causa.Persistence.Loaded opened ]
              Sessions = Map.empty }

        let session = (AppEngine.machineFor config connected).Initial opened

        { Program.initialTab with
            Engine = { connected with Sessions = Map.ofList [ investigation, session ] }
            Active = Some investigation }

    let kinds (events: EventEnvelope list) = events |> List.map (fun e -> let (EventPayload kind) = e.Payload in kind)

    /// Page events through the pure tab step, with deterministic command identities (1, 2, ...).
    let press (names: string list) =
        let counter = ref 0

        let next () =
            counter.Value <- counter.Value + 1
            commandId counter.Value

        names |> List.fold (fun t name -> fst (Program.stepTab next t (Incoming.Event(name, None)))) tab

[<Fact>]
let ``T32W a wired DomainCommand with a known body is decided as that command, not as an observation`` () =
    let after = Wired.press [ "submit.RecordObservation"; "submit.ActivateInvestigation" ]
    let session = after.Engine.Sessions[Wired.investigation]
    let held = AppEngine.unconfirmed session

    // The bodies are kept beside the frozen envelopes, by command identity.
    Assert.Equal(2, held.Length)
    Assert.Equal<Command list>([ RecordObservation; ActivateInvestigation ], held |> List.map (fun c -> after.Commands[c.CommandId]))

    // The engine's decision (the tab's config) gives the activation, at the next version, both accepted.
    let reapplied, history, state =
        AppEngine.decideAllWith (Program.configOf after).CommandBody (Program.configOf after) (Wired.opened.History, Some Wired.opened.Investigation) held

    Assert.Empty(reapplied.Refused)
    Assert.Equal<string list>([ "InvestigationStarted"; "ObservationRecorded"; "InvestigationActivated" ], Wired.kinds history)
    Assert.Equal(Some(AggregateVersion 3L), state |> Option.map (fun (s: Investigation) -> s.Version))

    // The page shows the activated investigation (one projection of the wired history).
    let view = Program.renderTab after []
    Assert.Contains(("investigationLifecycle", Text "Active"), view)

[<Fact>]
let ``T32W the save the wired tab sends commits the activation event`` () =
    let pressed = Wired.press [ "submit.RecordObservation"; "submit.ActivateInvestigation" ]
    let _, effects = Program.stepTab System.Guid.NewGuid pressed (Incoming.Event("save.Save", None))

    let bodies =
        effects
        |> List.choose (function
            | Http request -> request.Body
            | _ -> None)

    Assert.NotEmpty(bodies)

    // The commit's file contents are base64 in the request; decode every base64 run and read the event kinds.
    let decoded =
        bodies
        |> List.collect (fun body ->
            System.Text.RegularExpressions.Regex.Matches(body, "[A-Za-z0-9+/]{16,}={0,2}")
            |> Seq.choose (fun m ->
                try
                    Some(System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String m.Value))
                with _ ->
                    None)
            |> List.ofSeq)
        |> String.concat "\n"

    Assert.Contains("InvestigationActivated", decoded)
    Assert.Contains("ObservationRecorded", decoded)

[<Fact>]
let ``T32W decideAllWith a lookup equals deciding each command directly with Kernel.decideOn`` () =
    let bodies = Map.ofList [ Wired.commandId 1, RecordObservation; Wired.commandId 2, ActivateInvestigation ]
    let commands = [ Wired.envelope 1; Wired.envelope 2 ]

    let reapplied, history, state =
        AppEngine.decideAllWith (fun id -> Map.tryFind id bodies) Wired.config (Wired.opened.History, Some Wired.opened.Investigation) commands

    // The reference: each command decided in full against the history so far, its events stamped and appended.
    let direct =
        commands
        |> List.fold
            (fun (history: EventEnvelope list) (envelope: CommandEnvelope) ->
                let summary = Kernel.replay history |> Result.toOption
                let decidedOn = { envelope with ExpectedVersion = summary |> Option.map _.Version }

                match Kernel.decideOn Wired.config.Policy Wired.config.Actor history { Envelope = decidedOn; Command = bodies[envelope.CommandId] } with
                | Allowed payloads -> history @ Kernel.stamp decidedOn summary payloads
                | decision -> failwithf "the direct decision should allow %A: %A" envelope decision)
            Wired.opened.History

    Assert.Equal<CommandEnvelope list>(commands, reapplied.Accepted)
    Assert.Empty(reapplied.Refused)
    Assert.Equal<EventEnvelope list>(direct, history)
    Assert.Equal(Kernel.replay direct |> Result.toOption, state)

    // A refusal is the kernel's own: Activate before any observation is refused by the activation policy.
    let refused, unchanged, _ =
        AppEngine.decideAllWith (fun id -> Map.tryFind id bodies) Wired.config (Wired.opened.History, Some Wired.opened.Investigation) [ Wired.envelope 2 ]

    Assert.Empty(refused.Accepted)
    Assert.Single(refused.Refused) |> ignore
    Assert.Equal<EventEnvelope list>(Wired.opened.History, unchanged)

[<Fact>]
let ``T32W without a known body decideAll keeps the bare-envelope meaning`` () =
    let commands = [ Wired.envelope 1; Wired.envelope 2 ]
    let at = Wired.opened.History, Some Wired.opened.Investigation
    let bare = AppEngine.decideAll Wired.config at commands
    let noLookup = AppEngine.decideAllWith (fun _ -> None) Wired.config at commands
    let engineDefault = AppEngine.decideAllWith Wired.config.CommandBody Wired.config at commands

    Assert.Equal(bare, noLookup)
    Assert.Equal(bare, engineDefault)

    let reapplied, history, _ = bare
    Assert.Equal<CommandEnvelope list>(commands, reapplied.Accepted)
    // Kernel.bareCommand on an existing investigation is RecordObservation.
    Assert.Equal<string list>([ "InvestigationStarted"; "ObservationRecorded"; "ObservationRecorded" ], Wired.kinds history)

[<Fact>]
let ``T33-F-07 a disconnect that fails closed keeps the bodies; a confirmed disconnect drops them with the sessions`` () =
    let pressed = Wired.press [ "submit.RecordObservation"; "submit.ActivateInvestigation" ]
    let disconnect (tab: Tab) = fst (Program.stepTab System.Guid.NewGuid tab (Incoming.Event("connect.disconnect", None)))

    // Reauthenticating: SM-CONNECTION refuses DisconnectConfirmed, so the sessions keep their commands and the tab
    // keeps every body; the held commands are still decided as Activate, not as observations.
    let reauthenticating = { pressed with Engine = { pressed.Engine with Connection = Reauthenticating } }
    let failed = disconnect reauthenticating
    let held = AppEngine.unconfirmed failed.Engine.Sessions[Wired.investigation]
    Assert.Equal(2, held.Length)
    Assert.Equal<Map<System.Guid, Command>>(pressed.Commands, failed.Commands)

    let _, history, _ =
        AppEngine.decideAllWith (Program.configOf failed).CommandBody (Program.configOf failed) (Wired.opened.History, Some Wired.opened.Investigation) held

    Assert.Equal<string list>([ "InvestigationStarted"; "ObservationRecorded"; "InvestigationActivated" ], Wired.kinds history)

    // Connected: the engine drops the sessions, and with them the bodies.
    let confirmed = disconnect pressed
    Assert.Empty(confirmed.Engine.Sessions)
    Assert.Empty(confirmed.Commands)
    Assert.Equal(None, confirmed.Active)
