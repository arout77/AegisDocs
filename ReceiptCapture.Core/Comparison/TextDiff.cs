namespace ReceiptCapture.Core.Comparison;

public enum DiffKind { Equal, Delete, Insert }

/// <summary>
/// A stretch of the comparison. Equal: the same words in both. Delete: words only in the original.
/// Insert: words only in the revised text. Ranges count words from 0.
/// </summary>
public readonly record struct DiffSegment(DiffKind Kind, int OldStart, int OldCount, int NewStart, int NewCount);

public sealed record DiffResult(IReadOnlyList<DiffSegment> Segments, bool TooDifferent)
{
    public int Unchanged => Segments.Where(s => s.Kind == DiffKind.Equal).Sum(s => s.OldCount);
    public int Removed => Segments.Where(s => s.Kind == DiffKind.Delete).Sum(s => s.OldCount);
    public int Added => Segments.Where(s => s.Kind == DiffKind.Insert).Sum(s => s.NewCount);
}

/// <summary>
/// Finds the smallest set of word changes that turns one text into another (Myers' algorithm),
/// so unchanged text lines up and only real changes are marked.
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// Beyond this many changed words the exact comparison is abandoned and everything between the
    /// shared start and the shared end is shown as replaced. Keeps memory and time sensible for texts that barely resemble each other.
    /// </summary>
    public const int DefaultMaxChanges = 4000;

    public static DiffResult Compare(IReadOnlyList<string> original, IReadOnlyList<string> revised, int maxChanges = DefaultMaxChanges)
    {
        // Map words to numbers so comparing is fast.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] a = Intern(original, ids), b = Intern(revised, ids);

        // Whatever the two texts share at the start and the end needs no searching.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var middleA = new ArraySegment<int>(a, prefix, a.Length - prefix - suffix);
        var middleB = new ArraySegment<int>(b, prefix, b.Length - prefix - suffix);

        var edits = new List<DiffSegment>();
        if (prefix > 0) edits.Add(new DiffSegment(DiffKind.Equal, 0, prefix, 0, prefix));

        bool tooDifferent = false;
        if (middleA.Count == 0 && middleB.Count > 0)
            edits.Add(new DiffSegment(DiffKind.Insert, prefix, 0, prefix, middleB.Count));
        else if (middleB.Count == 0 && middleA.Count > 0)
            edits.Add(new DiffSegment(DiffKind.Delete, prefix, middleA.Count, prefix, 0));
        else if (middleA.Count > 0)
        {
            var script = Myers(middleA, middleB, maxChanges);
            if (script == null)
            {
                tooDifferent = true;
                edits.Add(new DiffSegment(DiffKind.Delete, prefix, middleA.Count, prefix, 0));
                edits.Add(new DiffSegment(DiffKind.Insert, prefix + middleA.Count, 0, prefix, middleB.Count));
            }
            else
            {
                foreach (var s in script)
                    edits.Add(s with { OldStart = s.OldStart + prefix, NewStart = s.NewStart + prefix });
            }
        }

        if (suffix > 0)
            edits.Add(new DiffSegment(DiffKind.Equal, a.Length - suffix, suffix, b.Length - suffix, suffix));

        return new DiffResult(Merge(edits), tooDifferent);
    }

    private static int[] Intern(IReadOnlyList<string> words, Dictionary<string, int> ids)
    {
        var result = new int[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            if (!ids.TryGetValue(words[i], out int id)) { id = ids.Count; ids[words[i]] = id; }
            result[i] = id;
        }
        return result;
    }

    /// <summary>Joins neighbouring segments of the same kind, and puts a Delete before an Insert where both touch.</summary>
    private static List<DiffSegment> Merge(List<DiffSegment> edits)
    {
        var merged = new List<DiffSegment>();
        foreach (var e in edits)
        {
            if (e.OldCount == 0 && e.NewCount == 0) continue;
            if (merged.Count > 0 && merged[^1].Kind == e.Kind)
            {
                var last = merged[^1];
                merged[^1] = last with { OldCount = last.OldCount + e.OldCount, NewCount = last.NewCount + e.NewCount };
            }
            else merged.Add(e);
        }

        // "Insert then Delete" side by side reads better as "Delete then Insert".
        for (int i = 0; i + 1 < merged.Count; i++)
        {
            if (merged[i].Kind == DiffKind.Insert && merged[i + 1].Kind == DiffKind.Delete)
                (merged[i], merged[i + 1]) = (merged[i + 1], merged[i]);
        }
        return merged;
    }

    /// <summary>Returns the edit script, or null if more than <paramref name="maxChanges"/> changes are needed.</summary>
    private static List<DiffSegment>? Myers(ArraySegment<int> a, ArraySegment<int> b, int maxChanges)
    {
        int n = a.Count, m = b.Count;
        int max = Math.Min(n + m, maxChanges);
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();

        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            // Remember the state before this round so the path can be walked back later.
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, 2 * d + 3);
            trace.Add(snapshot);

            for (int k = -d; k <= d; k += 2)
            {
                int x = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;

                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;

                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;

        // Walk back from the end to the start, collecting single-word steps.
        var steps = new List<(DiffKind Kind, int OldIndex, int NewIndex)>();
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var snap = trace[d];                       // index of diagonal k in snap is k + d + 1
            int k = cx - cy;
            bool fromAbove = k == -d || (k != d && snap[k - 1 + d + 1] < snap[k + 1 + d + 1]);
            int prevK = fromAbove ? k + 1 : k - 1;
            int prevX = snap[prevK + d + 1];
            int prevY = prevX - prevK;

            // The one edit of this round leaves us here; everything after it up to (cx, cy) is a run of matches.
            int afterEditX = fromAbove ? prevX : prevX + 1;
            int afterEditY = afterEditX - k;
            while (cx > afterEditX && cy > afterEditY) { cx--; cy--; steps.Add((DiffKind.Equal, cx, cy)); }

            steps.Add(fromAbove ? (DiffKind.Insert, prevX, prevY) : (DiffKind.Delete, prevX, prevY));
            cx = prevX; cy = prevY;
        }
        while (cx > 0 && cy > 0) { cx--; cy--; steps.Add((DiffKind.Equal, cx, cy)); }
        steps.Reverse();

        var segments = new List<DiffSegment>();
        foreach (var s in steps)
        {
            var seg = s.Kind switch
            {
                DiffKind.Equal => new DiffSegment(DiffKind.Equal, s.OldIndex, 1, s.NewIndex, 1),
                DiffKind.Delete => new DiffSegment(DiffKind.Delete, s.OldIndex, 1, s.NewIndex, 0),
                _ => new DiffSegment(DiffKind.Insert, s.OldIndex, 0, s.NewIndex, 1),
            };
            segments.Add(seg);
        }
        return Merge(segments);
    }
}
