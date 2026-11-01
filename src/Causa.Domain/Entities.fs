// Owner: domain. WI-R8-T13: the investigation aggregate as the kernel evolves it.
namespace Causa.Domain

/// The full investigation state folded from its history. The frozen Investigation (IF41-DOMAIN) is its summary.
type InvestigationState =
    { Id: InvestigationId
      Version: AggregateVersion
      Lifecycle: InvestigationLifecycle
      Observations: int
      Hypotheses: int
      Conclusions: int
      /// Every event id in the history, for duplicate-submission detection.
      EventIds: Set<EventId> }

[<RequireQualifiedAccess>]
module InvestigationState =
    let summary (state: InvestigationState) : Investigation = { Id = state.Id; Version = state.Version }
