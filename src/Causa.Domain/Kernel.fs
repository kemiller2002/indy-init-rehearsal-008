// Owner: domain. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Domain

/// IF41-DOMAIN: the F# kernel (Boundaries A-C). Pure: no network, clock, randomness, GitHub or browser types.
type ReplayError =
    | EmptyHistory
    | NonContiguousVersion of expected: AggregateVersion * found: AggregateVersion
    | IllegalEvent of ProjectionError
    | UnknownEventKind of string

type InvestigationProjection =
    { Investigation: Investigation
      Timeline: EventEnvelope list
      Capabilities: string list }

type DomainKernel =
    { Decide: PolicySet -> ActorContext -> Investigation option -> CommandEnvelope -> Decision
      Evolve: Investigation option -> EventEnvelope -> Result<Investigation, ProjectionError>
      Replay: EventEnvelope list -> Result<Investigation, ReplayError>
      Project: Investigation -> InvestigationProjection }

/// Replay preconditions (Boundary C), owned by domain and pure. Not part of the frozen IF41-DOMAIN shapes above:
/// the kernel's Replay (T13) composes this before folding Evolve, so a history with a gap, a duplicate or a
/// reordered version is refused with a typed ReplayError and is never folded.
[<RequireQualifiedAccess>]
module ReplayOrder =
    let private successor (AggregateVersion v) = AggregateVersion(v + 1L)

    /// Ok with the history unchanged when its versions run genesis, genesis+1, ... without a gap or repeat;
    /// EmptyHistory for no events; otherwise NonContiguousVersion naming the first version that breaks the run.
    let contiguousFrom (genesis: AggregateVersion) (history: EventEnvelope list) : Result<EventEnvelope list, ReplayError> =
        let rec check expected remaining =
            match remaining with
            | [] -> Ok history
            | (event: EventEnvelope) :: rest when event.Version = expected -> check (successor expected) rest
            | event :: _ -> Error(NonContiguousVersion(expected, event.Version))

        match history with
        | [] -> Error EmptyHistory
        | _ -> check genesis history
