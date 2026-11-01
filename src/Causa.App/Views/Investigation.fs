// Owner: ui (WI-R8-T22). The investigation workspace views (S02; INVESTIGATION-WORKSPACE sections 3-23; R9.4, R9.5):
// header, observations, hypotheses, evidence, conclusions with their review gate, blockers and next actions, the
// timeline and an inspector of the latest authoritative change. Pure: one replay of one history (Kernel.replayState)
// and one capability derivation (Capabilities.derive) feed every region, so every region carries the same projection
// version (ADRP-004). Legality and blocker reasons are the kernel's; nothing here re-derives them (CR-T17-02 keeps the
// key names T17 used: projectionVersion, investigationLifecycle, timelineCount, timelineLatest, capability<Command>,
// outcomeState, outcomeText).
namespace Causa.App.Views

open Causa.Domain
open Causa.App

module Investigation =
    /// The one projection of a history every region reads.
    type Projection =
        { State: InvestigationState option
          Capabilities: Capability list
          Fault: string option }

    /// Pure: one replay and one capability derivation. An empty history is "no investigation yet".
    let project (policy: PolicySet) (actor: ActorContext) (history: EventEnvelope list) : Projection =
        match history with
        | [] ->
            { State = None
              Capabilities = Capabilities.derive policy actor None
              Fault = None }
        | _ ->
            match Kernel.replayState history with
            | Ok state ->
                { State = Some state
                  Capabilities = Capabilities.derive policy actor (Some state)
                  Fault = None }
            | Error _ ->
                // Fail closed (UI-010-O4) in plain words; the replay error itself is technical detail (UI-024-O5).
                { State = None
                  Capabilities = []
                  Fault = Some "This investigation's history could not be read, so none of it is shown. Nothing was changed." }

    /// The status and command words come from the one presentation-language mapping (UI-024-O1).
    let lifecycleName = PresentationLanguage.lifecycleName

    /// A command in the words a person reads (no state-machine vocabulary, R9.4).
    let commandLabel = PresentationLanguage.commandLabel

    let private kindOf (event: EventEnvelope) =
        let (EventPayload kind) = event.Payload
        kind

    let private versionNumber (AggregateVersion v) = v

    let private lifecycleOf (projection: Projection) = projection.State |> Option.map _.Lifecycle

    /// Pure: a refused decision as the sentence the page shows, in plain language (UI-024-O5); the kernel's codes
    /// stay data (PresentationLanguage.decisionCodes).
    let refusalText (command: Command) (lifecycle: InvestigationLifecycle option) (decision: Decision) =
        PresentationLanguage.refusalReason command lifecycle decision

    let private str (s: string) = Str s
    let private num (n: int64) = Str(string n)

    let private versionOf (projection: Projection) =
        projection.State |> Option.map (fun s -> versionNumber s.Version) |> Option.defaultValue 0L

    /// The entries of one kind, in history order, as "<Noun> n (version v)" (sequence preserved, section 20).
    let private entries (noun: string) (kind: string) (history: EventEnvelope list) =
        history
        |> List.filter (fun e -> kindOf e = kind)
        |> List.mapi (fun i e -> $"{noun} {i + 1} (version {versionNumber e.Version})")

    let private listKeys (prefix: string) (empty: string) (items: string list) : ShellKeys =
        [ prefix + "Count", num (int64 items.Length)
          prefix + "List", str (if items.IsEmpty then empty else String.concat "; " items)
          prefix + "EmptyHidden", Bit(not items.IsEmpty) ]

    let private availability (projection: Projection) (command: Command) =
        projection.Capabilities |> List.tryFind (fun c -> c.Command = command) |> Option.map _.Availability

    let private capabilityText (projection: Projection) (command: Command) =
        match availability projection command with
        | Some Available -> "Available"
        | Some(Unavailable blockers) ->
            "Unavailable: " + PresentationLanguage.blockerReasons command (lifecycleOf projection) blockers
        | None -> "Unavailable: the investigation could not be read."

    /// One key per command: "Available", or the kernel's reason it is not (discoverable, never hidden: section 19).
    let private capabilityKeys (projection: Projection) : ShellKeys =
        Command.all |> List.map (fun c -> "capability" + Command.name c, str (capabilityText projection c))

    /// The investigation's progress commands, in the order a person takes them (next actions, section 17).
    let private progress =
        [ StartInvestigation
          RecordObservation
          ActivateInvestigation
          Command.RecordHypothesis
          MarkAwaitingEvidence
          MarkAwaitingVerification
          RecordConclusion
          ResolveInvestigation
          CloseInvestigation ]

    let private nextKeys (projection: Projection) : ShellKeys =
        let available = progress |> List.filter (fun c -> availability projection c = Some Available)
        let resolve = availability projection ResolveInvestigation

        let reasons = PresentationLanguage.blockerReasons ResolveInvestigation (lifecycleOf projection)

        let blockers =
            match resolve with
            | Some(Unavailable bs) -> bs
            | _ -> []

        [ "nextActions",
          str (
              match available with
              | [] -> "No action is available now."
              | cs -> cs |> List.map commandLabel |> String.concat ", "
          )
          "blockerCount", num (int64 blockers.Length)
          "blockerList",
          str (
              if blockers.IsEmpty then
                  "Nothing blocks resolving this investigation."
              else
                  reasons blockers
          )
          "blockerCodes", str (PresentationLanguage.blockerCodes blockers)
          // Conclusion review (section 23): whether the investigation can be resolved, with the kernel's gate.
          "conclusionReview",
          str (
              match resolve with
              | Some Available -> "Resolve is available: the policy gates are met."
              | Some(Unavailable bs) -> $"Resolve is unavailable: {reasons bs}"
              | None -> "Resolve is unavailable: the investigation could not be read."
          ) ]

    /// The inspector (detail panel, ADRP-002-O2) of the latest authoritative change.
    let private inspectorKeys (history: EventEnvelope list) : ShellKeys =
        match List.tryLast history with
        | None ->
            [ "inspectorHidden", Bit true
              "inspectorKind", str ""
              "inspectorVersion", str ""
              "inspectorEventId", str ""
              "inspectorCause", str ""
              "lastChange", str "No change yet." ]
        | Some e ->
            let cause =
                DomainEvent.ofPayload e.Payload
                |> Option.map (DomainEvent.command >> commandLabel)
                |> Option.defaultValue "an event this version of Causa does not know"

            let (EventId id) = e.EventId

            [ "inspectorHidden", Bit false
              "inspectorKind", str (kindOf e)
              "inspectorVersion", num (versionNumber e.Version)
              "inspectorEventId", str (id.ToString "D")
              "inspectorCause", str cause
              "lastChange", str $"{kindOf e} at version {versionNumber e.Version}" ]

    /// The last answer in plain language (UI-024-O5): what was asked, in the page's words, and the reason it was
    /// refused. The kernel's codes are data only (outcomeCodes), never the visible sentence.
    let private outcomeKeys (projection: Projection) (last: SubmitOutcome option) : ShellKeys =
        let state, text, codes =
            match last with
            | None -> "none", "No command sent yet.", ""
            | Some(CommandAccepted(command, _)) -> "accepted", $"{commandLabel command}: done.", ""
            | Some(CommandRefused(command, decision)) ->
                "refused",
                $"{commandLabel command} was not done: {refusalText command (lifecycleOf projection) decision}",
                PresentationLanguage.decisionCodes decision

        [ "outcomeState", str state; "outcomeText", str text; "outcomeCodes", str codes ]

    let private stateKeys (projection: Projection) (history: EventEnvelope list) : ShellKeys =
        [ "projectionVersion", num (versionOf projection)
          "investigationLifecycle",
          str (projection.State |> Option.map (fun s -> lifecycleName s.Lifecycle) |> Option.defaultValue "No investigation")
          "timelineCount", num (int64 history.Length)
          "timelineLatest", str (history |> List.tryLast |> Option.map kindOf |> Option.defaultValue "No events")
          "timelineList",
          str (
              match history with
              | [] -> "No events yet."
              | _ -> history |> List.map (fun e -> $"{versionNumber e.Version}. {kindOf e}") |> String.concat "; "
          )
          "projectionFault", str (projection.Fault |> Option.defaultValue "")
          "projectionFaultHidden", Bit projection.Fault.IsNone ]

    /// The workspace regions (section 3): observations, hypotheses and conclusions come from the history; this
    /// kernel records no evidence items yet, so the evidence region shows its empty state, never invented items.
    let private regionKeys (history: EventEnvelope list) : ShellKeys =
        listKeys "observation" "No observation recorded yet." (entries "Observation" "ObservationRecorded" history)
        @ listKeys "hypothesis" "No hypothesis recorded yet." (entries "Hypothesis" "HypothesisRecorded" history)
        @ listKeys "conclusion" "No conclusion recorded yet." (entries "Conclusion" "ConclusionRecorded" history)
        @ listKeys "evidence" "No evidence recorded yet. Evidence items are not part of this version of Causa." []

    /// Which regions a tab shows (UI-029 secondary navigation; ADRP-002-O2 one workspace with views). Without a tab
    /// (the start screen) every region shows.
    let private visibilityKeys (tab: InvestigationTab option) : ShellKeys =
        let shows region =
            match tab, region with
            | None, _
            | Some InvestigationTab.Overview, _ -> true
            | Some InvestigationTab.Hypotheses, ("hypotheses" | "inspector") -> true
            | Some InvestigationTab.Evidence, ("evidence" | "observations" | "inspector") -> true
            | Some InvestigationTab.Timeline, ("timeline" | "inspector") -> true
            | Some InvestigationTab.CausalAnalysis, ("conclusions" | "hypotheses" | "blockers") -> true
            | Some InvestigationTab.CorrectiveActions, ("conclusions" | "blockers") -> true
            | Some _, _ -> false

        [ "observations"; "hypotheses"; "evidence"; "conclusions"; "blockers"; "timeline"; "inspector" ]
        |> List.map (fun r -> "region" + string (System.Char.ToUpperInvariant r[0]) + r.Substring 1 + "Hidden", Bit(not (shows r)))

    /// Pure: every workspace key from one projection of one history.
    let keys (policy: PolicySet) (actor: ActorContext) (tab: InvestigationTab option) (history: EventEnvelope list) (last: SubmitOutcome option) : ShellKeys =
        let projection = project policy actor history

        stateKeys projection history
        @ regionKeys history
        @ capabilityKeys projection
        @ nextKeys projection
        @ inspectorKeys history
        @ outcomeKeys projection last
        @ visibilityKeys tab
