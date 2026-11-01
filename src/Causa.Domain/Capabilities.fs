// Owner: domain. WI-R8-T13: capabilities derived in F# from the same decision path, with reasons for blocked actions.
namespace Causa.Domain

type Availability =
    | Available
    | Unavailable of Blocker list

type Capability = { Command: Command; Availability: Availability }

[<RequireQualifiedAccess>]
module Capabilities =
    let private reasons (decision: Decision) : Availability =
        match decision with
        | Allowed _ -> Available
        | Blocked blockers -> Unavailable blockers
        | UnsupportedSemantics names ->
            Unavailable [ Blockers.make "unsupported-semantics" (sprintf "Unsupported semantics: %s." (String.concat ", " names)) ]
        | RequiresReview reasons -> Unavailable(reasons |> List.map (Blockers.make "requires-review"))
        | Conflict(expected, actual) -> Unavailable [ Blockers.make "version-conflict" (sprintf "Expected %A, found %A." expected actual) ]
        | Duplicate _ -> Unavailable [ Blockers.make "duplicate" "This command was already applied." ]

    /// Every command with whether it is available now and, when it is not, why.
    let derive (policy: PolicySet) (actor: ActorContext) (state: InvestigationState option) : Capability list =
        Command.all
        |> List.map (fun command ->
            { Command = command
              Availability = reasons (Decide.evaluate policy actor state command) })

    let availableNames (capabilities: Capability list) : string list =
        capabilities
        |> List.choose (fun c ->
            match c.Availability with
            | Available -> Some(Command.name c.Command)
            | Unavailable _ -> None)
