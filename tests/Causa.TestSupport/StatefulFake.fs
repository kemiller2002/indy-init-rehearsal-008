// Owner: persistence. T18: the stateful GitHub fake (PERSIST-025-O2, PF-013), built from the recorded answer forms.
namespace Causa.TestSupport

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Causa.Persistence

/// One commit of the fake's object store: real ancestry through Parents.
type FakeCommit =
    { Oid: CommitOid
      Parents: CommitOid list
      Tree: Map<string, byte[]>
      Headline: string }

/// What the fake does with the next request it receives (consumed in order; when none is queued it answers).
type FakeDirective =
    /// Apply the request and answer it.
    | Deliver
    /// Drop the request before applying it: nothing changes on the "server" (PERSIST-025-O2).
    | LoseBeforeApplying of TransportFailure
    /// Apply the request, then lose the answer: the commit exists but the caller never hears (PERSIST-025-O2).
    | LoseAfterApplying of TransportFailure
    /// Answer with a recorded form without applying anything (401, 403, rate limits, 5xx ...).
    | AnswerWith of Recording
    /// Another writer commits these files to the branch first, then the request is handled.
    | OtherWriterFirst of branch: string * files: (string * byte[]) list

type FakeState =
    { Owner: string
      Name: string
      Commits: Map<string, FakeCommit>
      Branches: Map<string, CommitOid>
      Script: FakeDirective list
      /// Every request handled, with its outcome, oldest first (for assertions on request counts and bounds).
      Log: (HttpRequest * HttpOutcome) list }

/// A pure model of the GitHub endpoints persistence uses. `handle` is a function of state and request; the effectful
/// wrapper `executor` exists only at the test edge.
[<RequireQualifiedAccess>]
module StatefulFake =

    let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

    let private oidOf (parents: CommitOid list) (tree: Map<string, byte[]>) (headline: string) : CommitOid =
        let text =
            [ yield! parents |> List.map (fun (CommitOid p) -> "parent " + p)
              yield! tree |> Map.toList |> List.map (fun (path, bytes) -> path + " " + hex (Security.Cryptography.SHA1.HashData bytes))
              yield "message " + headline ]
            |> String.concat "\n"

        CommitOid(hex (Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes text)))

    let private key (CommitOid oid) = oid

    /// A repository with one root commit on `branch` holding `files`.
    let create (owner: string) (name: string) (branch: string) (files: (string * byte[]) list) : FakeState =
        let tree = Map.ofList files
        let root = { Oid = oidOf [] tree "root"; Parents = []; Tree = tree; Headline = "root" }

        { Owner = owner
          Name = name
          Commits = Map.ofList [ key root.Oid, root ]
          Branches = Map.ofList [ branch, root.Oid ]
          Script = []
          Log = [] }

    let head (branch: string) (state: FakeState) : CommitOid option = state.Branches.TryFind branch

    let withScript (directives: FakeDirective list) (state: FakeState) = { state with Script = state.Script @ directives }

    /// Commit `files` on top of the branch head (no guard): a simulated other writer.
    let commitOnBranch (branch: string) (headline: string) (files: (string * byte[]) list) (state: FakeState) : FakeState * CommitOid =
        let parent = state.Commits[key state.Branches[branch]]
        let tree = files |> List.fold (fun t (p, b) -> Map.add p b t) parent.Tree
        let commit = { Oid = oidOf [ parent.Oid ] tree headline; Parents = [ parent.Oid ]; Tree = tree; Headline = headline }

        { state with
            Commits = Map.add (key commit.Oid) commit state.Commits
            Branches = Map.add branch commit.Oid state.Branches },
        commit.Oid

    /// Rewrite the branch to an unrelated commit (force-push), for GH-HISTORY-REWRITTEN.
    let rewriteBranch (branch: string) (files: (string * byte[]) list) (state: FakeState) : FakeState * CommitOid =
        let tree = Map.ofList files
        let commit = { Oid = oidOf [] tree "rewritten"; Parents = []; Tree = tree; Headline = "rewritten" }

        { state with
            Commits = Map.add (key commit.Oid) commit state.Commits
            Branches = Map.add branch commit.Oid state.Branches },
        commit.Oid

    /// All ancestors of a commit including itself.
    let ancestors (oid: CommitOid) (state: FakeState) : Set<string> =
        let rec walk (seen: Set<string>) (frontier: CommitOid list) =
            match frontier with
            | [] -> seen
            | c :: rest when seen.Contains(key c) -> walk seen rest
            | c :: rest ->
                let parents = state.Commits.TryFind(key c) |> Option.map _.Parents |> Option.defaultValue []
                walk (seen.Add(key c)) (parents @ rest)

        walk Set.empty [ oid ]

    let private rateHeaders = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-remaining", "4999" ]

    let private json (id: RequestId) (status: int) (body: JsonNode) =
        Answered(id, status, rateHeaders, body.ToJsonString())

    let private message (id: RequestId) (status: int) (text: string) =
        let o = JsonObject()
        o["message"] <- JsonValue.Create text
        json id status o

    let private pathOf (url: string) =
        let uri = Uri url
        uri.AbsolutePath, uri.Query.TrimStart '?'

    let private queryValue (name: string) (query: string) =
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun kv ->
            match kv.Split('=', 2) with
            | [| k; v |] when k = name -> Some(Uri.UnescapeDataString v)
            | _ -> None)

    let private createCommit (id: RequestId) (body: string) (state: FakeState) : FakeState * HttpOutcome =
        let input = (JsonNode.Parse body |> nonNull)["variables"] |> nonNull |> fun v -> v["input"] |> nonNull
        let branch = input["branch"] |> nonNull |> fun b -> (b["branchName"] |> nonNull).GetValue<string>()
        let expected = CommitOid((input["expectedHeadOid"] |> nonNull).GetValue<string>())
        let headline = input["message"] |> nonNull |> fun m -> (m["headline"] |> nonNull).GetValue<string>()

        // GitHub keeps the whole message: headline, blank line, body (T19: the write-marker trailer is in the body).
        let headline =
            match input["message"] |> nonNull |> fun m -> m["body"] with
            | null -> headline
            | body -> headline + "\n\n" + body.GetValue<string>()

        let additions =
            match input["fileChanges"] |> Option.ofObj |> Option.bind (fun f -> f["additions"] |> Option.ofObj) with
            | Some(:? JsonArray as arr) ->
                [ for a in arr ->
                      let a = nonNull a
                      (a["path"] |> nonNull).GetValue<string>(), Convert.FromBase64String((a["contents"] |> nonNull).GetValue<string>()) ]
            | _ -> []

        match state.Branches.TryFind branch with
        | None ->
            state,
            Answered(id, 200, rateHeaders, """{"data":{"createCommitOnBranch":null},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Ref"}]}""")
        | Some actual when actual <> expected ->
            // The refusal form comes from the recordings (GH-STALE-HEAD, FORBIDDEN moved-head form).
            let (CommitOid a), (CommitOid e) = actual, expected
            state, Answered(id, 200, rateHeaders, Recordings.movedHeadBody a e)
        | Some _ ->
            let next, oid = commitOnBranch branch headline additions state
            let o = JsonObject()
            let data = JsonObject()
            let cc = JsonObject()
            let commit = JsonObject()
            commit["oid"] <- JsonValue.Create(key oid)
            cc["commit"] <- commit
            data["createCommitOnBranch"] <- cc
            o["data"] <- data
            next, json id 200 o

    let private compare (id: RequestId) (baseOid: string) (headOid: string) (query: string) (state: FakeState) : HttpOutcome =
        match state.Commits.TryFind baseOid, state.Commits.TryFind headOid with
        | Some b, Some h ->
            let fromBase = ancestors b.Oid state
            let fromHead = ancestors h.Oid state
            let ahead = Set.difference fromHead fromBase
            let behind = Set.difference fromBase fromHead

            let status =
                match ahead.IsEmpty, behind.IsEmpty with
                | true, true -> "identical"
                | false, true -> "ahead"
                | true, false -> "behind"
                | false, false -> "diverged"

            // Oldest first: order the ahead commits by depth from the head (deepest first).
            let rec chain (c: CommitOid) acc =
                if ahead.Contains(key c) then
                    match state.Commits[key c].Parents with
                    | p :: _ -> chain p (c :: acc)
                    | [] -> c :: acc
                else
                    acc

            let ordered = chain h.Oid [] |> List.truncate 250
            let perPage = queryValue "per_page" query |> Option.map int |> Option.defaultValue 250
            let page = queryValue "page" query |> Option.map int |> Option.defaultValue 1
            let listed = ordered |> List.skip (min ordered.Length ((page - 1) * perPage)) |> List.truncate perPage
            let o = JsonObject()
            o["status"] <- JsonValue.Create status
            o["ahead_by"] <- JsonValue.Create ahead.Count
            o["behind_by"] <- JsonValue.Create behind.Count
            o["total_commits"] <- JsonValue.Create ahead.Count
            let arr = JsonArray()

            for c in listed do
                let item = JsonObject()
                item["sha"] <- JsonValue.Create(key c)
                let commit = JsonObject()
                commit["message"] <- JsonValue.Create state.Commits[key c].Headline
                item["commit"] <- commit
                arr.Add item

            o["commits"] <- arr

            // The files changed between base and head, as GitHub lists them (T19 compares the investigation's files).
            let changed =
                Set.union (Set.ofSeq b.Tree.Keys) (Set.ofSeq h.Tree.Keys)
                |> Set.filter (fun p ->
                    match b.Tree.TryFind p, h.Tree.TryFind p with
                    | Some x, Some y -> x <> y
                    | _ -> true)

            let files = JsonArray()

            for p in changed do
                let f = JsonObject()
                f["filename"] <- JsonValue.Create p
                files.Add f

            o["files"] <- files
            json id 200 o
        | _ -> message id 404 "Not Found"

    /// T19: the GraphQL file-set query (GitHubRequests.fileSetAtBranch / fileSetAtCommit): the commit and three files.
    let private fileSetQuery (id: RequestId) (body: string) (state: FakeState) : HttpOutcome =
        let vars = (JsonNode.Parse body |> nonNull)["variables"] |> nonNull
        let text (name: string) = vars[name] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())

        let commit =
            match text "oid", text "ref" with
            | Some oid, _ -> state.Commits.TryFind oid
            | None, Some r -> state.Branches.TryFind(r.Replace("refs/heads/", "")) |> Option.bind (fun (CommitOid o) -> state.Commits.TryFind o)
            | None, None -> None

        let repo = JsonObject()
        let errors = JsonArray()

        match commit with
        | Some c ->
            let target = JsonObject()
            target["oid"] <- JsonValue.Create(key c.Oid)
            // CR-T21-01 (PF-013): GitHub answers `file(path:)` for a path the commit lacks with a null field and a
            // NOT_FOUND error naming it, never a bare null; the fake answers the same way.
            let targetPath =
                match text "oid" with
                | Some _ -> [ "repository"; "object" ]
                | None -> [ "repository"; "ref"; "target" ]

            for slot in [ "manifest"; "events"; "snapshot" ] do
                match text slot with
                | Some p ->
                    match c.Tree.TryFind p with
                    | Some bytes ->
                        let blob = JsonObject()
                        blob["text"] <- JsonValue.Create(Encoding.UTF8.GetString bytes)
                        blob["isTruncated"] <- JsonValue.Create false
                        let entry = JsonObject()
                        entry["object"] <- blob
                        target[slot] <- entry
                    | None ->
                        target[slot] <- null
                        errors.Add(Recordings.missingPathError (targetPath @ [ slot ]) p)
                | None -> target[slot] <- null

            match text "oid" with
            | Some _ -> repo["object"] <- target
            | None ->
                let r = JsonObject()
                r["target"] <- target
                repo["ref"] <- r
        | None ->
            repo["ref"] <- null
            repo["object"] <- null

        let data = JsonObject()
        data["repository"] <- repo
        let o = JsonObject()
        o["data"] <- data

        if errors.Count > 0 then
            o["errors"] <- errors

        json id 200 o

    let private apply (request: HttpRequest) (state: FakeState) : FakeState * HttpOutcome =
        let id = request.Id
        let path, query = pathOf request.Url
        let repoPrefix = sprintf "/repos/%s/%s" state.Owner state.Name

        let rest (p: string) = if p.StartsWith repoPrefix then Some(p.Substring repoPrefix.Length) else None

        match request.Method, path, request.Body with
        | Post, "/graphql", Some body when body.Contains "createCommitOnBranch" -> createCommit id body state
        | Post, "/graphql", Some body -> state, fileSetQuery id body state
        | Get, p, _ when rest p = Some "" ->
            let o = JsonObject()
            o["full_name"] <- JsonValue.Create(state.Owner + "/" + state.Name)
            let perms = JsonObject()
            perms["pull"] <- JsonValue.Create true
            perms["push"] <- JsonValue.Create true
            o["permissions"] <- perms
            state, json id 200 o
        | Get, p, _ when (rest p |> Option.exists _.StartsWith("/git/ref/heads/")) ->
            let branch = Uri.UnescapeDataString((rest p).Value.Substring "/git/ref/heads/".Length)

            match state.Branches.TryFind branch with
            | Some oid ->
                let o = JsonObject()
                o["ref"] <- JsonValue.Create("refs/heads/" + branch)
                let obj = JsonObject()
                obj["sha"] <- JsonValue.Create(key oid)
                obj["type"] <- JsonValue.Create "commit"
                o["object"] <- obj
                state, json id 200 o
            | None -> state, message id 404 "Not Found"
        | Get, p, _ when (rest p |> Option.exists _.StartsWith("/contents/")) ->
            let file = (rest p).Value.Substring "/contents/".Length |> Uri.UnescapeDataString
            let at = queryValue "ref" query |> Option.defaultValue ""

            match state.Commits.TryFind at |> Option.bind (fun c -> c.Tree.TryFind file) with
            | Some bytes ->
                let o = JsonObject()
                o["type"] <- JsonValue.Create "file"
                o["path"] <- JsonValue.Create file
                o["encoding"] <- JsonValue.Create "base64"
                o["size"] <- JsonValue.Create bytes.Length
                o["content"] <- JsonValue.Create(Convert.ToBase64String bytes)
                state, json id 200 o
            | None -> state, message id 404 "Not Found"
        | Get, p, _ when (rest p |> Option.exists _.StartsWith("/compare/")) ->
            match (rest p).Value.Substring("/compare/".Length).Split("...") with
            | [| b; h |] -> state, compare id (Uri.UnescapeDataString b) (Uri.UnescapeDataString h) query state
            | _ -> state, message id 404 "Not Found"
        | Post, p, Some body when rest p = Some "/git/refs" ->
            let node = JsonNode.Parse body |> nonNull
            let refName = (node["ref"] |> nonNull).GetValue<string>()
            let sha = (node["sha"] |> nonNull).GetValue<string>()
            let branch = refName.Replace("refs/heads/", "")

            if state.Branches.ContainsKey branch then
                state, message id 422 "Reference already exists"
            elif not (state.Commits.ContainsKey sha) then
                state, message id 422 "Object does not exist"
            else
                { state with Branches = Map.add branch (CommitOid sha) state.Branches }, message id 201 "created"
        | _ -> state, message id 404 "Not Found"

    /// Handle one request: consume the next directive, apply it, record it. Pure.
    let handle (state: FakeState) (request: HttpRequest) : FakeState * HttpOutcome =
        let directive, remaining =
            match state.Script with
            | d :: rest -> d, rest
            | [] -> Deliver, []

        let state = { state with Script = remaining }

        let next, outcome =
            match directive with
            | Deliver -> apply request state
            | LoseBeforeApplying failure -> state, NotAnswered(request.Id, failure)
            | LoseAfterApplying failure -> fst (apply request state), NotAnswered(request.Id, failure)
            | AnswerWith recording -> state, Recordings.asOutcome request.Id recording
            | OtherWriterFirst(branch, files) ->
                let afterOther, _ = commitOnBranch branch "other writer" files state
                apply request afterOther

        { next with Log = next.Log @ [ request, outcome ] }, outcome

    /// The test-edge wrapper: an Execute (IF41-WIRE) over a mutable cell holding the pure state.
    type Server(initial: FakeState) =
        let mutable state = initial
        member _.State = state
        member _.Script(directives: FakeDirective list) = state <- withScript directives state
        member _.Mutate(f: FakeState -> FakeState * CommitOid) =
            let next, oid = f state
            state <- next
            oid
        member _.Execute: Execute =
            fun request ->
                lock initial (fun () ->
                    let next, outcome = handle state request
                    state <- next
                    outcome)

/// The live side of the provider interface (PERSIST-025-O1): the same Execute, over HttpClient, used only by the
/// live suite in CI. It executes once and never retries; credentials stay inside the request's Authorization header.
[<RequireQualifiedAccess>]
module LiveGitHub =
    open System.Net.Http

    let private client = new HttpClient()

    /// Execute one request with a client-side deadline. A deadline that expires after sending is TimedOut (the
    /// request may have applied: the probe's lost-answer case).
    let executeWithin (deadline: TimeSpan) : Execute =
        fun request ->
            let method =
                match request.Method with
                | Get -> HttpMethod.Get
                | Post -> HttpMethod.Post
                | Put -> HttpMethod.Put
                | Patch -> HttpMethod.Patch

            use message = new HttpRequestMessage(method, request.Url)

            for (k, v) in request.Headers do
                message.Headers.TryAddWithoutValidation(k, v) |> ignore

            request.Body
            |> Option.iter (fun b -> message.Content <- new StringContent(b, Encoding.UTF8, "application/json"))

            use cts = new Threading.CancellationTokenSource(deadline)

            try
                use response = client.Send(message, cts.Token)
                let body = (new IO.StreamReader(response.Content.ReadAsStream())).ReadToEnd()

                let headers =
                    [ for h in response.Headers do
                          yield h.Key, String.Join(",", h.Value)
                      for h in response.Content.Headers do
                          yield h.Key, String.Join(",", h.Value) ]

                Answered(request.Id, int response.StatusCode, headers, body)
            with
            | :? OperationCanceledException -> NotAnswered(request.Id, TimedOut)
            | :? HttpRequestException as e when e.HttpRequestError = HttpRequestError.ConnectionError ->
                NotAnswered(request.Id, FailedBeforeSending)
            | :? HttpRequestException -> NotAnswered(request.Id, LostAfterSending)

    let execute: Execute = executeWithin (TimeSpan.FromSeconds 30.0)

    /// The secret named, or BLOCKED (PF-015: a missing secret is BLOCKED, never PASS). The value is never printed.
    let requireSecret (name: string) : string =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> failwithf "BLOCKED: secret %s is not available to this run; live verification did not run (PF-015, PF-020)." name
        | v -> v

/// Where a run's answers came from. A fake pass never reads as live (PERSIST-025-O4, UI-015, PF-020).
type ProviderMode =
    | FakeProvider
    | LiveProvider of repository: string

type ConformanceStep = { Step: string; Class: string; Commit: string option }

type ConformanceRun =
    { Mode: ProviderMode
      Steps: ConformanceStep list
      /// Commits this run created (on the live provider: real commits on GitHub, PERSIST-025-O5).
      Created: string list }

/// The provider-conformance scenario, written once against the provider interface (Execute) and run against the
/// stateful fake and the real test repository alike (PERSIST-025-O1..O3, PF-011 level 3).
[<RequireQualifiedAccess>]
module Conformance =
    open Causa.Persistence.ErrorMatrix

    let private field (path: string list) (body: string) : string option =
        try
            let rec go (node: JsonNode | null) (path: string list) =
                match node, path with
                | null, _ -> None
                | n, [] -> Some(n.ToString())
                | n, p :: rest ->
                    match n with
                    | :? JsonObject as o -> go o[p] rest
                    | _ -> None

            go (JsonNode.Parse body) path
        with _ ->
            None

    let commitOid (outcome: HttpOutcome) : string option =
        match outcome with
        | Answered(_, _, _, body) -> field [ "data"; "createCommitOnBranch"; "commit"; "oid" ] body
        | NotAnswered _ -> None

    let headOid (outcome: HttpOutcome) : string option =
        match outcome with
        | Answered(_, 200, _, body) -> field [ "object"; "sha" ] body
        | _ -> None

    let mutable private nextId = 1000L

    let freshId () = RequestId(Threading.Interlocked.Increment(&nextId))

    let evidence (run: ConformanceRun) : string =
        let mode =
            match run.Mode with
            | FakeProvider -> "provider=stateful-fake (NOT live verification)"
            | LiveProvider repo -> sprintf "provider=live-github repository=%s" repo

        let steps = run.Steps |> List.map (fun s -> sprintf "%s=%s%s" s.Step s.Class (s.Commit |> Option.map (sprintf "@%s") |> Option.defaultValue ""))
        String.concat "; " (mode :: steps @ [ "created=" + String.concat "," run.Created ])

    /// Commit, then a second commit guarded by the same (now stale) head, then read head and compare.
    let run (mode: ProviderMode) (execute: Execute) (connection: ConnectionContext) (tag: string) : ConformanceRun =
        let head0 = execute (GitHubRequests.branchHead (freshId ()) connection)
        let h0 = headOid head0 |> Option.defaultWith (fun () -> failwithf "branch head unreadable: %A" (classify (Read "rest:ref") head0).Result)
        let file name = RepoPath(sprintf "probes/%s/%s.txt" tag name), Encoding.UTF8.GetBytes(sprintf "%s %s" tag name)
        let first = execute (GitHubRequests.createCommitOnBranch (freshId ()) connection (CommitOid h0) (tag + " first") [ file "first" ])
        let firstClass = classify (Mutation(CommitOid h0)) first
        let a = commitOid first
        let second = execute (GitHubRequests.createCommitOnBranch (freshId ()) connection (CommitOid h0) (tag + " second") [ file "second" ])
        let secondClass = classify (Mutation(CommitOid h0)) second
        let head1 = execute (GitHubRequests.branchHead (freshId ()) connection)
        let h1 = headOid head1

        let compareStep =
            match a with
            | Some a ->
                let c = execute (GitHubRequests.compare (freshId ()) connection (CommitOid h0) (CommitOid a) 1)

                let status =
                    match c with
                    | Answered(_, 200, _, body) -> field [ "status" ] body |> Option.defaultValue "?"
                    | _ -> "unreadable"

                [ { Step = "compare-h0-first"; Class = classId (classify (Compare(CommitOid h0)) c).Class + ":" + status; Commit = None } ]
            | None -> []

        { Mode = mode
          Steps =
            [ { Step = "first-commit"; Class = classId firstClass.Class; Commit = a }
              { Step = "second-commit-stale"; Class = classId secondClass.Class; Commit = commitOid second }
              { Step = "head-after"; Class = (if h1 = a then "HEAD=FIRST" else "HEAD-OTHER"); Commit = h1 } ]
            @ compareStep
          Created = List.choose id [ a; commitOid second ] }

    /// Live setup: a fresh branch from the default branch head (one per test class).
    let freshBranch (execute: Execute) (connection: ConnectionContext) (prefix: string) : ConnectionContext =
        let repo = execute (GitHubRequests.repository (freshId ()) connection)

        let defaultBranch =
            match repo with
            | Answered(_, 200, _, body) -> field [ "default_branch" ] body |> Option.defaultValue "main"
            | other -> failwithf "repository unreadable: %A" (classify ConnectRepository other).Result

        let onDefault = { connection with Repository = { connection.Repository with Branch = defaultBranch } }
        let head = execute (GitHubRequests.branchHead (freshId ()) onDefault) |> headOid |> Option.defaultWith (fun () -> failwith "default branch head unreadable")
        let runId = Environment.GetEnvironmentVariable "GITHUB_RUN_ID" |> Option.ofObj |> Option.defaultValue "local"
        let branch = sprintf "%s/%s-%d" prefix runId (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

        match execute (GitHubRequests.createBranch (freshId ()) onDefault branch (CommitOid head)) with
        | Answered(_, 201, _, _) -> { connection with Repository = { connection.Repository with Branch = branch } }
        | other -> failwithf "could not create probe branch: %A" (classify (Read "rest:ref") other).Result

    /// The write-test repository connection from the CI secret, or BLOCKED.
    let liveWriteConnection () : ConnectionContext =
        let token = LiveGitHub.requireSecret "CAUSA_WRITE_TOKEN"

        let owner, name =
            match Environment.GetEnvironmentVariable "CAUSA_WRITE_REPOSITORY" with
            | null
            | "" -> "kemiller2002", "causa-write-test"
            | v ->
                match v.Split '/' with
                | [| o; n |] -> o, n
                | _ -> failwithf "CAUSA_WRITE_REPOSITORY must be owner/name"

        { Repository = { Owner = owner; Name = name; Branch = "main" }
          Credential = Credential.create token }

    let fakeConnection: ConnectionContext =
        { Repository = { Owner = "fake-owner"; Name = "fake-repo"; Branch = "main" }
          Credential = Credential.create "fake-credential" }

    let newFake () = StatefulFake.Server(StatefulFake.create "fake-owner" "fake-repo" "main" [ "README.md", Encoding.UTF8.GetBytes "fake" ])

/// T19: the stateful fake as the invariant suite's FakeGitHub needs it (INV-09): one branch holding one verified
/// investigation, an external writer touching other files, and the commit graph.
[<RequireQualifiedAccess>]
module InvariantFake =
    type Handle =
        { Server: StatefulFake.Server
          Branch: string
          Investigation: Causa.Domain.InvestigationId }

    let create (owner: string) (name: string) (branch: string) (investigation: Causa.Domain.InvestigationId) : Handle =
        let event: Causa.Domain.EventEnvelope =
            { EventId = Causa.Domain.EventId(Guid "00000000-0000-4000-8000-00000000f001")
              Investigation = investigation
              Version = Causa.Domain.AggregateVersion 1L
              Payload = Causa.Domain.EventPayload "InvestigationStarted" }

        let files = StorageV1.encodeHistory investigation [ event ]
        let file (RepoPath p) bytes = p, bytes

        let tree =
            [ file (StoragePaths.manifest investigation) files.Manifest
              file (StoragePaths.events investigation) files.Events
              file (StoragePaths.snapshot investigation) files.Snapshot ]

        { Server = StatefulFake.Server(StatefulFake.create owner name branch tree)
          Branch = branch
          Investigation = investigation }

    let head (h: Handle) : CommitOid = h.Server.State.Branches[h.Branch]

    /// The investigation verified at the branch head, as a load reads it.
    let openAtHead (h: Handle) : VerifiedInvestigation =
        let at = head h
        let (CommitOid oid) = at
        let tree = h.Server.State.Commits[oid].Tree
        let read (RepoPath p) = tree[p]

        let files =
            { Manifest = read (StoragePaths.manifest h.Investigation)
              Events = read (StoragePaths.events h.Investigation)
              Snapshot = read (StoragePaths.snapshot h.Investigation) }

        match StorageV1.verify at files with
        | Ok verified -> verified
        | Error failures -> failwithf "the fake's investigation does not verify: %A" failures

    let mutable private externalCount = 0

    /// Another writer commits an unrelated file.
    let externalCommit (h: Handle) : CommitOid =
        let n = Threading.Interlocked.Increment(&externalCount)
        h.Server.Mutate(StatefulFake.commitOnBranch h.Branch "external writer" [ sprintf "other/external-%d.txt" n, Encoding.UTF8.GetBytes(string n) ])

    /// Every commit except the root.
    let created (h: Handle) : CommitOid list =
        h.Server.State.Commits |> Map.toList |> List.filter (fun (_, c) -> not c.Parents.IsEmpty) |> List.map (fun (_, c) -> c.Oid)

    let reachableFromHead (h: Handle) (CommitOid oid) : bool =
        (StatefulFake.ancestors (head h) h.Server.State).Contains oid
