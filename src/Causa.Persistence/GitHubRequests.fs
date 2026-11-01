// Owner: persistence. T18: the GitHub requests the provider-conformance probe and the stateful fake share.
namespace Causa.Persistence

open System.Text.Json.Nodes

/// IF41-WIRE request builders. Each function is pure: it returns the request as data and never executes it. The
/// credential reaches the request only through Credential.authorization (PF-015, INV-21); no URL carries it.
[<RequireQualifiedAccess>]
module GitHubRequests =

    let apiRoot = "https://api.github.com"
    let graphqlUrl = apiRoot + "/graphql"

    let private headers (connection: ConnectionContext) : (string * string) list =
        [ Credential.authorization connection.Credential
          "Accept", "application/vnd.github+json"
          "X-GitHub-Api-Version", "2022-11-28"
          "User-Agent", "causa-persistence" ]

    let private repoUrl (repository: Repository) (suffix: string) =
        sprintf "%s/repos/%s/%s%s" apiRoot repository.Owner repository.Name suffix

    let private escape (segment: string) = System.Uri.EscapeDataString segment

    let private escapePath (path: string) =
        path.Split '/' |> Array.map escape |> String.concat "/"

    let private get (id: RequestId) (connection: ConnectionContext) (url: string) : HttpRequest =
        { Id = id; Method = Get; Url = url; Headers = headers connection; Body = None }

    /// Connect: GET the repository; its `permissions` object is the only source of write access (EXT-DATA-005).
    let repository (id: RequestId) (connection: ConnectionContext) : HttpRequest =
        get id connection (repoUrl connection.Repository "")

    /// The branch head: GET /git/ref/heads/<branch>.
    let branchHead (id: RequestId) (connection: ConnectionContext) : HttpRequest =
        get id connection (repoUrl connection.Repository ("/git/ref/heads/" + escapePath connection.Repository.Branch))

    /// A file at a named commit (reads name the commit they read, PERSIST-007).
    let contentsAt (id: RequestId) (connection: ConnectionContext) (CommitOid commit) (RepoPath path) : HttpRequest =
        get id connection (repoUrl connection.Repository (sprintf "/contents/%s?ref=%s" (escapePath path) (escape commit)))

    /// compare base...head, one page of at most 100 commits (GitHub lists at most 250 in total: GH-TRUNCATED-LIST).
    let compare (id: RequestId) (connection: ConnectionContext) (CommitOid baseOid) (CommitOid headOid) (page: int) : HttpRequest =
        get
            id
            connection
            (repoUrl connection.Repository (sprintf "/compare/%s...%s?per_page=100&page=%d" (escape baseOid) (escape headOid) page))

    /// Create a branch at a commit (the live probe's fresh branch per test class).
    let createBranch (id: RequestId) (connection: ConnectionContext) (branch: string) (CommitOid at) : HttpRequest =
        let body = JsonObject()
        body["ref"] <- JsonValue.Create("refs/heads/" + branch)
        body["sha"] <- JsonValue.Create at

        { Id = id
          Method = Post
          Url = repoUrl connection.Repository "/git/refs"
          Headers = headers connection
          Body = Some(body.ToJsonString()) }

    let createCommitMutation =
        "mutation($input: CreateCommitOnBranchInput!) { createCommitOnBranch(input: $input) { commit { oid } } }"

    /// The one write primitive: GraphQL createCommitOnBranch guarded by expectedHeadOid (F-009, PERSIST-005).
    /// `additions` are (path, bytes); the bytes travel base64-encoded as GitHub requires.
    let createCommitOnBranch
        (id: RequestId)
        (connection: ConnectionContext)
        (CommitOid expectedHead)
        (headline: string)
        (additions: (RepoPath * byte[]) list)
        : HttpRequest =
        let fileAdditions = JsonArray()

        for (RepoPath path, bytes) in additions do
            let addition = JsonObject()
            addition["path"] <- JsonValue.Create path
            addition["contents"] <- JsonValue.Create(System.Convert.ToBase64String bytes)
            fileAdditions.Add addition

        let branch = JsonObject()
        branch["repositoryNameWithOwner"] <- JsonValue.Create(connection.Repository.Owner + "/" + connection.Repository.Name)
        branch["branchName"] <- JsonValue.Create connection.Repository.Branch
        let message = JsonObject()
        message["headline"] <- JsonValue.Create headline
        let changes = JsonObject()
        changes["additions"] <- fileAdditions
        let input = JsonObject()
        input["branch"] <- branch
        input["message"] <- message
        input["expectedHeadOid"] <- JsonValue.Create expectedHead
        input["fileChanges"] <- changes
        let variables = JsonObject()
        variables["input"] <- input
        let body = JsonObject()
        body["query"] <- JsonValue.Create createCommitMutation
        body["variables"] <- variables

        { Id = id
          Method = Post
          Url = graphqlUrl
          Headers = headers connection
          Body = Some(body.ToJsonString()) }

    /// T19: the save mutation with its write marker as a trailer in the commit message body (PERSIST-014). Same
    /// guard and additions as createCommitOnBranch; additions only (no deletions, no forced ref update).
    let createCommitWithTrailer
        (id: RequestId)
        (connection: ConnectionContext)
        (expectedHead: CommitOid)
        (headline: string)
        (body: string)
        (additions: (RepoPath * byte[]) list)
        : HttpRequest =
        let request = createCommitOnBranch id connection expectedHead headline additions

        match request.Body with
        | Some text ->
            let node = JsonNode.Parse text |> nonNull
            let message = node["variables"] |> nonNull |> fun v -> v["input"] |> nonNull |> fun i -> i["message"] |> nonNull
            message["body"] <- JsonValue.Create body
            { request with Body = Some(node.ToJsonString()) }
        | None -> request

    /// compare base...head listing up to 250 commits on one page (GitHub lists at most 250: GH-TRUNCATED-LIST).
    let compareAll (id: RequestId) (connection: ConnectionContext) (CommitOid baseOid) (CommitOid headOid) : HttpRequest =
        get id connection (repoUrl connection.Repository (sprintf "/compare/%s...%s?per_page=250" (escape baseOid) (escape headOid)))

    let private graphqlQuery (id: RequestId) (connection: ConnectionContext) (query: string) (variables: (string * string) list) =
        let vars = JsonObject()

        for (k, v) in variables do
            vars[k] <- JsonValue.Create v

        let body = JsonObject()
        body["query"] <- JsonValue.Create query
        body["variables"] <- vars

        { Id = id
          Method = Post
          Url = graphqlUrl
          Headers = headers connection
          Body = Some(body.ToJsonString()) }

    let private fileFields =
        "oid manifest: file(path: $manifest) { object { ... on Blob { text isTruncated } } } "
        + "events: file(path: $events) { object { ... on Blob { text isTruncated } } } "
        + "snapshot: file(path: $snapshot) { object { ... on Blob { text isTruncated } } }"

    let private fileVariables (RepoPath manifest) (RepoPath events) (RepoPath snapshot) =
        [ "manifest", manifest; "events", events; "snapshot", snapshot ]

    /// The investigation's three files at the branch's current head, with the head's oid, in one read (a query, never
    /// a mutation): SM-CONFLICT's read of the GitHub version.
    let fileSetAtBranch (id: RequestId) (connection: ConnectionContext) (manifest: RepoPath) (events: RepoPath) (snapshot: RepoPath) : HttpRequest =
        graphqlQuery
            id
            connection
            ("query($owner: String!, $name: String!, $ref: String!, $manifest: String!, $events: String!, $snapshot: String!) "
             + "{ repository(owner: $owner, name: $name) { ref(qualifiedName: $ref) { target { ... on Commit { "
             + fileFields
             + " } } } } }")
            ([ "owner", connection.Repository.Owner
               "name", connection.Repository.Name
               "ref", "refs/heads/" + connection.Repository.Branch ]
             @ fileVariables manifest events snapshot)

    /// The investigation's three files at a named commit in one read (PERSIST-005: file reads name their commit).
    let fileSetAtCommit
        (id: RequestId)
        (connection: ConnectionContext)
        (CommitOid commit)
        (manifest: RepoPath)
        (events: RepoPath)
        (snapshot: RepoPath)
        : HttpRequest =
        graphqlQuery
            id
            connection
            ("query($owner: String!, $name: String!, $oid: GitObjectID!, $manifest: String!, $events: String!, $snapshot: String!) "
             + "{ repository(owner: $owner, name: $name) { object(oid: $oid) { ... on Commit { "
             + fileFields
             + " } } } }")
            ([ "owner", connection.Repository.Owner; "name", connection.Repository.Name; "oid", commit ]
             @ fileVariables manifest events snapshot)

    /// T20 (GH-LARGE-FILE): a blob by its sha, the one re-read GitHub allows for a file the contents API returns with
    /// `encoding: none` (files over 1 MB). A query, never a mutation.
    let blob (id: RequestId) (connection: ConnectionContext) (sha: string) : HttpRequest =
        get id connection (repoUrl connection.Repository ("/git/blobs/" + escape sha))
