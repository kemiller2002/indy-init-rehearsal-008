// Owner: ui (WI-R8-T22). SM-CONNECTION and SM-NETWORK (requirements-compiled/v4.1.1/state-machine-contracts.json).
// Pure and total: every (state, event) pair is decided here; a pair the contract does not list fails closed with a
// typed fault naming the machine, the state and the event, and the state is unchanged (invalidTransition rule).
namespace Causa.App

open Causa.Persistence

[<RequireQualifiedAccess>]
module ConnectionMachine =
    [<Literal>]
    let Name = "SM-CONNECTION"

    let stateName (state: ConnectionState) =
        match state with
        | Disconnected -> "Disconnected"
        | Connecting _ -> "Connecting"
        | Connected _ -> "Connected"
        | CredentialRefused -> "CredentialRefused"
        | Reauthenticating -> "Reauthenticating"

    let inputName (input: ConnectionInput) =
        match input with
        | ConnectionInput.ConnectSubmitted _ -> "ConnectSubmitted"
        | ConnectionInput.ConnectSucceeded _ -> "ConnectSucceeded"
        | ConnectionInput.ConnectRefused _ -> "ConnectRefused"
        | ConnectionInput.AuthFailed -> "AuthFailed"
        | ConnectionInput.ReconnectSubmitted _ -> "ReconnectSubmitted"
        | ConnectionInput.DisconnectConfirmed -> "DisconnectConfirmed"

    /// The guard "credential not blank" (T-N01, T-N05) is enforced where the credential is made: the Connect and
    /// Reconnect forms produce a credential only from a non-blank entry, so a blank entry never becomes an event.
    let credentialOf (entry: string) : Credential.T option =
        match entry.Trim() with
        | "" -> None
        | token -> Some(Credential.create token)

    let private fault (state: ConnectionState) (input: ConnectionInput) : SaveFault =
        { Machine = Name
          State = stateName state
          Input = inputName input
          Reason = sprintf "%s/%s/%s is not a transition of %s" Name (stateName state) (inputName input) Name }

    /// What a legal transition does besides changing state (the contract's effects, as data).
    type Effect =
        /// T-N01: keep the connection context (credential in memory only) and send the Connect check.
        | KeepContext of ConnectionContext
        /// T-N05: replace the credential of the same repository and check it.
        | ReplaceCredential of Credential.T
        /// T-N03, T-N07: state the cause (never the credential).
        | StateCause of ProviderError
        /// T-N04: affected saves go to SaveFailed with Reconnect; investigations and unsaved changes are kept.
        | CredentialNoLongerAccepted
        /// T-N08, T-N09: drop the credential and the tab state.
        | DropCredentialAndTabState

    /// Pure: one SM-CONNECTION step. `next` is the identity the Connect check request carries (T-N01).
    let step (next: RequestId) (state: ConnectionState) (input: ConnectionInput) : Result<ConnectionState * Effect list, SaveFault> =
        match state, input with
        | Disconnected, ConnectionInput.ConnectSubmitted(repository, credential) ->
            Ok(Connecting next, [ KeepContext { Repository = repository; Credential = credential } ])
        | Connecting _, ConnectionInput.ConnectSucceeded canWrite -> Ok(Connected canWrite, [])
        | Connecting _, ConnectionInput.ConnectRefused error -> Ok(Disconnected, [ StateCause error ])
        | Connected _, ConnectionInput.AuthFailed -> Ok(CredentialRefused, [ CredentialNoLongerAccepted ])
        | Connected _, ConnectionInput.DisconnectConfirmed -> Ok(Disconnected, [ DropCredentialAndTabState ])
        | CredentialRefused, ConnectionInput.ReconnectSubmitted credential -> Ok(Reauthenticating, [ ReplaceCredential credential ])
        | CredentialRefused, ConnectionInput.DisconnectConfirmed -> Ok(Disconnected, [ DropCredentialAndTabState ])
        | Reauthenticating, ConnectionInput.ConnectSucceeded canWrite -> Ok(Connected canWrite, [])
        | Reauthenticating, ConnectionInput.ConnectRefused error -> Ok(CredentialRefused, [ StateCause error ])
        | _ -> Error(fault state input)

    /// The words the workspace header uses for the connection (PERSIST-033-O1, EXT-DATA-007). Never the credential.
    let label (state: ConnectionState) =
        match state with
        | Disconnected -> "Not connected"
        | Connecting _ -> "Connecting to GitHub"
        | Connected true -> "Connected to GitHub (read and write)"
        | Connected false -> "Connected to GitHub (read only)"
        | CredentialRefused -> "GitHub refused the credential"
        | Reauthenticating -> "Checking the new credential"

[<RequireQualifiedAccess>]
module NetworkMachine =
    [<Literal>]
    let Name = "SM-NETWORK"

    let stateName (state: NetworkState) =
        match state with
        | NetworkState.Online -> "Online"
        | NetworkState.Offline -> "Offline"

    let inputName (input: NetworkInput) =
        match input with
        | NetworkInput.OfflineObserved -> "OfflineObserved"
        | NetworkInput.OnlineObserved -> "OnlineObserved"

    /// The SM-SAVE event a legal network transition sends to every investigation in the tab (T-W01, T-W02).
    let saveInputFor (input: NetworkInput) : SaveInput =
        match input with
        | NetworkInput.OfflineObserved -> SaveInput.WentOffline
        | NetworkInput.OnlineObserved -> SaveInput.WentOnline

    /// Pure: one SM-NETWORK step; on a legal step, the SM-SAVE input every investigation receives.
    let step (state: NetworkState) (input: NetworkInput) : Result<NetworkState * SaveInput, SaveFault> =
        match state, input with
        | NetworkState.Online, NetworkInput.OfflineObserved -> Ok(NetworkState.Offline, saveInputFor input)
        | NetworkState.Offline, NetworkInput.OnlineObserved -> Ok(NetworkState.Online, saveInputFor input)
        | _ ->
            Error
                { Machine = Name
                  State = stateName state
                  Input = inputName input
                  Reason = sprintf "%s/%s/%s is not a transition of %s" Name (stateName state) (inputName input) Name }

    /// A browser connectivity signal to the network event it means, or None when it tells the tab nothing new.
    let observe (state: NetworkState) (online: bool) : NetworkInput option =
        match state, online with
        | NetworkState.Online, false -> Some NetworkInput.OfflineObserved
        | NetworkState.Offline, true -> Some NetworkInput.OnlineObserved
        | _ -> None
