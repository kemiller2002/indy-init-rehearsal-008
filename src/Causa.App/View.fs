// Owner: integration. The composition of the shell view the Limen kernel binds (data-text, data-bind-*, data-if).
// A Limen ViewState is flat: string, number or boolean per key. Views (ui, ui-2) contribute their keys through the
// ScreenView projection once the engine (IF41-APP) is wired; this file only composes and never decides legality.
namespace Causa.App

type ViewValue =
    | Text of string
    | Number of float
    | Flag of bool

/// One complete Limen ViewState: every bound key, every time (never a patch).
type ShellView = (string * ViewValue) list

/// What the wire layer knows before the application engine is attached: has Limen initialized us, and where are we.
type ShellState = { Booted: bool; Path: string }

module View =
    /// The engine identity the page and the smoke tests read (DEP-CURRENT-004 Limen startup).
    [<Literal>]
    let EngineName = "causa"

    let initial: ShellState = { Booted = false; Path = "/" }

    /// Pure: state to the complete view.
    let render (state: ShellState) : ShellView =
        [ "engine", Text EngineName
          "engineReady", Flag state.Booted
          "engineLoading", Flag(not state.Booted)
          "status", Text(if state.Booted then "Causa engine ready" else "Loading Causa")
          "path", Text state.Path ]
