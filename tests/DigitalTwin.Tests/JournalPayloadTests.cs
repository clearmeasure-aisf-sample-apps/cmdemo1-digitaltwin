using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;
using Xunit;
using F = DigitalTwin.Tests.Infrastructure.TwinStateFixtures;

namespace DigitalTwin.Tests;

public sealed class JournalPayloadTests
{
    [Fact]
    public void LargeSourcePayloadsAreReplacedByAStubButStatePayloadsAreKept()
    {
        var padding = new string('x', 10_000);
        var body = $$$"""{"zen":"{{{padding}}}","repository":{"name":"cmdemo1-workorders","full_name":"clearmeasure-aisf-sample-apps/cmdemo1-workorders"}}""";
        var webhook = RawIntakeEvent.ForWebhook(EventSourceKind.GitHub, IntakeKind.GitHubWebhook, body,
            [new("X-GitHub-Event", "ping"), new("X-GitHub-Delivery", "big-1")], F.T0);

        var journaled = Assert.Single(IntakeNormalizer.Normalize(webhook));
        Assert.True(journaled.PayloadJson.Length <= JournalPayload.MaxChars);
        using var stub = JsonDocument.Parse(journaled.PayloadJson);
        Assert.True(stub.RootElement.GetProperty("omitted").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), stub.RootElement.GetProperty("bytes").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), stub.RootElement.GetProperty("sha256").GetString());

        var resources = Enumerable.Range(0, 60).Select(i => F.Resource($"ca-cmdemo1-{i}")).ToList();
        var state = StateObservations.ToEvent(StateKinds.AzureResource, "azure", resources, "hash", EventSourceKind.Azure, F.T0);
        Assert.True(state.PayloadJson.Length > JournalPayload.MaxChars);
        var batch = RawIntakeEvent.ForObservedBatch([state], EventSourceKind.Azure, IngestionChannel.Poll, F.T0);
        Assert.Equal(state.PayloadJson, Assert.Single(IntakeNormalizer.Normalize(batch)).PayloadJson);
    }
}
