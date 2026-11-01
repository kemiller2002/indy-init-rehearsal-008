// Owner: persistence. T18: the live provider-conformance probe (INV-22, R7L-09, R7L-10, R7L-19, PERSIST-008-O2).
namespace Causa.Invariants.Live

open System
open System.IO
open System.Text
open Xunit
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

/// Runs only in the live suite (SU-DOTNET-LIVE, CI with the repository secrets). It probes api.github.com, writes
/// every answer it receives as a sanitized CapturedLive recording to the run's output directory, and classifies
/// each with the matrix. A missing secret fails as BLOCKED; nothing here passes without a live answer.
module INV22 =

    let private bytes (s: string) = Encoding.UTF8.GetBytes s

    let private runId () = Environment.GetEnvironmentVariable "GITHUB_RUN_ID" |> Option.ofObj |> Option.defaultValue "local"
    let private sha () = Environment.GetEnvironmentVariable "GITHUB_SHA" |> Option.ofObj |> Option.defaultValue "unknown"

    let private record (name: string) (operation: string) (classified: Classified) (outcome: HttpOutcome) : Recording =
        let status, headers, body =
            match outcome with
            | Answered(_, s, h, b) -> s, Recordings.sanitizeHeaders h, Recordings.sanitizeBody b
            | NotAnswered(_, f) -> 0, [], sprintf "<not answered: %A>" f

        { Name = name
          MatrixClass = classId classified.Class
          Operation = operation
          Status = status
          Headers = headers
          Body = body
          Provenance = CapturedLive(runId (), DateTimeOffset.UtcNow.ToString "o", sha ()) }

    let private write (secrets: string list) (recordings: Recording list) =
        let dir =
            match Environment.GetEnvironmentVariable "CAUSA_RECORDINGS_OUT" with
            | null
            | "" -> Path.Combine(Environment.CurrentDirectory, "TestResults", "provider-recordings")
            | d -> d

        let json = Recordings.toJson recordings
        // AR-014-O2, PF-015: no secret value may reach an artifact.
        for s in secrets do
            Assert.False(json.Contains s, "a recording contains a secret value")

        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, sprintf "inv22-%s.json" (runId ())), json)

    let private withHeader (name: string) (value: string) (r: HttpRequest) = { r with Headers = r.Headers @ [ name, value ] }

    [<Fact>]
    let ThreeSimultaneousCommits () =
        let live = Conformance.liveWriteConnection ()
        let branch = Conformance.freshBranch LiveGitHub.execute live "probe/inv22"
        let h0 = (LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch) |> Conformance.headOid).Value
        let expected = CommitOid h0

        // 1. Three simultaneous commits guarded by the same head (R7L-10).
        let requests =
            [ for i in 1..3 ->
                  GitHubRequests.createCommitOnBranch
                      (Conformance.freshId ())
                      branch
                      expected
                      (sprintf "inv22 writer %d" i)
                      [ RepoPath(sprintf "probes/inv22/writer-%d.txt" i), bytes (string i) ] ]

        let outcomes = requests |> List.map (fun r -> async { return LiveGitHub.execute r }) |> Async.Parallel |> Async.RunSynchronously |> List.ofArray
        let classes = outcomes |> List.map (classify (Mutation expected))
        let race = List.mapi (fun i (c, o) -> record (sprintf "race-writer-%d" (i + 1)) "graphql:createCommitOnBranch" c o) (List.zip classes outcomes)

        // 2. The fake answers the same race; its classes must be a form the live provider also gave (PF-013).
        let fake = Conformance.newFake ()
        let fh = (StatefulFake.head "main" fake.State).Value
        let fakeClasses =
            [ for i in 1..3 ->
                  GitHubRequests.createCommitOnBranch (Conformance.freshId ()) Conformance.fakeConnection fh "w" [ RepoPath(sprintf "w%d" i), bytes "w" ]
                  |> fake.Execute
                  |> classify (Mutation fh)
                  |> _.Class ]

        // 3. 401: a credential GitHub cannot accept (a fixed non-secret string).
        let bogus = { branch with Credential = Credential.create "causa-probe-invalid-credential" }
        let authOutcome = LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) bogus)
        let auth = classify (Read "rest:ref") authOutcome

        // 4. 422: creating a branch that already exists.
        let dupOutcome = LiveGitHub.execute (GitHubRequests.createBranch (Conformance.freshId ()) branch branch.Repository.Branch expected)
        let dup = classify (Read "rest:ref") dupOutcome

        // 5. Cache headers on a mutable read, and the conditional re-read.
        let refOutcome = LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch)
        let refClass = classify (Read "rest:ref") refOutcome

        let etag =
            match refOutcome with
            | Answered(_, _, h, _) -> h |> List.tryFind (fun (k, _) -> k.Equals("etag", StringComparison.OrdinalIgnoreCase)) |> Option.map snd
            | _ -> None

        let conditional =
            etag
            |> Option.map (fun e ->
                let o = LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch |> withHeader "If-None-Match" e)
                o, classify (Read "rest:ref") o)

        // 6. Lost answer: a commit whose answer the client abandons after sending; then at most three reads
        //    (GH-NETWORK-AFTER bound) find out whether it applied.
        let headNow = LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch) |> Conformance.headOid |> Option.defaultValue h0
        let lostOutcome =
            LiveGitHub.executeWithin
                (TimeSpan.FromMilliseconds 1.0)
                (GitHubRequests.createCommitOnBranch (Conformance.freshId ()) branch (CommitOid headNow) "inv22 lost answer" [ RepoPath "probes/inv22/lost.txt", bytes "lost" ])
        let lost = classify (Mutation(CommitOid headNow)) lostOutcome

        let reads =
            [ 1 .. maxAttempts GhNetworkAfter ]
            |> List.map (fun i ->
                Threading.Thread.Sleep(TimeSpan.FromSeconds(float (pown 2 i)))
                LiveGitHub.execute (GitHubRequests.branchHead (Conformance.freshId ()) branch) |> Conformance.headOid)

        let applied = reads |> List.exists (fun h -> h.IsSome && h <> Some headNow)

        // 7. Permission (403/FORBIDDEN): the read-only data token attempting a commit on the data repository with an
        //    all-zero expected head, so that nothing can be written even if the token were misconfigured.
        let permission =
            match Environment.GetEnvironmentVariable "CAUSA_GITHUB_TOKEN" with
            | null
            | "" -> None
            | token ->
                let data =
                    { Repository = { Owner = "kemiller2002"; Name = "causa-data"; Branch = "main" }
                      Credential = Credential.create token }

                let zero = CommitOid(String('0', 40))
                let o = LiveGitHub.execute (GitHubRequests.createCommitOnBranch (Conformance.freshId ()) data zero "causa probe: must be refused" [ RepoPath "probes/never.txt", bytes "never" ])
                Some(o, classify (Mutation zero) o, token)

        let recordings =
            race
            @ [ record "auth-invalid-credential" "rest:ref" auth authOutcome
                record "validation-duplicate-ref" "rest:ref" dup dupOutcome
                record "cache-headers-ref" "rest:ref" refClass refOutcome ]
            @ (conditional |> Option.map (fun (o, c) -> [ record "conditional-ref-if-none-match" "rest:ref" c o ]) |> Option.defaultValue [])
            @ [ record (sprintf "lost-answer-%s" (if applied then "applied" else "not-applied")) "graphql:createCommitOnBranch" lost lostOutcome ]
            @ (permission |> Option.map (fun (o, c, _) -> [ record "permission-read-token-commit" "graphql:createCommitOnBranch" c o ]) |> Option.defaultValue [])

        let secrets = [ LiveGitHub.requireSecret "CAUSA_WRITE_TOKEN" ] @ (permission |> Option.map (fun (_, _, t) -> [ t ]) |> Option.defaultValue [])
        write secrets recordings

        for r in recordings do
            Console.WriteLine(sprintf "INV22 %s: status %d -> %s" r.Name r.Status r.MatrixClass)

        // Assertions on what GitHub answered.
        let accepted = classes |> List.filter (fun c -> c.Class = Accepted)
        Assert.Equal(1, accepted.Length)

        for c in classes do
            Assert.True(
                List.contains c.Class [ Accepted; GhStaleHead; GhRateSecondary ],
                sprintf "race answer classified %s; every refusal must be a stale head (or an observed secondary limit), never permission or unknown" (classId c.Class)
            )

        Assert.Equal<MatrixClass list>([ Accepted; GhStaleHead; GhStaleHead ], fakeClasses)
        Assert.True(classes |> List.forall (fun c -> List.contains c.Class fakeClasses || c.Class = GhRateSecondary), "fake/real divergence")
        Assert.Equal(GhAuth, auth.Class)
        Assert.Equal(GhValidation, dup.Class)
        Assert.Equal(Accepted, refClass.Class)
        Assert.True(lost.Class = GhNetworkAfter || lost.Class = Accepted, sprintf "lost answer classified %s" (classId lost.Class))

        match permission with
        | Some(_, c, _) -> Assert.True(List.contains c.Class [ GhPermission; GhNotFound ], sprintf "read token commit classified %s" (classId c.Class))
        | None -> LiveGitHub.requireSecret "CAUSA_GITHUB_TOKEN" |> ignore
