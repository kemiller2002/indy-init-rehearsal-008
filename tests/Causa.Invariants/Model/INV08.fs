// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
// This is the first domain-owned file in the project's compile order, so the shared, pure test support lives here.
namespace Causa.Invariants

open System
open System.Text.Json
open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App

/// What INV-09 needs from the stateful fake: the host's Execute against one simulated branch, an external writer, and
/// the branch's commit graph. Test-side only; not a frozen interface.
type FakeGitHub =
    { Execute: Execute
      /// The verified investigation at the fake branch's current head, as a load would read it.
      Open: unit -> VerifiedInvestigation
      ExternalCommit: unit -> CommitOid
      Head: unit -> CommitOid
      Created: unit -> CommitOid list
      ReachableFromHead: CommitOid -> bool }

/// The implementations the invariant tests exercise. Each is bound by its owner when it exists (a one-line change of
/// None to Some, by pull request to this domain-owned file). Until then every test that needs it fails with a
/// RED BY DESIGN message: an unbound implementation is never treated as a pass and no test is skipped.
module Bindings =
    /// The F# kernel (IF41-DOMAIN), T13 (domain).
    let kernel: DomainKernel option = Some Kernel.domain
    /// SM-SAVE, SM-CONFLICT and SM-LOAD (IF41-SAVE), persistence.
    let saveMachine: SaveMachine option = Some(SaveProcess.forNewTab ())
    /// causa-storage v1.0.0 codec (IF41-STORAGE), persistence.
    let storageCodec: StorageCodec option = Some StorageV1.codec
    /// The github-error-matrix classifier (IF41-ERRORS), persistence, per answer context (a 404 means different
    /// things for Connect, a read, a compare and a mutation; review T35 R-01).
    let classifier: (ErrorMatrix.AnswerContext -> Classifier) option = Some ErrorMatrix.classifier
    /// The view projections (IF41-VIEW), ui.
    let projections: Projections option = Some Projection.value
    /// The App engine (IF41-APP), ui.
    let engine: Engine option = Some AppEngine.engine
    /// The stateful GitHub fake (tests/Causa.TestSupport/StatefulFake.fs), persistence: a fresh fake per call.
    let statefulFake: (unit -> FakeGitHub) option =
        Some(fun () ->
            let fake =
                Causa.TestSupport.InvariantFake.create "kemiller2002" "causa-data" "main" (InvestigationId(Guid "00000000-0000-4000-8000-000000000001"))

            { Execute = fake.Server.Execute
              Open = fun () -> Causa.TestSupport.InvariantFake.openAtHead fake
              ExternalCommit = fun () -> Causa.TestSupport.InvariantFake.externalCommit fake
              Head = fun () -> Causa.TestSupport.InvariantFake.head fake
              Created = fun () -> Causa.TestSupport.InvariantFake.created fake
              ReachableFromHead = Causa.TestSupport.InvariantFake.reachableFromHead fake })

module Require =
    let bound (contract: string) (owner: string) (binding: 'a option) : 'a =
        match binding with
        | Some implementation -> implementation
        | None ->
            failwithf
                "RED BY DESIGN: no implementation of %s is bound in Causa.Invariants.Bindings yet (owner: %s). This invariant is not verified until it is."
                contract
                owner

    let kernel () = bound "IF41-DOMAIN DomainKernel" "domain, T13" Bindings.kernel
    let saveMachine () = bound "IF41-SAVE SaveMachine" "persistence" Bindings.saveMachine
    let storageCodec () = bound "IF41-STORAGE StorageCodec" "persistence" Bindings.storageCodec
    let classifier () = bound "IF41-ERRORS Classifier" "persistence" Bindings.classifier
    let projections () = bound "IF41-VIEW Projections" "ui" Bindings.projections
    let engine () = bound "IF41-APP Engine" "ui" Bindings.engine
    let statefulFake () = bound "StatefulFake" "persistence" Bindings.statefulFake

/// A pure, seeded generator (SplitMix64): the same seed always yields the same case, so a failure is reproducible
/// from the seed it prints.
type Seed = Seed of uint64

type Gen<'a> = Gen of (Seed -> 'a * Seed)

[<RequireQualifiedAccess>]
module Gen =
    let private mix (z: uint64) =
        let z = (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        let z = (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z ^^^ (z >>> 31)

    let private next (Seed s) =
        let s' = s + 0x9E3779B97F4A7C15UL
        mix s', Seed s'

    let run (Gen g) seed = g seed
    let constant x = Gen(fun s -> x, s)
    let map f (Gen g) = Gen(fun s -> let a, s' = g s in f a, s')
    let bind f (Gen g) = Gen(fun s -> let a, s' = g s in run (f a) s')
    let map2 f a b = bind (fun x -> map (f x) b) a
    let map3 f a b c = bind (fun x -> map2 (f x) b c) a

    let intRange lo hi =
        Gen(fun s ->
            let v, s' = next s
            lo + int (v % uint64 (hi - lo + 1)), s')

    let oneOf (choices: 'a list) = intRange 0 (List.length choices - 1) |> map (fun i -> List.item i choices)

    let listOfLength n g =
        Gen(fun s -> List.init n id |> List.mapFold (fun seed _ -> run g seed) s)

    let listOf maxLength g = intRange 0 maxLength |> bind (fun n -> listOfLength n g)

    /// Weighted choice among generators.
    let frequency (weighted: (int * Gen<'a>) list) =
        let total = weighted |> List.sumBy fst

        intRange 1 total
        |> bind (fun pick ->
            let rec choose remaining items =
                match items with
                | [ (_, g) ] -> g
                | (w, g) :: rest -> if remaining <= w then g else choose (remaining - w) rest
                | [] -> invalidArg "weighted" "frequency needs at least one generator"

            choose pick weighted)

/// Runs a property over generated cases and fails with the first counterexample and its seed.
[<RequireQualifiedAccess>]
module Check =
    let check (cases: int) (gen: Gen<'a>) (property: 'a -> Result<unit, string>) : unit =
        let failure =
            Seq.init cases (fun i -> uint64 (i + 1))
            |> Seq.map (fun seed -> seed, fst (Gen.run gen (Seed seed)))
            |> Seq.tryPick (fun (seed, case) ->
                match property case with
                | Ok() -> None
                | Error reason -> Some(seed, case, reason))

        match failure with
        | None -> ()
        | Some(seed, case, reason) -> Assert.Fail(sprintf "counterexample (seed %d): %s\ncase: %A" seed reason case)

    /// The first error of a sequence of checks, or Ok.
    let all (results: Result<unit, string> seq) =
        results
        |> Seq.tryPick (function
            | Error e -> Some e
            | Ok() -> None)
        |> Option.map Error
        |> Option.defaultValue (Ok())

    let expect condition reason = if condition then Ok() else Error reason

/// Fixed, recognisable values. The test token is not a credential; INV-21 scans for it.
module Fixture =
    let private guid (n: int) = Guid(sprintf "00000000-0000-4000-8000-%012d" n)
    let investigationId = InvestigationId(guid 1)
    let head (n: int) = CommitOid(sprintf "%040x" n)
    let headText (CommitOid oid) = oid

    let fileSet (tag: string) : FileSet =
        { Manifest = Text.Encoding.UTF8.GetBytes("manifest:" + tag)
          Events = Text.Encoding.UTF8.GetBytes("events:" + tag)
          Snapshot = Text.Encoding.UTF8.GetBytes("snapshot:" + tag) }

    let event (version: int64) : EventEnvelope =
        { EventId = EventId(guid (1000 + int version))
          Investigation = investigationId
          Version = AggregateVersion version
          Payload = EventPayload "InvestigationOpened" }

    let verifiedAt (n: int) : VerifiedInvestigation =
        { Investigation = { Id = investigationId; Version = AggregateVersion 1L }
          History = [ event 1L ]
          ReadAt = head n
          Bytes = fileSet (string n) }

    let command (n: int) : CommandEnvelope =
        { CommandId = guid (5000 + n)
          Investigation = investigationId
          ExpectedVersion = None }

    let testToken = "ghp_CausaInvariantTestToken000000000000"

    let connection =
        { Repository = { Owner = "kemiller2002"; Name = "causa-data"; Branch = "main" }
          Credential = Credential.create testToken }

    let context (n: int64) =
        { Connection = connection
          NextId = RequestId n
          MutationInFlightElsewhere = false }

    let confirmation (n: int) =
        let written = fileSet ("written" + string n)

        Confirmation.ofReadBack investigationId (head n) DateTimeOffset.UnixEpoch written written
        |> Option.defaultWith (fun () -> failwith "a matching read-back must confirm")

    let policy: PolicySet = { Id = "causa-default" }
    let actor: ActorContext = { ActorId = "invariant-tests" }

/// Reading request bodies (GraphQL JSON) without depending on persistence's exact shape.
module Json =
    let rec private collect (name: string) (element: JsonElement) : string list =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.toList
            |> List.collect (fun p ->
                let here =
                    if p.Name = name && p.Value.ValueKind = JsonValueKind.String then
                        match p.Value.GetString() with
                        | null -> []
                        | text -> [ text ]
                    else
                        []

                here @ collect name p.Value)
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.toList |> List.collect (collect name)
        | _ -> []

    /// Every string value of a property with this name, anywhere in the body; [] when the body is not JSON.
    let strings (name: string) (body: string) : string list =
        try
            use doc = JsonDocument.Parse body
            collect name doc.RootElement
        with :? JsonException ->
            []

module Requests =
    let isMutation (request: HttpRequest) =
        request.Body |> Option.exists (fun body -> body.Contains "createCommitOnBranch")

    let sends (effects: SaveEffect list) =
        effects
        |> List.choose (function
            | Send r -> Some r
            | ScheduleReconcileRead _
            | Announce _
            | ExportCommands _ -> None)

    let mutations effects = sends effects |> List.filter isMutation

    let schedules (effects: SaveEffect list) =
        effects
        |> List.choose (function
            | ScheduleReconcileRead(_, ms) -> Some ms
            | Send _
            | Announce _
            | ExportCommands _ -> None)

/// Names of machine states, for messages and comparisons.
module Names =
    let save (state: SaveState) =
        match state with
        | SaveState.Saved _ -> "Saved"
        | SaveState.Unsaved -> "Unsaved"
        | SaveState.Saving -> "Saving"
        | SaveState.Offline -> "Offline"
        | SaveState.Conflict _ -> "Conflict"
        | SaveState.UnknownOutcome _ -> "UnknownOutcome"
        | SaveState.SaveFailed _ -> "SaveFailed"

/// A generated SM-SAVE move, resolved against the current session so that sequences reach deep states
/// (MutationSent names the outstanding request; confirmations name a fresh commit).
type Move =
    | Raw of SaveInput
    | SendOutstanding
    | ConfirmByReadBack of commit: int
    | ConfirmByMarker of commit: int
    | Supersede

type Step =
    { Move: Move
      Input: SaveInput
      Before: InvestigationSession
      Outcome: Result<InvestigationSession * SaveEffect list, SaveFault> }

module Drive =
    let resolve (session: InvestigationSession) (move: Move) : SaveInput =
        match move with
        | Raw input -> input
        | SendOutstanding ->
            match session.Outstanding with
            | Some(id, _) -> SaveInput.MutationSent id
            | None -> SaveInput.MutationSent(RequestId -1L)
        | ConfirmByReadBack n -> SaveInput.ReadBackMatched(Fixture.confirmation n)
        | ConfirmByMarker n -> SaveInput.ReconcileMarkerBytesMatch(Fixture.confirmation n)
        | Supersede -> SaveInput.SupersededReply(RequestId -7L)

    let after (step: Step) =
        match step.Outcome with
        | Ok(session, _) -> session
        | Error _ -> step.Before

    let effects (step: Step) =
        match step.Outcome with
        | Ok(_, effects) -> effects
        | Error _ -> []

    let start (machine: SaveMachine) = machine.Initial(Fixture.verifiedAt 1)

    /// Folds moves through Step from the initial session at head 1. A fault keeps the session (fail closed).
    let runFrom (contextOf: int64 -> RequestContext) (machine: SaveMachine) (initial: InvestigationSession) (moves: Move list) =
        moves
        |> List.mapFold
            (fun (session, n) move ->
                let input = resolve session move

                let step =
                    { Move = move
                      Input = input
                      Before = session
                      Outcome = machine.Step (contextOf n) session input }

                step, (after step, n + 1L))
            (initial, 100L)
        |> fst

    let run machine moves = runFrom Fixture.context machine (start machine) moves

    let finalSession (machine: SaveMachine) (steps: Step list) =
        steps |> List.tryLast |> Option.map after |> Option.defaultWith (fun () -> start machine)

/// Generators of SM-SAVE moves, weighted toward the path through Saving and UnknownOutcome.
module Moves =
    let private heads = [ 2; 3; 4; 5 ]

    let private providerErrors =
        [ AuthenticationFailed
          PermissionDenied "createCommitOnBranch"
          RateLimited(Secondary(Some 60))
          ValidationFailed [ "invalid" ]
          NetworkUnavailable LostAfterSending
          Unrecognised 502 ]

    let reply: Gen<Move> =
        Gen.frequency
            [ 3, Gen.intRange 10 40 |> Gen.map ConfirmByReadBack
              2, Gen.oneOf heads |> Gen.map (fun n -> Raw(SaveInput.ReadBackMismatched(Fixture.head n)))
              2, Gen.oneOf heads |> Gen.map (fun n -> Raw(SaveInput.StaleHeadUnrelated(Fixture.head n)))
              1, Gen.oneOf heads |> Gen.map (fun n -> Raw(SaveInput.StaleHeadSameInvestigation(Fixture.head n)))
              1, Gen.oneOf providerErrors |> Gen.map (fun e -> Raw(SaveInput.ProviderRefused e))
              1, Gen.constant (Raw SaveInput.CredentialRefused)
              3, Gen.constant (Raw SaveInput.AnswerLost)
              1, Gen.constant (Raw SaveInput.FailedBeforeSending)
              2, Gen.intRange 41 60 |> Gen.map ConfirmByMarker
              1, Gen.oneOf heads |> Gen.map (fun n -> Raw(SaveInput.ReconcileMarkerBytesDiffer(Fixture.head n)))
              2, Gen.constant (Raw SaveInput.ReconcileNothingWrittenHeadUnchanged)
              1, Gen.oneOf heads |> Gen.map (fun n -> Raw(SaveInput.ReconcileNothingWrittenHeadMoved(Fixture.head n)))
              1, Gen.constant (Raw SaveInput.ReconcileHistoryRewritten)
              2, Gen.constant (Raw SaveInput.ReconcileReadFailed)
              1, Gen.constant (Raw SaveInput.ReconcileReadsSpent)
              1, Gen.constant (Raw SaveInput.RebaseReadFailed)
              1, Gen.constant (Raw SaveInput.ReadBackReadFailed)
              2, Gen.constant Supersede ]

    let user: Gen<Move> =
        Gen.frequency
            [ 4, Gen.intRange 1 30 |> Gen.map (fun n -> Raw(SaveInput.Edit(Fixture.command n)))
              4, Gen.constant (Raw SaveInput.SaveRequested)
              4, Gen.constant SendOutstanding
              1, Gen.constant (Raw SaveInput.CheckAgain)
              1, Gen.constant (Raw SaveInput.WentOffline)
              1, Gen.constant (Raw SaveInput.WentOnline)
              1, Gen.constant (Raw SaveInput.QueuedSaveStarted) ]

    let move = Gen.frequency [ 1, user; 1, reply ]

    /// Starts with an edit, a save request and the mutation sent, so every sequence leaves Saved.
    let sequence =
        Gen.listOf 40 move
        |> Gen.map (fun rest ->
            Raw(SaveInput.Edit(Fixture.command 0)) :: Raw SaveInput.SaveRequested :: SendOutstanding :: rest)

/// Exploring SM-CONFLICT: every step reachable from ReadingRemote by bounded sequences of inputs.
module ConflictModel =
    let remote1 = Fixture.verifiedAt 21
    let remote2 = Fixture.verifiedAt 22

    let inputs: ConflictInput list =
        [ ConflictInput.RemoteVerified remote1
          ConflictInput.RemoteVerified remote2
          ConflictInput.RemoteReadFailed LostAfterSending
          ConflictInput.RemoteMissingOrInvalid None
          ConflictInput.ReadAgain
          ConflictInput.ReviewChosen
          ConflictInput.ReapplyChosen
          ConflictInput.ReapplyDone([ Fixture.command 1 ], [ { Command = Fixture.command 2; Reason = "blocked" } ])
          ConflictInput.DiscardChosen
          ConflictInput.DiscardConfirmed
          ConflictInput.DiscardCancelled
          ConflictInput.ExportChosen
          ConflictInput.SupersededReply(RequestId -7L) ]

    /// Every (view, input, outcome) reachable within depth steps of ReadingRemote.
    let explore (machine: SaveMachine) (depth: int) =
        let key (view: ConflictView) = sprintf "%A" view

        let rec go level frontier seen acc =
            if level = depth || List.isEmpty frontier then
                List.rev acc
            else
                let steps =
                    frontier
                    |> List.collect (fun view ->
                        inputs
                        |> List.mapi (fun i input ->
                            view, input, machine.StepConflict (Fixture.context (int64 (1000 * level + i))) view input))

                let next =
                    steps
                    |> List.choose (fun (_, _, outcome) ->
                        match outcome with
                        | Ok(view, _) -> Some view
                        | Error _ -> None)
                    |> List.distinctBy key
                    |> List.filter (fun v -> not (Set.contains (key v) seen))

                let seen' = next |> List.fold (fun s v -> Set.add (key v) s) seen
                go (level + 1) next seen' (List.rev steps @ acc)

        let start = ReadingRemote(RequestId 1L)
        go 0 [ start ] (Set.singleton (key start)) []

namespace Causa.Invariants.Model

open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-08 (PERSIST-012-O7): no action replaces the GitHub version with the tab's version; conflict resolution offers
/// only review, reapply through the engine, and confirmed discard.
module INV08 =
    let private sessionIn (machine: SaveMachine) view =
        { Drive.start machine with
            State = SaveState.Conflict view
            Unsaved = [ Fixture.command 1; Fixture.command 2 ] }

    [<Fact>]
    let ConflictActionsNeverOverwrite () =
        let machine = Require.saveMachine ()
        let reachable = ConflictModel.explore machine 5

        // Every reachable SM-CONFLICT step writes nothing to GitHub.
        reachable
        |> List.iter (fun (view, input, outcome) ->
            match outcome with
            | Ok(_, effects) ->
                Assert.True(
                    List.isEmpty (Requests.mutations effects),
                    sprintf "SM-CONFLICT %A on %A emitted a commit mutation" view input
                )
            | Error _ -> ())

        // Discard is reached only through its confirmation.
        reachable
        |> List.iter (fun (view, input, outcome) ->
            match outcome, view with
            | Ok(Discarded _, _), ConfirmingDiscard _
            | Ok(Discarded _, _), Discarded _ -> ()
            | Ok(Discarded _, _), _ -> Assert.Fail(sprintf "Discarded reached from %A by %A without confirmation" view input)
            | _ -> ())

        // Reapply rebases the unsaved commands on the GitHub version and saves nothing.
        let remote = ConflictModel.remote2

        match
            machine.Step (Fixture.context 1L) (sessionIn machine (Reapplied(remote, []))) (SaveInput.ConflictResolvedReapplied [ Fixture.command 1 ])
        with
        | Ok(session, effects) ->
            Assert.Equal(SaveState.Unsaved, session.State)
            Assert.Equal(remote.ReadAt, session.BaseHead)
            Assert.Empty(Requests.mutations effects)
        | Error fault -> Assert.Fail(sprintf "reapply resolution refused: %A" fault)

        // Confirmed discard takes the verified GitHub version; it never writes the tab's version.
        match machine.Step (Fixture.context 2L) (sessionIn machine (Discarded remote)) (SaveInput.ConflictResolvedDiscarded remote) with
        | Ok(session, effects) ->
            Assert.Equal(SaveState.Saved(VerifiedAt remote), session.State)
            Assert.Equal(remote.ReadAt, session.BaseHead)
            Assert.Empty(session.Unsaved)
            Assert.Empty(Requests.mutations effects)
        | Error fault -> Assert.Fail(sprintf "discard resolution refused: %A" fault)
