// Owner: domain. WI-R8-T13: the semantic events and their wire kinds (COMMAND-EVENT-CATALOG.md sections 3, 4, 7, 21).
namespace Causa.Domain

open System

/// Each event is the one primary event of exactly one command, so the transition table is written once (Lifecycle).
type DomainEvent =
    | InvestigationStarted
    | InvestigationRenamed
    | InvestigationActivated
    | InvestigationMarkedAwaitingEvidence
    | InvestigationMarkedAwaitingVerification
    | InvestigationResolved
    | InvestigationClosed
    | InvestigationReopened
    | InvestigationArchived
    | ObservationRecorded
    | HypothesisRecorded
    | ConclusionRecorded

[<RequireQualifiedAccess>]
module DomainEvent =
    let ofCommand (command: Command) : DomainEvent =
        match command with
        | StartInvestigation -> InvestigationStarted
        | RenameInvestigation -> InvestigationRenamed
        | ActivateInvestigation -> InvestigationActivated
        | MarkAwaitingEvidence -> InvestigationMarkedAwaitingEvidence
        | MarkAwaitingVerification -> InvestigationMarkedAwaitingVerification
        | ResolveInvestigation -> InvestigationResolved
        | CloseInvestigation -> InvestigationClosed
        | ReopenInvestigation -> InvestigationReopened
        | ArchiveInvestigation -> InvestigationArchived
        | RecordObservation -> ObservationRecorded
        | RecordHypothesis -> HypothesisRecorded
        | RecordConclusion -> ConclusionRecorded

    let command (event: DomainEvent) : Command =
        match event with
        | InvestigationStarted -> StartInvestigation
        | InvestigationRenamed -> RenameInvestigation
        | InvestigationActivated -> ActivateInvestigation
        | InvestigationMarkedAwaitingEvidence -> MarkAwaitingEvidence
        | InvestigationMarkedAwaitingVerification -> MarkAwaitingVerification
        | InvestigationResolved -> ResolveInvestigation
        | InvestigationClosed -> CloseInvestigation
        | InvestigationReopened -> ReopenInvestigation
        | InvestigationArchived -> ArchiveInvestigation
        | ObservationRecorded -> RecordObservation
        | HypothesisRecorded -> RecordHypothesis
        | ConclusionRecorded -> RecordConclusion

    let all = Command.all |> List.map ofCommand

    /// The event kind written in EventPayload: the catalog's past-tense name.
    let kind (event: DomainEvent) =
        match event with
        | InvestigationStarted -> "InvestigationStarted"
        | InvestigationRenamed -> "InvestigationRenamed"
        | InvestigationActivated -> "InvestigationActivated"
        | InvestigationMarkedAwaitingEvidence -> "InvestigationMarkedAwaitingEvidence"
        | InvestigationMarkedAwaitingVerification -> "InvestigationMarkedAwaitingVerification"
        | InvestigationResolved -> "InvestigationResolved"
        | InvestigationClosed -> "InvestigationClosed"
        | InvestigationReopened -> "InvestigationReopened"
        | InvestigationArchived -> "InvestigationArchived"
        | ObservationRecorded -> "ObservationRecorded"
        | HypothesisRecorded -> "HypothesisRecorded"
        | ConclusionRecorded -> "ConclusionRecorded"

    let toPayload (event: DomainEvent) = EventPayload(kind event)

    /// The event a payload names; None for a kind this kernel does not know (never guessed).
    let ofPayload (EventPayload k) : DomainEvent option = all |> List.tryFind (fun e -> kind e = k)

/// Event identity derived from the command that caused it: the first 16 bytes of SHA-256 over the command id and the
/// event's index (deterministic, collision-resistant across commands). A resubmitted command therefore names events the
/// history already holds, which is how Decide sees duplicates.
[<RequireQualifiedAccess>]
module EventIds =
    let forCommand (commandId: Guid) (index: int) : EventId =
        let input = Array.append (commandId.ToByteArray()) (BitConverter.GetBytes index)
        let digest = Security.Cryptography.SHA256.HashData input
        EventId(Guid(Array.sub digest 0 16))
