// Owner: integration. WI-R8-T17: the engine's wire edge as the host shim calls it (Limen JSON in, Limen JSON out).
// A typed command crosses decode -> step -> encode, the domain kernel decides it, and the answer is one complete
// ViewState from one projection; a refusal carries the kernel's reason and changes nothing else.
module Causa.Contract.Tests.App.ConsumerIntegration

open System.Text.Json
open Xunit
open Causa.Domain
open Causa.App

let private initialize = """{"kind":"Initialize","location":{"path":"/dist/index.html","hash":"#/investigations/new"}}"""
let private event (name: string) = """{"kind":"Event","event":{"name":""" + "\"" + name + "\"}}"

/// Runs messages through the pure wire edge, returning the final state and every encoded answer.
let private run (messages: string list) =
    messages
    |> List.fold
        (fun (state, answers) message ->
            let next, answer = Program.step state (Program.decode message)
            next, answers @ [ Program.encode answer ])
        (View.initial, [])

let private view (json: string) = JsonDocument.Parse(json).RootElement.GetProperty "view"
let private text (json: string) (key: string) = (view json).GetProperty(key).GetString()
let private number (json: string) (key: string) = (view json).GetProperty(key).GetDouble()

[<Fact>]
let ShimRoundTrip () =
    let state, answers =
        run [ initialize; event "submit.StartInvestigation"; event "submit.ActivateInvestigation"; event "submit.RecordObservation" ]

    let started, refused, observed = answers[1], answers[2], answers[3]

    // Allowed: one event stamped at the genesis version and evolved into the projection.
    Assert.Equal("StartInvestigation accepted: InvestigationStarted.", text started "outcomeText")
    Assert.Equal(1.0, number started "projectionVersion")
    Assert.Equal("Draft", text started "investigationLifecycle")
    Assert.Contains("(activation-policy)", text started "capabilityActivateInvestigation")

    // Refused by the kernel's policy gate, with its reason; the projection is unchanged.
    Assert.Equal("refused", text refused "outcomeState")
    Assert.Contains("(activation-policy)", text refused "outcomeText")
    Assert.Equal(1.0, number refused "projectionVersion")
    Assert.Equal(1.0, number refused "timelineCount")

    // The next legal command continues from the same history.
    Assert.Equal(2.0, number observed "projectionVersion")
    Assert.Equal("ObservationRecorded", text observed "timelineLatest")

    // The tab's history replays through the kernel to the version every view showed.
    let history = state.Workspace.History
    Assert.Equal(Ok(AggregateVersion 2L), Kernel.replay history |> Result.map _.Version)
    Assert.All(history, fun e -> Assert.Equal(Workspace.investigation, e.Investigation))

    // Every answer is a complete Limen message with the same keys (a ViewState is never a patch).
    let keys (json: string) =
        (view json).EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    Assert.All(answers, fun a -> Assert.Equal<Set<string>>(keys answers[0], keys a))

[<Fact>]
let ``unknown events and unreadable messages leave the state unchanged`` () =
    let state, _ = run [ initialize; event "submit.StartInvestigation" ]

    for message in [ event "submit.Nothing"; event "increment"; "not json"; """{"kind":"Mystery"}""" ] do
        let next, answer = Program.step state (Program.decode message)
        Assert.Equal(state, next)
        Assert.Equal(1.0, number (Program.encode answer) "projectionVersion")

[<Fact>]
let ``every offered command is a Limen event the engine decodes back to the same command`` () =
    for command in Workspace.offered do
        Assert.Equal(Some command, Workspace.commandOfEvent (Workspace.eventName command))
