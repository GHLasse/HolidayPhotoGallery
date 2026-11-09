using System.Text.RegularExpressions;

namespace PhotoGallery.Core.Import;

/// <summary>
/// A site's robots.txt (F-8): which addresses the website import may read, and how long to wait between requests.
/// The rules for the app's own name win over the ones for every robot ("*"); the longest matching rule decides,
/// and Allow wins a tie, as Google reads them.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(string Pattern, bool Allow)> _rules;

    private RobotsRules(List<(string Pattern, bool Allow)> rules, TimeSpan? crawlDelay)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    /// <summary>Everything allowed: no robots.txt, or one that can't be read.</summary>
    public static RobotsRules AllowAll { get; } = new([], null);

    /// <summary>The pause the site asks for between requests, if any.</summary>
    public TimeSpan? CrawlDelay { get; }

    public static RobotsRules Parse(string text, string agent)
    {
        // Groups of user-agent lines followed by their rules.
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent || current is null)
                {
                    current = new Group();
                    groups.Add(current);
                }
                current.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (current is not { } group)
                continue;
            switch (key)
            {
                case "disallow" when value.Length > 0:
                    group.Rules.Add((value, false));
                    break;
                case "allow" when value.Length > 0:
                    group.Rules.Add((value, true));
                    break;
                case "crawl-delay" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0:
                    group.Delay = TimeSpan.FromSeconds(seconds);
                    break;
            }
        }
        agent = agent.ToLowerInvariant();
        var own = groups.Where(g => g.Agents.Any(a => a != "*" && agent.Contains(a))).ToList();
        var chosen = own.Count > 0 ? own : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList(), chosen.Select(g => g.Delay).FirstOrDefault(d => d is not null));
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<(string Pattern, bool Allow)> Rules { get; } = [];

        public TimeSpan? Delay { get; set; }
    }

    public bool Allows(Uri uri)
    {
        var path = uri.PathAndQuery;
        var best = -1;
        var allow = true;
        foreach (var (pattern, isAllow) in _rules)
        {
            if (!Matches(pattern, path))
                continue;
            if (pattern.Length > best || (pattern.Length == best && isAllow))
            {
                best = pattern.Length;
                allow = isAllow;
            }
        }
        return allow;
    }

    // A rule is a path prefix, with * for any characters and $ for the end.
    private static bool Matches(string pattern, string path)
    {
        if (!pattern.Contains('*') && !pattern.EndsWith('$'))
            return path.StartsWith(pattern, StringComparison.Ordinal);
        var regex = "^" + Regex.Escape(pattern.TrimEnd('$')).Replace(@"\*", ".*") + (pattern.EndsWith('$') ? "$" : "");
        return Regex.IsMatch(path, regex);
    }
}
