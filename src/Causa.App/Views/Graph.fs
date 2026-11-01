// Owner: ui-2. WI-R8-T23: the causal graph as a projection of the kernel's investigation state (UI-002, ADRP-007,
// WDL-003/004/005/009). Pure: a history the kernel replays (Kernel.replayState) to typed graph nodes and relationships,
// then to (1) a Forma portable workflow document that the existing Echelon layout engine (@echelon-foundry/forma-workflow,
// WDL-001) lays out in the browser, and (2) the flat keys and row lists Limen binds, including the textual relationship
// list (ADRP-007-O1, UI-002-O4). Causa decides graph semantics only: the document carries no coordinates, sizes or
// routes (WDL-002), and every node and relationship comes from an event the kernel accepted (no inference, ADRP-007-O4,
// UI-002-O5). Small functions, so no method grows large under the .NET WebAssembly interpreter (AR-029-O3).
namespace Causa.App.Views

open System
open System.IO
open System.Text
open System.Text.Json
open Causa.Domain

module Graph =
    /// What a node stands for. The kernel records observations, hypotheses and conclusions as events of an
    /// investigation; it represents no causal link, cause role or evidence item yet, so the graph shows none.
    type NodeRole =
        | InvestigationRoot
        | Observation
        | Hypothesis
        | Conclusion

    type GraphNode =
        { Id: string
          Role: NodeRole
          Label: string
          /// Provenance: the event that recorded it (its id and canonical version).
          Provenance: string
          Version: int64 }

    /// The only relationship the kernel's projection holds: an investigation records an entry (the event envelope
    /// names its investigation). Causal relationships join this union when the domain represents them.
    type Relationship = Records

    type GraphEdge =
        { Id: string
          Source: string
          Target: string
          Relationship: Relationship }

    type CausalGraph =
        { Title: string
          Nodes: GraphNode list
          Edges: GraphEdge list }

    /// One row of a Limen data-each list: flat string fields, "key" first.
    type Row = (string * string) list

    /// The graph's contribution to the view: flat keys, and the row lists the page repeats with data-each.
    type GraphView = { Keys: ShellKeys; Lists: (string * Row list) list }

    let empty: CausalGraph = { Title = "No investigation"; Nodes = []; Edges = [] }

    let private guidText (g: Guid) = g.ToString "D"

    let private versionOf (AggregateVersion v) = v

    let roleOf (event: DomainEvent) : NodeRole option =
        match event with
        | ObservationRecorded -> Some Observation
        | HypothesisRecorded -> Some Hypothesis
        | ConclusionRecorded -> Some Conclusion
        | InvestigationStarted
        | InvestigationRenamed
        | InvestigationActivated
        | InvestigationMarkedAwaitingEvidence
        | InvestigationMarkedAwaitingVerification
        | InvestigationResolved
        | InvestigationClosed
        | InvestigationReopened
        | InvestigationArchived -> None

    let roleName (role: NodeRole) =
        match role with
        | InvestigationRoot -> "Investigation"
        | Observation -> "Observation"
        | Hypothesis -> "Hypothesis"
        | Conclusion -> "Conclusion"

    let relationshipName (relationship: Relationship) =
        match relationship with
        | Records -> "records"

    let lifecycleName (lifecycle: InvestigationLifecycle) =
        match lifecycle with
        | Draft -> "Draft"
        | Active -> "Active"
        | AwaitingEvidence -> "Awaiting evidence"
        | AwaitingVerification -> "Awaiting verification"
        | Resolved -> "Resolved"
        | Closed -> "Closed"
        | Archived -> "Archived"

    /// The Forma workflow state for the investigation's domain lifecycle; the label always says it in words.
    let private lifecycleState (lifecycle: InvestigationLifecycle) =
        match lifecycle with
        | Draft -> "pending"
        | Active -> "active"
        | AwaitingEvidence
        | AwaitingVerification -> "waiting"
        | Resolved
        | Closed -> "complete"
        | Archived -> "none"

    let private rootNode (state: InvestigationState) : GraphNode =
        let (InvestigationId id) = state.Id
        { Id = "inv-" + guidText id
          Role = InvestigationRoot
          Label = "Investigation " + (guidText id).Substring(0, 8)
          Provenance = sprintf "Investigation %s, version %d, %s" (guidText id) (versionOf state.Version) (lifecycleName state.Lifecycle)
          Version = 0L }

    /// The recorded entries of a history, in canonical version order (ADRP-007-O2), each with its role.
    let private entries (history: EventEnvelope list) : (EventEnvelope * NodeRole) list =
        history
        |> List.sortBy (fun e -> versionOf e.Version)
        |> List.choose (fun e -> DomainEvent.ofPayload e.Payload |> Option.bind roleOf |> Option.map (fun role -> e, role))

    let private entryNode (ordinal: int) (envelope: EventEnvelope, role: NodeRole) : GraphNode =
        let (EventId id) = envelope.EventId
        let (EventPayload kind) = envelope.Payload
        { Id = "ev-" + guidText id
          Role = role
          Label = sprintf "%s %d" (roleName role) ordinal
          Provenance = sprintf "%s, event %s, version %d" kind (guidText id) (versionOf envelope.Version)
          Version = versionOf envelope.Version }

    /// Each entry with its 1-based ordinal within its role.
    let private numbered (recorded: (EventEnvelope * NodeRole) list) : GraphNode list =
        recorded
        |> List.mapFold
            (fun (counts: Map<NodeRole, int>) (envelope, role) ->
                let n = (counts |> Map.tryFind role |> Option.defaultValue 0) + 1
                entryNode n (envelope, role), Map.add role n counts)
            Map.empty
        |> fst

    let private recordsEdge (root: GraphNode) (node: GraphNode) : GraphEdge =
        { Id = "rel-" + node.Id.Substring 3
          Source = root.Id
          Target = node.Id
          Relationship = Records }

    /// Pure: the graph of a history the kernel replays. A history it refuses has no graph (the page shows the fault).
    let ofHistory (history: EventEnvelope list) : CausalGraph =
        match history with
        | [] -> empty
        | _ ->
            match Kernel.replayState history with
            | Error _ -> empty
            | Ok state ->
                let root = rootNode state
                let recorded = numbered (entries history)
                { Title = root.Label
                  Nodes = root :: recorded
                  Edges = recorded |> List.map (recordsEdge root) }

    // ---- The Forma portable workflow document (format 1.0.0). Semantics only: no layout, no coordinates. ----

    let private kindOf (role: NodeRole) =
        match role with
        | InvestigationRoot -> "start"
        | Observation -> "app.causa:observation"
        | Hypothesis -> "app.causa:hypothesis"
        | Conclusion -> "app.causa:conclusion"

    /// Shape as well as label, so a role never depends on colour alone (UI-002-O3, WDL-007).
    let private variantOf (role: NodeRole) =
        match role with
        | InvestigationRoot -> "pill"
        | Observation -> "rectangle"
        | Hypothesis -> "diamond"
        | Conclusion -> "rounded"

    let private writeStatus (w: Utf8JsonWriter) (lifecycle: InvestigationLifecycle) =
        w.WriteStartObject "status"
        w.WriteString("state", lifecycleState lifecycle)
        w.WriteString("label", lifecycleName lifecycle)
        w.WriteEndObject()

    let private writeNode (w: Utf8JsonWriter) (lifecycle: InvestigationLifecycle option) (node: GraphNode) =
        w.WriteStartObject()
        w.WriteString("id", node.Id)
        w.WriteString("kind", kindOf node.Role)
        if node.Role <> InvestigationRoot then w.WriteString("kindLabel", roleName node.Role)
        w.WriteString("label", node.Label)
        w.WriteString("description", node.Provenance)
        w.WriteString("variant", variantOf node.Role)
        match node.Role, lifecycle with
        | InvestigationRoot, Some l -> writeStatus w l
        | _ -> ()
        w.WriteEndObject()

    let private writeEndpoint (w: Utf8JsonWriter) (name: string) (node: string) =
        w.WriteStartObject name
        w.WriteString("node", node)
        w.WriteEndObject()

    let private writeEdge (w: Utf8JsonWriter) (edge: GraphEdge) =
        w.WriteStartObject()
        w.WriteString("id", edge.Id)
        writeEndpoint w "source" edge.Source
        writeEndpoint w "target" edge.Target
        w.WriteString("kind", "association")
        w.WriteString("label", relationshipName edge.Relationship)
        w.WriteEndObject()

    let private writeHeader (w: Utf8JsonWriter) (graph: CausalGraph) =
        w.WriteString("format", "forma-workflow")
        w.WriteString("formatVersion", "1.0.0")
        w.WriteString("id", "causa-causal-graph")
        w.WriteString("title", graph.Title)
        w.WriteString("description", "The investigation and the entries the domain kernel recorded, laid out by the Forma workflow engine.")
        w.WriteString("language", "en")
        w.WriteString("direction", "ltr")
        w.WriteStartObject "forma"
        w.WriteString("version", "0.4.0")
        w.WriteEndObject()

    /// Pure: the graph as a Forma portable workflow document. Identical graphs give identical text (WDL-009).
    let toWorkflow (lifecycle: InvestigationLifecycle option) (graph: CausalGraph) : string =
        use stream = new MemoryStream()
        (
            use w = new Utf8JsonWriter(stream)
            w.WriteStartObject()
            writeHeader w graph
            w.WriteStartArray "nodes"
            graph.Nodes |> List.iter (writeNode w lifecycle)
            w.WriteEndArray()
            w.WriteStartArray "edges"
            graph.Edges |> List.iter (writeEdge w)
            w.WriteEndArray()
            w.WriteEndObject()
        )
        Encoding.UTF8.GetString(stream.ToArray())

    // ---- The view: flat keys and the textual alternative. ----

    let private labelOf (graph: CausalGraph) (id: string) =
        graph.Nodes |> List.tryFind (fun n -> n.Id = id) |> Option.map _.Label |> Option.defaultValue id

    let private relationshipRow (graph: CausalGraph) (edge: GraphEdge) : Row =
        let source = labelOf graph edge.Source
        let target = labelOf graph edge.Target
        let relationship = relationshipName edge.Relationship
        [ "key", edge.Id
          "source", source
          "relationship", relationship
          "target", target
          "text", $"{source} {relationship} {target}" ]

    let private nodeRow (node: GraphNode) : Row =
        [ "key", node.Id; "label", node.Label; "role", roleName node.Role; "provenance", node.Provenance ]

    let private plural (n: int) one many = if n = 1 then $"1 {one}" else $"{n} {many}"

    let summaryText (graph: CausalGraph) =
        match graph.Nodes with
        | [] -> "No graph yet: start an investigation to see its entries."
        | nodes ->
            sprintf "%s and %s, laid out by the Forma workflow engine." (plural nodes.Length "node" "nodes") (plural graph.Edges.Length "relationship" "relationships")

    let private keysOf (lifecycle: InvestigationLifecycle option) (graph: CausalGraph) : ShellKeys =
        let hasNodes = not graph.Nodes.IsEmpty
        [ "graphWorkflow", Str(toWorkflow lifecycle graph)
          "graphSummary", Str(summaryText graph)
          "graphNodeCount", Str(string graph.Nodes.Length)
          "graphRelationshipCount", Str(string graph.Edges.Length)
          "graphFigureHidden", Bit(not hasNodes)
          "graphEmptyHidden", Bit hasNodes ]

    /// Pure: the graph's view for one history: every key and list, every time.
    let project (history: EventEnvelope list) : GraphView =
        let graph = ofHistory history
        let lifecycle =
            match history with
            | [] -> None
            | _ -> Kernel.replayState history |> Result.toOption |> Option.map _.Lifecycle
        { Keys = keysOf lifecycle graph
          Lists =
            [ "graphRelationships", graph.Edges |> List.map (relationshipRow graph)
              "graphNodes", graph.Nodes |> List.map nodeRow ] }
