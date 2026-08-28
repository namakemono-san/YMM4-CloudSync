namespace YMM4CloudSync.Core.Commons.Utilities;

public static class NaturalOrder
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var startA = i;
                var startB = j;

                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var digitsA = a.AsSpan(startA, i - startA).TrimStart('0');
                var digitsB = b.AsSpan(startB, j - startB).TrimStart('0');

                if (digitsA.Length != digitsB.Length) return digitsA.Length - digitsB.Length;

                var digitCompare = digitsA.SequenceCompareTo(digitsB);

                if (digitCompare != 0) return digitCompare;
            }
            else
            {
                var charCompare = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));

                if (charCompare != 0) return charCompare;

                i++;
                j++;
            }
        }

        var remaining = (a.Length - i) - (b.Length - j);

        return remaining != 0 ? remaining : string.CompareOrdinal(a, b);
    }
}
