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

    /// Pure: the shell's complete projection for one route.
    let project (route: AppRoute) : ShellKeys =
        [ "screen", Str(screenId route)
          "screenTitle", Str(screenTitle route)
          "pageTitle", Str(pageTitle route)
          "canonicalUrl", Str(Routes.format route)
          "syncLabel", Str "Not connected" ]
        @ primaryKeys route
        @ secondaryKeys route
        @ contextKeys route
        @ screenKeys route

    /// Pure: the projection for a Limen location hash.
    let projectHash (hash: string) : ShellKeys = project (Routes.parse hash)
