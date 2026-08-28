using Xunit;
using YMM4CloudSync.Core.Commons.Utilities;

namespace YMM4CloudSync.Tests;

public class NaturalOrderTests
{
    private static List<string> Sort(params string[] names)
    {
        var list = names.ToList();

        list.Sort(NaturalOrder.Compare);

        return list;
    }

    [Fact]
    public void OrdersEmbeddedNumbersNumerically()
    {
        Assert.Equal(
            ["a2.png", "a10.png", "a100.png"],
            Sort("a10.png", "a100.png", "a2.png"));
    }

    [Fact]
    public void IgnoresLeadingZerosWhenComparingNumbers()
    {
        Assert.True(NaturalOrder.Compare("a002.png", "a10.png") < 0);
        Assert.True(NaturalOrder.Compare("a010.png", "a2.png") > 0);
    }

    [Fact]
    public void ComparesLettersWithoutRegardToCase()
    {
        Assert.True(NaturalOrder.Compare("apple", "Banana") < 0);
        Assert.True(NaturalOrder.Compare("Banana", "apple") > 0);
    }

    [Fact]
    public void HandlesJapaneseNames()
    {
        Assert.Equal(
            ["背景 1.png", "背景 2.png", "背景 10.png"],
            Sort("背景 10.png", "背景 2.png", "背景 1.png"));
    }

    [Fact]
    public void ShorterPrefixComesFirst()
    {
        Assert.True(NaturalOrder.Compare("clip", "clip2") < 0);
    }

    [Fact]
    public void NullsSortFirst()
    {
        Assert.True(NaturalOrder.Compare(null, "a") < 0);
        Assert.True(NaturalOrder.Compare("a", null) > 0);
        Assert.Equal(0, NaturalOrder.Compare(null, null));
    }

    [Fact]
    public void EqualStringsCompareEqual()
    {
        Assert.Equal(0, NaturalOrder.Compare("a10.png", "a10.png"));
    }

    [Fact]
    public void DistinguishesNamesThatDifferOnlyByCase()
    {
        Assert.NotEqual(0, NaturalOrder.Compare("A.png", "a.png"));
    }
}
