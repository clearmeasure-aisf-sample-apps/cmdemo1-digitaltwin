using System.Runtime.CompilerServices;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Ports;

namespace DigitalTwin.Application.Intake;

/// <summary>
/// The complete raw intake history in capture order: archived days first, then the SQL raw table. Rows present in both
/// (archived, but not yet pruned when retention was interrupted) are read once.
/// </summary>
public sealed class RawIntakeHistory(IRawIntakeArchive archive, IRawIntakeStore rawStore)
{
    public async IAsyncEnumerable<IReadOnlyList<RawIntakeEvent>> ReadPagesAsync(int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        var archived = new HashSet<Guid>();
        foreach (var day in await archive.ListDaysAsync(ct))
        {
            var page = new List<RawIntakeEvent>(pageSize);
            await foreach (var ev in archive.ReadDayAsync(day, ct))
            {
                if (!archived.Add(ev.Id))
                {
                    continue;
                }
                page.Add(ev);
                if (page.Count == pageSize)
                {
                    yield return page;
                    page = new List<RawIntakeEvent>(pageSize);
                }
            }
            if (page.Count > 0)
            {
                yield return page;
            }
        }

        long after = 0;
        while (true)
        {
            var page = await rawStore.ReadPageAsync(after, pageSize, ct);
            if (page.Count == 0)
            {
                yield break;
            }
            after = page[^1].Sequence;
            var fresh = archived.Count == 0 ? page : page.Where(r => !archived.Contains(r.Id)).ToList();
            if (fresh.Count > 0)
            {
                yield return fresh;
            }
        }
    }
}
