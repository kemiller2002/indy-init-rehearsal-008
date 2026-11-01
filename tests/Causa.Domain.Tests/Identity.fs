// Owner: domain. WI-R8-T13: domain unit tests for the semantic kernel (decide/evolve/replay, policy, capabilities).
// The project compiles this one file, so the kernel's table-driven tests live here (dotnet:Causa.Domain.Tests.Identity).
module Causa.Domain.Tests.Identity

open System
open Xunit
open Causa.Domain

let private investigation = InvestigationId(Guid.Parse "00000000-0000-4000-8000-0000000000d1")
let private policy: PolicySet = { Id = Policies.defaultPolicyId }
let private actor: ActorContext = { ActorId = "domain-tests" }
let private commandId (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)

let private envelopeAt (n: int) (expected: AggregateVersion option) : CommandEnvelope =
    { CommandId = commandId n
      Investigation = investigation
      ExpectedVersion = expected }

/// Decides and applies commands in order through decideOn/stamp, returning the history (refusals are not applied).
let private run (commands: Command list) : EventEnvelope list =
    commands
    |> List.indexed
    |> List.fold
        (fun history (i, command) ->
            let summary = Kernel.replay history |> Result.toOption
            let envelope = envelopeAt (100 + i) (summary |> Option.map _.Version)

            match Kernel.decideOn policy actor history { Envelope = envelope; Command = command } with
            | Allowed payloads -> history @ Kernel.stamp envelope summary payloads
            | _ -> history)
        []

let private lifecycleOf history =
    Kernel.replayState history |> Result.map _.Lifecycle |> Result.toOption

let private toActive = [ StartInvestigation; RecordObservation; ActivateInvestigation ]

/// Every lifecycle state, reached through legal commands only.
let private reach (state: InvestigationLifecycle) : Command list =
    match state with
    | Draft -> [ StartInvestigation ]
    | Active -> toActive
    | AwaitingEvidence -> toActive @ [ MarkAwaitingEvidence ]
    | AwaitingVerification -> toActive @ [ MarkAwaitingVerification ]
    | Resolved -> toActive @ [ RecordConclusion; ResolveInvestigation ]
    | Closed -> toActive @ [ RecordConclusion; ResolveInvestigation; CloseInvestigation ]
    | Archived -> toActive @ [ ArchiveInvestigation ]

let private states = [ Draft; Active; AwaitingEvidence; AwaitingVerification; Resolved; Closed; Archived ]

[<Fact>]
let ``every lifecycle state is reachable by legal commands`` () =
    states |> List.iter (fun s -> Assert.Equal(Some s, lifecycleOf (run (reach s))))

[<Fact>]
let ``state x command: decide and replay agree with the one transition table`` () =
    for state in states do
        let history = run (reach state)
        let summary = Kernel.replay history |> Result.toOption

        for command in Command.all do
            let envelope = envelopeAt 900 (summary |> Option.map _.Version)

            match Lifecycle.transition (Some state) command, Kernel.decideOn policy actor history { Envelope = envelope; Command = command } with
            | Error _, Blocked blockers -> Assert.Contains(blockers, fun b -> b.Code = "illegal-transition")
            | Error _, other -> Assert.Fail(sprintf "%A from %A: illegal but decided %A" command state other)
            | Ok next, Allowed payloads ->
                let after = history @ Kernel.stamp envelope summary payloads
                Assert.Equal(Some next, lifecycleOf after)
            | Ok _, Blocked blockers ->
                // Structurally legal but blocked: only by a policy gate, with its reason.
                Assert.All(blockers, fun b -> Assert.Contains(b.Code, [ "activation-policy"; "resolution-gate" ]))
            | Ok _, other -> Assert.Fail(sprintf "%A from %A: unexpected %A" command state other)

[<Fact>]
let ``forged histories with an illegal transition are refused by replay`` () =
    let forge kinds =
        kinds
        |> List.mapi (fun i kind ->
            { EventId = EventIds.forCommand (commandId (500 + i)) 0
              Investigation = investigation
              Version = AggregateVersion(int64 i + 1L)
              Payload = EventPayload kind })

    let refused kinds =
        match Kernel.replay (forge kinds) with
        | Error(IllegalEvent _) -> true
        | _ -> false

    Assert.True(refused [ "InvestigationStarted"; "InvestigationClosed" ], "Draft -> Closed skipped resolution")
    Assert.True(refused [ "InvestigationStarted"; "InvestigationStarted" ], "started twice")
    Assert.True(refused [ "ObservationRecorded" ], "no InvestigationStarted first")
    Assert.True(refused [ "InvestigationStarted"; "InvestigationArchived"; "ObservationRecorded" ], "record on archived")
    Assert.Equal(Error(UnknownEventKind "WorkspaceUpdated"), Kernel.replay (forge [ "InvestigationStarted"; "WorkspaceUpdated" ]))
    // Positive control.
    Assert.True(Result.isOk (Kernel.replay (forge [ "InvestigationStarted"; "ObservationRecorded"; "InvestigationActivated" ])))

[<Fact>]
let ``repeated event id and foreign investigation are refused by replay`` () =
    let history = run toActive
    let repeated = history @ [ { history[1] with Version = AggregateVersion 4L } ]
    let foreign = history @ [ { history[1] with Version = AggregateVersion 4L; EventId = EventId(Guid.NewGuid()); Investigation = InvestigationId Guid.Empty } ]
    Assert.True(Result.isError (Kernel.replay repeated))
    Assert.True(Result.isError (Kernel.replay foreign))

[<Fact>]
let ``policy gates block structurally legal transitions with reasons`` () =
    let draft = run [ StartInvestigation ]
    let summary = Kernel.replay draft |> Result.toOption
    let env = envelopeAt 700 (summary |> Option.map _.Version)

    match Kernel.decideOn policy actor draft { Envelope = env; Command = ActivateInvestigation } with
    | Blocked [ b ] -> Assert.Equal("activation-policy", b.Code)
    | other -> Assert.Fail(sprintf "activation without an observation: %A" other)

    let active = run toActive
    let summary = Kernel.replay active |> Result.toOption
    let env = envelopeAt 701 (summary |> Option.map _.Version)

    match Kernel.decideOn policy actor active { Envelope = env; Command = ResolveInvestigation } with
    | Blocked [ b ] -> Assert.Equal("resolution-gate", b.Code)
    | other -> Assert.Fail(sprintf "resolution without a conclusion: %A" other)

[<Fact>]
let ``negatives: stale version, duplicate submission, unauthorized, unsupported policy, wrong investigation`` () =
    let history = run toActive // versions 1..3, commands 100..102
    let current = AggregateVersion 3L
    let submit envelope command = Kernel.decideOn policy actor history { Envelope = envelope; Command = command }

    Assert.Equal(Conflict(AggregateVersion 2L, current), submit (envelopeAt 800 (Some(AggregateVersion 2L))) RecordObservation)
    Assert.Equal(Duplicate [ EventIds.forCommand (commandId 101) 0 ], submit (envelopeAt 101 (Some(AggregateVersion 1L))) RecordObservation)

    match Kernel.decideOn policy { ActorId = " " } history { Envelope = envelopeAt 801 (Some current); Command = RecordObservation } with
    | Blocked [ b ] -> Assert.Equal("unauthorized", b.Code)
    | other -> Assert.Fail(sprintf "anonymous actor: %A" other)

    Assert.Equal(UnsupportedSemantics [ "unknown-pack" ], Kernel.decideOn { Id = "unknown-pack" } actor history { Envelope = envelopeAt 802 (Some current); Command = RecordObservation })

    match submit { envelopeAt 803 (Some current) with Investigation = InvestigationId Guid.Empty } RecordObservation with
    | Blocked [ b ] -> Assert.Equal("investigation-mismatch", b.Code)
    | other -> Assert.Fail(sprintf "foreign investigation: %A" other)

    match submit (envelopeAt 804 None) RecordObservation with
    | Blocked [ b ] -> Assert.Equal("expected-version-required", b.Code)
    | other -> Assert.Fail(sprintf "missing expected version: %A" other)

[<Fact>]
let ``capabilities are derived with reasons for blocked actions`` () =
    let draft = Kernel.replayState (run [ StartInvestigation ]) |> Result.toOption
    let caps = Capabilities.derive policy actor draft
    let find c = caps |> List.find (fun cap -> cap.Command = c)
    Assert.Equal(Available, (find RecordObservation).Availability)

    match (find ActivateInvestigation).Availability with
    | Unavailable [ b ] -> Assert.Equal("activation-policy", b.Code)
    | other -> Assert.Fail(sprintf "%A" other)

    match (find CloseInvestigation).Availability with
    | Unavailable [ b ] -> Assert.Equal("illegal-transition", b.Code)
    | other -> Assert.Fail(sprintf "%A" other)

    Assert.All(Capabilities.derive policy { ActorId = "" } draft, fun c -> Assert.NotEqual(Available, c.Availability))

[<Fact>]
let ``frozen kernel: a bare envelope is decided end to end and replays`` () =
    let k = Kernel.domain
    let first = envelopeAt 1 None

    match k.Decide policy actor None first with
    | Allowed payloads ->
        let events = Kernel.stamp first None payloads
        Assert.Equal<EventPayload list>([ EventPayload "InvestigationStarted" ], payloads)
        let evolved = events |> List.fold (fun s e -> s |> Result.bind (fun st -> k.Evolve st e |> Result.map Some)) (Ok None)
        Assert.Equal(Ok(Some { Id = investigation; Version = AggregateVersion 1L }), evolved)
        Assert.Equal(Ok { Id = investigation; Version = AggregateVersion 1L }, k.Replay events)
        let p = k.Project { Id = investigation; Version = AggregateVersion 1L }
        Assert.Equal<string list>([ "RecordObservation" ], p.Capabilities)
    | other -> Assert.Fail(sprintf "start refused: %A" other)

[<Fact>]
let ``hypothesis transition table`` () =
    let all = [ RecordHypothesis; MarkHypothesisPlausible; MarkHypothesisSupported; MarkHypothesisWeakened; FalsifyHypothesis; SupersedeHypothesis; ReopenHypothesis ]
    let legal =
        set
            [ None, RecordHypothesis, Proposed
              Some Proposed, MarkHypothesisPlausible, Plausible
              Some Plausible, MarkHypothesisSupported, Supported
              Some Plausible, MarkHypothesisWeakened, Weakened
              Some Supported, MarkHypothesisWeakened, Weakened
              Some Falsified, ReopenHypothesis, Plausible
              Some Weakened, MarkHypothesisPlausible, Plausible
              Some Weakened, MarkHypothesisSupported, Supported ]
        + set [ for s in [ Proposed; Plausible; Supported; Weakened ] do
                    yield Some s, FalsifyHypothesis, Falsified
                    yield Some s, SupersedeHypothesis, Superseded ]

    for from in None :: ([ Proposed; Plausible; Supported; Weakened; Falsified; Superseded ] |> List.map Some) do
        for command in all do
            let expected = legal |> Set.toList |> List.tryPick (fun (f, c, t) -> if f = from && c = command then Some t else None)

            match expected, HypothesisLifecycle.transition from command with
            | Some t, Ok actual -> Assert.Equal(t, actual)
            | None, Error _ -> ()
            | e, actual -> Assert.Fail(sprintf "%A %A: expected %A, got %A" from command e actual)
