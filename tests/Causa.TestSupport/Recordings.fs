// Owner: persistence. T18: GitHub answer forms the stateful fake and the classifier tests are built from.
namespace Causa.TestSupport

open System
open System.Text.Json.Nodes
open Causa.Persistence

/// How a recording was obtained. Nothing here is presented as a live capture unless it is one.
type Provenance =
    /// Captured from api.github.com by the CI live probe (tests/Causa.Invariants/Live/INV22.fs), sanitized.
    | CapturedLive of runId: string * capturedAt: string * sourceSha: string
    /// The answer form restated from analysis/github-error-matrix.json (forms observed live in Rehearsal 7); the
    /// surrounding text is not a Rehearsal 8 capture. Replaced by CapturedLive entries once the CI probe has run.
    | DocumentedForm of source: string

type Recording =
    { Name: string
      /// The matrix class id the form belongs to (GH-*), as the matrix states it.
      MatrixClass: string
      Operation: string
      Status: int
      Headers: (string * string) list
      Body: string
      Provenance: Provenance }

[<RequireQualifiedAccess>]
module Recordings =

    /// Response headers kept in a recording; everything else (cookies, tokens, server internals) is dropped (PF-013).
    let keptHeaders =
        set
            [ "content-type"; "etag"; "last-modified"; "cache-control"; "vary"; "retry-after"; "x-ratelimit-limit"
              "x-ratelimit-remaining"; "x-ratelimit-reset"; "x-ratelimit-resource"; "x-ratelimit-used"; "x-github-request-id"
              "link"; "x-accepted-github-permissions" ]

    let sanitizeHeaders (headers: (string * string) list) : (string * string) list =
        headers
        |> List.map (fun (k, v) -> k.ToLowerInvariant(), v)
        |> List.filter (fun (k, _) -> keptHeaders.Contains k)
        |> List.sortBy fst

    /// File contents are private payloads: a recorded body keeps the shape, never the bytes (PF-013).
    let sanitizeBody (body: string) : string =
        let rec scrub (node: JsonNode | null) =
            match node with
            | :? JsonObject as o ->
                for key in [ "content"; "contents"; "token" ] do
                    if o.ContainsKey key then
                        o[key] <- JsonValue.Create "<elided>"

                for kv in List.ofSeq o do
                    scrub kv.Value
            | :? JsonArray as a ->
                for item in List.ofSeq a do
                    scrub item
            | _ -> ()

        try
            match JsonNode.Parse body with
            | null -> body
            | node ->
                scrub node
                node.ToJsonString()
        with _ ->
            if body.Length > 512 then body.Substring(0, 512) + "<truncated>" else body

    let private oidA = String('a', 40)
    let private oidB = String('b', 40)

    let private documented =
        DocumentedForm
            "restated from requirements-compiled/v4.1.1/analysis/github-error-matrix.json detection rules; message text illustrative, not captured in Rehearsal 8"

    /// The moved-head refusal (GH-STALE-HEAD, FORBIDDEN form, Rehearsal 7 KD-022). `actual` is the branch head,
    /// `expected` the expectedHeadOid sent.
    let movedHeadBody (actual: string) (expected: string) : string =
        let error = JsonObject()
        error["type"] <- JsonValue.Create "FORBIDDEN"
        error["message"] <- JsonValue.Create(sprintf "is at %s but expected %s" actual expected)
        let errors = JsonArray()
        errors.Add error
        let body = JsonObject()
        let data = JsonObject()
        data["createCommitOnBranch"] <- null
        body["data"] <- data
        body["errors"] <- errors
        body.ToJsonString()

    /// The answer forms known before the CI probe has run. Each is DocumentedForm: a restatement, not a capture.
    let documentedForms: Recording list =
        let gql (errorType: string) (message: string) =
            sprintf """{"data":{"createCommitOnBranch":null},"errors":[{"type":"%s","message":"%s"}]}""" errorType message

        [ { Name = "stale-head-forbidden-moved-head"
            MatrixClass = "GH-STALE-HEAD"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = [ "content-type", "application/json; charset=utf-8"; "x-ratelimit-remaining", "4990" ]
            Body = movedHeadBody oidA oidB
            Provenance = documented }
          { Name = "stale-head-stale-data"
            MatrixClass = "GH-STALE-HEAD"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = gql "STALE_DATA" ("Expected branch to point to " + oidB)
            Provenance = documented }
          { Name = "permission-forbidden"
            MatrixClass = "GH-PERMISSION"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = gql "FORBIDDEN" "Resource not accessible by personal access token"
            Provenance = documented }
          { Name = "permission-rest-403"
            MatrixClass = "GH-PERMISSION"
            Operation = "rest:contents"
            Status = 403
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = """{"message":"Resource not accessible by personal access token","status":"403"}"""
            Provenance = documented }
          { Name = "auth-401"
            MatrixClass = "GH-AUTH"
            Operation = "rest:ref"
            Status = 401
            Headers = []
            Body = """{"message":"Bad credentials","status":"401"}"""
            Provenance = documented }
          { Name = "rate-primary-403"
            MatrixClass = "GH-RATE-PRIMARY"
            Operation = "rest:contents"
            Status = 403
            Headers = [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1793557504" ]
            Body = """{"message":"API rate limit exceeded for user."}"""
            Provenance = documented }
          { Name = "rate-secondary-403-retry-after"
            MatrixClass = "GH-RATE-SECONDARY"
            Operation = "rest:contents"
            Status = 403
            Headers = [ "retry-after", "60"; "x-ratelimit-remaining", "4990" ]
            Body = """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}"""
            Provenance = documented }
          { Name = "rate-secondary-graphql"
            MatrixClass = "GH-RATE-SECONDARY"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = gql "RATE_LIMITED" "You have exceeded a secondary rate limit."
            Provenance = documented }
          { Name = "validation-422"
            MatrixClass = "GH-VALIDATION"
            Operation = "rest:contents"
            Status = 422
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = """{"message":"Invalid request.","errors":["sha wasn't supplied."],"status":"422"}"""
            Provenance = documented }
          { Name = "validation-graphql-unprocessable"
            MatrixClass = "GH-VALIDATION"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = []
            Body = gql "UNPROCESSABLE" "A path was requested for deletion which does not exist"
            Provenance = documented }
          { Name = "not-found-404"
            MatrixClass = "GH-NOT-FOUND"
            Operation = "rest:contents"
            Status = 404
            Headers = [ "x-ratelimit-remaining", "4990" ]
            Body = """{"message":"Not Found","status":"404"}"""
            Provenance = documented }
          { Name = "not-found-graphql"
            MatrixClass = "GH-NOT-FOUND"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = []
            Body = gql "NOT_FOUND" "Could not resolve to a Ref"
            Provenance = documented }
          { Name = "unknown-502"
            MatrixClass = "GH-UNKNOWN-OUTCOME"
            Operation = "graphql:createCommitOnBranch"
            Status = 502
            Headers = []
            Body = "<html>Bad gateway</html>"
            Provenance = documented }
          { Name = "malformed-200"
            MatrixClass = "GH-MALFORMED"
            Operation = "graphql:createCommitOnBranch"
            Status = 200
            Headers = []
            Body = """{"data":{"createCommitOnBranch":{"commit":{}}}}"""
            Provenance = documented }
          { Name = "large-file-encoding-none"
            MatrixClass = "GH-LARGE-FILE"
            Operation = "rest:contents"
            Status = 200
            Headers = []
            Body = """{"type":"file","encoding":"none","size":1500000,"content":""}"""
            Provenance = documented } ]

    /// Recordings captured live and committed after a CI probe run. Empty until the first probe's artifact is
    /// reviewed and committed here as CapturedLive entries (nothing is invented in the meantime).
    let captured: Recording list = []

    let all: Recording list = captured @ documentedForms

    let private provenanceNode (p: Provenance) : JsonObject =
        let o = JsonObject()

        match p with
        | CapturedLive(runId, at, sha) ->
            o["kind"] <- JsonValue.Create "captured-live"
            o["runId"] <- JsonValue.Create runId
            o["capturedAt"] <- JsonValue.Create at
            o["sourceSha"] <- JsonValue.Create sha
        | DocumentedForm source ->
            o["kind"] <- JsonValue.Create "documented-form"
            o["source"] <- JsonValue.Create source

        o

    /// Serialize recordings (headers and bodies already sanitized) for the probe's output artifact.
    let toJson (recordings: Recording list) : string =
        let arr = JsonArray()

        for r in recordings do
            let o = JsonObject()
            o["name"] <- JsonValue.Create r.Name
            o["matrixClass"] <- JsonValue.Create r.MatrixClass
            o["operation"] <- JsonValue.Create r.Operation
            o["status"] <- JsonValue.Create r.Status
            let h = JsonObject()

            for (k, v) in r.Headers do
                h[k] <- JsonValue.Create v

            o["headers"] <- h
            o["body"] <- JsonValue.Create r.Body
            o["provenance"] <- provenanceNode r.Provenance
            arr.Add o

        arr.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true))

    /// The recording as the outcome of a request, for the classifier and the fake.
    let asOutcome (id: RequestId) (r: Recording) : HttpOutcome = Answered(id, r.Status, r.Headers, r.Body)
