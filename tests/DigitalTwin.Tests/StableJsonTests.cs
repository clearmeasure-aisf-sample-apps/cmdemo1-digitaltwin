using System.Text.Json;
using DigitalTwin.Core.Projections;
using Xunit;

namespace DigitalTwin.Tests;

public sealed class StableJsonTests
{
    [Fact]
    public void KeyOrderAndGenerationStampsDoNotChangeTheCanonicalForm()
    {
        using var a = JsonDocument.Parse("""{"tier":"nonprod","environment":"tdd","purpose":"demo"}""");
        using var b = JsonDocument.Parse("""{"purpose":"demo","environment":"tdd","tier":"nonprod"}""");
        Assert.Equal(StableJson.SortedObject(a.RootElement), StableJson.SortedObject(b.RootElement));

        var first = StableJson.Without("""{"generated":"2026-11-22T12:42:21Z","nodes":[{"name":"n1","b":2,"a":1}]}""", "generated");
        var second = StableJson.Without("""{"nodes":[{"a":1,"name":"n1","b":2}],"generated":"2026-11-22T14:46:16Z"}""", "generated");
        Assert.Equal(first, second);
        Assert.DoesNotContain("generated", first, StringComparison.Ordinal);
        Assert.Equal("not json", StableJson.Without("not json", "generated"));
    }
}
