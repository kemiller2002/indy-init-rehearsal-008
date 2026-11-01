// Owner: persistence. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.Persistence

/// IF41-ERRORS: the classification of github-error-matrix.json. Every provider answer maps to exactly one case.
type RateLimitKind =
    | Primary of resetAtEpochSeconds: int64 option
    | Secondary of retryAfterSeconds: int option

type ProviderError =
    | AuthenticationFailed
    | PermissionDenied of operation: string
    | RateLimited of RateLimitKind
    | StaleHead of expected: CommitOid * actual: CommitOid option
    | ValidationFailed of messages: string list
    | NotFound of what: string
    | HistoryRewritten of expected: CommitOid
    | NetworkUnavailable of TransportFailure
    | Malformed of detail: string
    | ReadBackMismatch of commit: CommitOid
    | Unrecognised of status: int

/// A 2xx answer that decoded: the request it answers and its body, for the caller to decode further.
type ProviderAnswer = { Request: RequestId; Status: int; Body: string }

type Classifier = HttpOutcome -> Result<ProviderAnswer, ProviderError>

/// The answer to Connect (EXT-DATA-005): write access comes only from the permissions GitHub states for the
/// repository, never from a successful read.
type ConnectResult =
    | ConnectAccepted of canWrite: bool
    | ConnectRejected of ProviderError
    | ConnectSuperseded

/// Persistence builds the Connect request and decodes its answer; the App maps ConnectResult to SM-CONNECTION's events.
type Connector =
    { ConnectRequest: RequestId -> ConnectionContext -> HttpRequest
      InterpretConnect: RequestId -> HttpOutcome -> ConnectResult }

/// T18: the classifier of analysis/github-error-matrix.json. Pure functions over HttpOutcome; the class is decided
/// from the answer's typed content and headers with the matrix's precedence rule, never from a shared status alone.
module ErrorMatrix =
    open System
    open System.Text.Json
    open System.Text.RegularExpressions

    /// The matrix rows, one case per class id (GH-*).
    type MatrixClass =
        | GhAuth
        | GhPermission
        | GhRatePrimary
        | GhRateSecondary
        | GhStaleHead
        | GhValidation
        | GhNotFound
        | GhHistoryRewritten
        | GhNetworkBefore
        | GhNetworkAfter
        | GhUnknownOutcome
        | GhMalformed
        | GhReadBackMismatch
        | GhLargeFile
        | GhTruncatedList
        /// Not a matrix row: an answer every check accepted.
        | Accepted

    let classId (c: MatrixClass) : string =
        match c with
        | GhAuth -> "GH-AUTH"
        | GhPermission -> "GH-PERMISSION"
        | GhRatePrimary -> "GH-RATE-PRIMARY"
        | GhRateSecondary -> "GH-RATE-SECONDARY"
        | GhStaleHead -> "GH-STALE-HEAD"
        | GhValidation -> "GH-VALIDATION"
        | GhNotFound -> "GH-NOT-FOUND"
        | GhHistoryRewritten -> "GH-HISTORY-REWRITTEN"
        | GhNetworkBefore -> "GH-NETWORK-BEFORE"
        | GhNetworkAfter -> "GH-NETWORK-AFTER"
        | GhUnknownOutcome -> "GH-UNKNOWN-OUTCOME"
        | GhMalformed -> "GH-MALFORMED"
        | GhReadBackMismatch -> "GH-READBACK-MISMATCH"
        | GhLargeFile -> "GH-LARGE-FILE"
        | GhTruncatedList -> "GH-TRUNCATED-LIST"
        | Accepted -> "ACCEPTED"

    /// The matrix's maxAttempts per class: no path may retry a class beyond this bound.
    let maxAttempts (c: MatrixClass) : int =
        match c with
        | GhAuth
        | GhPermission
        | GhRateSecondary
        | GhValidation
        | GhNotFound
        | GhHistoryRewritten
        | GhNetworkBefore
        | GhReadBackMismatch
        | Accepted -> 0
        | GhLargeFile -> 1
        | GhRatePrimary
        | GhStaleHead
        | GhNetworkAfter
        | GhUnknownOutcome
        | GhMalformed
        | GhTruncatedList -> 3

    /// What the request was, which the classifier needs where the matrix makes the meaning depend on it.
    type AnswerContext =
        /// createCommitOnBranch guarded by this expected head.
        | Mutation of expected: CommitOid
        /// The repository read during Connect (a 404 there means the token cannot see it).
        | ConnectRepository
        /// A file or ref read at a named commit after Connect confirmed access.
        | Read of what: string
        /// compare <expected>...head during reconciliation.
        | Compare of expected: CommitOid

    type Classified =
        { Class: MatrixClass
          Result: Result<ProviderAnswer, ProviderError> }

    /// One GraphQL `errors[]` entry: its type, message and the response path it names (field names and list
    /// indices as text; empty when GitHub names none).
    type GraphQLError =
        { Type: string option
          Message: string
          Path: string list }

    let private header (name: string) (headers: (string * string) list) : string option =
        headers
        |> List.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    let private tryParse (body: string) : JsonElement option =
        try
            use doc = JsonDocument.Parse body
            Some(doc.RootElement.Clone())
        with :? JsonException ->
            None

    let private prop (name: string) (e: JsonElement) : JsonElement option =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private str (e: JsonElement) : string option =
        match e.ValueKind with
        | JsonValueKind.String ->
            match e.GetString() with
            | null -> None
            | v -> Some v
        | _ -> None

    /// GraphQL `errors[]` as (type, message) pairs; empty when the body has none.
    let graphQLErrors (body: JsonElement option) : GraphQLError list =
        match body |> Option.bind (prop "errors") with
        | Some arr when arr.ValueKind = JsonValueKind.Array ->
            [ for e in arr.EnumerateArray() ->
                  { Type = prop "type" e |> Option.bind str
                    Message = prop "message" e |> Option.bind str |> Option.defaultValue ""
                    Path =
                      match prop "path" e with
                      | Some p when p.ValueKind = JsonValueKind.Array ->
                          [ for step in p.EnumerateArray() ->
                                match step.ValueKind with
                                | JsonValueKind.String -> str step |> Option.defaultValue ""
                                | _ -> step.ToString() ]
                      | _ -> [] } ]
        | _ -> []

    /// The form GitHub answers for `Commit.file(path:)` when the commit's tree has no such path (CR-T21-01, seen live
    /// in run 41340579555): a GraphQL error of type NOT_FOUND, "Could not resolve file for path '<path>'.".
    let private missingPathPattern = Regex("^Could not resolve file for path '([^']+)'\\.?$", RegexOptions.CultureInvariant)

    /// The file each aliased slot of the file-set query reads (GitHubRequests.fileSetAtBranch / fileSetAtCommit bind
    /// `manifest`, `events` and `snapshot` to StoragePaths.manifest/events/snapshot of one investigation).
    let private slotPath (slot: string) (dir: string) : string option =
        match slot with
        | "manifest" -> Some(dir + "manifest.json")
        | "events" -> Some(dir + "events.ndjson")
        | "snapshot" -> Some(dir + "snapshot.json")
        | _ -> None

    /// An investigation directory as StoragePaths.prefix writes it: `investigations/<lower-case guid>/`.
    let private investigationDir = Regex("^investigations/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/", RegexOptions.CultureInvariant)

    /// The path an absent-file error quotes, when it is exactly the path the query read for the slot the error names
    /// (review T36 F-05): the error's response path ends in a slot alias, and the quoted text equals that slot's
    /// storage path under an investigation directory. None for any other quoted path.
    let private quotedSlotPath (e: GraphQLError) : string option =
        let m = missingPathPattern.Match e.Message

        if not m.Success then
            None
        else
            let quoted = m.Groups.[1].Value
            let dir = investigationDir.Match quoted

            match List.tryLast e.Path with
            | Some slot when dir.Success && slotPath slot dir.Value = Some quoted -> Some quoted
            | _ -> None

    let private dataAt (steps: string list) (body: JsonElement option) : JsonElement option =
        steps
        |> List.fold
            (fun acc step ->
                acc
                |> Option.bind (fun (e: JsonElement) ->
                    match e.ValueKind with
                    | JsonValueKind.Object -> prop step e
                    | JsonValueKind.Array ->
                        match Int32.TryParse step with
                        | true, i when i >= 0 && i < e.GetArrayLength() -> Some(e.[i])
                        | _ -> None
                    | _ -> None))
            (body |> Option.bind (prop "data"))

    /// The commit a file read answered at: `data.repository.ref.target.oid` (at a branch) or
    /// `data.repository.object.oid` (at a named commit).
    let private commitOidOfRead (body: JsonElement option) : string option =
        dataAt [ "repository"; "ref"; "target"; "oid" ] body
        |> Option.orElse (dataAt [ "repository"; "object"; "oid" ] body)
        |> Option.bind str

    /// R-07 (CR-T21-01): a file read at a commit that exists, where the error says the commit's tree has no such
    /// path, says the file is absent; it is not a refusal. The matrix's GraphQL GH-NOT-FOUND is a missing branch or
    /// ref, never a missing path inside a commit that was found. Only with the commit oid in the answer, and only when
    /// the field the error names (if it names one) answered null; everything else keeps its class.
    let isAbsentFile (body: JsonElement option) (e: GraphQLError) : bool =
        e.Type = Some "NOT_FOUND"
        && (quotedSlotPath e).IsSome
        && (commitOidOfRead body).IsSome
        && (match dataAt e.Path body with
            | None -> true
            | Some v -> v.ValueKind = JsonValueKind.Null)

    /// The absent-file errors of one read are absorbed only together, and only when they all quote files of one
    /// investigation: a read names one investigation's three paths, so errors quoting two directories cannot all be
    /// the paths read (review T36 F-05).
    let private absentFiles (body: JsonElement option) (errors: GraphQLError list) : GraphQLError list =
        let absent = errors |> List.filter (isAbsentFile body)

        let dirs =
            absent
            |> List.choose quotedSlotPath
            |> List.map (fun p -> p.Substring(0, p.LastIndexOf '/' + 1))
            |> List.distinct

        match dirs with
        | [ _ ] -> absent
        | _ -> []

    let private restMessage (body: JsonElement option) : string =
        body |> Option.bind (prop "message") |> Option.bind str |> Option.defaultValue ""

    let private oidPattern = "[0-9a-fA-F]{7,64}"

    /// The moved-head form GitHub returns with type FORBIDDEN (Rehearsal 7, KD-022): "... is at <oid> but expected <oid>".
    let movedHead (message: string) : (CommitOid * CommitOid) option =
        let m = Regex.Match(message, sprintf "is at (%s) but expected (%s)" oidPattern oidPattern)

        if m.Success then
            Some(CommitOid m.Groups.[1].Value, CommitOid m.Groups.[2].Value)
        else
            None

    let private expectedBranchForm (message: string) =
        message.Contains("Expected branch to point to", StringComparison.OrdinalIgnoreCase)

    let private mentionsSecondary (message: string) =
        let m = message.ToLowerInvariant()
        m.Contains "secondary rate limit" || m.Contains "too quickly" || m.Contains "abuse"

    /// A rate limit named in the message without the primary or secondary evidence (R-03): "API rate limit exceeded".
    let private mentionsRateLimit (message: string) =
        message.ToLowerInvariant().Contains "rate limit"

    let private validationTypes = set [ "UNPROCESSABLE"; "INVALID"; "INPUT_VALIDATION"; "ARGUMENT_ERROR" ]

    let private isRateStatus status = status = 403 || status = 429

    let private refuse c e = { Class = c; Result = Error e }

    let private accept c (id, status, body) =
        { Class = c; Result = Ok { Request = id; Status = status; Body = body } }

    let private tryInt (s: string option) =
        s |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None)

    let private tryInt64 (s: string option) =
        s |> Option.bind (fun v -> match Int64.TryParse v with | true, n -> Some n | _ -> None)

    let private successBody (context: AnswerContext) (id, status, rawBody: string) (body: JsonElement option) : Classified =
        match context, body with
        | _, None -> refuse GhMalformed (Malformed "the 2xx body does not decode as JSON")
        | Mutation _, Some b ->
            let oid =
                b
                |> prop "data"
                |> Option.bind (prop "createCommitOnBranch")
                |> Option.bind (prop "commit")
                |> Option.bind (prop "oid")
                |> Option.bind str

            match oid with
            | Some _ -> accept Accepted (id, status, rawBody)
            | None -> refuse GhMalformed (Malformed "createCommitOnBranch answer has no commit oid")
        | Compare expected, Some b ->
            match b |> prop "status" |> Option.bind str with
            | Some "diverged"
            | Some "behind" -> refuse GhHistoryRewritten (HistoryRewritten expected)
            | Some _ ->
                let total =
                    b
                    |> prop "total_commits"
                    |> Option.bind (fun e ->
                        match e.ValueKind with
                        | JsonValueKind.Number ->
                            match e.TryGetInt64() with
                            | true, n -> Some n
                            | _ -> None
                        | _ -> None)
                let listed =
                    b |> prop "commits" |> Option.map (fun e -> if e.ValueKind = JsonValueKind.Array then int64 (e.GetArrayLength()) else 0L)

                match total, listed with
                | Some t, Some l when t > l -> accept GhTruncatedList (id, status, rawBody)
                | _ -> accept Accepted (id, status, rawBody)
            | None -> refuse GhMalformed (Malformed "compare answer has no status")
        | ConnectRepository, Some b ->
            match b |> prop "permissions" with
            | Some p when p.ValueKind = JsonValueKind.Object -> accept Accepted (id, status, rawBody)
            | _ -> refuse GhMalformed (Malformed "the repository answer has no permissions object")
        | Read what, Some b ->
            // R-06: a read is accepted only with the content it asked for: a file's content (or the large-file
            // form), a ref's object sha, or a GraphQL read's data.
            let hasString name (e: JsonElement) = e |> prop name |> Option.bind str |> Option.isSome

            match b |> prop "encoding" |> Option.bind str with
            | Some "none" -> accept GhLargeFile (id, status, rawBody)
            | _ when hasString "content" b -> accept Accepted (id, status, rawBody)
            | _ when b |> prop "object" |> Option.exists (hasString "sha") -> accept Accepted (id, status, rawBody)
            | _ when b |> prop "data" |> Option.exists (fun d -> d.ValueKind = JsonValueKind.Object) -> accept Accepted (id, status, rawBody)
            | _ -> refuse GhMalformed (Malformed(sprintf "the %s read has no content" what))

    /// Classify one outcome in its context: exactly one matrix class, by the matrix's precedence order
    /// (GH-AUTH, GH-RATE-PRIMARY, GH-RATE-SECONDARY, GH-STALE-HEAD, GH-LARGE-FILE, GH-PERMISSION, GH-VALIDATION,
    /// GH-NOT-FOUND, then the rest).
    let classify (context: AnswerContext) (outcome: HttpOutcome) : Classified =
        match outcome with
        | NotAnswered(_, FailedBeforeSending) -> refuse GhNetworkBefore (NetworkUnavailable FailedBeforeSending)
        | NotAnswered(_, failure) -> refuse GhNetworkAfter (NetworkUnavailable failure)
        | Answered(id, status, headers, rawBody) ->
            let body = tryParse rawBody

            let errors =
                match context with
                // R-07: on a read, a missing path inside a found commit is an absent file, decoded by the reader.
                | Read _ ->
                    let all = graphQLErrors body
                    let absent = absentFiles body all
                    all |> List.filter (fun e -> not (List.contains e absent))
                | Mutation _
                | ConnectRepository
                | Compare _ -> graphQLErrors body
            let message = restMessage body
            let remaining = header "x-ratelimit-remaining" headers
            let retryAfter = header "retry-after" headers
            let primaryEvidence = remaining = Some "0"
            let gqlType t = errors |> List.exists (fun e -> e.Type = Some t)
            let gqlMessages = errors |> List.map (fun e -> e.Message)

            let stale =
                errors
                |> List.tryPick (fun e ->
                    match movedHead e.Message with
                    | Some(actual, expected) -> Some(StaleHead(expected, Some actual))
                    | None when e.Type = Some "STALE_DATA" || expectedBranchForm e.Message ->
                        // R-06: only a guarded mutation can be stale; elsewhere no expected head exists to name.
                        match context with
                        | Mutation expected -> Some(StaleHead(expected, None))
                        | _ -> None
                    | None -> None)

            let tooLarge =
                status = 403
                && (errors |> List.exists (fun e -> e.Type = Some "too_large")
                    || (body |> Option.bind (prop "errors") |> Option.map (fun e -> e.ToString().Contains "too_large") |> Option.defaultValue false))

            if status = 401 then
                refuse GhAuth AuthenticationFailed
            elif primaryEvidence && (isRateStatus status || gqlType "RATE_LIMITED") then
                refuse GhRatePrimary (RateLimited(Primary(tryInt64 (header "x-ratelimit-reset" headers))))
            elif
                (isRateStatus status && (retryAfter.IsSome || mentionsSecondary message || List.exists mentionsSecondary gqlMessages))
                || gqlType "RATE_LIMITED"
            then
                refuse GhRateSecondary (RateLimited(Secondary(tryInt retryAfter)))
            elif status = 429 || (status = 403 && (mentionsRateLimit message || List.exists mentionsRateLimit gqlMessages)) then
                // R-03: a 403 whose message names a rate limit is never a permission failure; with no remaining-0
                // or retry-after evidence it is the primary limit (reset unknown unless the header says).
                refuse GhRatePrimary (RateLimited(Primary(tryInt64 (header "x-ratelimit-reset" headers))))
            elif stale.IsSome then
                refuse GhStaleHead stale.Value
            elif tooLarge then
                accept GhLargeFile (id, status, rawBody)
            elif status = 403 then
                refuse GhPermission (PermissionDenied message)
            elif status = 404 then
                match context with
                | ConnectRepository -> refuse GhPermission (PermissionDenied "repository not visible to this credential")
                | Compare expected -> refuse GhHistoryRewritten (HistoryRewritten expected)
                | Read what -> refuse GhNotFound (NotFound what)
                | Mutation _ -> refuse GhNotFound (NotFound "graphql endpoint")
            elif gqlType "FORBIDDEN" then
                refuse GhPermission (PermissionDenied(String.concat "; " gqlMessages))
            elif status = 422 then
                let restErrors =
                    match body |> Option.bind (prop "errors") with
                    | Some arr when arr.ValueKind = JsonValueKind.Array ->
                        [ for e in arr.EnumerateArray() ->
                              match e.ValueKind with
                              | JsonValueKind.String -> str e |> Option.defaultValue ""
                              | _ -> prop "message" e |> Option.bind str |> Option.defaultValue (e.ToString()) ]
                    | _ -> []

                refuse GhValidation (ValidationFailed(message :: restErrors |> List.filter (String.IsNullOrEmpty >> not)))
            elif errors |> List.exists (fun e -> e.Type |> Option.exists validationTypes.Contains) then
                refuse GhValidation (ValidationFailed gqlMessages)
            elif gqlType "NOT_FOUND" then
                refuse GhNotFound (NotFound(String.concat "; " gqlMessages))
            elif not errors.IsEmpty then
                refuse GhUnknownOutcome (Unrecognised status)
            elif status >= 200 && status < 300 then
                successBody context (id, status, rawBody) body
            else
                refuse GhUnknownOutcome (Unrecognised status)

    /// What a refused or unrecognised answer means for the path that sent it (R-05, matrix GH-NETWORK-AFTER,
    /// GH-UNKNOWN-OUTCOME, GH-MALFORMED): for a mutation the outcome is unknown and must be reconciled; for a read
    /// the read failed. Definite refusals (auth, permission, rate limit, stale head, validation, not found, history
    /// rewritten) and accepted answers keep their class.
    type Meaning =
        | Definite
        | UnknownMutationOutcome
        | FailedBeforeSendingMeaning
        | FailedRead

    let meaning (context: AnswerContext) (classified: Classified) : Meaning =
        let uncertain =
            match classified.Class with
            | GhNetworkAfter
            | GhUnknownOutcome
            | GhMalformed -> true
            | _ -> false

        match classified.Class, context with
        | GhNetworkBefore, _ -> FailedBeforeSendingMeaning
        | _, Mutation _ when uncertain -> UnknownMutationOutcome
        | _, (ConnectRepository | Read _ | Compare _) when uncertain -> FailedRead
        | _ -> Definite

    /// `meaning`, with the one case the class alone cannot tell (review T36 F-06): an HTTP 404 from the GraphQL
    /// endpoint on the mutation. The matrix's GH-NOT-FOUND for createCommitOnBranch is a GraphQL NOT_FOUND error
    /// (branch missing); a bare 404 status names no branch, so its outcome is unknown and is reconciled (PERSIST-015),
    /// never reported as "branch not found".
    let meaningOf (context: AnswerContext) (outcome: HttpOutcome) (classified: Classified) : Meaning =
        match context, outcome, classified.Class with
        | Mutation _, Answered(_, 404, _, _), GhNotFound -> UnknownMutationOutcome
        | _ -> meaning context classified

    /// The matrix classifier as the IF41-ERRORS Classifier type for one context.
    let classifier (context: AnswerContext) : Classifier = fun outcome -> (classify context outcome).Result
