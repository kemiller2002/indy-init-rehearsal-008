// Owner: ui-2. WI-R8-T29: the Administration page (UI-018, VER-007) and the Settings page (UI-019).
// Pure: what the running application has actually loaded, as facts, to the flat keys and row lists web/index.html binds.
// A version is shown only when the loaded component itself reports it: the engine's own assembly (application version
// and build commit), the .NET runtime it runs on, the storage codec compiled into the loaded Causa.Persistence, and the
// browser's report of the Echelon libraries that registered themselves on the page (window.__ECHELON_RUNTIME__,
// relayed by web/boundary.js as the Limen event "runtime.reported"). The engine never touches browser APIs: the browser
// reports facts as data. A component that does not report its version, or a fact the application cannot know, is shown
// as unavailable, never invented (VER-007-O2, UI-018-O4). Each fact is in one of four states (UI-018-O3): verified,
// unknown (not reported yet), unavailable (cannot be known here) or failed. Administration is read-only (UI-018-O6) and
// its keys are bound only inside the Administration section (UI-018-O5, VER-007-O1). Settings shows only what this
// version supports, with scope and effective value, and keeps preferences apart from repository access the provider
// checks (UI-019). Settings carries no version, commit or provider detail (UI-031-O4, UI-021-O2).
namespace Causa.App.Views

open System
open System.Reflection
open System.Text.Json
open Causa.App
open Causa.Domain
open Causa.Persistence

module Administration =
    /// UI-018-O3: the four states a fact can be in.
    type FactState =
        | Verified of value: string
        | Unknown of reason: string
        | Unavailable of reason: string
        | Failed of reason: string

    type Fact = { Key: string; Label: string; State: FactState; Source: string }

    /// What the browser reported about the libraries it loaded: name to version, as registered on the page.
    type BrowserReport =
        | NotReported
        | Reported of Map<string, string>
        | Unreadable of reason: string

    /// Facts the loaded engine knows about itself (read once at the effect edge by `loadedEngine`).
    type EngineFacts =
        { InformationalVersion: string option
          Runtime: string option }

    /// The Limen event web/boundary.js sends with the browser's report as its value.
    [<Literal>]
    let ReportEvent = "runtime.reported"

    /// The frozen interface manifest the build implements (evidence/interface-manifest.json "version"; a contract test
    /// checks the two are equal, so a re-freeze that forgets this value fails the build's tests).
    [<Literal>]
    let InterfaceManifestVersion = "1.0.0"

    let notReported = NotReported

    // ---- browser report (pure) -------------------------------------------------------------------------------------

    let private versionOf (library: JsonProperty) =
        match library.Value.ValueKind with
        | JsonValueKind.Object ->
            match library.Value.TryGetProperty "version" with
            | true, v when v.ValueKind = JsonValueKind.String ->
                v.GetString()
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map (fun s -> library.Name, s)
            | _ -> None
        | _ -> None

    let private librariesOf (root: JsonElement) =
        match root.ValueKind with
        | JsonValueKind.Object ->
            match root.TryGetProperty "libraries" with
            | true, libs when libs.ValueKind = JsonValueKind.Object ->
                Reported(libs.EnumerateObject() |> Seq.choose versionOf |> Map.ofSeq)
            | _ -> Unreadable "the report has no libraries object"
        | _ -> Unreadable "the report is not an object"

    /// Pure: the event value web/boundary.js sends to a BrowserReport. Never throws.
    let report (value: string option) : BrowserReport =
        match value with
        | None
        | Some "" -> Unreadable "the browser sent an empty report"
        | Some json ->
            try
                use doc = JsonDocument.Parse json
                librariesOf doc.RootElement
            with :? JsonException as ex ->
                Unreadable ex.Message

    // ---- engine facts ----------------------------------------------------------------------------------------------

    /// Pure: "1.0.0+<sha>" to its version and build commit (the .NET SDK appends the source revision at build time).
    let splitInformational (informational: string) : string * string option =
        match informational.Split('+', 2) with
        | [| version; commit |] when commit <> "" -> version, Some commit
        | parts -> parts.[0], None

    let private nonBlank (s: string) = Some s |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// The effect edge: read the loaded engine's own assembly metadata and runtime once.
    let loadedEngine: EngineFacts =
        { InformationalVersion =
            typeof<BrowserReport>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            |> Option.ofObj
            |> Option.bind (fun a -> nonBlank a.InformationalVersion)
          Runtime = nonBlank Runtime.InteropServices.RuntimeInformation.FrameworkDescription }

    // ---- facts (pure) ----------------------------------------------------------------------------------------------

    let private fact key label source state =
        { Key = key; Label = label; State = state; Source = source }

    let private orUnavailable reason value =
        value |> Option.map Verified |> Option.defaultValue (Unavailable reason)

    let private engineFacts (engine: EngineFacts) =
        let split = engine.InformationalVersion |> Option.map splitInformational

        [ fact "app" "Causa application" "the loaded engine assembly (Causa.App)"
              (split |> Option.map fst |> orUnavailable "The engine assembly does not report a version.")
          fact "commit" "Build commit" "the loaded engine assembly's source revision"
              (split |> Option.bind snd |> orUnavailable "The engine was built without a source revision.")
          fact "runtime" ".NET runtime" "the runtime the engine is running on"
              (engine.Runtime |> orUnavailable "The runtime does not describe itself.") ]

    /// A library the browser reports by the name it registers on the page (Echelon runtime registry).
    let private libraryFact (browser: BrowserReport) (key, label, registeredAs) =
        let state =
            match browser with
            | NotReported -> Unknown "The browser has not reported its loaded libraries yet."
            | Unreadable reason -> Failed("The browser's report could not be read: " + reason)
            | Reported libraries ->
                match libraries.TryFind registeredAs with
                | Some version -> Verified version
                | None -> Unavailable "The loaded package does not report its version."

        fact key label "the browser's Echelon runtime registry" state

    /// The browser-side packages the page loads, with the name each would register under.
    let browserLibraries =
        [ "limen", "Limen", "limen"
          "forma", "Forma design system", "forma"
          "formaWorkflow", "Forma workflow", "forma-workflow"
          "folio", "Folio print components", "folio" ]

    let private contractFacts =
        [ fact "storage" "Storage contract" "the codec compiled into the loaded Causa.Persistence"
              (Verified("causa-storage " + StorageV1.formatVersion))
          fact "interfaces" "Interface manifest" "the frozen interface manifest this build implements"
              (Verified InterfaceManifestVersion) ]

    /// Pure: the loaded versions (VER-007), engine first, then the browser, then the contracts.
    let versions (engine: EngineFacts) (browser: BrowserReport) : Fact list =
        engineFacts engine @ (browserLibraries |> List.map (libraryFact browser)) @ contractFacts

    let private providerFact (connection: ConnectionState) =
        let state =
            match connection with
            | Disconnected -> Unavailable "Not connected: no repository has been connected in this tab."
            | Connecting _ -> Unknown "Connecting; the provider has not answered yet."
            | Reauthenticating -> Unknown "Reconnecting; the provider has not answered yet."
            | CredentialRefused -> Failed "The provider refused the credential."
            | Connected canWrite -> Verified(if canWrite then "GitHub, read and write" else "GitHub, read only")

        fact "provider" "Provider connection" "the connection state of this tab (the credential is never shown)" state

    let private replayFact (history: EventEnvelope list) =
        let state =
            match history with
            | [] -> Unavailable "This tab holds no investigation history to check."
            | _ ->
                match Kernel.replayState history with
                | Ok _ -> Verified $"{history.Length} events checked in order"
                | Error error -> Failed $"The history could not be rebuilt: %A{error}"

        fact "replay" "Investigation history check" "the domain kernel, rebuilding this tab's history" state

    /// Pure: the operational status (UI-018-O2) that is not a version.
    let status (connection: ConnectionState) (history: EventEnvelope list) : Fact list =
        [ providerFact connection
          fact "catalog" "Catalog and source reference" "the connected repository"
              (Unavailable "No catalog is loaded: no repository is connected in this tab.")
          fact "dataset" "Dataset validation" "the connected repository"
              (Unavailable "No dataset is loaded: no repository is connected in this tab.")
          replayFact history ]

    // ---- projection ------------------------------------------------------------------------------------------------

    let stateName (state: FactState) =
        match state with
        | Verified _ -> "verified"
        | Unknown _ -> "unknown"
        | Unavailable _ -> "unavailable"
        | Failed _ -> "failed"

    /// The words a fact shows: its value when verified, otherwise its state named in words and why (never a value).
    let valueText (state: FactState) =
        match state with
        | Verified value -> value
        | Unknown reason -> "Unknown. " + reason
        | Unavailable reason -> "Not available. " + reason
        | Failed reason -> "Failed. " + reason

    let row (f: Fact) : Graph.Row =
        [ "key", f.Key
          "label", f.Label
          "value", valueText f.State
          "state", stateName f.State
          "source", f.Source ]

    // ---- settings (UI-019) -----------------------------------------------------------------------------------------

    type Setting = { Key: string; Label: string; Effective: string; Scope: string; Note: string }

    let private accessEffective (connection: ConnectionState) =
        match connection with
        | Connected _ -> "Checked by the provider for this tab; details in Administration"
        | Connecting _
        | Reauthenticating -> "Waiting for the provider; details in Administration"
        | CredentialRefused -> "Refused by the provider; details in Administration"
        | Disconnected -> "None: no repository has been opened in this tab"

    /// Pure: the settings this version supports, each with its effective value and scope. None is adjustable in
    /// Causa, so the page offers no control (UI-019-O4).
    let settings (connection: ConnectionState) : Setting list =
        [ { Key = "appearance"
            Label = "Appearance"
            Effective = "Follows this device's light or dark setting"
            Scope = "Device (set in the operating system)"
            Note = "Causa has no theme control of its own." }
          { Key = "access"
            Label = "Repository access"
            Effective = accessEffective connection
            Scope = "Session (this tab only)"
            Note =
              "Not a saved preference: the provider checks access each time. Credentials are never stored or shown." } ]

    let settingRow (s: Setting) : Graph.Row =
        [ "key", s.Key; "label", s.Label; "effective", s.Effective; "scope", s.Scope; "note", s.Note ]

    /// The page's contribution: flat keys and the row lists web/index.html repeats with data-each.
    type AdministrationView = { Keys: ShellKeys; Lists: (string * Graph.Row list) list }

    let private countOf name (facts: Fact list) =
        facts |> List.filter (fun f -> stateName f.State = name) |> List.length |> string

    /// Pure: the Administration and Settings projection.
    let view (engine: EngineFacts) (browser: BrowserReport) (connection: ConnectionState) (history: EventEnvelope list) =
        let loaded = versions engine browser
        let operational = status connection history
        let all = loaded @ operational

        { Keys =
            [ "adminVerifiedCount", Str(countOf "verified" all)
              "adminUnknownCount", Str(countOf "unknown" all)
              "adminUnavailableCount", Str(countOf "unavailable" all)
              "adminFailedCount", Str(countOf "failed" all) ]
          Lists =
            [ "adminVersions", loaded |> List.map row
              "adminStatus", operational |> List.map row
              "settingsRows", settings connection |> List.map settingRow ] }
