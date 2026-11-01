// Owner: persistence. T19: SM-LOAD transition tests (state-machine-contracts.json), one per transition, and the
// illegal pairs.
namespace Causa.Persistence.Tests.StateMachines

open Xunit
open Causa.Persistence
open Support

module SMLOAD =
    let private loaded = Loaded(verifiedAt 1)
    let private step state input = ok (online.StepLoad (ctx 30L) state input)

    let private states: (string * LoadState) list =
        [ "NotLoaded", NotLoaded
          "Loading", Loading(RequestId 5L, LoadHead)
          "Loaded", loaded
          "LoadFailed", LoadFailed(ProviderFailed(NetworkUnavailable TimedOut))
          "UnavailableOffline", UnavailableOffline ]

    let private events: (string * LoadInput) list =
        [ "OpenRequested", LoadInput.OpenRequested investigation
          "LoadVerified", LoadInput.LoadVerified(verifiedAt 2)
          "LoadRejected", LoadInput.LoadRejected(Unverifiable [ DigestMismatch "eventsSha256" ])
          "WentOffline", LoadInput.WentOffline
          "WentOnline", LoadInput.WentOnline
          "RefreshRequested", LoadInput.RefreshRequested
          "SupersededReply", LoadInput.SupersededReply(RequestId 99L) ]

    let legal =
        set
            [ "NotLoaded", "OpenRequested"; "Loading", "LoadVerified"; "Loading", "LoadRejected"; "Loading", "SupersededReply"
              "Loaded", "WentOffline"; "Loaded", "RefreshRequested"; "Loaded", "SupersededReply"; "LoadFailed", "OpenRequested"
              "UnavailableOffline", "WentOnline" ]

    let private readsAt (n: int64) (effects: SaveEffect list) =
        match sends effects with
        | [ request ] ->
            Assert.Equal(RequestId n, request.Id)
            Assert.False(isMutation request)
        | other -> Assert.Fail(sprintf "expected one read: %A" other)

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        let pairs = illegalPairs states events legal
        Assert.Equal(26, pairs.Length)

        for (sn, s, en, e) in pairs do
            for machine in [ online; offline ] do
                let fault = faults (machine.StepLoad (ctx 1L) s e)
                Assert.Equal("SM-LOAD", fault.Machine)
                Assert.Equal(sn, fault.State)
                Assert.Equal(en, fault.Input)

    [<Fact>]
    let ``T-L01`` () =
        let state, effects = step NotLoaded (LoadInput.OpenRequested investigation)
        Assert.Equal(Loading(RequestId 30L, LoadHead), state)
        readsAt 30L effects

    [<Fact>]
    let ``T-L02`` () =
        let state, effects = ok (offline.StepLoad (ctx 30L) NotLoaded (LoadInput.OpenRequested investigation))
        Assert.Equal(UnavailableOffline, state)
        Assert.Empty(effects)

    [<Fact>]
    let ``T-L03`` () =
        let state, _ = step (Loading(RequestId 5L, LoadHead)) (LoadInput.LoadVerified(verifiedAt 2))
        Assert.Equal(Loaded(verifiedAt 2), state)
        // SM-SAVE initial: Saved at that commit.
        let session = online.Initial(verifiedAt 2)
        Assert.Equal(SaveState.Saved(VerifiedAt(verifiedAt 2)), session.State)
        Assert.Equal(head 2, session.BaseHead)

    [<Fact>]
    let ``T-L04`` () =
        let failure = ProviderFailed AuthenticationFailed
        let state, effects = step (Loading(RequestId 5L, LoadHead)) (LoadInput.LoadRejected failure)
        Assert.Equal(LoadFailed failure, state)
        Assert.Empty(sends effects)

    [<Fact>]
    let ``T-L05`` () =
        let state, effects = step loaded LoadInput.WentOffline
        Assert.Equal(loaded, state)
        Assert.Empty(effects)

    [<Fact>]
    let ``T-L06`` () =
        let state, _ = step UnavailableOffline LoadInput.WentOnline
        Assert.Equal(NotLoaded, state)

    [<Fact>]
    let ``T-L07`` () =
        let state, effects = step loaded LoadInput.RefreshRequested
        Assert.Equal(Loading(RequestId 30L, LoadHead), state)
        readsAt 30L effects

    [<Fact>]
    let ``T-L08`` () =
        let state, effects = step (LoadFailed(ProviderFailed(NetworkUnavailable TimedOut))) (LoadInput.OpenRequested investigation)
        Assert.Equal(Loading(RequestId 30L, LoadHead), state)
        readsAt 30L effects

    [<Fact>]
    let ``T-L09`` () =
        let loading = Loading(RequestId 5L, LoadHead)
        let state, effects = step loading (LoadInput.SupersededReply(RequestId 4L))
        Assert.Equal(loading, state)
        Assert.Empty(effects)

        match online.InterpretLoad (ctx 6L) loading (Answered(RequestId 4L, 200, [], "{}")) with
        | LoadEvent(LoadInput.SupersededReply(RequestId 4L)) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

    [<Fact>]
    let ``T-L10`` () =
        let state, effects = step loaded (LoadInput.SupersededReply(RequestId 4L))
        Assert.Equal(loaded, state)
        Assert.Empty(effects)

        match online.InterpretLoad (ctx 6L) loaded (Answered(RequestId 4L, 200, [], "{}")) with
        | LoadEvent(LoadInput.SupersededReply _) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

    /// An answer to the current load is verified with the storage codec; a forged file set is rejected.
    [<Fact>]
    let InterpretVerifiesTheFiles () =
        let files = StorageV1.encodeHistory investigation [ event 1L ]
        let text (b: byte[]) = System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.UTF8.GetString b)
        let blob b = sprintf """{"object":{"text":%s,"isTruncated":false}}""" (text b)

        let body events =
            sprintf
                """{"data":{"repository":{"ref":{"target":{"oid":"%040x","manifest":%s,"events":%s,"snapshot":%s}}}}}"""
                7
                (blob files.Manifest)
                (blob events)
                (blob files.Snapshot)

        let loading = Loading(RequestId 5L, LoadHead)

        match online.InterpretLoad (ctx 6L) loading (Answered(RequestId 5L, 200, [], body files.Events)) with
        | LoadEvent(LoadInput.LoadVerified v) -> Assert.Equal(head 7, v.ReadAt)
        | other -> Assert.Fail(sprintf "%A" other)

        let forged = Array.append files.Events (System.Text.Encoding.UTF8.GetBytes "{}\n")

        match online.InterpretLoad (ctx 6L) loading (Answered(RequestId 5L, 200, [], body forged)) with
        | LoadEvent(LoadInput.LoadRejected(Unverifiable _)) -> ()
        | other -> Assert.Fail(sprintf "%A" other)

        match online.InterpretLoad (ctx 6L) loading (Answered(RequestId 5L, 401, [], """{"message":"Bad credentials"}""")) with
        | LoadEvent(LoadInput.LoadRejected(ProviderFailed AuthenticationFailed)) -> ()
        | other -> Assert.Fail(sprintf "%A" other)
