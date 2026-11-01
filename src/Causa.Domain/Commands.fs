// Owner: domain. WI-R8-T13: investigation commands (COMMAND-EVENT-CATALOG.md sections 3, 4, 7, 21).
namespace Causa.Domain

/// The investigation-level commands the kernel decides. The frozen CommandEnvelope (IF41-DOMAIN) carries no command
/// body, so a Submission pairs the envelope with the command it carries.
type Command =
    | StartInvestigation
    | RenameInvestigation
    | ActivateInvestigation
    | MarkAwaitingEvidence
    | MarkAwaitingVerification
    | ResolveInvestigation
    | CloseInvestigation
    | ReopenInvestigation
    | ArchiveInvestigation
    | RecordObservation
    | RecordHypothesis
    | RecordConclusion

type Submission = { Envelope: CommandEnvelope; Command: Command }

[<RequireQualifiedAccess>]
module Command =
    let all =
        [ StartInvestigation
          RenameInvestigation
          ActivateInvestigation
          MarkAwaitingEvidence
          MarkAwaitingVerification
          ResolveInvestigation
          CloseInvestigation
          ReopenInvestigation
          ArchiveInvestigation
          RecordObservation
          RecordHypothesis
          RecordConclusion ]

    let name (command: Command) =
        match command with
        | StartInvestigation -> "StartInvestigation"
        | RenameInvestigation -> "RenameInvestigation"
        | ActivateInvestigation -> "ActivateInvestigation"
        | MarkAwaitingEvidence -> "MarkAwaitingEvidence"
        | MarkAwaitingVerification -> "MarkAwaitingVerification"
        | ResolveInvestigation -> "ResolveInvestigation"
        | CloseInvestigation -> "CloseInvestigation"
        | ReopenInvestigation -> "ReopenInvestigation"
        | ArchiveInvestigation -> "ArchiveInvestigation"
        | RecordObservation -> "RecordObservation"
        | RecordHypothesis -> "RecordHypothesis"
        | RecordConclusion -> "RecordConclusion"
