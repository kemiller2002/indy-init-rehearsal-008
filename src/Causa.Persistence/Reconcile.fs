// Owner: persistence. T19: decoding of the answers the save, reconciliation, conflict and load paths read, and the
// write marker (PERSIST-014, PERSIST-015). Pure.
namespace Causa.Persistence

open System
open System.Text
open System.Text.Json

/// One compare base...head answer: the commits after base (oldest first), how many GitHub counted, and the changed
/// file names when GitHub listed them.
type CompareView =
    { Commits: (CommitOid * string) list
      Total: int64
      Files: string list option }

[<RequireQualifiedAccess>]
module CompareView =
    /// GitHub's compare lists at most 300 changed files for the whole comparison and sets no truncation flag.
    let fileListLimit = 300

    /// The changed file names when the answer proves the list complete: every commit after base listed, and fewer
    /// files than GitHub's cap. A list of exactly the cap or more may be truncated, so it is unknown (review T36 F-01):
    /// a caller treats unknown as "the investigation possibly changed" and never overwrites on it (PERSIST-011).
    let completeFiles (view: CompareView) : string list option =
        match view.Files with
        | Some files when view.Total = int64 view.Commits.Length && files.Length < fileListLimit -> Some files
        | _ -> None

    /// The changed file names of a paged compare's first page (GitHubRequests.compare, page 1): GitHub shows the file
    /// list on the first page only, and there it covers the entire comparison, up to its 300-file cap, however many
    /// commits the comparison holds. A list at the cap or more may be truncated, so it is unknown (review T36 F-01).
    let firstPageFiles (view: CompareView) : string list option =
        match view.Files with
        | Some files when files.Length < fileListLimit -> Some files
        | _ -> None

/// A file set read in one GraphQL query: the commit it was read at and the three files (None when a file is absent).
type FileSetRead =
    { At: CommitOid
      Manifest: byte[] option
      Events: byte[] option
      Snapshot: byte[] option }

[<RequireQualifiedAccess>]
module Answers =
    let private tryParse (body: string) =
        try
            use doc = JsonDocument.Parse body
            Some(doc.RootElement.Clone())
        with :? JsonException ->
            None

    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        | _ -> None

    let private str (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.String -> e.GetString() |> Option.ofObj
        | _ -> None

    let private path (names: string list) (body: string) =
        tryParse body |> Option.bind (fun root -> names |> List.fold (fun acc n -> acc |> Option.bind (prop n)) (Some root))

    /// The commit a createCommitOnBranch answer names.
    let commitOid (body: string) : CommitOid option =
        path [ "data"; "createCommitOnBranch"; "commit"; "oid" ] body |> Option.bind str |> Option.map CommitOid

    /// The commit a ref read names (GET /git/ref/heads/<branch>).
    let headOid (body: string) : CommitOid option =
        path [ "object"; "sha" ] body |> Option.bind str |> Option.map CommitOid

    /// A contents read: the file's path and its bytes (base64 with line breaks, as GitHub sends it).
    let contents (body: string) : (string * byte[]) option =
        match tryParse body with
        | Some root ->
            match prop "path" root |> Option.bind str, prop "encoding" root |> Option.bind str, prop "content" root |> Option.bind str with
            | Some p, Some "base64", Some content ->
                try
                    Some(p, Convert.FromBase64String(content.Replace("\n", "").Replace("\r", "")))
                with :? FormatException ->
                    None
            | _ -> None
        | None -> None

    let compare (body: string) : CompareView option =
        match tryParse body with
        | Some root ->
            let commits =
                match prop "commits" root with
                | Some arr when arr.ValueKind = JsonValueKind.Array ->
                    [ for c in arr.EnumerateArray() ->
                          prop "sha" c |> Option.bind str,
                          prop "commit" c |> Option.bind (prop "message") |> Option.bind str |> Option.defaultValue "" ]
                    |> List.choose (fun (sha, message) -> sha |> Option.map (fun s -> CommitOid s, message))
                | _ -> []

            let total =
                match prop "total_commits" root with
                | Some e when e.ValueKind = JsonValueKind.Number ->
                    match e.TryGetInt64() with
                    | true, n -> n
                    | _ -> int64 commits.Length
                | _ -> int64 commits.Length

            let files =
                match prop "files" root with
                | Some arr when arr.ValueKind = JsonValueKind.Array ->
                    Some [ for f in arr.EnumerateArray() do
                               match prop "filename" f |> Option.bind str with
                               | Some name -> yield name
                               | None -> () ]
                | _ -> None

            Some { Commits = commits; Total = total; Files = files }
        | None -> None

    /// The head of the range a compare answer covers, from the `url` GitHub echoes for the requested base...head
    /// (".../compare/<base>...<head>"). Only a full commit id counts: a branch name or abbreviation is not a commit.
    let compareHead (body: string) : CommitOid option =
        path [ "url" ] body
        |> Option.bind str
        |> Option.bind (fun url ->
            match url.LastIndexOf("/compare/", StringComparison.Ordinal) with
            | -1 -> None
            | i ->
                let range = url.Substring(i + "/compare/".Length).Split('?')[0]

                match range.Split("...") with
                | [| _; head |] when head.Length = 40 && head |> Seq.forall Uri.IsHexDigit -> Some(CommitOid head)
                | _ -> None)

    /// A GraphQL file-set read (GitHubRequests.fileSetAtBranch or fileSetAtCommit). None when the answer does not
    /// decode or a file is truncated; a missing file is None in its slot.
    let fileSet (body: string) : FileSetRead option =
        let target =
            path [ "data"; "repository"; "ref"; "target" ] body
            |> Option.orElse (path [ "data"; "repository"; "object" ] body)

        match target with
        | Some t ->
            match prop "oid" t |> Option.bind str with
            | Some oid ->
                let file name =
                    match prop name t |> Option.bind (prop "object") with
                    | Some blob ->
                        match prop "isTruncated" blob with
                        | Some v when v.ValueKind = JsonValueKind.True -> Error()
                        | _ -> Ok(prop "text" blob |> Option.bind str |> Option.map Encoding.UTF8.GetBytes)
                    | None -> Ok None

                match file "manifest", file "events", file "snapshot" with
                | Ok m, Ok e, Ok s -> Some { At = CommitOid oid; Manifest = m; Events = e; Snapshot = s }
                | _ -> None
            | None -> None
        | None -> None

    /// True when the repository has no ref (the branch is gone) in a GraphQL file-set read.
    let refMissing (body: string) : bool =
        match path [ "data"; "repository" ] body with
        | Some repo -> (prop "ref" repo).IsNone && (prop "object" repo).IsNone
        | None -> false

/// The write marker (PERSIST-014): the tab's random session identifier and a sequence number, carried as a trailer
/// in the commit message; audit metadata used only for reconciliation.
[<RequireQualifiedAccess>]
module Markers =
    let trailerKey = "Causa-Write-Marker"

    let trailer (marker: WriteMarker) =
        sprintf "%s: %s/%d" trailerKey (marker.Session.ToString("D")) marker.Sequence

    /// True when a commit message carries a marker of this tab session.
    let ofSession (session: Guid) (message: string) =
        message.Contains(sprintf "%s: %s/" trailerKey (session.ToString("D")), StringComparison.Ordinal)

    /// A tab's marker source: one random session identifier per tab and a strictly increasing sequence, so markers
    /// never repeat across tabs, page loads or saves. The counter is the tab's only mutable cell, held at the edge.
    let forTab (session: Guid) : unit -> WriteMarker =
        let sequence = ref 0L
        fun () -> { Session = session; Sequence = Threading.Interlocked.Increment(&sequence.contents) }
