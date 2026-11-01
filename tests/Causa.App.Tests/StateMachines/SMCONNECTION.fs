// Owner: ui (WI-R8-T22). SM-CONNECTION (state-machine-contracts.json): every legal transition by its id, and every
// pair the contract does not list fails closed with a typed fault and an unchanged state.
namespace Causa.App.Tests.StateMachines

open Xunit
open Causa.Domain
open Causa.Persistence
open Causa.App

module SMCONNECTION =
    let private repository = { Owner = "kemiller2002"; Name = "causa-data"; Branch = "main" }
    let private secret = "ghp_SmConnectionTestToken0000000000000000"
    let private credential = Credential.create secret
    let private next = RequestId 7L

    let private model connection : Model =
        { Connection = connection
          Context = Some { Repository = repository; Credential = credential }
          NextRequestId = next
          Network = NetworkState.Online
          Route = Route "#/"
          Loads = Map.empty
          Sessions = Map.empty }

    let private stepOk state input =
        match ConnectionMachine.step next state input with
        | Ok result -> result
        | Error fault -> failwithf "expected a legal transition, got %A" fault

    let private states =
        [ Disconnected; Connecting next; Connected true; CredentialRefused; Reauthenticating ]

    let private inputs =
        [ ConnectionInput.ConnectSubmitted(repository, credential)
          ConnectionInput.ConnectSucceeded true
          ConnectionInput.ConnectRefused AuthenticationFailed
          ConnectionInput.AuthFailed
          ConnectionInput.ReconnectSubmitted credential
          ConnectionInput.DisconnectConfirmed ]

    /// The nine legal (state, event) pairs of the contract.
    let private legal =
        set
            [ "Disconnected", "ConnectSubmitted"
              "Connecting", "ConnectSucceeded"
              "Connecting", "ConnectRefused"
              "Connected", "AuthFailed"
              "Connected", "DisconnectConfirmed"
              "CredentialRefused", "ReconnectSubmitted"
              "CredentialRefused", "DisconnectConfirmed"
              "Reauthenticating", "ConnectSucceeded"
              "Reauthenticating", "ConnectRefused" ]

    [<Fact>]
    let ``T-N01`` () =
        let state, effects = stepOk Disconnected (ConnectionInput.ConnectSubmitted(repository, credential))
        Assert.Equal(Connecting next, state)
        Assert.Equal<ConnectionMachine.Effect list>([ ConnectionMachine.KeepContext { Repository = repository; Credential = credential } ], effects)
        // Guard "credential not blank": a blank entry never becomes a credential, so it never becomes an event.
        Assert.True((ConnectionMachine.credentialOf "   ").IsNone)
        Assert.True((ConnectionMachine.credentialOf "").IsNone)
        Assert.True((ConnectionMachine.credentialOf secret).IsSome)
        // Kept in memory only: the model keeps the context; the credential prints as an opaque value.
        let after = Update.connection (ConnectionInput.ConnectSubmitted(repository, credential)) { model Disconnected with Context = None }

        match after with
        | Ok m ->
            Assert.Equal(Some repository, m.Context |> Option.map _.Repository)
            Assert.DoesNotContain(secret, sprintf "%A" m)
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``T-N02`` () =
        Assert.Equal(Connected false, fst (stepOk (Connecting next) (ConnectionInput.ConnectSucceeded false)))
        // Write is never inferred: the recorded permission is the one the Connect check reported.
        Assert.Equal(Connected true, fst (stepOk (Connecting next) (ConnectionInput.ConnectSucceeded true)))

    [<Fact>]
    let ``T-N03`` () =
        let state, effects = stepOk (Connecting next) (ConnectionInput.ConnectRefused(PermissionDenied "read"))
        Assert.Equal(Disconnected, state)
        Assert.Equal<ConnectionMachine.Effect list>([ ConnectionMachine.StateCause(PermissionDenied "read") ], effects)
        Assert.DoesNotContain(secret, ProviderMessage.ofError (PermissionDenied "read"))

    [<Fact>]
    let ``T-N04`` () =
        let state, effects = stepOk (Connected true) ConnectionInput.AuthFailed
        Assert.Equal(CredentialRefused, state)
        Assert.Equal<ConnectionMachine.Effect list>([ ConnectionMachine.CredentialNoLongerAccepted ], effects)

        match Update.connection ConnectionInput.AuthFailed (model (Connected true)) with
        | Ok m -> Assert.True(m.Context.IsSome, "investigations, unsaved changes and the connection context are kept")
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``T-N05`` () =
        let fresh = Credential.create "ghp_SmConnectionReplacementToken00000000"
        let state, effects = stepOk CredentialRefused (ConnectionInput.ReconnectSubmitted fresh)
        Assert.Equal(Reauthenticating, state)
        Assert.Equal<ConnectionMachine.Effect list>([ ConnectionMachine.ReplaceCredential fresh ], effects)

        match Update.connection (ConnectionInput.ReconnectSubmitted fresh) (model CredentialRefused) with
        | Ok m -> Assert.Equal(Some repository, m.Context |> Option.map _.Repository)
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``T-N06`` () =
        Assert.Equal(Connected true, fst (stepOk Reauthenticating (ConnectionInput.ConnectSucceeded true)))

    [<Fact>]
    let ``T-N07`` () =
        let state, effects = stepOk Reauthenticating (ConnectionInput.ConnectRefused AuthenticationFailed)
        Assert.Equal(CredentialRefused, state)
        Assert.Equal<ConnectionMachine.Effect list>([ ConnectionMachine.StateCause AuthenticationFailed ], effects)

    let private dropsEverything from =
        match Update.connection ConnectionInput.DisconnectConfirmed (model from) with
        | Ok m ->
            Assert.Equal(Disconnected, m.Connection)
            Assert.True(m.Context.IsNone, "the credential is dropped")
            Assert.True(m.Sessions.IsEmpty && m.Loads.IsEmpty, "the tab state is dropped")
        | Error fault -> failwithf "%A" fault

    [<Fact>]
    let ``T-N08`` () = dropsEverything (Connected true)

    [<Fact>]
    let ``T-N09`` () = dropsEverything CredentialRefused

    [<Fact>]
    let EveryIllegalPairFailsClosed () =
        let illegal =
            [ for s in states do
                  for i in inputs do
                      if not (legal.Contains(ConnectionMachine.stateName s, ConnectionMachine.inputName i)) then
                          yield s, i ]

        Assert.Equal(21, illegal.Length)

        for state, input in illegal do
            match ConnectionMachine.step next state input with
            | Ok result -> failwithf "%A on %A should fail closed, got %A" input state result
            | Error fault ->
                Assert.Equal("SM-CONNECTION", fault.Machine)
                Assert.Equal(ConnectionMachine.stateName state, fault.State)
                Assert.Equal(ConnectionMachine.inputName input, fault.Input)
                Assert.DoesNotContain(secret, fault.Reason)

            // On the model: the state, the context and the tab state are unchanged.
            let before = model state

            match Update.connection input before with
            | Ok m -> failwithf "%A on %A should fail closed, got %A" input state m
            | Error _ -> ()
