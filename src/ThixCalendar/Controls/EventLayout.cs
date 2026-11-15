using ThixCalendar.Core.Models;

namespace ThixCalendar.Controls;

/// <summary>Where an event sits in one day column: minutes of the day and its lane among overlapping events.</summary>
internal sealed record EventSlot(EventItem Item, double StartMinute, double EndMinute, int Lane, int Lanes);

/// <summary>
/// Places the timed events of one day side by side where they overlap (like Apple Calendar and Outlook): events that
/// overlap form a cluster; each gets the first free lane; all events of a cluster share its number of lanes.
/// </summary>
internal static class EventLayout
{
    /// <summary>Events shorter than this still get the height of a readable block (minutes).</summary>
    public const double MinimumMinutes = 20;

    public static List<EventSlot> Arrange(DateTime day, IEnumerable<EventItem> items)
    {
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);
        var pieces = items
            .Where(item => !item.Event.IsAllDay && item.Event.StartLocal < dayEnd && (item.Event.EndLocal > dayStart || (item.Event.EndLocal == item.Event.StartLocal && item.Event.StartLocal >= dayStart)))
            .Select(item =>
            {
                var start = Math.Max(0, (item.Event.StartLocal - dayStart).TotalMinutes);
                var end = Math.Min(24 * 60, (item.Event.EndLocal - dayStart).TotalMinutes);
                return (Item: item, Start: start, End: Math.Max(end, Math.Min(24 * 60, start + MinimumMinutes)));
            })
            .OrderBy(piece => piece.Start)
            .ThenByDescending(piece => piece.End)
            .ThenBy(piece => piece.Item.Event.Title, StringComparer.CurrentCulture)
            .ToList();

        var slots = new List<EventSlot>();
        var cluster = new List<(EventItem Item, double Start, double End, int Lane)>();
        var laneEnds = new List<double>();
        var clusterEnd = double.MinValue;

        void Flush()
        {
            var lanes = Math.Max(1, laneEnds.Count);
            slots.AddRange(cluster.Select(piece => new EventSlot(piece.Item, piece.Start, piece.End, piece.Lane, lanes)));
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var piece in pieces)
        {
            if (piece.Start >= clusterEnd && cluster.Count > 0)
            {
                Flush();
            }

            var lane = laneEnds.FindIndex(end => end <= piece.Start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(piece.End);
            }
            else
            {
                laneEnds[lane] = piece.End;
            }

            cluster.Add((piece.Item, piece.Start, piece.End, lane));
            clusterEnd = cluster.Count == 1 ? piece.End : Math.Max(clusterEnd, piece.End);
        }

        Flush();
        return slots;
    }
}
