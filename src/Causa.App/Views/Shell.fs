// Owner: ui (T16). The application shell projection (ADRP-003, UI-014, UI-020, UI-029, R9.8).
// Pure: a route (and what the shell knows of the connection) to the flat keys web/index.html binds. Every key is
// present in every projection (a Limen ViewState is complete, never a patch). Split into small functions so no single
// method grows large under the .NET WebAssembly interpreter (AR-029-O3).
namespace Causa.App.Views

open Causa.App

/// One flat Limen view value. View.fs (integration) maps it to its ViewValue.
type ShellValue =
    | Str of string
    | Bit of bool

type ShellKeys = (string * ShellValue) list

module Shell =
    /// A primary destination (UI-020-O1, UI-029-O1).
    type Destination =
        | Investigations
        | Administration
        | Settings

    let destinationOf (route: AppRoute) : Destination option =
        match route with
        | AppRoute.Home
        | AppRoute.InvestigationStart
        | AppRoute.Investigation _ -> Some Investigations
        | AppRoute.Administration -> Some Administration
        | AppRoute.Settings -> Some Settings
        | AppRoute.NotFound -> None

    let screenId (route: AppRoute) =
        match route with
        | AppRoute.Home -> "home"
        | AppRoute.InvestigationStart -> "investigation-start"
        | AppRoute.Investigation _ -> "investigation"
        | AppRoute.Administration -> "administration"
        | AppRoute.Settings -> "settings"
        | AppRoute.NotFound -> "not-found"

    let screenTitle (route: AppRoute) =
        match route with
        | AppRoute.Home -> "Investigations"
        | AppRoute.InvestigationStart -> "Start an investigation"
        | AppRoute.Investigation(_, _, tab) -> Routes.tabLabel tab
        | AppRoute.Administration -> "Administration"
        | AppRoute.Settings -> "Settings"
        | AppRoute.NotFound -> "Page not found"

    /// The document title: screen, then context, then the application name (UI-014-O1 page titles).
    let pageTitle (route: AppRoute) =
        match route with
        | AppRoute.Investigation _ -> $"{screenTitle route} · Investigation · Causa"
        | _ -> $"{screenTitle route} · Causa"

    /// aria-current for a navigation link: "page" when current, "false" otherwise.
    let private current isCurrent = Str(if isCurrent then "page" else "false")

    let private primaryKeys (route: AppRoute) : ShellKeys =
        let at d = destinationOf route = Some d
        [ "navInvestigationsCurrent", current (at Investigations)
          "navAdministrationCurrent", current (at Administration)
          "navSettingsCurrent", current (at Settings) ]

    let private tabKey tab =
        match tab with
        | InvestigationTab.Overview -> "Overview"
        | InvestigationTab.CausalAnalysis -> "CausalAnalysis"
        | InvestigationTab.Evidence -> "Evidence"
        | InvestigationTab.Hypotheses -> "Hypotheses"
        | InvestigationTab.Timeline -> "Timeline"
        | InvestigationTab.CorrectiveActions -> "CorrectiveActions"

    /// Secondary navigation inside an investigation (UI-029-O2): one href and one aria-current per tab. Outside an
    /// investigation the links point home and the navigation is hidden.
    let private secondaryKeys (route: AppRoute) : ShellKeys =
        let link tab =
            match route with
            | AppRoute.Investigation(id, repository, selected) ->
                Routes.format (AppRoute.Investigation(id, repository, tab)), selected = tab
            | _ -> Routes.format AppRoute.Home, false
        Routes.tabs
        |> List.collect (fun tab ->
            let href, isCurrent = link tab
            [ $"tab{tabKey tab}Href", Str href; $"tab{tabKey tab}Current", current isCurrent ])

    /// The investigation context the header shows (UI-014-O3): which investigation and which repository.
    let private contextKeys (route: AppRoute) : ShellKeys =
        match route with
        | AppRoute.Investigation(id, repository, _) ->
            [ "investigationNavHidden", Bit false
              "investigationLabel", Str("Investigation " + id.ToString("D").Substring(0, 8))
              "investigationRepository",
              Str(repository |> Option.defaultValue "No repository named in this link")
              "investigationHref", Str(Routes.format (AppRoute.Investigation(id, repository, InvestigationTab.Overview))) ]
        | _ ->
            [ "investigationNavHidden", Bit true
              "investigationLabel", Str ""
              "investigationRepository", Str ""
              "investigationHref", Str(Routes.format AppRoute.Home) ]

    /// Which screen region is shown: exactly one of these is false.
    let private screenKeys (route: AppRoute) : ShellKeys =
        let shown = screenId route
        [ "home"; "investigation-start"; "investigation"; "administration"; "settings"; "not-found" ]
        |> List.map (fun id ->
            let key =
                "screen"
                + (id.Split '-' |> Array.map (fun w -> string (System.Char.ToUpperInvariant w[0]) + w.Substring 1) |> String.concat "")
                + "Hidden"
            key, Bit(id <> shown))

    /// An investigation link this tab has not loaded (UI-010-O2, UI-013-O4): what happened, what is preserved and
    /// the one safe next step the connection allows (SM-CONNECTION). Total over ConnectionState; a link without a
    /// repository has nothing to load, so its next step is back to Investigations.
    let recoveryKeys (connection: ConnectionState) (route: AppRoute) : ShellKeys =
        let preserved = "Nothing has changed. Unsaved changes are kept only in this tab's memory."

        let detail, action =
            match route, connection with
            | AppRoute.Investigation(_, None, _), _ ->
                $"This link does not name the repository that holds the investigation. {preserved}",
                Some("Open Investigations", Routes.format AppRoute.Home)
            | AppRoute.Investigation(_, Some repository, _), (Disconnected | CredentialRefused) ->
                $"Connect {repository} to open it. {preserved}", Some("Connect data repository", Routes.format AppRoute.Home)
            | AppRoute.Investigation(_, Some repository, _), Reauthenticating ->
                $"The connection to {repository} needs to be renewed before it can open. {preserved}",
                Some("Connect data repository again", Routes.format AppRoute.Home)
            | AppRoute.Investigation(_, Some repository, _), Connecting _ ->
                $"Connecting to {repository}; the investigation can be opened once the connection is ready. {preserved}", None
            | AppRoute.Investigation(_, Some _, _), Connected _ ->
                $"The repository is connected; open the investigation from its list. {preserved}",
                Some("Open Investigations", Routes.format AppRoute.Home)
            | _ -> "", None

        [ "unloadedDetail", Str detail
          "unloadedActionHidden", Bit action.IsNone
          "unloadedActionLabel", Str(action |> Option.map fst |> Option.defaultValue "")
          "unloadedActionHref", Str(action |> Option.map snd |> Option.defaultValue (Routes.format AppRoute.Home)) ]

    /// Pure: the keys that depend on the route alone.
    let private routeKeys (route: AppRoute) : ShellKeys =
        [ "screen", Str(screenId route)
          "screenTitle", Str(screenTitle route)
          "pageTitle", Str(pageTitle route)
          "canonicalUrl", Str(Routes.format route)
          "syncLabel", Str "Not connected" ]
        @ primaryKeys route
        @ secondaryKeys route
        @ contextKeys route
        @ screenKeys route

    /// Pure: the shell's complete projection for one route and what the shell knows of the connection.
    let projectWith (connection: ConnectionState) (route: AppRoute) : ShellKeys =
        routeKeys route @ recoveryKeys connection route

    /// Pure: the shell's complete projection for one route. The shell this build composes (View.fs) holds no
    /// connection yet, so it is Disconnected, which the header also shows ("Not connected").
    let project (route: AppRoute) : ShellKeys = projectWith Disconnected route

    /// Pure: the projection for a Limen location hash.
    let projectHash (hash: string) : ShellKeys = project (Routes.parse hash)
