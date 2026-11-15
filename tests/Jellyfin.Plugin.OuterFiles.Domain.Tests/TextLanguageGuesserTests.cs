using Jellyfin.Plugin.OuterFiles.Domain.Tracks;

namespace Jellyfin.Plugin.OuterFiles.Domain.Tests;

public sealed class TextLanguageGuesserTests
{
    private static string Ass(params string[] lines) =>
        "[Script Info]\nTitle: Default Aegisub file\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        + string.Concat(lines.Select(l => "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\an8}" + l + "\n"));

    [Fact]
    public void RecognisesRussianAssDespiteLatinStyleNames() =>
        Assert.Equal("rus", TextLanguageGuesser.Guess(Ass(Enumerable.Repeat("Я не могу поверить, что это происходит на самом деле.\\NПошли отсюда!", 6).ToArray()), ".ass"));

    [Fact]
    public void RecognisesUkrainian() =>
        Assert.Equal("ukr", TextLanguageGuesser.Guess(Ass(Enumerable.Repeat("Я не вірю, що це відбувається. Їжа є на столі, ґанок відкритий.", 6).ToArray()), ".ass"));

    [Fact]
    public void RecognisesEnglishSrt()
    {
        var srt = string.Concat(Enumerable.Range(1, 12).Select(i => $"{i}\n00:00:0{i % 10},000 --> 00:00:0{i % 10},500\nI don't know what you want from me, but this is the end of it.\n\n"));
        Assert.Equal("eng", TextLanguageGuesser.Guess(srt, ".srt"));
    }

    [Fact]
    public void DoesNotCallOtherLatinLanguagesEnglish()
    {
        var srt = string.Concat(Enumerable.Repeat("1\n00:00:01,000 --> 00:00:02,000\nNo sé qué quieres de mí, pero esto se termina aquí mismo, amigo mío.\n\n", 10));
        Assert.Null(TextLanguageGuesser.Guess(srt, ".srt"));
    }

    [Fact]
    public void RecognisesJapaneseAndRefusesTinyFiles()
    {
        Assert.Equal("jpn", TextLanguageGuesser.Guess(Ass(Enumerable.Repeat("お前はもう死んでいる。なにを言っているんだ、本当に。", 6).ToArray()), ".ass"));
        Assert.Null(TextLanguageGuesser.Guess(Ass("Да."), ".ass"));
    }
}
