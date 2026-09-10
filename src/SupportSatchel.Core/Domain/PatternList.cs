namespace SupportSatchel.Core.Domain;

/// <summary>Order-sensitive sequence equality/hash helpers for domain records.</summary>
internal static class PatternList
{
    internal static bool Equal(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static int Hash(IReadOnlyList<string> items)
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal static int Combine(params int[] parts)
    {
        var hash = new HashCode();
        foreach (var part in parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }

    internal static bool EqualRules(IReadOnlyList<RedactionRule> left, IReadOnlyList<RedactionRule> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static int RulesHash(IReadOnlyList<RedactionRule> rules)
    {
        var hash = new HashCode();
        foreach (var rule in rules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }

    internal static bool Equal<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : IEquatable<T>
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static int HashOf<T>(IReadOnlyList<T> items)
        where T : IEquatable<T>
    {
        var hash = new HashCode();
        foreach (var item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
