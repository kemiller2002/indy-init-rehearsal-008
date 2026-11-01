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
/// A count or version key: since CR-T22-1 (Views/Investigation.fs) these are Limen strings holding the integer.
let private number (json: string) (key: string) = (view json).GetProperty(key).GetString() |> float

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

// WI-R8-CAPS: the handshake answer to Initialize selects Limen's schedule and files capabilities when the host offers
// exactly the identities this engine was built against, and no handshake (Limen legacy mode) for any other host.
let private offer (capabilities: LimenIdentity list) : HostOffer =
    { Major = 1
      Minor = 4
      Contract = LimenHandshake.core
      Capabilities = capabilities }

[<Fact>]
let ``the handshake selects the offered schedule and files capabilities`` () =
    let answer = LimenHandshake.answer (Some(offer [ LimenHandshake.files; LimenHandshake.schedule ]))
    Assert.Equal(Some [ LimenHandshake.schedule; LimenHandshake.files ], answer |> Option.map _.Selected)
    Assert.Equal(Some 4, answer |> Option.map _.Minor)

[<Fact>]
let ``a host with another contract, an unknown capability version or no offer gets no capability`` () =
    Assert.Equal(None, LimenHandshake.answer None)
    Assert.Equal(None, LimenHandshake.answer (Some { offer [ LimenHandshake.schedule ] with Contract = { LimenHandshake.core with Version = 2 } }))
    let changed = LimenHandshake.answer (Some(offer [ { LimenHandshake.schedule with Fingerprint = "sha256:other" } ]))
    Assert.Equal(Some [], changed |> Option.map _.Selected)

[<Fact>]
let ``Initialize with a host offer is answered with an Accepted handshake on the wire`` () =
    let message =
        """{"kind":"Initialize","protocolVersion":1,"capabilities":[],"location":{"path":"/","query":"","hash":""},"handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":[{"id":"limen.schedule","version":1,"fingerprint":"sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0"}]}}"""

    let _, answer = Program.step View.initial (Program.decode message)
    let handshake = JsonDocument.Parse(Program.encode answer).RootElement.GetProperty "handshake"
    Assert.Equal("Accepted", handshake.GetProperty("kind").GetString())
    Assert.Equal("limen.schedule", handshake.GetProperty("capabilities").[0].GetProperty("id").GetString())
    Assert.Equal(1, handshake.GetProperty("capabilities").GetArrayLength())

[<Fact>]
let ``a reconcile timer becomes a limen.schedule timeout and its Fired answer the engine's TimerFired`` () =
    let tab = { Program.initialTab with Capabilities = set [ LimenHandshake.schedule.Id ] }
    let key = ReconcileTimer(InvestigationId(System.Guid.NewGuid()))
    let next, wire = Program.toWire tab [ Timer(key, 2000); SetLeaveGuard true ]
    Assert.Equal<WireEffect list>([ WireEffect.Timeout("schedule-1", 2000) ], wire)
    Assert.Equal(Some key, Map.tryFind "schedule-1" next.Timers)

    let fired =
        """{"kind":"EffectResult","result":{"kind":"CapabilityResult","correlationId":"schedule-1","capability":"limen.schedule","version":1,"outcome":{"kind":"Completed","result":{"kind":"Fired","elapsedMs":2001}}}}"""

    Assert.Equal(Incoming.CapabilityResult("schedule-1", "limen.schedule", CapabilityAnswer.Fired), Program.decode fired)
    let after, _ = Program.stepTab System.Guid.NewGuid next (Program.decode fired)
    Assert.True(after.Timers.IsEmpty)

[<Fact>]
let ``without the capability a timer is dropped and a download says it cannot be offered`` () =
    let next, wire = Program.toWire Program.initialTab [ Timer(ReconcileTimer(InvestigationId(System.Guid.NewGuid())), 2000); Download("f.json", "{}") ]
    Assert.Empty wire
    Assert.Equal("This browser cannot download the export.", next.Notice)
