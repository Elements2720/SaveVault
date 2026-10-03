using SaveVault.App.Infrastructure;
using SaveVault.Core.Games;

namespace SaveVault.App.ViewModels;

public sealed class SaveCandidateSelection(SaveCandidate candidate) : ObservableObject
{
    private bool selected;
    public SaveCandidate Candidate { get; } = candidate;
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public string Root => Candidate.Root;
    public string Selection => Candidate.Selection;
    public string ContentKind => Candidate.ContentKind;
    public string Confidence => Candidate.Confidence switch
    {
        DiscoveryConfidence.KnownLocation => "Known location",
        DiscoveryConfidence.LikelyAlternate => "Likely alternate",
        DiscoveryConfidence.ObservedChange => "Observed change",
        _ => "Possible location"
    };
    public string Evidence => Candidate.Evidence;
    public string Size => MainViewModel.FormatBytes(Candidate.Bytes);
    public int FileCount => Candidate.FileCount;
    public DateTime? LatestWriteUtc => Candidate.LatestWriteUtc;
}
