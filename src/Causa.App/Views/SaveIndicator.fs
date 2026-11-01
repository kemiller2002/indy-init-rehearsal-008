// Owner: ui (WI-R8-T22). The save-state indicator and the workspace header as flat Limen keys (PERSIST-030..033).
// Pure: a SaveIndicatorView (Projections.fs) to keys; every key is present in every projection (complete ViewState).
// Forma: status lozenge (state, glyph, name in text), operation status (explanation and actions), freshness (time of
// the last confirmed save). The announcement goes to the polite or the assertive live region (PERSIST-030-O6).
namespace Causa.App.Views

open Causa.Domain
open Causa.Persistence
open Causa.App

module SaveIndicator =
    let private lozengeOf (label: string) =
        match label with
        | "Saved" -> "ok"
        | "Unsaved"
        | "Offline" -> "attention"
        | "Saving"
        | "Unknown commit outcome" -> "unknown"
        | _ -> "blocked"

    /// The indicator of the tab-local investigation, which has no repository yet: its changes exist only in this tab.
    /// No Save is offered (it could not act without a connection, UI-010); the text says how to save.
    let forTab (investigation: InvestigationId) (changes: int) : SaveIndicatorView option =
        match changes with
        | 0 -> None
        | n ->
            let what = if n = 1 then "1 change exists" else $"{n} changes exist"

            Some
                { InvestigationId = investigation
                  Label = "Unsaved"
                  Text = $"{what} only in this tab and have not been sent to GitHub. Connect a repository to save."
                  Glyph = SaveIndicatorProjection.glyph SaveState.Unsaved
                  Actions = []
                  Politeness = Polite
                  LastSavedCommitTime = None }

    let private actionKeys (actions: SaveAction list) : ShellKeys =
        SaveIndicatorProjection.allActions
        |> List.map (fun a -> $"saveAction{SaveIndicatorProjection.actionName a}Hidden", Bit(not (List.contains a actions)))

    /// Pure: the indicator's keys. None (no investigation displayed) hides the indicator.
    let keys (view: SaveIndicatorView option) : ShellKeys =
        match view with
        | None ->
            [ "saveIndicatorHidden", Bit true
              "saveStateLabel", Str ""
              "saveStateLozenge", Str "unknown"
              "saveStateGlyph", Str ""
              "saveStateText", Str ""
              "saveStatePoliteText", Str ""
              "saveStateAssertiveText", Str ""
              "saveFreshnessHidden", Bit true
              "saveFreshnessText", Str ""
              "saveFreshnessDateTime", Str "" ]
            @ actionKeys []
        | Some v ->
            let announcement = $"{v.Label}. {v.Text}"

            let polite, assertive =
                match v.Politeness with
                | Polite -> announcement, ""
                | Assertive -> "", announcement

            let freshness =
                v.LastSavedCommitTime
                |> Option.map (fun t -> t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'"), t.ToUniversalTime().ToString("o"))

            [ "saveIndicatorHidden", Bit false
              "saveStateLabel", Str v.Label
              "saveStateLozenge", Str(lozengeOf v.Label)
              "saveStateGlyph", Str v.Glyph
              "saveStateText", Str v.Text
              "saveStatePoliteText", Str polite
              "saveStateAssertiveText", Str assertive
              "saveFreshnessHidden", Bit freshness.IsNone
              "saveFreshnessText", Str(freshness |> Option.map (fun (t, _) -> "Commit time " + t) |> Option.defaultValue "")
              "saveFreshnessDateTime", Str(freshness |> Option.map snd |> Option.defaultValue "") ]
            @ actionKeys v.Actions

module WorkspaceHeader =
    /// Pure: the header's keys (PERSIST-033-O1/O2): the connection in words, offline, and how many investigations of
    /// this tab are not Saved with a link to the first; the count is the summarized investigations' own count.
    let keys (header: WorkspaceHeaderView) : ShellKeys =
        let count = header.NotSaved.Length

        let notSavedText =
            match count with
            | 0 -> ""
            | 1 -> "1 investigation in this tab is not saved."
            | n -> $"{n} investigations in this tab are not saved."

        [ "headerConnection", Str(ConnectionMachine.label header.Connection)
          "headerOfflineHidden", Bit(not header.Offline)
          "headerOfflineText", Str(if header.Offline then "Offline: changes cannot be sent to GitHub until the connection returns." else "")
          "headerNotSavedHidden", Bit(count = 0)
          "headerNotSavedText", Str notSavedText
          "headerNotSavedHref", Str(header.NotSaved |> List.tryHead |> Option.map snd |> Option.defaultValue "#/")
          "headerNotSavedLinks", Str(header.NotSaved |> List.map snd |> String.concat " ") ]

    /// The header of a tab that holds only the tab-local investigation (no Model yet): connection and its count.
    let forTab (connection: ConnectionState) (offline: bool) (notSaved: (InvestigationId * string) list) : WorkspaceHeaderView =
        { Connection = connection
          Offline = offline
          NotSaved = notSaved }
