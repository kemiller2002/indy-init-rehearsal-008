// Owner: ui. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
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
