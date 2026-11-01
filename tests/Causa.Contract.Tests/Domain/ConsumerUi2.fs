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
