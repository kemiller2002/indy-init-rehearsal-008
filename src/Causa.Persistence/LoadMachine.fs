// Owner: persistence. T19: SM-LOAD (state-machine-contracts.json): loading one investigation into a tab. Pure.
namespace Causa.Persistence

open Causa.Domain
open Causa.Persistence.ErrorMatrix

[<RequireQualifiedAccess>]
module LoadMachine =
    let name = "SM-LOAD"

    let stateName (state: LoadState) =
        match state with
        | NotLoaded -> "NotLoaded"
        | Loading _ -> "Loading"
        | Loaded _ -> "Loaded"
        | LoadFailed _ -> "LoadFailed"
        | UnavailableOffline -> "UnavailableOffline"

    let inputName (input: LoadInput) =
        match input with
        | LoadInput.OpenRequested _ -> "OpenRequested"
        | LoadInput.LoadVerified _ -> "LoadVerified"
        | LoadInput.LoadRejected _ -> "LoadRejected"
        | LoadInput.WentOffline -> "WentOffline"
        | LoadInput.WentOnline -> "WentOnline"
        | LoadInput.RefreshRequested -> "RefreshRequested"
        | LoadInput.SupersededReply _ -> "SupersededReply"

    /// T-L01: the head is read uncached together with the files at that head, in one query, so the files are the
    /// files of the commit read (PERSIST-005).
    let private open' (context: RequestContext) (investigation: InvestigationId) =
        Loading(context.NextId, LoadHead),
        [ Send(
              GitHubRequests.fileSetAtBranch
                  context.NextId
                  context.Connection
                  (StoragePaths.manifest investigation)
                  (StoragePaths.events investigation)
                  (StoragePaths.snapshot investigation)
          ) ]

    let step (context: RequestContext) (state: LoadState) (input: LoadInput) : Result<LoadState * SaveEffect list, SaveFault> =
        match state, input with
        // T-L01 (online and connected: the engine steps OpenRequested only then; offline it shows T-L02's state).
        | NotLoaded, LoadInput.OpenRequested id -> Ok(open' context id)
        // T-L03
        | Loading _, LoadInput.LoadVerified verified -> Ok(Loaded verified, [])
        // T-L04
        | Loading _, LoadInput.LoadRejected failure ->
            Ok(LoadFailed failure, [ Announce(Assertive, "The investigation could not be opened. Try again.") ])
        // T-L09, T-L10
        | Loading _, LoadInput.SupersededReply _
        | Loaded _, LoadInput.SupersededReply _ -> Ok(state, [])
        // T-L05
        | Loaded _, LoadInput.WentOffline -> Ok(state, [])
        // T-L07 (the engine refuses Refresh unless SM-SAVE is Saved, INV-07).
        | Loaded verified, LoadInput.RefreshRequested -> Ok(open' context verified.Investigation.Id)
        // T-L08
        | LoadFailed _, LoadInput.OpenRequested id -> Ok(open' context id)
        // T-L06
        | UnavailableOffline, LoadInput.WentOnline -> Ok(NotLoaded, [])
        | _ ->
            Error
                { Machine = name
                  State = stateName state
                  Input = inputName input
                  Reason = "not a transition of SM-LOAD in this state (fail closed)" }

    let interpret (codec: StorageCodec) (_: RequestContext) (state: LoadState) (outcome: HttpOutcome) : LoadInterpretation =
        let answerId =
            match outcome with
            | Answered(id, _, _, _)
            | NotAnswered(id, _) -> id

        match state with
        | Loading(current, _) when current = answerId ->
            match (classify (Read "graphql:load-file-set") outcome).Result with
            | Ok answer ->
                match Answers.fileSet answer.Body with
                | Some { At = at; Manifest = Some m; Events = Some e; Snapshot = Some s } ->
                    match codec.Verify at { Manifest = m; Events = e; Snapshot = s } with
                    | Ok verified -> LoadEvent(LoadInput.LoadVerified verified)
                    | Error failures -> LoadEvent(LoadInput.LoadRejected(Unverifiable failures))
                | Some _ -> LoadEvent(LoadInput.LoadRejected(ProviderFailed(NotFound "the investigation's files")))
                | None when Answers.refMissing answer.Body -> LoadEvent(LoadInput.LoadRejected(ProviderFailed(NotFound "the branch")))
                | None -> LoadEvent(LoadInput.LoadRejected(ProviderFailed(Malformed "the file-set answer does not decode")))
            | Error e -> LoadEvent(LoadInput.LoadRejected(ProviderFailed e))
        | _ -> LoadEvent(LoadInput.SupersededReply answerId)
