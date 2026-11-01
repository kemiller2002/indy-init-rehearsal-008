// Owner: persistence. T20: PERSIST-005 GitHub is the authoritative durable store, and the provider scenario the T20
// live suites share (connect, open, explicit create, save through SM-SAVE). Every test runs its scenario against the
// stateful fake first and then against the real write-test repository (kemiller2002/causa-write-test, CI live.yml);
// without CAUSA_WRITE_TOKEN the live half is BLOCKED (fails), never skipped (PF-015, PF-020).
namespace Causa.Acceptance.Live

open System
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.Persistence.ErrorMatrix
open Causa.TestSupport

/// One provider under test: the fake or the live repository on a fresh branch, with every request recorded.
type ProviderUnderTest =
    { Mode: ProviderMode
      Execute: Execute
      Connection: ConnectionContext
      Sent: unit -> HttpRequest list }

module ProviderScenario =

    let private recording (execute: Execute) : Execute * (unit -> HttpRequest list) =
        let sent = Collections.Concurrent.ConcurrentQueue<HttpRequest>()

        (fun request ->
            sent.Enqueue request
            execute request),
        (fun () -> List.ofSeq sent)

    let fake () : ProviderUnderTest =
        let server = Conformance.newFake ()
        let execute, sent = recording server.Execute

        { Mode = FakeProvider
          Execute = execute
          Connection = Conformance.fakeConnection
          Sent = sent }

    /// The live write-test repository on a fresh branch (setup requests are not recorded). BLOCKED without the secret.
    let live (prefix: string) : ProviderUnderTest =
        let connection = Conformance.freshBranch LiveGitHub.execute (Conformance.liveWriteConnection ()) prefix
        let execute, sent = recording LiveGitHub.execute

        { Mode = LiveProvider(connection.Repository.Owner + "/" + connection.Repository.Name)
          Execute = execute
          Connection = connection
          Sent = sent }

    /// The scenario against the fake, then against the live repository.
    let onBoth (prefix: string) (scenario: ProviderUnderTest -> unit) =
        scenario (fake ())
        scenario (live prefix)

    let context (p: ProviderUnderTest) : RequestContext =
        { Connection = p.Connection
          NextId = Conformance.freshId ()
          MutationInFlightElsewhere = false }

    let head (p: ProviderUnderTest) : CommitOid =
        match p.Execute(GitHubRequests.branchHead (Conformance.freshId ()) p.Connection) |> Conformance.headOid with
        | Some oid -> CommitOid oid
        | None -> failwithf "%A: branch head unreadable" p.Mode

    let connect (p: ProviderUnderTest) : ConnectResult =
        let id = Conformance.freshId ()
        GitHubProvider.connector.InterpretConnect id (p.Execute(GitHubProvider.connector.ConnectRequest id p.Connection))

    let openAtHead (p: ProviderUnderTest) (investigation: InvestigationId) : OpenResult =
        GitHubProvider.interpretOpen StorageV1.codec (p.Execute(GitHubProvider.openRequest (Conformance.freshId ()) p.Connection investigation))

    let readAt (p: ProviderUnderTest) (commit: CommitOid) (investigation: InvestigationId) : FileSetRead option =
        match
            p.Execute(
                GitHubRequests.fileSetAtCommit
                    (Conformance.freshId ())
                    p.Connection
                    commit
                    (StoragePaths.manifest investigation)
                    (StoragePaths.events investigation)
                    (StoragePaths.snapshot investigation)
            )
        with
        | Answered(_, 200, _, body) -> Answers.fileSet body
        | _ -> None

    let started (investigation: InvestigationId) : EventEnvelope list =
        [ { EventId = EventId(Guid.NewGuid())
            Investigation = investigation
            Version = AggregateVersion 1L
            Payload = EventPayload "InvestigationStarted" } ]

    let tab = Guid.NewGuid()
    let markers = Markers.forTab tab

    /// Connect, open (empty), explicit create; returns the created commit and the bytes written.
    let create (p: ProviderUnderTest) (investigation: InvestigationId) : CommitOid * FileSet =
        let write =
            match connect p with
            | ConnectAccepted w -> w
            | other -> failwithf "%A: connect failed: %A" p.Mode other

        let files = StorageV1.encodeHistory investigation (started investigation)

        let empty = openAtHead p investigation

        let expected =
            match empty with
            | AuthorizedEmpty h -> h
            | other -> failwithf "%A: expected an empty head, got %A" p.Mode other

        match GitHubProvider.createRequest (context p) write empty investigation files (markers ()) with
        | Ok request ->
            let answer = p.Execute request

            match (classify (Mutation expected) answer).Result, Conformance.commitOid answer with
            | Ok _, Some oid -> CommitOid oid, files
            | result, _ -> failwithf "%A: create failed: %A" p.Mode result
        | Error refusal -> failwithf "%A: create refused: %A" p.Mode refusal

    /// The events the "kernel" decides for the session's in-flight commands (the engine's part of FileSetOf).
    let decided (opened: VerifiedInvestigation) (session: InvestigationSession) : EventEnvelope list =
        let (AggregateVersion v) = opened.Investigation.Version

        session.Unsaved
        |> List.mapi (fun i c ->
            { EventId = EventId c.CommandId
              Investigation = session.Id
              Version = AggregateVersion(v + int64 i + 1L)
              Payload = EventPayload "InvestigationRenamed" })

    let command (investigation: InvestigationId) : CommandEnvelope =
        { CommandId = Guid.NewGuid(); Investigation = investigation; ExpectedVersion = None }

    /// One save through SM-SAVE against the provider: Edit each command, SaveRequested, then execute every request
    /// the machine emits and step what its answers mean, until no request is outstanding.
    let save (p: ProviderUnderTest) (opened: VerifiedInvestigation) (commands: CommandEnvelope list) : InvestigationSession * FileSet =
        let fileSetOf = GitHubProvider.fileSetOf StorageV1.codec opened (decided opened)
        let setup = GitHubProvider.saveSetup tab markers fileSetOf false
        let machine = SaveProcess.create setup

        let step session input =
            match machine.Step (context p) session input with
            | Ok next -> next
            | Error fault -> failwithf "%A: SM-SAVE fault %A" p.Mode fault

        let edited = commands |> List.fold (fun s c -> fst (step s (SaveInput.Edit c))) (machine.Initial opened)
        let written = fileSetOf edited

        let rec run (session: InvestigationSession) (effects: SaveEffect list) (budget: int) =
            match effects |> List.tryPick (function Send r -> Some r | _ -> None) with
            | _ when budget = 0 -> failwithf "%A: the save did not settle" p.Mode
            | None -> session
            | Some request ->
                match machine.Interpret (context p) session (p.Execute request) with
                | MachineEvent input ->
                    let next, more = step session input
                    run next more (budget - 1)
                | NextRequest(next, request) -> run next [ Send request ] (budget - 1)

        let session, effects = step edited SaveInput.SaveRequested
        run session effects 32, written

    let opened (result: OpenResult) : VerifiedInvestigation =
        match result with
        | Opened v -> v
        | other -> failwithf "expected Opened, got %A" other

    let isQuery (request: HttpRequest) =
        match request.Method, request.Body with
        | Get, _ -> true
        | Post, Some body -> request.Url = GitHubRequests.graphqlUrl && not (body.Contains "mutation")
        | _ -> false

    let mutations (p: ProviderUnderTest) =
        p.Sent() |> List.filter (fun r -> r.Body |> Option.exists (fun b -> b.Contains "createCommitOnBranch"))

module PERSIST005Tests =
    open ProviderScenario

    /// O1: the branch is the only durable store: what a save wrote is what a later read of the branch returns, and
    /// nothing outside GitHub holds it (a new provider handle with no shared memory reads it back).
    [<Fact>]
    let ``PERSIST-005-O1`` () =
        onBoth "probe/persist005-o1" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            let _, files = create p investigation
            let reopened = opened (openAtHead p investigation)
            Assert.Equal<byte[]>(files.Events, reopened.Bytes.Events)
            Assert.Equal<byte[]>(files.Manifest, reopened.Bytes.Manifest))

    /// O2: an investigation is opened by reading it at the branch head and verifying it as a reader would.
    [<Fact>]
    let ``PERSIST-005-O2`` () =
        onBoth "probe/persist005-o2" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            Assert.Equal(AuthorizedEmpty(head p), openAtHead p investigation)
            create p investigation |> ignore
            let v = opened (openAtHead p investigation)
            Assert.Equal(investigation, v.Investigation.Id)
            Assert.Equal(1, v.History.Length))

    /// O3: the tab records the commit it read as the base head.
    [<Fact>]
    let ``PERSIST-005-O3`` () =
        onBoth "probe/persist005-o3" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            let created, _ = create p investigation
            let v = opened (openAtHead p investigation)
            Assert.Equal(created, v.ReadAt)
            Assert.Equal(created, (SaveProcess.initial v).BaseHead))

    /// O4: the head read is an uncacheable POST that returns the oid it read at; file reads name their commit.
    [<Fact>]
    let ``PERSIST-005-O4`` () =
        onBoth "probe/persist005-o4" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            create p investigation |> ignore
            let v = opened (openAtHead p investigation)
            let openSent = p.Sent() |> List.filter (fun r -> r.Body |> Option.exists (fun b -> b.Contains "qualifiedName")) |> List.last
            Assert.Equal(Post, openSent.Method)
            let atCommit = (readAt p v.ReadAt investigation).Value
            Assert.Equal(v.ReadAt, atCommit.At)
            Assert.Equal<byte[]>(v.Bytes.Events, atCommit.Events.Value)
            let contents = GitHubRequests.contentsAt (RequestId 1L) p.Connection v.ReadAt (StoragePaths.events investigation)
            let (CommitOid c) = v.ReadAt
            Assert.Contains("ref=" + c, contents.Url))

    /// O5 (PAT-10): an investigation opened in a new tab equals its last confirmed commit on GitHub.
    [<Fact>]
    let ``PERSIST-005-O5`` () =
        onBoth "probe/persist005-o5" (fun p ->
            let investigation = InvestigationId(Guid.NewGuid())
            create p investigation |> ignore
            let first = opened (openAtHead p investigation)
            let session, written = save p first [ command investigation ]

            let confirmedAt =
                match session.State with
                | SaveState.Saved(Confirmed c) -> c.CommitOid
                | SaveState.Saved(VerifiedAt v) -> v.ReadAt
                | other -> failwithf "%A: expected Saved, got %A" p.Mode other

            let newTab = opened (openAtHead p investigation)
            Assert.Equal(confirmedAt, newTab.ReadAt)
            Assert.Equal<byte[]>(written.Events, newTab.Bytes.Events)
            Assert.Equal(2, newTab.History.Length))

    /// EXT-DATA-006-O1 (T20 completion): connect and read send queries only; the branch head does not move.
    [<Fact>]
    let ``Connect and read never mutate`` () =
        onBoth "probe/t20-connect-read" (fun p ->
            let before = head p

            match connect p with
            | ConnectAccepted _ -> ()
            | other -> failwithf "%A: connect failed: %A" p.Mode other

            openAtHead p (InvestigationId(Guid.NewGuid())) |> ignore
            Assert.All(p.Sent(), fun r -> Assert.True(isQuery r, r.Url))
            Assert.Equal(before, head p))
