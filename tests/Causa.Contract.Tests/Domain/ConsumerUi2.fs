// Owner: ui-2. WI-R8-T23: IF41-DOMAIN as the causal graph consumes it (Views/Graph.fs). The graph is a projection of
// the kernel's replayed history: nodes and relationships only from accepted events (ADRP-007-O1, UI-002-O5), stable
// identities from event ids, canonical version order, deterministic output (UI-002-O4, WDL-009), and a Forma workflow
// document that authors no geometry (WDL-002, WDL-005).
namespace Causa.Contract.Tests.Domain

open System
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.App.Views

module ConsumerUi2 =
    let private policy: PolicySet = { Id = Policies.defaultPolicyId }
    let private actor: ActorContext = { ActorId = "graph-consumer" }
    let private investigation = InvestigationId(Guid.Parse "00000000-0000-4000-8000-0000000000a1")

    /// A history the kernel itself builds: each command decided against the history so far, its events stamped.
    let private historyOf (commands: Command list) : EventEnvelope list =
        commands
        |> List.indexed
        |> List.fold
            (fun history (i, command) ->
                let summary = Kernel.replay history |> Result.toOption
                let envelope =
                    { CommandId = Guid.Parse("00000000-0000-4000-8000-" + (i + 1).ToString("D12"))
                      Investigation = investigation
                      ExpectedVersion = summary |> Option.map _.Version }
                match Kernel.decideOn policy actor history { Envelope = envelope; Command = command } with
                | Allowed payloads -> history @ Kernel.stamp envelope summary payloads
                | other -> failwithf "%A refused: %A" command other)
            []

    let private populated =
        historyOf [ StartInvestigation; RecordObservation; Command.RecordHypothesis; ActivateInvestigation; RecordObservation; RecordConclusion ]

    [<Fact>]
    let EmptyHistoryHasNoGraph () =
        let view = Graph.project []
        Assert.Empty((Graph.ofHistory []).Nodes)
        Assert.Contains(("graphFigureHidden", Bit true), view.Keys)
        Assert.Contains(("graphEmptyHidden", Bit false), view.Keys)
        Assert.All(view.Lists, fun (_, rows) -> Assert.Empty rows)

    [<Fact>]
    let NodesAreTheInvestigationAndItsRecordedEntriesInVersionOrder () =
        let graph = Graph.ofHistory populated
        let labels = graph.Nodes |> List.map _.Label
        Assert.Equal<string list>([ "Investigation 00000000"; "Observation 1"; "Hypothesis 1"; "Observation 2"; "Conclusion 1" ], labels)
        // Lifecycle events are not entries; every entry node is one accepted recording event, by its id.
        let recorded =
            populated
            |> List.filter (fun e -> DomainEvent.ofPayload e.Payload |> Option.bind Graph.roleOf |> Option.isSome)
            |> List.map (fun e -> let (EventId g) = e.EventId in "ev-" + g.ToString "D")
        Assert.Equal<string list>(recorded, graph.Nodes |> List.tail |> List.map _.Id)

    [<Fact>]
    let RelationshipsComeOnlyFromTheProjection () =
        let graph = Graph.ofHistory populated
        // One "records" relationship per recorded entry, from the investigation; no inferred causal edge.
        Assert.Equal(graph.Nodes.Length - 1, graph.Edges.Length)
        Assert.All(graph.Edges, fun e -> Assert.Equal((List.head graph.Nodes).Id, e.Source))
        Assert.All(graph.Edges, fun e -> Assert.Equal(Graph.Records, e.Relationship))
        Assert.Equal<string list>(graph.Nodes |> List.tail |> List.map _.Id, graph.Edges |> List.map _.Target)

    [<Fact>]
    let AHistoryTheKernelRefusesHasNoGraph () =
        // A gap in versions: the kernel's replay refuses it, so the graph shows nothing rather than guessing.
        let broken = populated |> List.mapi (fun i e -> if i = 3 then { e with Version = AggregateVersion 99L } else e)
        Assert.True(Result.isError (Kernel.replayState broken))
        Assert.Empty((Graph.ofHistory broken).Nodes)

    [<Fact>]
    let TheWorkflowDocumentIsDeterministicAndAuthorsNoGeometry () =
        let view = Graph.project populated
        let workflowOf (v: Graph.GraphView) = v.Keys |> List.pick (function | "graphWorkflow", Str s -> Some s | _ -> None)
        let text = workflowOf view
        Assert.Equal(text, workflowOf (Graph.project populated))
        Assert.Equal(view, Graph.project (historyOf [ StartInvestigation; RecordObservation; Command.RecordHypothesis; ActivateInvestigation; RecordObservation; RecordConclusion ]))
        use doc = JsonDocument.Parse text
        let root = doc.RootElement
        Assert.Equal("forma-workflow", root.GetProperty("format").GetString())
        Assert.False(fst (root.TryGetProperty "layout"))
        for node in root.GetProperty("nodes").EnumerateArray() do
            Assert.False(fst (node.TryGetProperty "layout"), "a node authors no layout")
        Assert.Equal(5, root.GetProperty("nodes").GetArrayLength())
        Assert.Equal(4, root.GetProperty("edges").GetArrayLength())
        // The investigation's status is its domain lifecycle, in words as well as state.
        let status = root.GetProperty("nodes").[0].GetProperty "status"
        Assert.Equal("Active", status.GetProperty("label").GetString())

    [<Fact>]
    let TheTextualAlternativeListsEveryRelationshipAndEntry () =
        let view = Graph.project populated
        let rows name = view.Lists |> List.find (fun (n, _) -> n = name) |> snd
        let texts = rows "graphRelationships" |> List.map (List.find (fun (k, _) -> k = "text") >> snd)
        Assert.Equal<string list>(
            [ "Investigation 00000000 records Observation 1"
              "Investigation 00000000 records Hypothesis 1"
              "Investigation 00000000 records Observation 2"
              "Investigation 00000000 records Conclusion 1" ],
            texts)
        Assert.Equal(5, (rows "graphNodes").Length)
        Assert.All(rows "graphNodes", fun row -> Assert.Equal("key", fst (List.head row)))

    // ---- WI-R8-T28: history, time travel and semantic diff (S14, S15; DEC-R7-005) over IF41-DOMAIN. ----
    // Time travel is a replay of a history prefix (Kernel.replayState); the diff compares two replays in the kernel's
    // terms; nothing changes the live history, and nothing is inferred (ADRP-007-O2, ADRP-007-O4).

    let private versionsOf (history: EventEnvelope list) = history |> List.map (fun e -> let (AggregateVersion v) = e.Version in v)

    [<Fact>]
    let HistoryTimelineIsInCanonicalVersionOrder () =
        // The timeline follows canonical version order whatever order the list holds (never wall-clock).
        let entries = History.timeline (List.rev populated)
        Assert.Equal<int64 list>([ 1L .. 6L ], entries |> List.map _.Version)
        Assert.Equal<string list>(
            [ "Investigation started"; "Observation recorded"; "Hypothesis recorded"; "Investigation activated"; "Observation recorded"; "Conclusion recorded" ],
            entries |> List.map _.Summary)
        Assert.Equal<string list>(populated |> List.map (fun e -> let (EventId g) = e.EventId in g.ToString "D"), entries |> List.map _.EventId)

    [<Fact>]
    let HistoryStateAtVersionReplaysThePrefixOnly () =
        let before = populated
        let at3 = History.stateAt policy actor 3L populated
        let expected = Kernel.replayState (List.truncate 3 populated) |> Result.toOption
        Assert.Equal(expected, at3.State)
        Assert.Equal(3L, at3.Version)
        Assert.Equal(Some Draft, at3.State |> Option.map _.Lifecycle)
        Assert.Equal(Some 1, at3.State |> Option.map _.Observations)
        Assert.Equal<Capability list>(Capabilities.derive policy actor expected, at3.Capabilities)
        // The head is the live state; the live history is unchanged by travelling.
        Assert.Equal(Kernel.replayState populated |> Result.toOption, (History.stateAt policy actor 6L populated).State)
        Assert.Equal<EventEnvelope list>(before, populated)
        Assert.Equal<int64 list>([ 1L .. 6L ], versionsOf populated)
        // A refused history replays to a fault, never a guessed state.
        let broken = populated |> List.mapi (fun i e -> if i = 1 then { e with Version = AggregateVersion 2L; Investigation = InvestigationId(Guid.Empty) } else e)
        let faulted = History.stateAt policy actor 3L broken
        Assert.True(faulted.State.IsNone)
        Assert.True(faulted.Fault.IsSome)

    [<Fact>]
    let HistorySemanticDiffIsInTheKernelsTerms () =
        let d = History.diff policy actor populated 3L 6L
        Assert.Equal((3L, 6L), (d.From, d.To))
        Assert.Equal(None, d.Fault)
        Assert.Contains(History.LifecycleChange(Draft, Active), d.Changes)
        let added = d.Changes |> List.choose (function | History.EntryAdded(role, n, kind, v) -> Some(role, n, kind, v) | _ -> None)
        Assert.Equal<(Graph.NodeRole * int * string * int64) list>(
            [ Graph.Observation, 2, "ObservationRecorded", 5L; Graph.Conclusion, 1, "ConclusionRecorded", 6L ], added)
        // Capability changes are exactly the commands whose derived availability (available, or the blocker codes)
        // differs between the two replays.
        let derive n = Capabilities.derive policy actor (Kernel.replayState (List.truncate n populated) |> Result.toOption)
        let codes (c: Capability) =
            match c.Availability with
            | Available -> None
            | Unavailable blockers -> Some(blockers |> List.map _.Code)
        let changed =
            List.zip (derive 3) (derive 6)
            |> List.filter (fun (a, b) -> codes a <> codes b)
            |> List.map (fun (a, _) -> a.Command)
        let reported = d.Changes |> List.choose (function | History.CapabilityChange(c, _, _) -> Some c | _ -> None)
        Assert.Equal<Command list>(changed, reported)
        Assert.All(d.Changes, fun c -> Assert.False(String.IsNullOrWhiteSpace(History.changeText c)))
        // The order of the two versions does not matter; a version compared with itself has no change.
        Assert.Equal(d, History.diff policy actor populated 6L 3L)
        Assert.Empty((History.diff policy actor populated 4L 4L).Changes)

    [<Fact>]
    let HistoryDiffNeverInventsAChange () =
        // Every addition is one accepted recording event between the two versions; lifecycle-only spans add nothing.
        for a in 1L .. 6L do
            for b in a .. 6L do
                let d = History.diff policy actor populated a b
                let recorded =
                    populated
                    |> List.filter (fun e -> let (AggregateVersion v) = e.Version in v > a && v <= b)
                    |> List.filter (fun e -> DomainEvent.ofPayload e.Payload |> Option.bind Graph.roleOf |> Option.isSome)
                let added = d.Changes |> List.filter (function | History.EntryAdded _ -> true | _ -> false)
                Assert.Equal(recorded.Length, added.Length)
        Assert.DoesNotContain(History.diff policy actor populated 1L 3L |> _.Changes, fun c -> History.changeCategory c = "lifecycle")

    [<Fact>]
    let HistorySelectionIsClampedAndNeverGuessed () =
        let sel = History.select populated
        Assert.Equal(History.At 3L, sel History.Latest History.SelectEvent (Some "3"))
        Assert.Equal(History.Latest, sel (History.At 2L) History.SelectEvent (Some "6"))
        Assert.Equal(History.Latest, sel (History.At 2L) History.SelectEvent (Some "60"))
        Assert.Equal(History.At 2L, sel (History.At 2L) History.SelectEvent (Some "zero"))
        Assert.Equal(History.At 2L, sel (History.At 2L) History.SelectEvent (Some "0"))
        Assert.Equal(History.At 2L, sel (History.At 2L) History.SelectEvent None)
        Assert.Equal(History.Latest, sel (History.At 2L) History.LatestEvent None)
        Assert.Equal(History.At 2L, sel (History.At 2L) "submit.StartInvestigation" (Some "1"))

    [<Fact>]
    let HistoryProjectionShowsHistoricalModeReadOnly () =
        let key (v: History.HistoryView) name = v.Keys |> List.pick (fun (n, value) -> if n = name then Some value else None)
        let rows (v: History.HistoryView) name = v.Lists |> List.find (fun (n, _) -> n = name) |> snd
        let latest = History.project policy actor populated History.Latest
        Assert.Equal(Str "latest", key latest "historyMode")
        Assert.Equal(Bit true, key latest "historyReadOnlyHidden")
        Assert.Equal(Str "6", key latest "historyAtVersion")
        Assert.Empty(rows latest "historyDiff")
        let past = History.project policy actor populated (History.At 3L)
        Assert.Equal(Str "historical", key past "historyMode")
        Assert.Equal(Bit false, key past "historyReadOnlyHidden")
        Assert.Equal(Str "Viewing version 3 of 6. Historical view: read-only; the investigation is unchanged.", key past "historyReadOnlyText")
        Assert.Equal(Str "Draft", key past "historyStateLifecycle")
        Assert.Equal(Str(sprintf "From version 3 to version 6: %d changes." (History.diff policy actor populated 3L 6L).Changes.Length), key past "historyDiffSummary")
        let current = rows past "historyTimeline" |> List.filter (List.contains ("current", "true"))
        Assert.Equal(1, current.Length)
        Assert.Contains(("version", "3"), List.head current)
        Assert.Equal(Str(History.policyText policy), key past "historyPolicyText")
        // Deterministic, and every key every time.
        Assert.Equal(past, History.project policy actor populated (History.At 3L))
        Assert.Equal<string list>(latest.Keys |> List.map fst, past.Keys |> List.map fst)
        let empty = History.project policy actor [] History.Latest
        Assert.Equal(Bit true, key empty "historyBodyHidden")
        Assert.Equal(Bit false, key empty "historyEmptyHidden")
        Assert.Equal<string list>(latest.Keys |> List.map fst, empty.Keys |> List.map fst)

    // A frozen causa-storage v1 history (CONTRACT.md §5), built here: start, two evidence items, a supersession,
    // activation and a verification assessment.
    let private v1Investigation = InvestigationId(Guid.Parse "00000000-0000-4000-8000-0000000000b1")

    let private v1Event (n: int) (eventType: string) (payload: (string * V1Value) list) : V1Event =
        { EventId = EventId(Guid.Parse("00000000-0000-4000-8001-" + n.ToString("D12")))
          Investigation = v1Investigation
          Version = AggregateVersion(int64 n)
          EventType = eventType
          OccurredAt = sprintf "2026-11-01T18:00:0%dZ" n
          Actor = VObject [ "kind", VText "human"; "id", VText "tester" ]
          CorrelationId = Guid.Parse("00000000-0000-4000-8002-" + n.ToString("D12"))
          Payload = VObject payload }

    let private e1, e2 = "00000000-0000-4000-8003-000000000001", "00000000-0000-4000-8003-000000000002"

    let private evidence id =
        [ "evidenceId", VText id; "contentKind", VText "text"; "content", VText "log excerpt"; "category", VText "log"
          "source", VText "tester"; "capturedAt", VText "2026-11-01T17:59:00Z" ]

    let private v1History =
        [ v1Event 1 "InvestigationStarted" [ "title", VText "Outage"; "observationId", VText "00000000-0000-4000-8003-000000000000"; "original", VText "It broke" ]
          v1Event 2 "EvidenceAdmitted" (evidence e1)
          v1Event 3 "EvidenceAdmitted" (evidence e2)
          v1Event 4 "EvidenceSuperseded" [ "evidenceId", VText e1; "replacementEvidenceId", VText e2 ]
          v1Event 5 "LifecycleChanged" [ "command", VText "activateInvestigation"; "from", VText "draft"; "to", VText "active" ]
          v1Event 6 "EvidenceVerificationAssessed" [ "evidenceId", VText e2; "verification", VText "verified" ] ]

    [<Fact>]
    let HistoryV1DiffReportsAdditionsSupersessionAndLifecycle () =
        Assert.True(Result.isOk (StorageV1Replay.replay v1History), sprintf "%A" (StorageV1Replay.replay v1History))
        match History.diffV1 v1History 1L 3L, History.diffV1 v1History 3L 6L with
        | Ok early, Ok late ->
            Assert.Equal<History.V1Change list>([ History.V1EntityAdded("evidence", e1); History.V1EntityAdded("evidence", e2) ], early)
            Assert.Equal<History.V1Change list>(
                [ History.V1LifecycleChange("draft", "active")
                  History.V1Superseded("evidence", e1, Some e2)
                  History.V1FieldChange("evidence", e2, "verification", "unverified", "verified") ],
                late)
            Assert.Equal(sprintf "Removed by supersession: evidence %s, superseded by %s." e1 e2, History.v1ChangeText late.[1])
        | other -> failwithf "the v1 history should replay at every prefix: %A" other
        Assert.Equal(History.diffV1 v1History 3L 6L, History.diffV1 v1History 6L 3L)
        // A refused prefix is the kernel's refusal, never a guessed diff.
        let forged = v1History |> List.map (fun e -> if e.Version = AggregateVersion 4L then { e with EventType = "Invented" } else e)
        Assert.True(Result.isError (History.diffV1 forged 1L 6L))
