using System.Text;

namespace Typescribe.Application.Services;

public sealed class WordCountService
{
    public int Count(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var count = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var wordChar = Rune.IsLetterOrDigit(rune) || rune.Value is '\'' or 0x2019;
            if (wordChar && !inWord) count++;
            inWord = wordChar;
        }
        return count;
    }
}
