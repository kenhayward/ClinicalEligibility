using EligibilityProcessing.Web.Export;
using Xunit;

namespace EligibilityProcessing.Integration.Tests;

public class DeterministicIdTests
{
    private static readonly Guid Ns = new("7a6f3d2c-8b1e-4c5a-9f0d-2e4b6c8a1d33");

    [Fact]
    public void Same_namespace_and_name_yield_same_guid()
    {
        Assert.Equal(DeterministicId.Create(Ns, "alpha"), DeterministicId.Create(Ns, "alpha"));
    }

    [Fact]
    public void Different_name_yields_different_guid()
    {
        Assert.NotEqual(DeterministicId.Create(Ns, "alpha"), DeterministicId.Create(Ns, "beta"));
    }

    [Fact]
    public void Different_namespace_yields_different_guid()
    {
        var other = new Guid("00000000-0000-0000-0000-000000000001");
        Assert.NotEqual(DeterministicId.Create(Ns, "alpha"), DeterministicId.Create(other, "alpha"));
    }

    [Fact]
    public void Result_is_version_5()
    {
        // The version nibble lives in the high 4 bits of the 7th byte (index 6).
        var bytes = DeterministicId.Create(Ns, "anything").ToByteArray();
        // Guid stores the time_hi_and_version field little-endian, so byte
        // index 7 in the in-memory array holds the version nibble.
        Assert.Equal(0x50, bytes[7] & 0xF0);
    }

    [Fact]
    public void Matches_rfc4122_known_vector()
    {
        // RFC 4122 DNS namespace + "www.example.org" → well-known UUIDv5.
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(
            new Guid("74738ff5-5367-5958-9aee-98fffdcd1876"),
            DeterministicId.Create(dns, "www.example.org"));
    }
}
