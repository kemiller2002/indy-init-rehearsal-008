// Owner: domain. WI-R8-T12: invariant tests first (requirements-compiled/v4/invariants.json).
namespace Causa.Invariants.Negative

open System.IO
open System.Text.RegularExpressions
open Xunit

/// INV-24 (PERSIST-030-O2): every Forma custom property the stylesheet references exists in Forma 0.12.0's published
/// token set (Rehearsal 7 post-deadline finding: four nonexistent token names). The keyboard and screen-reader half of
/// INV-24 is the WebKit browser test invariants/INV-24.spec.js.
module INV24 =
    /// requirements-compiled/v4.1.1/analysis/forma-0.12.0-inventory.json (@echelon-foundry/design-system 0.12.0, tarball SHA-256 b824568d528562658ded3d53ecd06504e907f7d42129facd86a8f298d1cb7aae): every custom
    /// property and hook property, without the "--ef-" prefix (384 names).
    let formaTokens: Set<string> =
        [|
        "breakout-max"; "callout-rule"; "card-columns"; "card-rule"; "cluster-gap"; "cluster-space"; "col"
        "color-accent-hover"; "color-accent-primary"; "color-accent-secondary"; "color-border-functional"
        "color-border-subtle"; "color-control-active"; "color-control-thumb"; "color-control-track"; "color-focus-gap"
        "color-focus-ring"; "color-status-danger"; "color-status-info"; "color-status-success"; "color-status-warning"
        "color-surface-elevated"; "color-surface-inverse"; "color-surface-inverse-secondary"; "color-surface-primary"
        "color-surface-secondary"; "color-text-heading"; "color-text-inverse"; "color-text-muted"
        "color-text-on-secondary-surface"; "color-text-primary"; "color-text-secondary"; "command-space"
        "container-width"; "density-space"; "diagram-accent"; "diagram-canvas-h"; "diagram-canvas-w"
        "diagram-connector-stroke"; "diagram-connector-width"; "diagram-fill"; "diagram-foreground"
        "diagram-group-accent"; "diagram-group-fill"; "diagram-group-foreground"; "diagram-group-stroke"; "diagram-h"
        "diagram-node-accent"; "diagram-node-fill"; "diagram-node-foreground"; "diagram-node-stroke"
        "diagram-port-offset"; "diagram-stroke"; "diagram-w"; "diagram-x"; "diagram-y"; "drag-x"; "drag-y"
        "effect-shadow-raised"; "effect-transition-duration"; "effect-transition-easing"; "field-control-block-size"
        "field-line-height"; "flyout-closed-x"; "focus-halo-width"; "focus-ring-offset"; "focus-ring-width"
        "focus-stage-space"; "focus-stage-support"; "form-gap"; "form-max"; "form-row-gap"; "form-row-min"; "frame-ratio"
        "grid-background"; "grid-boundary"; "grid-columns"; "grid-emphasis"; "grid-error"; "grid-field"
        "grid-field-surface"; "grid-focus"; "grid-font-size"; "grid-foreground"; "grid-gap"; "grid-information"
        "grid-inset"; "grid-line-height"; "grid-min"; "grid-muted"; "grid-protected"; "grid-row-pitch"; "grid-rows"
        "grid-status-rows"; "grid-success"; "grid-validation"; "grid-warning"; "gutter"; "height"; "icon-size"
        "landmark-space"; "layout-block-space"; "layout-card-min"; "layout-fact-min"; "layout-grid-gap"; "layout-gutter"
        "layout-header-offset"; "layout-measure"; "layout-measure-narrow"; "layout-section-space"; "layout-stack-space"
        "layout-target-min"; "layout-width-max"; "layout-width-narrow"; "layout-width-standard"; "layout-width-wide"
        "ledger-cell-space"; "len"; "measure"; "mosaic-space"; "motion-base-duration"; "motion-cadence-easing"
        "motion-cadence-period"; "motion-cadence-rotation-period"; "motion-damped-easing"; "motion-damping"
        "motion-direct-duration"; "motion-direct-easing"; "motion-distance"; "motion-exit-duration"; "motion-fade-easing"
        "motion-gravity"; "motion-gravity-duration"; "motion-inertia-duration"; "motion-mass"
        "motion-perceptual-duration"; "motion-perceptual-easing"; "motion-perceptual-emphasis-duration"
        "motion-press-duration"; "motion-spring-easing"; "motion-state-duration"; "motion-stiffness"; "navigation-space"
        "navigation-width"; "overlay-motion-duration"; "overlay-spatial-easing"; "primitive-border-width-accent"
        "primitive-border-width-thick"; "primitive-border-width-thin"; "primitive-color-amber-light"
        "primitive-color-analog-blue"; "primitive-color-analog-teal"; "primitive-color-black"; "primitive-color-carbon"
        "primitive-color-clinical"; "primitive-color-clinical-muted"; "primitive-color-clinical-secondary"
        "primitive-color-clinical-text"; "primitive-color-comp-blue"; "primitive-color-comp-rust"
        "primitive-color-deep-slate"; "primitive-color-earth-bg"; "primitive-color-earth-green"
        "primitive-color-earth-surface"; "primitive-color-earth-text"; "primitive-color-editorial-bg"
        "primitive-color-editorial-surface"; "primitive-color-forest"; "primitive-color-forged-iron"
        "primitive-color-foundry-charcoal"; "primitive-color-graphite"; "primitive-color-ice"; "primitive-color-ink"
        "primitive-color-institutional-bg"; "primitive-color-institutional-blue"
        "primitive-color-institutional-surface"; "primitive-color-institutional-text"; "primitive-color-mono-accent"
        "primitive-color-muted-plum"; "primitive-color-muted-teal"; "primitive-color-nature-bg"
        "primitive-color-nature-blue"; "primitive-color-nature-green"; "primitive-color-nature-surface"
        "primitive-color-nature-text"; "primitive-color-navy"; "primitive-color-neutral-100"
        "primitive-color-neutral-200"; "primitive-color-neutral-650"; "primitive-color-neutral-800"
        "primitive-color-neutral-950"; "primitive-color-ops-blue"; "primitive-color-ops-red"
        "primitive-color-oxide-bronze"; "primitive-color-oxide-bronze-deep"; "primitive-color-oxide-bronze-light"
        "primitive-color-oxide-bronze-pale"; "primitive-color-paper"; "primitive-color-paper-muted"
        "primitive-color-parchment"; "primitive-color-parchment-light"; "primitive-color-signal-blue"
        "primitive-color-signal-cyan"; "primitive-color-slate-surface"; "primitive-color-split-blue"
        "primitive-color-split-plum"; "primitive-color-split-rust"; "primitive-color-status-danger"
        "primitive-color-status-danger-light"; "primitive-color-status-info"; "primitive-color-status-info-light"
        "primitive-color-status-success"; "primitive-color-status-success-light"; "primitive-color-status-warning"
        "primitive-color-status-warning-light"; "primitive-color-stone"; "primitive-color-tech-amber"
        "primitive-color-tech-bg"; "primitive-color-tech-cyan"; "primitive-color-tech-surface"
        "primitive-color-tech-text"; "primitive-color-verdigris"; "primitive-color-verdigris-light"
        "primitive-color-white"; "primitive-focus-halo-width"; "primitive-focus-ring-offset"
        "primitive-focus-ring-width"; "primitive-font-family-display"; "primitive-font-family-mono"
        "primitive-font-family-sans"; "primitive-font-size-2xl"; "primitive-font-size-2xs"; "primitive-font-size-3xl"
        "primitive-font-size-fluid-display-1"; "primitive-font-size-fluid-display-2"
        "primitive-font-size-fluid-display-3"; "primitive-font-size-fluid-lead"; "primitive-font-size-lg"
        "primitive-font-size-md"; "primitive-font-size-sm"; "primitive-font-size-xl"; "primitive-font-size-xs"
        "primitive-font-weight-bold"; "primitive-font-weight-medium"; "primitive-font-weight-regular"
        "primitive-font-weight-semibold"; "primitive-letter-spacing-normal"; "primitive-letter-spacing-tight"
        "primitive-letter-spacing-wide"; "primitive-line-height-display"; "primitive-line-height-normal"
        "primitive-line-height-relaxed"; "primitive-line-height-snug"; "primitive-motion-duration-fast"
        "primitive-motion-duration-instant"; "primitive-motion-duration-slow"; "primitive-motion-duration-standard"
        "primitive-motion-easing-enter"; "primitive-motion-easing-exit"; "primitive-motion-easing-standard"
        "primitive-radius-medium"; "primitive-radius-none"; "primitive-radius-pill"; "primitive-radius-small"
        "primitive-shadow-raised"; "primitive-size-container-max"; "primitive-size-container-narrow"
        "primitive-size-container-standard"; "primitive-size-container-wide"; "primitive-size-fluid-block-space"
        "primitive-size-fluid-gutter"; "primitive-size-fluid-section-space"; "primitive-size-measure"
        "primitive-size-measure-narrow"; "primitive-size-target-min"; "primitive-space-1"; "primitive-space-2"
        "primitive-space-3"; "primitive-space-4"; "primitive-space-5"; "primitive-space-6"; "primitive-space-7"
        "primitive-space-8"; "primitive-spacing-1"; "primitive-spacing-10"; "primitive-spacing-2"; "primitive-spacing-3"
        "primitive-spacing-4"; "primitive-spacing-5"; "primitive-spacing-6"; "primitive-spacing-7"; "primitive-spacing-8"
        "primitive-spacing-9"; "priority-min"; "priority-space"; "rail-content-min"; "rail-measure"; "rail-space"
        "responsive-grid-min"; "responsive-grid-space"; "reveal-char-ms"; "reveal-line-ms"; "reveal-max-ms"
        "reveal-row-ms"; "reveal-rows"; "reveal-scale"; "reveal-total-ms"; "row"; "section-space"; "shape-border-width"
        "shape-border-width-accent"; "shape-radius"; "sidebar-content-min"; "sidebar-space"; "sidebar-width"
        "site-backdrop"; "site-backdrop-opacity"; "site-backdrop-size"; "space-1"; "space-2"; "space-3"; "space-4"; "space-5"
        "space-6"; "space-7"; "space-8"; "split-max"; "split-min"; "split-primary"; "split-secondary"; "split-size"
        "split-space"; "stack-gap"; "stack-space"; "surface-accent-text-color"; "surface-space"; "switch-gap"
        "switch-height"; "switch-thumb"; "switch-width"; "switcher-space"; "switcher-threshold"; "tone-action-background"
        "tone-action-hover"; "tone-action-text"; "tone-background"; "tone-heading"; "tone-hover"; "tone-label"; "tone-link"
        "tone-link-hover"; "tone-muted"; "tone-rule"; "tone-rule-strong"; "tone-text"; "type-family-body"
        "type-family-display"; "type-family-mono"; "type-leading-body"; "type-leading-heading"; "type-leading-lead"
        "type-leading-snug"; "type-measure-h1"; "type-measure-h2"; "type-size-body"; "type-size-h1"; "type-size-h2"
        "type-size-h3"; "type-size-h4"; "type-size-label"; "type-size-lead"; "type-size-small"; "type-size-statement"
        "type-tracking-body"; "type-tracking-display"; "type-tracking-label"; "type-weight-body"; "type-weight-heading"
        "type-weight-label"; "type-weight-nav"; "type-weight-strong"; "workspace-inspector"; "workspace-nav"
        "workspace-space" |]
        |> Array.map (fun suffix -> "--ef-" + suffix)
        |> Set.ofArray

    let private repositoryRoot () =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith "no Causa.slnx above the test assembly"
            | d when File.Exists(Path.Combine(d.FullName, "Causa.slnx")) -> d.FullName
            | d -> up d.Parent

        up (DirectoryInfo System.AppContext.BaseDirectory)

    let private referenced = Regex(@"--ef-[a-z0-9-]+", RegexOptions.IgnoreCase)

    [<Fact>]
    let FormaTokensExist () =
        Assert.Equal(384, formaTokens.Count)
        let stylesheet = Path.Combine(repositoryRoot (), "web", "causa.css")

        Assert.True(
            File.Exists stylesheet,
            "RED BY DESIGN until the stylesheet exists: web/causa.css (owner: integration) is missing, so its Forma tokens cannot be verified"
        )

        let missing =
            referenced.Matches(File.ReadAllText stylesheet)
            |> Seq.map (fun m -> m.Value.ToLowerInvariant())
            |> Seq.distinct
            |> Seq.filter (fun name -> not (Set.contains name formaTokens))
            |> Seq.toList

        Assert.True(List.isEmpty missing, sprintf "web/causa.css references Forma properties that Forma 0.12.0 does not publish: %A" missing)
