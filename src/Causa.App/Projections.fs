// Owner: ui. IF41-VIEW shapes (frozen at T14) and, beside them, their implementation (WI-R8-T22): the save-state
// indicator (PERSIST-030..032), the workspace header (PERSIST-033) and the screen projection. Pure: views read only
// these and never decide legality or persistence; a word that claims persistence appears only in Saved (INV-14).
namespace Causa.App

open Causa.Domain
open Causa.Persistence

/// IF41-VIEW: projections the views render (ui and ui-2). Views read only these; they never decide legality.
type SaveAction =
    | Save
    | Retry
    | Reconnect
    | CheckAgainAction
    | Review
    | ReapplyMine
    | DiscardMine
    | ExportMine

type SaveIndicatorView =
    { InvestigationId: InvestigationId
      Label: string
      Text: string
      Glyph: string
      Actions: SaveAction list
      Politeness: Politeness
      LastSavedCommitTime: System.DateTimeOffset option }

type WorkspaceHeaderView =
    { Connection: ConnectionState
      Offline: bool
      NotSaved: (InvestigationId * string) list }

type ScreenView =
    { Route: Route
      Title: string
      Header: WorkspaceHeaderView
      Indicator: SaveIndicatorView option }

type Projections =
    { SaveIndicator: InvestigationSession -> SaveIndicatorView
      Header: Model -> WorkspaceHeaderView
      Screen: Model -> ScreenView }

/// The cause of a provider refusal in words a person can act on (IF41-ERRORS consumer). Never the credential.
[<RequireQualifiedAccess>]
module ProviderMessage =
    let private short (CommitOid oid) = if oid.Length > 7 then oid.Substring(0, 7) else oid

    let ofError (error: ProviderError) : string =
        match error with
        | AuthenticationFailed -> "GitHub did not accept the credential."
        | PermissionDenied operation -> $"The credential is not allowed to do this on the repository ({operation})."
        | RateLimited(Primary _) -> "GitHub's request limit for this credential was reached; try again after it resets."
        | RateLimited(Secondary _) -> "GitHub asked Causa to slow down; try again in a minute."
        | StaleHead _ -> "The branch on GitHub moved since this investigation was read."
        | ValidationFailed messages -> "GitHub rejected the request: " + String.concat "; " messages + "."
        | NotFound what -> $"GitHub could not find the {what}."
        | HistoryRewritten expected -> $"The branch history on GitHub was rewritten; commit {short expected} is no longer on it."
        | NetworkUnavailable FailedBeforeSending -> "The request could not be sent; check the connection."
        | NetworkUnavailable LostAfterSending -> "The answer from GitHub was lost after the request was sent."
        | NetworkUnavailable TimedOut -> "GitHub did not answer in time."
        | Malformed detail -> $"GitHub's answer could not be read ({detail})."
        | ReadBackMismatch commit -> $"The files read back at commit {short commit} differ from what Causa wrote."
        | Unrecognised status -> $"GitHub answered with an unexpected status ({status})."

/// The seven save states as the indicator shows them (PERSIST-030-O3, PERSIST-031).
[<RequireQualifiedAccess>]
module SaveIndicatorProjection =
    let private short (CommitOid oid) = if oid.Length > 7 then oid.Substring(0, 7) else oid

    /// The Forma status-lozenge state for each save state (the lozenge draws its own glyph; colour is never alone).
    let lozengeState (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "ok"
        | SaveState.Unsaved
        | SaveState.Offline -> "attention"
        | SaveState.Saving
        | SaveState.UnknownOutcome _ -> "unknown"
        | SaveState.Conflict _
        | SaveState.SaveFailed _ -> "blocked"

    let label (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "Unknown commit outcome"
        | SaveState.SaveFailed _ -> "Save failed"

    let glyph (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "✓"
        | SaveState.Unsaved -> "●"
        | SaveState.Saving -> "↻"
        | SaveState.Offline -> "⊘"
        | SaveState.Conflict _ -> "⚠"
        | SaveState.UnknownOutcome _ -> "?"
        | SaveState.SaveFailed _ -> "✕"

    /// Conflict, Unknown commit outcome and Save failed are announced assertively, the rest politely (PERSIST-030-O6).
    let politeness (state: SaveState) =
        match state with
        | SaveState.Conflict _
        | SaveState.UnknownOutcome _
        | SaveState.SaveFailed _ -> Assertive
        | SaveState.Saved _
        | SaveState.Unsaved
        | SaveState.Saving
        | SaveState.Offline -> Polite

    let private changes (n: int) = if n = 1 then "1 change" else $"{n} changes"

    let private failureCause (failure: SaveFailure) =
        match failure with
        | Refused error -> ProviderMessage.ofError error
        | RepositoryKeptChanging -> "The branch on GitHub kept changing while Causa tried to write."
        | BytesDiffer commit -> $"The files at commit {short commit} differ from what this tab wrote."
        | HistoryWasRewritten -> "The branch history on GitHub was rewritten."
        | CredentialNotAccepted -> "GitHub no longer accepts the credential."

    /// Retry only when a retry can succeed; Reconnect when the credential was refused (PERSIST-031-O22, UI-010).
    let private failureActions (failure: SaveFailure) =
        match failure with
        | CredentialNotAccepted
        | Refused AuthenticationFailed -> [ Reconnect ]
        | Refused(PermissionDenied _) -> [ Reconnect; ExportMine ]
        | HistoryWasRewritten
        | Refused(HistoryRewritten _)
        | Refused(ValidationFailed _)
        | Refused(NotFound _) -> [ ExportMine ]
        | RepositoryKeptChanging
        | BytesDiffer _
        | Refused(RateLimited _)
        | Refused(StaleHead _)
        | Refused(NetworkUnavailable _)
        | Refused(Malformed _)
        | Refused(ReadBackMismatch _)
        | Refused(Unrecognised _) -> [ Retry; ExportMine ]

    let private conflictText (view: ConflictView) =
        let lead =
            "Another writer changed this investigation on GitHub. Nothing of this tab's change was written, and this tab's changes are kept."

        let detail, actions =
            match view with
            | ReadingRemote _ -> "Causa is reading the GitHub version to compare.", []
            | Reviewable remote -> $"Review the GitHub version at commit {short remote.ReadAt}, then reapply, discard or export this tab's changes.", [ Review; ReapplyMine; DiscardMine; ExportMine ]
            | Reapplying _ -> "Causa is re-deciding this tab's changes against the GitHub version.", []
            | Reapplied(_, []) -> "Every change of this tab was re-decided against the GitHub version and is ready to send.", [ Save ]
            | Reapplied(_, refused) -> $"{changes refused.Length} of this tab's changes could not be reapplied; review them.", [ Review; ExportMine ]
            | ConfirmingDiscard _ -> "Confirm that this tab's changes should be discarded in favour of the GitHub version.", [ DiscardMine; Review ]
            | Discarded _ -> "This tab now shows the GitHub version.", []
            | RemoteUnavailable -> "The GitHub version could not be read.", [ Retry; ExportMine ]
            | Unresolvable reason -> $"The conflict cannot be resolved here: {reason}", [ ExportMine ]

        lead + " " + detail, actions

    /// Pure: one session to its indicator. The words follow PERSIST-031; actions are offered only when they act.
    let project (session: InvestigationSession) : SaveIndicatorView =
        let pending = session.Unsaved.Length + session.AfterInFlight.Length
        let state = session.State

        let text, actions, committedAt =
            match state with
            | SaveState.Saved(Confirmed confirmation) ->
                $"Saved to GitHub at commit {short confirmation.CommitOid}.", [], Some confirmation.CommitTime
            | SaveState.Saved(VerifiedAt verified) -> $"Saved: matches GitHub at commit {short verified.ReadAt}.", [], None
            | SaveState.Unsaved -> $"{changes pending} exist only in this tab and have not been sent to GitHub.", [ Save ], None
            | SaveState.Saving -> "This change is being written to GitHub and is not yet confirmed.", [], None
            | SaveState.Offline ->
                $"{changes pending} exist only in this tab and cannot be sent to GitHub until the connection returns.", [], None
            | SaveState.Conflict view ->
                let t, a = conflictText view
                t, a, None
            | SaveState.UnknownOutcome readsLeft when readsLeft > 0 ->
                "The change may or may not have reached GitHub. Causa is checking by reading the branch.", [], None
            | SaveState.UnknownOutcome _ ->
                "The change may or may not have reached GitHub. The automatic checks are spent; check again to read the branch.",
                [ CheckAgainAction ],
                None
            | SaveState.SaveFailed failure ->
                $"{failureCause failure} The changes are kept in this tab.", failureActions failure, None

        { InvestigationId = session.Id
          Label = label state
          Text = text
          Glyph = glyph state
          Actions = actions
          Politeness = politeness state
          LastSavedCommitTime = committedAt }

    let actionName (action: SaveAction) =
        match action with
        | Save -> "Save"
        | Retry -> "Retry"
        | Reconnect -> "Reconnect"
        | CheckAgainAction -> "CheckAgain"
        | Review -> "Review"
        | ReapplyMine -> "ReapplyMine"
        | DiscardMine -> "DiscardMine"
        | ExportMine -> "ExportMine"

    let allActions = [ Save; Retry; Reconnect; CheckAgainAction; Review; ReapplyMine; DiscardMine; ExportMine ]

/// The workspace header (PERSIST-033) and the screen (IF41-VIEW).
[<RequireQualifiedAccess>]
module HeaderProjection =
    let private isSaved (session: InvestigationSession) =
        match session.State with
        | SaveState.Saved _ -> true
        | _ -> false

    /// Pure: the connection, whether the tab is offline, and every investigation that is not Saved with its link.
    let header (model: Model) : WorkspaceHeaderView =
        { Connection = model.Connection
          Offline = model.Network = NetworkState.Offline
          NotSaved =
            model.Sessions
            |> Map.toList
            |> List.filter (snd >> isSaved >> not)
            |> List.map (fun (InvestigationId g as id, _) ->
                let repository =
                    model.Context |> Option.map (fun c -> $"{c.Repository.Owner}/{c.Repository.Name}")

                id, Routes.format (AppRoute.Investigation(g, repository, InvestigationTab.Overview))) }

    let private title (route: AppRoute) =
        match route with
        | AppRoute.Home -> "Investigations"
        | AppRoute.InvestigationStart -> "Start an investigation"
        | AppRoute.Investigation(_, _, tab) -> Routes.tabLabel tab
        | AppRoute.Administration -> "Administration"
        | AppRoute.Settings -> "Settings"
        | AppRoute.NotFound -> "Page not found"

    let screen (model: Model) : ScreenView =
        let route = Routes.ofRoute model.Route

        let indicator =
            match route with
            | AppRoute.Investigation(g, _, _) ->
                model.Sessions |> Map.tryFind (InvestigationId g) |> Option.map SaveIndicatorProjection.project
            | _ -> None

        { Route = model.Route
          Title = title route
          Header = header model
          Indicator = indicator }

/// IF41-VIEW implementation, bound in tests/Causa.Invariants/Model/INV08.fs (Bindings.projections).
[<RequireQualifiedAccess>]
module Projection =
    let value: Projections =
        { SaveIndicator = SaveIndicatorProjection.project
          Header = HeaderProjection.header
          Screen = HeaderProjection.screen }
