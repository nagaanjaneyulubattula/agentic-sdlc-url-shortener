using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace UrlShortener.Api.Features.Analytics;

/// <summary>
/// One recorded click. Deliberately has no address field: visitors are identified only by the keyed hash
/// produced by Core.ClientKey, and only the referrer HOST (never path/query) is kept.
/// </summary>
public sealed record ClickEvent(DateTimeOffset At, string? ReferrerHost, string ClientKey);

public sealed record ClickSnapshot(long TotalClicks, IReadOnlyList<ClickEvent> Events);

public interface IClickStore
{
    void Record(string code, ClickEvent click);

    ClickSnapshot Snapshot(string code);
}

public sealed class InMemoryClickStore : IClickStore
{
    private sealed class LinkClicks
    {
        public readonly object Gate = new();
        public long Total;
        public readonly Queue<ClickEvent> Events = new();
    }

    private readonly ConcurrentDictionary<string, LinkClicks> _links = new(StringComparer.Ordinal);
    private readonly AnalyticsOptions _options;

    public InMemoryClickStore(IOptions<AnalyticsOptions> options) => _options = options.Value;

    public void Record(string code, ClickEvent click)
    {
        var clicks = _links.GetOrAdd(code, static _ => new LinkClicks());
        lock (clicks.Gate)
        {
            clicks.Total++;
            clicks.Events.Enqueue(click);

            // Ring buffer: bounded by count and by retention age; the Total counter is unaffected.
            var oldest = click.At.AddDays(-_options.RetentionDays);
            while (clicks.Events.Count > Math.Max(1, _options.MaxEventsPerLink) ||
                   (clicks.Events.Count > 0 && clicks.Events.Peek().At < oldest))
            {
                clicks.Events.Dequeue();
            }
        }
    }

    public ClickSnapshot Snapshot(string code)
    {
        if (!_links.TryGetValue(code, out var clicks))
        {
            return new ClickSnapshot(0, Array.Empty<ClickEvent>());
        }

        lock (clicks.Gate)
        {
            return new ClickSnapshot(clicks.Total, clicks.Events.ToArray());
        }
    }
}
