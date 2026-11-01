// Owner: persistence. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Persistence

open Causa.Domain

/// IF41-STORAGE: causa-storage v1.0.0 file sets (CONTRACT.md wins over docs/CAUSA-DATA-CONTRACT-v1.md).
type CommitOid = CommitOid of string
type RepoPath = RepoPath of string

type FileSet =
    { Manifest: byte[]
      Events: byte[]
      Snapshot: byte[] }

type VerificationFailure =
    | SchemaInvalid of file: string * detail: string
    | DigestMismatch of field: string
    | ContractValueMismatch of field: string * expected: string * actual: string
    | SnapshotDiffersFromReplay of paths: string list
    | MissingRequiredField of file: string * field: string
    | ReplayRejected of ReplayError

type VerifiedInvestigation =
    { Investigation: Investigation
      History: EventEnvelope list
      ReadAt: CommitOid
      Bytes: FileSet }

type StorageCodec =
    { PathOf: InvestigationId -> RepoPath
      Encode: VerifiedInvestigation -> EventEnvelope list -> FileSet
      Verify: CommitOid -> FileSet -> Result<VerifiedInvestigation, VerificationFailure list> }
