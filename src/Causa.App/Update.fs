// Owner: ui (WI-R8-T22). The engine's domain-command handling and its network/connection steps, as additive pure
// functions beside the frozen IF41-APP shapes (CR-T17-01: the frozen Msg.DomainCommand of CommandEnvelope cannot carry
// the command body, DEV-R8-09). Integration retires Program.fs's interim Submit by calling Update.submit.
namespace Causa.App

open Causa.Domain
open Causa.Persistence

/// The kernel's answer to one submitted command.
type SubmitOutcome =
    | CommandAccepted of command: Command * events: EventEnvelope list
    | CommandRefused of command: Command * decision: Decision

[<RequireQualifiedAccess>]
module Update =
    /// The envelope a command is sent in: the expected version is the version of the history it was decided on.
    let envelope (commandId: System.Guid) (investigation: InvestigationId) (history: EventEnvelope list) : CommandEnvelope =
        { CommandId = commandId
          Investigation = investigation
          ExpectedVersion = history |> List.tryLast |> Option.map _.Version }

    /// Pure: one domain command decided by the kernel against the history (Kernel.decideOn), and on Allowed the
    /// stamped events (Kernel.stamp) appended. A refusal returns the history unchanged with the kernel's decision.
    let submit
        (policy: PolicySet)
        (actor: ActorContext)
        (history: EventEnvelope list)
        (envelope: CommandEnvelope)
        (command: Command)
        : EventEnvelope list * SubmitOutcome =
        match Kernel.decideOn policy actor history { Envelope = envelope; Command = command } with
        | Allowed payloads ->
            let summary = Kernel.replay history |> Result.toOption
            let events = Kernel.stamp envelope summary payloads
            history @ events, CommandAccepted(command, events)
        | decision -> history, CommandRefused(command, decision)

    /// A save machine effect as the host effect that performs it (effects stay data until the host runs them).
    let hostEffect (effect: SaveEffect) : HostEffect =
        match effect with
        | Send request -> Http request
        | ScheduleReconcileRead(id, ms) -> Timer(ReconcileTimer id, ms)
        | Announce(politeness, text) -> LiveAnnounce(politeness, text)
        | ExportCommands commands ->
            let ids = commands |> List.map (fun c -> "\"" + c.CommandId.ToString "D" + "\"") |> String.concat ","
            Download("causa-unsaved-commands.json", "{\"commands\":[" + ids + "]}")

    let private requestContext (model: Model) (context: ConnectionContext) : RequestContext =
        { Connection = context
          NextId = model.NextRequestId
          MutationInFlightElsewhere = false }

    let private advance (RequestId n) = RequestId(n + 1L)

    /// T-W01 / T-W02: a network transition sends WentOffline / WentOnline to every investigation's SM-SAVE. An
    /// illegal network pair fails closed (state unchanged, no effect). A session whose step faults keeps its state.
    let network (machine: SaveMachine) (input: NetworkInput) (model: Model) : Result<Model * HostEffect list, SaveFault> =
        NetworkMachine.step model.Network input
        |> Result.map (fun (network, saveInput) ->
            let folded =
                model.Sessions
                |> Map.fold
                    (fun (current: Model, effects) id session ->
                        match current.Context with
                        | None -> current, effects
                        | Some context ->
                            match machine.Step (requestContext current context) session saveInput with
                            | Ok(next, saveEffects) ->
                                let sent = saveEffects |> List.exists (function Send _ -> true | _ -> false)

                                { current with
                                    Sessions = Map.add id next current.Sessions
                                    NextRequestId = if sent then advance current.NextRequestId else current.NextRequestId },
                                effects @ (saveEffects |> List.map hostEffect)
                            | Error _ -> current, effects)
                    ({ model with Network = network }, [])

            folded)

    /// One SM-CONNECTION step on the model: the context is kept on Connect, the credential replaced on Reconnect and
    /// dropped (with the tab state) on Disconnect. Illegal pairs fail closed.
    let connection (input: ConnectionInput) (model: Model) : Result<Model, SaveFault> =
        ConnectionMachine.step model.NextRequestId model.Connection input
        |> Result.map (fun (state, effects) ->
            effects
            |> List.fold
                (fun (m: Model) effect ->
                    match effect with
                    | ConnectionMachine.KeepContext context ->
                        { m with
                            Context = Some context
                            NextRequestId = advance m.NextRequestId }
                    | ConnectionMachine.ReplaceCredential credential ->
                        { m with
                            Context = m.Context |> Option.map (fun c -> { c with Credential = credential })
                            NextRequestId = advance m.NextRequestId }
                    | ConnectionMachine.DropCredentialAndTabState ->
                        { m with
                            Context = None
                            Loads = Map.empty
                            Sessions = Map.empty }
                    | ConnectionMachine.StateCause _
                    | ConnectionMachine.CredentialNoLongerAccepted -> m)
                { model with Connection = state })
