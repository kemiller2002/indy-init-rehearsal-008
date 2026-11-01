// Owner: domain. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
// The domain stubs of the interface contracts; the domain owner implements the full shapes.
namespace Causa.Domain

open System

/// Abbreviated from docs/FSHARP-TYPE-SHAPES.md (sections 2, 30-38) for the contract typecheck only. The domain owner
/// implements the full shapes; these stubs fix only the names the boundaries use.
type InvestigationId = InvestigationId of Guid
type EventId = EventId of Guid
type AggregateVersion = AggregateVersion of int64
type Investigation = { Id: InvestigationId; Version: AggregateVersion }
type CommandEnvelope = { CommandId: Guid; Investigation: InvestigationId; ExpectedVersion: AggregateVersion option }
type EventPayload = EventPayload of kind: string
type EventEnvelope = { EventId: EventId; Investigation: InvestigationId; Version: AggregateVersion; Payload: EventPayload }
type Blocker = { Code: string; Message: string }
type Decision =
    | Allowed of EventPayload list
    | Blocked of Blocker list
    | RequiresReview of string list
    | UnsupportedSemantics of string list
    | Conflict of expected: AggregateVersion * actual: AggregateVersion
    | Duplicate of EventId list
type PolicySet = { Id: string }
type ActorContext = { ActorId: string }
type ProjectionError = { At: AggregateVersion; Reason: string }
