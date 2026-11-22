using System.Text;
using System.Text.RegularExpressions;
using BackupMirror.Core.Platform;

namespace BackupMirror.Core.Mirror;

/// <summary>
/// gitignore-flavoured globs matched against '/'-separated relative paths.
/// A pattern without '/' matches any single path segment (e.g. <c>node_modules</c>, <c>*.tmp</c>);
/// a pattern with '/' is anchored at the job root (<c>cache/**</c>, <c>photos/*.raw</c>).
/// <c>*</c> stays within a segment, <c>**</c> crosses segments, <c>?</c> is one character.
/// </summary>
public sealed class ExcludeMatcher
{
    /// <summary>Always excluded: our own in-flight temp files.</summary>
    public const string TempSuffix = ".bmpart";

    /// <summary>Always excluded at the job root: where removed/overwritten files are kept when enabled.</summary>
    public const string TrashFolderName = ".backupmirror-trash";

    private readonly List<Regex> _segment = new();
    private readonly List<Regex> _anchored = new();

    public static ExcludeMatcher None { get; } = new(Array.Empty<string>());

    public ExcludeMatcher(IEnumerable<string>? patterns)
    {
        var options = RegexOptions.CultureInvariant | (PathRules.CaseInsensitive ? RegexOptions.IgnoreCase : 0);
        foreach (var raw in patterns ?? Array.Empty<string>())
        {
            var p = raw.Trim().Replace('\\', '/');
            if (p.Length == 0 || p.StartsWith('#')) continue;
            p = p.TrimEnd('/');
            if (p.StartsWith('/')) p = p.TrimStart('/');
            else if (!p.Contains('/'))
            {
                _segment.Add(new Regex("^" + GlobToRegex(p) + "$", options));
                continue;
            }

            _anchored.Add(new Regex("^" + GlobToRegex(p) + "(/.*)?$", options));
        }
    }

    public bool IsEmpty => _segment.Count == 0 && _anchored.Count == 0;

    /// <param name="portableRelative">Relative path with '/' separators.</param>
    public bool IsExcluded(string portableRelative)
    {
        if (portableRelative.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        if (portableRelative.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)
            || portableRelative.StartsWith(TrashFolderName + "/", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsEmpty) return false;

        foreach (var re in _anchored)
            if (re.IsMatch(portableRelative)) return true;

        if (_segment.Count > 0)
        {
            foreach (var seg in portableRelative.Split('/'))
                foreach (var re in _segment)
                    if (re.IsMatch(seg)) return true;
        }

        return false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    // "**/" matches zero or more directories
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }

        return sb.ToString();
    }
}
