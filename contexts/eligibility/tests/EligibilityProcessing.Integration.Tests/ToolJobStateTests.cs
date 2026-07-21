using EligibilityProcessing.Core;
using EligibilityProcessing.Web;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

/// <summary>
/// KindName is the wire string the Tools view's data-tool / data-panel attributes
/// and its count-refresh JavaScript match on. It has a fallback arm
/// (kind.ToString()), so a missing or wrong mapping does not fail to compile -
/// it silently emits e.g. "NormalizeConditions" instead of "normalize-conditions"
/// and breaks the card's live progress and count with no error anywhere. These
/// assert the exact strings, not just that the values differ.
/// </summary>
public class ToolJobStateTests
{
    [Fact]
    public void NormalizeUmls_maps_to_normalize_umls()
    {
        Assert.Equal("normalize-umls", ToolJobState.KindName(ToolJobKind.NormalizeUmls));
    }

    [Fact]
    public void EmbedStudies_maps_to_embed_studies()
    {
        Assert.Equal("embed-studies", ToolJobState.KindName(ToolJobKind.EmbedStudies));
    }

    [Fact]
    public void NormalizeConditions_maps_to_normalize_conditions()
    {
        Assert.Equal("normalize-conditions", ToolJobState.KindName(ToolJobKind.NormalizeConditions));
    }
}
