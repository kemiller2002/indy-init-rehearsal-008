// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Property

open Causa.Persistence
open Causa.Invariants
open Xunit

/// INV-05 (PERSIST-006-O3): every mutation is one createCommitOnBranch whose expectedHeadOid is a head this tab read in
/// its session, touching only paths under the investigation's prefix, carrying a write marker that never repeats
/// across tabs and page loads; no forced ref update, per-file write or headless write.
module INV05 =
    let private forbidden = [ "updateRef"; "updateRefs"; "createRef"; "deleteRef"; "force"; "createOrUpdateFileContents"; "/contents/" ]

    /// Heads the tab has read so far: the loaded head and every head a reply or resolution reported.
    let private headsIn (input: SaveInput) =
        match input with
        | SaveInput.StaleHeadUnrelated h
        | SaveInput.StaleHeadSameInvestigation h
        | SaveInput.ReconcileNothingWrittenHeadMoved h -> [ h ]
        | SaveInput.ReadBackMatched c
        | SaveInput.ReconcileMarkerBytesMatch c -> [ c.CommitOid ]
        | SaveInput.ConflictResolvedDiscarded v -> [ v.ReadAt ]
        | _ -> []

    let mutationBodies (steps: Step list) =
        steps |> List.collect (Drive.effects >> Requests.mutations) |> List.choose (fun r -> r.Body)

    let shaped (prefix: string) (steps: Step list) =
        let checks, _ =
            steps
            |> List.mapFold
                (fun (known: Set<string>) step ->
                    let known = Set.union known (headsIn step.Input |> List.map Fixture.headText |> Set.ofList) |> Set.add (Fixture.headText step.Before.BaseHead)

                    let check =
                        Drive.effects step
                        |> Requests.mutations
                        |> List.map (fun request ->
                            let body = defaultArg request.Body ""
                            let expected = Json.strings "expectedHeadOid" body

                            Check.all
                                [ Check.expect (request.Method = Post && request.Url = "https://api.github.com/graphql") (sprintf "mutation sent as %A %s" request.Method request.Url)
                                  Check.expect (expected.Length = 1 && Set.contains expected.Head known) (sprintf "expectedHeadOid %A is not a head this tab read" expected)
                                  Check.expect (forbidden |> List.forall (fun word -> not (body.Contains word))) "a forced, per-file or ref write"
                                  Check.expect (Json.strings "path" body |> List.forall (fun p -> p.StartsWith prefix)) (sprintf "a path outside %s" prefix) ])
                        |> Check.all

                    check, known)
                Set.empty

        Check.all (checks @ [ Check.expect (List.distinct (mutationBodies steps) = mutationBodies steps) "a mutation repeated a write marker" ])

    [<Fact>]
    let MutationShape () =
        let machine = Require.saveMachine ()
        let codec = Require.storageCodec ()
        let (RepoPath prefix) = codec.PathOf Fixture.investigationId

        Check.check 300 Moves.sequence (Drive.run machine >> shaped prefix)

        // Two page loads (or tabs) making the same edits never send the same marker.
        let moves = [ Raw(SaveInput.Edit(Fixture.command 1)); Raw SaveInput.SaveRequested ]
        let first = Drive.run machine moves |> mutationBodies
        let second = Drive.run machine moves |> mutationBodies
        Assert.NotEmpty(first)
        Assert.True(Set.isEmpty (Set.intersect (Set.ofList first) (Set.ofList second)), "two page loads sent an identical mutation (same write marker)")
