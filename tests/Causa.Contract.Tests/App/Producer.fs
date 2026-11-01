// Owner: ui (WI-R8-T22B). IF41-APP producer tests: the Engine record drives Connect, open, create, save, conflict and
// offline through the machines, with every request executed once by a host loop against the stateful GitHub fake
// (tests/Causa.TestSupport/StatefulFake.fs). No secret is used: the token below is a recognisable test value.
namespace Causa.Contract.Tests.App

open System
open System.IO
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App
open Causa.TestSupport

module Producer =
    let private token = "ghp_CausaEngineProducerTestToken0000000000"
    let private investigation = InvestigationId(Guid "00000000-0000-4000-8000-0000000000e1")
    let private repository = { Owner = "kemiller2002"; Name = "causa-write-test"; Branch = "main" }

    /// A deterministic tab id; each test gets a fresh marker source.
    let private config () = AppEngine.configFor (Guid "00000000-0000-4000-8000-0000000000ab")

    let private fake () = InvariantFake.create repository.Owner repository.Name repository.Branch investigation

    /// The host edge in a test: execute each Http effect exactly once and answer with Browser(HttpCompleted), until the
    /// engine asks for nothing more. Returns the model and every non-Http effect it asked for.
    let rec private drive (execute: Execute) (engine: Engine) (model: Model, effects: HostEffect list) : Model * HostEffect list =
        effects
        |> List.fold
            (fun (m, others) effect ->
                match effect with
                | Http request ->
                    let m', more = drive execute engine (engine.Update (Browser(HttpCompleted(execute request))) m)
                    m', others @ more
                | other -> m, others @ [ other ])
            (model, [])

    let private connect execute (engine: Engine) =
        let model, _ = engine.Init(Route "/")
        let credential = ConnectionMachine.credentialOf token |> Option.get
        drive execute engine (engine.Update (Connection(ConnectionInput.ConnectSubmitted(repository, credential))) model)

    let private edit (n: int) =
        { CommandId = Guid(sprintf "00000000-0000-4000-8000-%012d" (7000 + n))
          Investigation = investigation
          ExpectedVersion = None }

    let private stateName (model: Model) =
        model.Sessions
        |> Map.tryFind investigation
        |> Option.map (fun s -> SaveProcess.stateName s.State)
        |> Option.defaultValue "none"

    let private openIn execute (engine: Engine) model =
        drive execute engine (engine.Update (LoadStep(investigation, LoadInput.OpenRequested investigation)) model)

    [<Fact>]
    let ConnectOpenEditSaveIsConfirmedByReadBack () =
        let handle = fake ()
        let engine = AppEngine.create (config ())
        let execute = handle.Server.Execute
        let connected, _ = connect execute engine
        Assert.Equal(Connected true, connected.Connection)

        let opened, _ = openIn execute engine connected
        Assert.Equal("Saved", stateName opened)
        let headBefore = InvariantFake.head handle

        let edited, _ = drive execute engine (engine.Update (DomainCommand(edit 1)) opened)
        Assert.Equal("Unsaved", stateName edited)

        let saved, _ = drive execute engine (engine.Update (SessionInput(investigation, SaveInput.SaveRequested)) edited)

        match saved.Sessions[investigation].State with
        | SaveState.Saved(Confirmed confirmation) ->
            Assert.Equal(InvariantFake.head handle, confirmation.CommitOid)
            Assert.NotEqual(headBefore, confirmation.CommitOid)
        | other -> failwithf "the save was not confirmed by read-back: %A" other

        // A second save is based on the confirmed history: the branch then holds both edits.
        let edited2, _ = drive execute engine (engine.Update (DomainCommand(edit 2)) saved)
        let saved2, _ = drive execute engine (engine.Update (SessionInput(investigation, SaveInput.SaveRequested)) edited2)
        Assert.Equal("Saved", stateName saved2)
        let reread = InvariantFake.openAtHead handle
        Assert.Equal(3, reread.History.Length)
        Assert.Equal(2, InvariantFake.created handle |> List.length)

    [<Fact>]
    let CreateWritesOnlyAfterAnEmptyHeadReadAndOpensVerified () =
        let other = InvestigationId(Guid "00000000-0000-4000-8000-0000000000e2")
        let handle = fake ()
        let config = config ()
        let engine = AppEngine.create config
        let execute = handle.Server.Execute
        let connected, _ = connect execute engine

        let rec run (model: Model, creation: Creation, effects: HostEffect list) =
            match effects with
            | Http request :: _ ->
                let model', creation', more = Create.answer config creation (execute request) model

                match creation' with
                | CreateOpened _ -> fst (drive execute engine (model', more)), creation'
                | _ -> run (model', creation', more)
            | _ -> model, creation

        let created, creation = run (Create.start other connected)
        Assert.Equal(CreateOpened other, creation)

        match created.Sessions[other].State with
        | SaveState.Saved(VerifiedAt verified) -> Assert.Equal(InvariantFake.head handle, verified.ReadAt)
        | state -> failwithf "the created investigation was not opened verified: %A" state

        // Creating it again is refused from the head read: nothing more is written.
        let commits = InvariantFake.created handle |> List.length
        let _, again = run (Create.start other created)

        match again with
        | CreateRefused _ -> ()
        | unexpected -> failwithf "a second create was not refused: %A" unexpected

        Assert.Equal(commits, InvariantFake.created handle |> List.length)

    let private sessionIn (state: SaveState) (unsaved: CommandEnvelope list) =
        { Id = investigation
          BaseHead = CommitOid(String('a', 40))
          State = state
          Unsaved = unsaved
          AfterInFlight = []
          Outstanding = None
          Mutations = 1
          RebaseAttempts = 0
          ReconcileReads = 0 }

    let private connectedModel (engine: Engine) (session: InvestigationSession) =
        let model, _ = engine.Init(Route "/")

        { model with
            Connection = Connected true
            Context = Some { Repository = repository; Credential = Credential.create token }
            NextRequestId = RequestId 40L
            Sessions = Map.ofList [ investigation, session ] }

    /// Review T36 F-03(a): Read again from RemoteUnavailable / Unresolvable sends the read of the GitHub version with
    /// the identity the transition recorded, so the review never waits forever.
    [<Fact>]
    let ConflictReadAgainSendsTheRemoteRead () =
        let engine = AppEngine.create (config ())

        [ RemoteUnavailable; Unresolvable "the GitHub version does not verify" ]
        |> List.iter (fun view ->
            let model = connectedModel engine (sessionIn (SaveState.Conflict view) [ edit 1 ])
            let next, effects = engine.Update (ConflictStep(investigation, ConflictInput.ReadAgain)) model

            match next.Sessions[investigation].State, effects with
            | SaveState.Conflict(ReadingRemote id), [ Http request ] ->
                Assert.Equal(id, request.Id)
                Assert.Equal(RequestId 40L, id)
                Assert.Equal(RequestId 41L, next.NextRequestId)
                Assert.Contains("graphql", request.Url)
            | state, effects -> failwithf "Read again from %A gave %A with %A" view state effects)

    /// Review T36 F-03(b): Export downloads the session's unconfirmed commands, never an empty file.
    [<Fact>]
    let ExportCarriesTheUnsavedCommands () =
        let engine = AppEngine.create (config ())

        let session =
            { sessionIn (SaveState.Conflict(Unresolvable "missing")) [ edit 1; edit 2 ] with
                AfterInFlight = [ edit 3 ] }

        let _, effects = engine.Update (ConflictStep(investigation, ConflictInput.ExportChosen)) (connectedModel engine session)

        match effects with
        | [ Download(_, content) ] -> [ edit 1; edit 2; edit 3 ] |> List.iter (fun c -> Assert.Contains(c.CommandId.ToString "D", content))
        | other -> failwithf "Export asked for %A" other

    [<Fact>]
    let OfflineSaveSendsNothing () =
        let handle = fake ()
        let engine = AppEngine.create (config ())
        let execute = handle.Server.Execute
        let connected, _ = connect execute engine
        let opened, _ = openIn execute engine connected
        let offline, _ = engine.Update (Browser(ConnectivityChanged false)) opened
        let edited, _ = engine.Update (DomainCommand(edit 1)) offline
        let saved, effects = engine.Update (SessionInput(investigation, SaveInput.SaveRequested)) edited
        Assert.Equal("Offline", stateName saved)
        Assert.DoesNotContain(effects, (fun e -> match e with Http _ -> true | _ -> false))
        Assert.Empty(InvariantFake.created handle)

    [<Fact>]
    let CredentialNeverRendered () =
        let handle = fake ()
        let engine = AppEngine.create (config ())
        let execute = handle.Server.Execute
        let connected, _ = connect execute engine
        let opened, _ = openIn execute engine connected
        Assert.DoesNotContain(token, sprintf "%A" opened)
        Assert.DoesNotContain(token, sprintf "%A" (Projection.value.Screen opened))
        Assert.DoesNotContain(token, sprintf "%A" (Projection.value.Header opened))
        // The credential appears only in the Authorization header of a request the host executes.
        let _, effects = engine.Update (LoadStep(investigation, LoadInput.RefreshRequested)) opened

        match effects with
        | [ Http request ] ->
            Assert.Contains(("Authorization", "Bearer " + token), request.Headers)
            Assert.DoesNotContain(token, request.Url)
            Assert.DoesNotContain(token, request.Body |> Option.defaultValue "")
        | other -> failwithf "refresh of a saved investigation asked for %A" other

    [<Fact>]
    let UpdateIsPure () =
        let constant: AppEngine.Config =
            { config () with NextMarker = fun () -> { Session = Guid.Empty; Sequence = 1L } }

        let engine = AppEngine.create constant
        let model = connectedModel engine (sessionIn SaveState.Unsaved [ edit 1 ])

        [ SessionInput(investigation, SaveInput.SaveRequested)
          DomainCommand(edit 2)
          Network NetworkInput.OfflineObserved
          ConflictStep(investigation, ConflictInput.ReadAgain)
          Navigate(Route "/#/investigations") ]
        |> List.iter (fun msg -> Assert.Equal<Model * HostEffect list>(engine.Update msg model, engine.Update msg model))

        Assert.Equal<Model * HostEffect list>(engine.Init(Route "/"), engine.Init(Route "/"))

    [<Fact>]
    let LimenHttpEffectRoundTrip () =
        let request = GitHubRequests.repository (RequestId 7L) { Repository = repository; Credential = Credential.create token }
        use stream = new MemoryStream()

        (
            use w = new Utf8JsonWriter(stream)
            LimenHttp.writeRequest w request
        )

        use doc = JsonDocument.Parse(stream.ToArray())
        let root = doc.RootElement
        let text (name: string) = root.GetProperty(name).GetString()
        Assert.Equal("Http", text "kind")
        Assert.Equal("http-7", text "correlationId")
        Assert.Equal("GET", text "method")
        Assert.Equal("text", text "response")
        Assert.Equal("omit", text "credentials")

        let result (outcome: string) =
            use d = JsonDocument.Parse(sprintf """{"kind":"HttpResult","correlationId":"http-7","outcome":%s}""" outcome)
            LimenHttp.outcomeOf (d.RootElement.Clone())

        Assert.Equal(
            Some(Answered(RequestId 7L, 200, [ "x-ratelimit-remaining", "10" ], """{"a":1}""")),
            result """{"kind":"Success","status":200,"body":"{\"a\":1}","headers":{"X-RateLimit-Remaining":"10"}}"""
        )

        Assert.Equal(Some(NotAnswered(RequestId 7L, FailedBeforeSending)), result """{"kind":"Failure","reason":"network"}""")
        Assert.Equal(Some(NotAnswered(RequestId 7L, TimedOut)), result """{"kind":"OutcomeUnknown","reason":"timeout-after-dispatch"}""")
        Assert.Equal(Some(NotAnswered(RequestId 7L, LostAfterSending)), result """{"kind":"OutcomeUnknown","reason":"connection-lost"}""")
        Assert.Equal(Some(Answered(RequestId 7L, 502, [], "")), result """{"kind":"Failure","reason":"invalid-response","status":502}""")
