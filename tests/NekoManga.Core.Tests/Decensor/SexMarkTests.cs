using NekoManga.Core.Decensor;
using OpenCvSharp;

namespace NekoManga.Core.Tests;

/// <summary>사용자가 검열 조각에 찍은 성별 표시(SexMark)의 글 꼴 — 마스크 사이드카의 RegenSex("x,y,M;x,y,F;x,y,B")로 남는다.</summary>
public class SexMarkTests
{
    [Fact]
    public void Format_ThenParse_GivesTheSameMarks()
    {
        SexMark[] marks = [new(new Point(120, 3400), RegenKind.Male), new(new Point(0, 7), RegenKind.Female), new(new Point(55, 66), RegenKind.Both)];
        var text = SexMarks.Format(marks);
        Assert.Equal("120,3400,M;0,7,F;55,66,B", text);
        Assert.Equal(marks, SexMarks.Parse(text));
    }

    [Fact]
    public void Format_NothingToWrite_IsNull()
    {
        Assert.Null(SexMarks.Format([]));
        Assert.Null(SexMarks.Format([new SexMark(new Point(1, 2), RegenKind.Unknown)]));   // '자동'은 표시가 없는 것이다 — 적지 않는다
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("garbage")]
    public void Parse_NothingReadable_IsEmpty(string? text) => Assert.Empty(SexMarks.Parse(text));

    [Fact]
    public void Parse_SkipsBrokenEntries_KeepsTheRest()
    {
        // 손으로 고친 json: 망가진 칸만 버린다 — 시드와 달리 칸끼리 순서로 묶여 있지 않다
        var marks = SexMarks.Parse("10,20,M;x;30,40,f; 50 , 60 , B ;5,5,Q;-1,2,M;7,8;9,9,M,extra");
        Assert.Equal([new SexMark(new Point(10, 20), RegenKind.Male), new SexMark(new Point(30, 40), RegenKind.Female), new SexMark(new Point(50, 60), RegenKind.Both)], marks);
    }

    [Fact]
    public void Scaled_MovesThePointsToAnotherImageSize()
    {
        // 표시는 마스크(원본) 좌표로 적는다 — 채색된 장은 2차의 그림이 더 크다
        var marks = SexMarks.Scaled([new SexMark(new Point(100, 250), RegenKind.Male)], 2.0, 1.5);
        Assert.Equal([new SexMark(new Point(200, 375), RegenKind.Male)], marks);
        var same = new[] { new SexMark(new Point(3, 4), RegenKind.Female) };
        Assert.Equal(same, SexMarks.Scaled(same, 1.0, 1.0));
    }
}
