// Owner: ui (WI-R8-T22). S23 Persistence / Conflict Recovery as flat Limen keys (PERSIST-011/012, INVESTIGATION-
// WORKSPACE section 27). Pure: what this tab holds, the GitHub version, the re-decision result and the choices the
// SM-CONFLICT state offers. No raw JSON.
namespace Causa.App.Views

open Causa.Domain
open Causa.Persistence
open Causa.App

module ConflictReview =
    let private short (CommitOid oid) = if oid.Length > 7 then oid.Substring(0, 7) else oid

    let private version (v: VerifiedInvestigation) =
        let (AggregateVersion n) = v.Investigation.Version
        n

    let private remoteOf (view: ConflictView) =
        match view with
        | Reviewable r
        | Reapplying r
        | Reapplied(r, _)
        | ConfirmingDiscard r
        | Discarded r -> Some r
        | ReadingRemote _
        | RemoteUnavailable
        | Unresolvable _ -> None

    let private stage (view: ConflictView) =
        match view with
        | ReadingRemote _ -> "Reading the GitHub version."
        | Reviewable _ -> "Ready to review."
        | Reapplying _ -> "Re-deciding this tab's changes against the GitHub version."
        | Reapplied(_, []) -> "Every change of this tab was accepted against the GitHub version."
        | Reapplied(_, refused) -> $"{refused.Length} change(s) of this tab were refused against the GitHub version."
        | ConfirmingDiscard _ -> "Confirm discarding this tab's changes."
        | Discarded _ -> "This tab's changes were discarded; the tab shows the GitHub version."
        | RemoteUnavailable -> "The GitHub version could not be read."
        | Unresolvable reason -> $"Cannot be resolved here: {reason}"

    let private refusedText (view: ConflictView) =
        match view with
        | Reapplied(_, refused) ->
            refused
            |> List.map (fun r ->
                let id = r.Command.CommandId.ToString "D"
                $"Command {id.Substring(0, 8)}: {r.Reason}")
            |> String.concat " "
        | _ -> ""

    let private hidden (keys: (string * bool) list) : ShellKeys = keys |> List.map (fun (k, b) -> k, Bit(not b))

    /// Pure: the review's keys for one session; hidden unless the session is in Conflict.
    let keys (session: InvestigationSession option) : ShellKeys =
        match session |> Option.map (fun s -> s, s.State) with
        | Some(s, SaveState.Conflict view) ->
            let remote = remoteOf view
            let can a = (SaveIndicatorProjection.project s).Actions |> List.contains a

            [ "conflictReviewHidden", Bit false
              "conflictThisTab", Str($"This tab holds {s.Unsaved.Length + s.AfterInFlight.Length} unsaved command(s) based on commit {short s.BaseHead}.")
              "conflictRemote",
              Str(
                  remote
                  |> Option.map (fun r -> $"GitHub version: commit {short r.ReadAt}, version {version r}.")
                  |> Option.defaultValue "GitHub version: not read yet."
              )
              "conflictExplanation", Str "Another writer changed this investigation on GitHub. Nothing of this tab's change was written, and this tab's changes are kept."
              "conflictStage", Str(stage view)
              "conflictRefused", Str(refusedText view) ]
            @ hidden
                [ "conflictReapplyHidden", can ReapplyMine
                  "conflictDiscardHidden", can DiscardMine
                  "conflictExportHidden", can ExportMine
                  "conflictRetryHidden", can Retry ]
        | _ ->
            [ "conflictReviewHidden", Bit true
              "conflictThisTab", Str ""
              "conflictRemote", Str ""
              "conflictExplanation", Str ""
              "conflictStage", Str ""
              "conflictRefused", Str ""
              "conflictReapplyHidden", Bit true
              "conflictDiscardHidden", Bit true
              "conflictExportHidden", Bit true
              "conflictRetryHidden", Bit true ]
