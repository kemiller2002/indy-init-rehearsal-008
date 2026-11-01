// Owner: ui. Written at bootstrap by requirements-compiler v41; the owner replaces this content.
namespace Causa.App

open Causa.Domain
open Causa.Persistence

/// IF41-APP: the Limen engine boundary (App <-> browser host). The host relays only what the engine asks for.
/// The credential is opaque (IF41-WIRE Credential): created once from the Connect form, kept only in the model's
/// connection context, never rendered, logged or stored.
/// SM-CONNECTION states.
type ConnectionState =
    | Disconnected
    | Connecting of RequestId
    | Connected of canWrite: bool
    | CredentialRefused
    | Reauthenticating

/// SM-CONNECTION events.
[<RequireQualifiedAccess>]
type ConnectionInput =
    | ConnectSubmitted of Repository * Credential.T
    | ConnectSucceeded of canWrite: bool
    | ConnectRefused of ProviderError
    | AuthFailed
    | ReconnectSubmitted of Credential.T
    | DisconnectConfirmed

/// SM-NETWORK states.
[<RequireQualifiedAccess>]
type NetworkState =
    | Online
    | Offline

/// SM-NETWORK events.
[<RequireQualifiedAccess>]
type NetworkInput =
    | OfflineObserved
    | OnlineObserved

/// What a host timer is for.
type TimerKey = ReconcileTimer of InvestigationId

type BrowserSignal =
    | ConnectivityChanged of online: bool
    | TimerFired of TimerKey
    | HttpCompleted of HttpOutcome
    | LeaveAttempted

type HostEffect =
    | Http of HttpRequest
    | Timer of TimerKey * milliseconds: int
    | SetLeaveGuard of bool
    | Download of fileName: string * content: string
    | LiveAnnounce of Politeness * text: string

type Route = Route of string

type Msg =
    | Navigate of Route
    | Browser of BrowserSignal
    | Connection of ConnectionInput
    | Network of NetworkInput
    | SessionInput of InvestigationId * SaveInput
    | ConflictStep of InvestigationId * ConflictInput
    | LoadStep of InvestigationId * LoadInput
    | DomainCommand of CommandEnvelope

/// The model keeps the connection context after Connect succeeds and the next request identity of the tab.
type Model =
    { Connection: ConnectionState
      Context: ConnectionContext option
      NextRequestId: RequestId
      Network: NetworkState
      Route: Route
      Loads: Map<InvestigationId, LoadState>
      Sessions: Map<InvestigationId, InvestigationSession> }

type Engine =
    { Init: Route -> Model * HostEffect list
      Update: Msg -> Model -> Model * HostEffect list }
