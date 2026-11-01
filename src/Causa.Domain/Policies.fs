// Owner: domain. WI-R8-T13: the structural investigation transition table (STATE-TRANSITION-TABLES.md section 1),
// the default policy and actor authorization (POLICY-ENGINE.md). Pure: every refusal is a value, never an exception.
namespace Causa.Domain

[<RequireQualifiedAccess>]
module Blockers =
    let make code message : Blocker = { Code = code; Message = message }

    let illegalTransition (command: Command) (from: InvestigationLifecycle option) =
        let source =
            match from with
            | Some state -> sprintf "%A" state
            | None -> "no investigation"

        make "illegal-transition" (sprintf "%s is not a legal transition from %s." (Command.name command) source)

[<RequireQualifiedAccess>]
module Lifecycle =
    /// Epistemic records may be added while the investigation is open for work.
    let private openForWork state =
        match state with
        | Draft
        | Active
        | AwaitingEvidence
        | AwaitingVerification -> true
        | Resolved
        | Closed
        | Archived -> false

    /// The one structural transition table. Decide and Evolve (and so Replay) both use it, so a forged history that
    /// skips a transition is refused by the same rule that refuses the command.
    let transition (from: InvestigationLifecycle option) (command: Command) : Result<InvestigationLifecycle, Blocker> =
        let illegal () = Error(Blockers.illegalTransition command from)

        match from, command with
        | None, StartInvestigation -> Ok Draft
        | None, _ -> illegal ()
        | Some _, StartInvestigation -> illegal ()
        | Some(Draft | AwaitingEvidence | AwaitingVerification | Archived), ActivateInvestigation -> Ok Active
        | Some Active, MarkAwaitingEvidence -> Ok AwaitingEvidence
        | Some Active, MarkAwaitingVerification -> Ok AwaitingVerification
        | Some(Active | AwaitingVerification), ResolveInvestigation -> Ok Resolved
        | Some Resolved, CloseInvestigation -> Ok Closed
        | Some(Resolved | Closed), ReopenInvestigation -> Ok Active
        | Some Archived, ArchiveInvestigation -> illegal ()
        | Some _, ArchiveInvestigation -> Ok Archived
        | Some Archived, RenameInvestigation -> illegal ()
        | Some state, RenameInvestigation -> Ok state
        | Some state, (RecordObservation | RecordHypothesis | RecordConclusion) when openForWork state -> Ok state
        | Some _, _ -> illegal ()

/// The policies this kernel can evaluate. A policy set it does not know is UnsupportedSemantics, never a guess.
[<RequireQualifiedAccess>]
module Policies =
    let defaultPolicyId = "causa-default"
    let supported = [ defaultPolicyId ]
    let supports (policy: PolicySet) = List.contains policy.Id supported

    /// The default policy's gates over a structurally legal transition (STATE-TRANSITION-TABLES.md section 16, step 6).
    let gates (state: InvestigationState) (command: Command) : Blocker list =
        match command with
        | ActivateInvestigation when state.Lifecycle = Draft && state.Observations = 0 ->
            [ Blockers.make "activation-policy" "Record at least one observation before activating the investigation." ]
        | ResolveInvestigation when state.Conclusions = 0 ->
            [ Blockers.make "resolution-gate" "Record a conclusion before resolving the investigation." ]
        | _ -> []

    /// Actor authorization (step 7): an anonymous actor may not change an investigation.
    let authorize (actor: ActorContext) : Blocker list =
        if System.String.IsNullOrWhiteSpace actor.ActorId then
            [ Blockers.make "unauthorized" "The actor is not identified, so it is not authorized to change this investigation." ]
        else
            []
