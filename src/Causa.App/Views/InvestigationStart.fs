// Owner: ui. S01 Investigation Start: the original problem statement field and the optional title (SCREEN-CONTRACTS
// S01; R1.1, R1.2; requirement IDs INV-001, INV-002 and INV-008 of the proposed catalog CAT-CAUSA-INVESTIGATION in
// kemiller2002/Indy-init requirements/catalogs). Pure: a draft and the captured original to flat Limen keys.
//
// Not persisted. The frozen IF41-DOMAIN EventPayload carries only the event kind, so the statement and title have no
// place in the saved investigation. They are kept in this tab's memory only, the page says so, and the lifecycle
// obligations that need them saved (INV-001/V5/save, INV-008/V5/reload) fail verification until the data contract is
// changed through the owner's decision process. Nothing here pretends otherwise (R10.6).
namespace Causa.App.Views

open System

module InvestigationStart =
    /// What the person has typed so far, before the investigation starts.
    type Draft = { Statement: string; Title: string }

    /// The text as it was when the investigation started: preserved, never normalised (R1.2, A96).
    type Original = { Statement: string; Title: string option }

    let empty: Draft = { Statement = ""; Title = "" }

    let withStatement (text: string) (draft: Draft) = { draft with Statement = text }

    let withTitle (text: string) (draft: Draft) = { draft with Title = text }

    /// Pure: the original captured from a draft. The statement is kept verbatim; a blank title is no title.
    let capture (draft: Draft) : Original =
        { Statement = draft.Statement
          Title = if String.IsNullOrWhiteSpace draft.Title then None else Some draft.Title }

    /// Keep the first original: starting is captured once, later drafts never rewrite it.
    let captureOnce (draft: Draft) (existing: Original option) : Original option =
        existing |> Option.orElse (Some(capture draft))

    let keys (draft: Draft) (original: Original option) : ShellKeys =
        [ "startStatement", Str draft.Statement
          "startTitle", Str draft.Title
          "originalHidden", Bit original.IsNone
          "originalStatement",
          Str(
              original
              |> Option.map (fun o -> if String.IsNullOrWhiteSpace o.Statement then "No problem statement was entered." else o.Statement)
              |> Option.defaultValue ""
          )
          "originalTitle", Str(original |> Option.bind _.Title |> Option.defaultValue "Untitled") ]
