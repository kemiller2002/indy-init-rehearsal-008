// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open System
open Xunit
open Causa.Persistence
open Causa.Invariants

/// INV-21 (PF-015-O4): the credential is sent only in the Authorization header over HTTPS to the GitHub API, never in a
/// URL, body, log or printed value; every read and write carries it. Rehearsal 7 F14, written from the invariant.
module INV21 =
    let private token = Fixture.testToken

    let private printed: (string * string) list =
        let credential = Fixture.connection.Credential

        [ "ToString", credential.ToString()
          "%A credential", sprintf "%A" credential
          "%O credential", sprintf "%O" credential
          "%A connection", sprintf "%A" Fixture.connection
          "%A request context", sprintf "%A" (Fixture.context 1L)
          "string interpolation", $"{credential}"
          "JSON",
          (try
              Text.Json.JsonSerializer.Serialize Fixture.connection
           with ex ->
               ex.GetType().Name) ]

    let private formattingCase () =
        let leaks = printed |> List.filter (fun (_, text) -> text.Contains token) |> List.map fst
        Assert.True(List.isEmpty leaks, sprintf "the credential is printed by: %A" leaks)

    let private headerCase () =
        let name, value = Credential.authorization Fixture.connection.Credential
        Assert.Equal("Authorization", name)
        Assert.Equal("Bearer " + token, value)

    let private requestsCase () =
        let machine = Require.saveMachine ()

        Check.check 150 Moves.sequence (fun moves ->
            Drive.run machine moves
            |> List.collect (Drive.effects >> Requests.sends)
            |> List.map (fun request ->
                let authorization = request.Headers |> List.filter (fun (n, _) -> String.Equals(n, "Authorization", StringComparison.OrdinalIgnoreCase))
                let others = request.Headers |> List.filter (fun (n, _) -> not (String.Equals(n, "Authorization", StringComparison.OrdinalIgnoreCase)))

                Check.all
                    [ Check.expect (request.Url.StartsWith "https://api.github.com/") (sprintf "request to %s" request.Url)
                      Check.expect (not (request.Url.Contains token)) "credential in a URL"
                      Check.expect (request.Body |> Option.forall (fun b -> not (b.Contains token))) "credential in a body"
                      Check.expect (others |> List.forall (fun (_, v) -> not (v.Contains token))) "credential in another header"
                      Check.expect (authorization = [ Credential.authorization Fixture.connection.Credential ]) "a request without exactly one Authorization header" ])
            |> Check.all)

    [<Theory>]
    [<InlineData("formatting")>]
    [<InlineData("authorization-header")>]
    [<InlineData("requests")>]
    let CredentialDestinations (case: string) =
        match case with
        | "formatting" -> formattingCase ()
        | "authorization-header" -> headerCase ()
        | _ -> requestsCase ()
