// Owner: ui (T16). The route codec (R9.6, R9.7, R9.9, R9.10; SCREEN-CONTRACTS URL contract).
// Routes are hash routes (`index.html#/...`): relative links, no <base href>, so a reloaded or pasted deep link never
// 404s on GitHub Pages wherever the site is served from (on Pages the Limen location path carries the repository
// prefix; the hash does not). `parse` and `format` are pure and total; `format (parse url)` is the canonical URL.
namespace Causa.App

open System

/// The secondary destinations inside one investigation (UI-029-O2), carried as S02's `tab` parameter.
[<RequireQualifiedAccess>]
type InvestigationTab =
    | Overview
    | CausalAnalysis
    | Evidence
    | Hypotheses
    | Timeline
    | CorrectiveActions

/// An addressable screen of the application shell. Unknown, malformed or refused URLs are NotFound, never another
/// record (R9.8-O1/O2).
[<RequireQualifiedAccess>]
type AppRoute =
    | Home
    | InvestigationStart
    | Investigation of investigation: Guid * repository: string option * tab: InvestigationTab
    | Administration
    | Settings
    | NotFound

module Routes =
    /// Every investigation tab in navigation order (UI-029-O2).
    let tabs =
        [ InvestigationTab.Overview
          InvestigationTab.CausalAnalysis
          InvestigationTab.Evidence
          InvestigationTab.Hypotheses
          InvestigationTab.Timeline
          InvestigationTab.CorrectiveActions ]

    let tabSlug tab =
        match tab with
        | InvestigationTab.Overview -> "overview"
        | InvestigationTab.CausalAnalysis -> "causal-analysis"
        | InvestigationTab.Evidence -> "evidence"
        | InvestigationTab.Hypotheses -> "hypotheses"
        | InvestigationTab.Timeline -> "timeline"
        | InvestigationTab.CorrectiveActions -> "corrective-actions"

    let tabLabel tab =
        match tab with
        | InvestigationTab.Overview -> "Overview"
        | InvestigationTab.CausalAnalysis -> "Causal Analysis"
        | InvestigationTab.Evidence -> "Evidence"
        | InvestigationTab.Hypotheses -> "Hypotheses"
        | InvestigationTab.Timeline -> "Timeline"
        | InvestigationTab.CorrectiveActions -> "Corrective Actions"

    let private tabOfSlug (slug: string) = tabs |> List.tryFind (fun t -> tabSlug t = slug)

    /// Limen's reserved parameter names: a URL carrying one is refused, never interpreted (R9.9-O3).
    let reservedParameters =
        set
            [ "token"; "access_token"; "id_token"; "refresh_token"; "code"; "key"; "api_key"; "apikey"; "session"
              "password"; "secret"; "state"; "credential"; "auth"; "authorization" ]

    /// A GitHub `owner/name`; anything else is not a repository and is dropped from the URL.
    let private isRepository (value: string) =
        match value.Split '/' with
        | [| owner; name |] ->
            owner.Length > 0
            && name.Length > 0
            && owner |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')
            && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.')
        | _ -> false

    let private unescape (s: string) =
        try
            Uri.UnescapeDataString(s.Replace('+', ' '))
        with :? UriFormatException ->
            s

    /// Pure: a query string (without '?') to its parameters, first value per name.
    let private parameters (query: string) : Map<string, string> =
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun pair ->
            match pair.IndexOf '=' with
            | -1 -> unescape pair, ""
            | i -> unescape (pair.Substring(0, i)), unescape (pair.Substring(i + 1)))
        |> Array.rev
        |> Map.ofArray

    let private splitOnce (c: char) (s: string) =
        match s.IndexOf c with
        | -1 -> s, ""
        | i -> s.Substring(0, i), s.Substring(i + 1)

    /// Pure: a GUID in its canonical (lower-case, hyphenated) form only, so one entity has one URL (R9.9-O2).
    let private tryGuid (segment: string) =
        match Guid.TryParseExact(segment, "D") with
        | true, g when g.ToString "D" = segment -> Some g
        | _ -> None

    /// Pure, total: a location hash (`#/...`, `/...` or empty) to its route.
    let parse (hash: string) : AppRoute =
        let body = hash.TrimStart('#')
        let path, query = splitOnce '?' body
        let ps = parameters query
        if ps |> Map.exists (fun name _ -> reservedParameters.Contains(name.ToLowerInvariant())) then
            AppRoute.NotFound
        else
            match path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
            | [] -> AppRoute.Home
            | [ "investigations"; "new" ] -> AppRoute.InvestigationStart
            | [ "investigations"; id ] ->
                match tryGuid id with
                | Some g ->
                    let repository = ps |> Map.tryFind "repo" |> Option.filter isRepository
                    let tab =
                        ps
                        |> Map.tryFind "tab"
                        |> Option.bind tabOfSlug
                        |> Option.defaultValue InvestigationTab.Overview
                    AppRoute.Investigation(g, repository, tab)
                | None -> AppRoute.NotFound
            | [ "administration" ] -> AppRoute.Administration
            | [ "settings" ] -> AppRoute.Settings
            | _ -> AppRoute.NotFound

    /// Canonical query encoding: `%20` and upper-case hex; '/' kept literal in `owner/name`.
    let private escape (s: string) = Uri.EscapeDataString(s).Replace("%2F", "/")

    let private query (pairs: (string * string) list) =
        match pairs with
        | [] -> ""
        | _ -> "?" + (pairs |> List.map (fun (k, v) -> $"{k}={escape v}") |> String.concat "&")

    /// Pure, total: a route to its canonical hash URL (declared parameters only, in contract order, defaults omitted).
    let format (route: AppRoute) : string =
        match route with
        | AppRoute.Home -> "#/"
        | AppRoute.InvestigationStart -> "#/investigations/new"
        | AppRoute.Investigation(id, repository, tab) ->
            let repo = repository |> Option.map (fun r -> "repo", r) |> Option.toList
            let tabParam =
                match tab with
                | InvestigationTab.Overview -> []
                | other -> [ "tab", tabSlug other ]
            "#/investigations/" + id.ToString "D" + query (repo @ tabParam)
        | AppRoute.Administration -> "#/administration"
        | AppRoute.Settings -> "#/settings"
        | AppRoute.NotFound -> "#/not-found"

    /// IF41-APP's Route carries the canonical hash URL.
    let toRoute (route: AppRoute) = Route(format route)

    let ofRoute (Route hash) = parse hash

    /// Pure: whether a location hash is a route (`#/...` or none) rather than an in-page anchor such as the skip
    /// link's `#causa-main`, which leaves the current route as it is.
    let isRouteHash (hash: string) = hash = "" || hash = "#" || hash.StartsWith "#/"
