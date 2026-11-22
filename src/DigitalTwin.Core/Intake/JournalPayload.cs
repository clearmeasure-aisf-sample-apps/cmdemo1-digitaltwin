using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Projections;

namespace DigitalTwin.Core.Intake;

/// <summary>
/// Keeps the journal small: a source payload larger than <see cref="MaxChars"/> is replaced by a stub naming its size and
/// hash, because the raw intake table already keeps the original (compressed) and nothing reads it from the journal.
/// State observations keep their payload: read models and time travel are folded from it.
/// </summary>
public static class JournalPayload
{
    public const int MaxChars = 4096;

    public static ObservedEvent Bound(ObservedEvent ev)
    {
        if (StateObservations.IsStateEvent(ev) || ev.PayloadJson.Length <= MaxChars)
        {
            return ev;
        }
        var bytes = Encoding.UTF8.GetBytes(ev.PayloadJson);
        ev.PayloadJson = JsonSerializer.Serialize(new
        {
            omitted = true,
            reason = "source payload kept in the raw intake table",
            sourceEventId = ev.SourceEventId,
            bytes = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        }, IntakeJson.Options);
        return ev;
    }
}
