using MediaEngine.Domain.Authorization;

namespace MediaEngine.Domain.Tests;

public sealed class InvitationCodeTests
{
    [Fact]
    public void Generate_ProducesTenCharactersFromTheUnambiguousAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = InvitationCode.Generate();
            Assert.Equal(InvitationCode.Length, code.Length);
            Assert.All(code, c => Assert.Contains(c, InvitationCode.Alphabet));
        }

        Assert.DoesNotContain('0', InvitationCode.Alphabet);
        Assert.DoesNotContain('O', InvitationCode.Alphabet);
        Assert.DoesNotContain('1', InvitationCode.Alphabet);
        Assert.DoesNotContain('I', InvitationCode.Alphabet);
        Assert.DoesNotContain('L', InvitationCode.Alphabet);
    }

    [Fact]
    public void Format_ShowsTwoGroupsOfFive()
    {
        Assert.Equal("KQ7M4-XH2TA", InvitationCode.Format("KQ7M4XH2TA"));
    }

    [Theory]
    [InlineData("KQ7M4-XH2TA")]
    [InlineData("kq7m4-xh2ta")]
    [InlineData("  KQ7M4 XH2TA  ")]
    [InlineData("KQ7M4XH2TA")]
    public void Normalize_AcceptsWhatPeopleTypeOrPaste(string input)
    {
        Assert.Equal("KQ7M4XH2TA", InvitationCode.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KQ7M4-XH2T")]
    [InlineData("KQ7M4-XH2TAB")]
    [InlineData("KQ7M4-XH2T0")]
    [InlineData("KQ7M4-XH2TL")]
    public void Normalize_RejectsAnythingThatCannotBeACode(string? input)
    {
        Assert.Null(InvitationCode.Normalize(input));
    }

    [Fact]
    public void Hash_IsStableLowercaseSha256AndNotTheCode()
    {
        var hash = InvitationCode.Hash("KQ7M4XH2TA");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(hash, InvitationCode.Hash("KQ7M4XH2TA"));
        Assert.NotEqual(hash, InvitationCode.Hash("KQ7M4XH2TB"));
        Assert.DoesNotContain("KQ7M4", hash, StringComparison.OrdinalIgnoreCase);
    }
}
