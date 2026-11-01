// Owner: domain. WI-R8-T13: lifecycle vocabularies (docs/STATE-TRANSITION-TABLES.md sections 1 and 2).
namespace Causa.Domain

/// Persisted investigation lifecycle states (STATE-TRANSITION-TABLES.md section 1). Reopened is an event, not a state.
type InvestigationLifecycle =
    | Draft
    | Active
    | AwaitingEvidence
    | AwaitingVerification
    | Resolved
    | Closed
    | Archived

/// Persisted hypothesis lifecycle states (section 2). Leading is derived and Confirmed does not exist.
type HypothesisState =
    | Proposed
    | Plausible
    | Supported
    | Weakened
    | Falsified
    | Superseded

/// Hypothesis lifecycle commands (COMMAND-EVENT-CATALOG.md section 7). There is no generic SetHypothesisState.
type HypothesisCommand =
    | RecordHypothesis
    | MarkHypothesisPlausible
    | MarkHypothesisSupported
    | MarkHypothesisWeakened
    | FalsifyHypothesis
    | SupersedeHypothesis
    | ReopenHypothesis

[<RequireQualifiedAccess>]
module HypothesisLifecycle =
    /// The structural transition table of section 2: Ok with the new state, or Error with the reason it is illegal.
    let transition (current: HypothesisState option) (command: HypothesisCommand) : Result<HypothesisState, string> =
        match current, command with
        | None, RecordHypothesis -> Ok Proposed
        | Some Proposed, MarkHypothesisPlausible
        | Some Weakened, MarkHypothesisPlausible
        | Some Falsified, ReopenHypothesis -> Ok Plausible
        | Some Plausible, MarkHypothesisSupported
        | Some Weakened, MarkHypothesisSupported -> Ok Supported
        | Some Plausible, MarkHypothesisWeakened
        | Some Supported, MarkHypothesisWeakened -> Ok Weakened
        | Some(Proposed | Plausible | Supported | Weakened), FalsifyHypothesis -> Ok Falsified
        | Some(Proposed | Plausible | Supported | Weakened), SupersedeHypothesis -> Ok Superseded
        | None, _ -> Error(sprintf "%A needs an existing hypothesis" command)
        | Some state, _ -> Error(sprintf "%A is not legal from %A" command state)
