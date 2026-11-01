// Owner: ui-2. WI-R8-T26: the report preview (S22) and its Folio print export (R12.1-R12.3, UI-009).
// Pure: a history the domain kernel replays (Kernel.projectHistory / Kernel.replayState) to the frozen IF41-REPORT
// ReportProjection (Reporting.fs), then to the flat keys and row lists Limen binds into Folio's print components
// (@echelon-foundry/print-components 0.4.1). The report states only what the kernel records: lifecycle, versions and
// the accepted events with their provenance. The kernel records no observation text, evidence, experiment, causal
// link, corrective action or verification, so those sections say "Not recorded" in words and no conclusion is ever
// presented as verified (UI-009-O4). An investigation that is not resolved renders as an interim report that lists its
// unresolved work (R12.3). Small functions, so no method grows large under the .NET WebAssembly interpreter (AR-029-O3).
namespace Causa.App.Views

open System
open Causa.App
open Causa.Domain

module Report =
    /// Interim until the kernel records the investigation as resolved, closed or archived (R12.3).
    type ReportKind =
        | Interim
        | Final

    /// The report's contribution to the view: flat keys, and the row lists the page repeats with data-each.
    type ReportView = { Keys: ShellKeys; Lists: (string * Graph.Row list) list }

    let private guidText (g: Guid) = g.ToString "D"

    let private versionOf (AggregateVersion v) = v

    let private eventText (EventId id) = guidText id

    let private kindOf (envelope: EventEnvelope) =
        let (EventPayload kind) = envelope.Payload
        kind

    let kindOfLifecycle (lifecycle: InvestigationLifecycle) : ReportKind =
        match lifecycle with
        | Resolved
        | Closed
        | Archived -> Final
        | Draft
        | Active
        | AwaitingEvidence
        | AwaitingVerification -> Interim

    let kindName (kind: ReportKind) =
        match kind with
        | Interim -> "Interim report"
        | Final -> "Final report"

    let private kindState (kind: ReportKind) =
        match kind with
        | Interim -> "interim"
        | Final -> "final"

    /// The canonical order of a history (ADRP-007-O2): by aggregate version.
    let private ordered (history: EventEnvelope list) =
        history |> List.sortBy (fun e -> versionOf e.Version)

    let private recordedAs (event: DomainEvent) (history: EventEnvelope list) : EventEnvelope list =
        ordered history |> List.filter (fun e -> DomainEvent.ofPayload e.Payload = Some event)

    let private plural (n: int) one many = if n = 1 then $"1 {one}" else $"{n} {many}"

    let private provenanceSentence (envelopes: EventEnvelope list) =
        envelopes
        |> List.map (fun e -> $"{kindOf e} at version {versionOf e.Version}")
        |> String.concat "; "

    let private section heading paragraphs provenance : ReportSection =
        { Heading = heading; Paragraphs = paragraphs; Provenance = provenance }

    /// The words that open every section the kernel holds no record for.
    [<Literal>]
    let NotRecorded = "Not recorded."

    /// A section the kernel holds no record for: the report says so instead of inventing content.
    let notRecorded (heading: string) (what: string) : ReportSection =
        section heading [ $"{NotRecorded} The investigation kernel does not record {what}, so this report states none." ] []

    let private entriesSection heading (noun: string) (nouns: string) (event: DomainEvent) (history: EventEnvelope list) =
        let entries = recordedAs event history
        let count = $"{plural entries.Length noun nouns} recorded."
        let detail =
            match entries with
            | [] -> []
            | _ -> [ $"Recorded as {provenanceSentence entries}. The kernel records that each was made, not its wording." ]
        section heading (count :: detail) (entries |> List.map _.EventId)

    let private sourceSection (state: InvestigationState) (history: EventEnvelope list) =
        let (InvestigationId id) = state.Id
        let kind = kindOfLifecycle state.Lifecycle
        section
            "Source investigation"
            [ $"Investigation {guidText id}, version {versionOf state.Version}, lifecycle {Graph.lifecycleName state.Lifecycle}."
              $"""{kindName kind}, projected from {plural history.Length "accepted event" "accepted events"} replayed by the domain kernel.""" ]
            []

    let private chronologySection (history: EventEnvelope list) =
        let events = ordered history
        section "Chronology" [ $"""{plural events.Length "event" "events"} in canonical version order, listed in the chronology table.""" ] (events |> List.map _.EventId)

    let private conclusionSection (history: EventEnvelope list) =
        let conclusions = recordedAs ConclusionRecorded history
        let posture =
            match conclusions with
            | [] -> "No conclusion is recorded."
            | _ -> $"""{plural conclusions.Length "conclusion" "conclusions"} recorded ({provenanceSentence conclusions}); none is verified."""
        section
            "Conclusion posture and root-cause reasoning"
            [ posture
              "The kernel records no causal link and no root-cause reasoning, so this report states no cause and no verified conclusion." ]
            (conclusions |> List.map _.EventId)

    let private classificationSection =
        section
            "Classification and redaction"
            [ "No classification is recorded. The report holds only event kinds, identifiers and versions, and no entered text, so nothing is redacted." ]
            []

    /// Pure: the report's sections in reading order (R12.2, UI-009-O1, S22).
    let sections (state: InvestigationState) (history: EventEnvelope list) : ReportSection list =
        [ sourceSection state history
          entriesSection "Observation" "observation" "observations" ObservationRecorded history
          chronologySection history
          entriesSection "Hypotheses considered" "hypothesis" "hypotheses" HypothesisRecorded history
          notRecorded "Evidence" "evidence items or their provenance"
          notRecorded "Experiments" "experiments or their results"
          notRecorded "Falsified explanations" "falsified explanations or other negative knowledge"
          conclusionSection history
          notRecorded "Corrective action" "corrective actions"
          notRecorded "Verification" "verification outcomes"
          classificationSection ]

    /// Pure: the unresolved work the report names (R12.3, UI-009-O1 "unresolved unknowns").
    let unresolved (state: InvestigationState) : string list =
        let lifecycle =
            match kindOfLifecycle state.Lifecycle with
            | Interim -> [ $"The investigation is {Graph.lifecycleName state.Lifecycle}, not resolved: this is an interim report." ]
            | Final -> []
        let conclusion =
            match state.Conclusions with
            | 0 -> [ "No conclusion is recorded." ]
            | _ -> []
        lifecycle
        @ conclusion
        @ [ "No conclusion is verified: the kernel records no verification outcome."
            "No causal link is recorded: the kernel does not represent causal relationships yet."
            "Evidence, experiments, falsified explanations and corrective actions are not recorded by the kernel." ]

    /// Pure: the IF41-REPORT projection of a kernel projection (Kernel.projectHistory). A timeline the kernel cannot
    /// replay yields a report that says so and claims nothing.
    let project (projection: InvestigationProjection) : ReportProjection =
        match Kernel.replayState projection.Timeline with
        | Ok state ->
            { Investigation = state.Id
              Version = state.Version
              Sections = sections state projection.Timeline
              UnresolvedUncertainties = unresolved state }
        | Error error ->
            { Investigation = projection.Investigation.Id
              Version = projection.Investigation.Version
              Sections = [ section "Report unavailable" [ $"The history does not replay ({error}); the report states nothing from it." ] [] ]
              UnresolvedUncertainties = [ "The investigation's history could not be replayed." ] }

    /// IF41-REPORT (Boundary E): the report projection Folio renders.
    let reporting: Reporting = { Project = project }

    // ---- The page: flat keys and row lists bound into Folio's print components. ----

    let private sectionRow (index: int) (s: ReportSection) : Graph.Row =
        let provenance =
            match s.Provenance with
            | [] -> "No event recorded."
            | ids -> "Events: " + (ids |> List.map eventText |> String.concat ", ")
        let recorded = s.Paragraphs |> List.exists (fun p -> p.StartsWith NotRecorded) |> not
        [ "key", $"section-{index + 1}"
          "heading", s.Heading
          "body", String.concat " " s.Paragraphs
          "provenance", provenance
          "recorded", (if recorded then "recorded" else "not-recorded") ]

    let private chronologyRow (e: EventEnvelope) : Graph.Row =
        [ "key", eventText e.EventId
          "version", string (versionOf e.Version)
          "kind", kindOf e
          "event", eventText e.EventId ]

    let private unresolvedRow (index: int) (text: string) : Graph.Row =
        [ "key", $"unresolved-{index + 1}"; "text", text ]

    let private emptyKeys: ShellKeys =
        [ "reportHidden", Bit true
          "reportEmptyHidden", Bit false
          "reportTitle", Str "No report yet"
          "reportKind", Str "No report"
          "reportKindState", Str "none"
          "reportInvestigation", Str ""
          "reportVersion", Str "0"
          "reportLifecycle", Str "No investigation"
          "reportSummary", Str "No report yet: start an investigation to preview its report." ]

    let private keysOf (state: InvestigationState) (report: ReportProjection) : ShellKeys =
        let (InvestigationId id) = report.Investigation
        let kind = kindOfLifecycle state.Lifecycle
        let version = versionOf report.Version
        let items = plural report.UnresolvedUncertainties.Length "unresolved item" "unresolved items"
        [ "reportHidden", Bit false
          "reportEmptyHidden", Bit true
          "reportTitle", Str $"Investigation {(guidText id).Substring(0, 8)}"
          "reportKind", Str(kindName kind)
          "reportKindState", Str(kindState kind)
          "reportInvestigation", Str(guidText id)
          "reportVersion", Str(string version)
          "reportLifecycle", Str(Graph.lifecycleName state.Lifecycle)
          "reportSummary", Str $"{kindName kind} of version {version}, with {items}." ]

    let private emptyView: ReportView =
        { Keys = emptyKeys
          Lists = [ "reportSections", []; "reportChronology", []; "reportUnresolved", [] ] }

    let private viewOf (state: InvestigationState) (projection: InvestigationProjection) : ReportView =
        let report = project projection
        { Keys = keysOf state report
          Lists =
            [ "reportSections", report.Sections |> List.mapi sectionRow
              "reportChronology", ordered projection.Timeline |> List.map chronologyRow
              "reportUnresolved", report.UnresolvedUncertainties |> List.mapi unresolvedRow ] }

    /// Pure: the report's view for one history: every key and list, every time. A history the kernel refuses has no
    /// report (the page shows the projection fault).
    let view (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) : ReportView =
        match history with
        | [] -> emptyView
        | _ ->
            match Kernel.projectHistory policy actor history, Kernel.replayState history with
            | Ok(projection, _), Ok state -> viewOf state projection
            | _ -> emptyView
