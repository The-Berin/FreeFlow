using System.Text.RegularExpressions;

namespace FreeFlow.Core;

public record FormatResult(string Text, bool DeleteLast);

/// <summary>
/// Wispr-style auto-edits, done deterministically: fillers, spoken commands,
/// custom dictionary, snippets, and per-app tone. Pure function — unit-testable.
/// </summary>
public static class TextFormatter
{
    private static readonly string FillerPattern =
        @"(?<=^|\s|\p{P})(?:" +
        string.Join("|", new[] { "um", "umm", "uh", "uhh", "er", "erm", "hmm", "hm", "mhm", "mm-hmm" }
            .Select(Regex.Escape)) +
        @")\b[,.]?\s*";

    public static FormatResult Format(string raw, AppConfig cfg, string tone)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return new FormatResult("", false);

        if (tone == "Verbatim")
            return new FormatResult(text, false);

        // Whole-utterance "scratch that" deletes the previous dictation.
        string bare = StripEdgePunctuation(text).ToLowerInvariant();
        if (bare == "scratch that" || bare == "scratch this" || bare == "delete that")
            return new FormatResult("", true);

        // Snippets match the whole utterance ("my email" → the actual address).
        foreach (var s in cfg.Snippets.Where(s => s.Trigger.Trim().Length > 0))
        {
            if (string.Equals(StripEdgePunctuation(text), s.Trigger.Trim(), StringComparison.OrdinalIgnoreCase))
                return new FormatResult(s.Expansion, false);
        }

        // Mid-utterance "scratch that" drops everything said before it.
        var scratch = Regex.Match(text, @"\bscratch (that|this)\b[,.!?]?\s*", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        if (scratch.Success)
            text = text[(scratch.Index + scratch.Length)..].TrimStart();

        if (cfg.RemoveFillers)
        {
            text = Regex.Replace(text, FillerPattern, "", RegexOptions.IgnoreCase);
        }

        if (cfg.SpokenCommands)
        {
            text = Regex.Replace(text, @"[,.;:]?\s*\bnew paragraph\b[,.!?]?\s*", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[,.;:]?\s*\b(?:new line|newline)\b[,.!?]?\s*", "\n", RegexOptions.IgnoreCase);
        }

        if (cfg.PunctuationWords)
        {
            text = Regex.Replace(text, @"\s*\bcomma\b", ",", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bperiod\b", ".", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bfull stop\b", ".", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bquestion mark\b", "?", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bexclamation (?:point|mark)\b", "!", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bsemicolon\b", ";", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*\bcolon\b", ":", RegexOptions.IgnoreCase);
        }

        foreach (var d in cfg.Dictionary.Where(d => d.Spoken.Trim().Length > 0))
        {
            text = Regex.Replace(text, $@"\b{Regex.Escape(d.Spoken.Trim())}\b",
                d.Written.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        text = TidyWhitespace(text);
        text = CapitalizeAfterBreaks(text);

        switch (tone)
        {
            case "Casual":
                // one short sentence → drop the trailing period, chat style
                if (!text.Contains('\n') && text.EndsWith('.') && !text.EndsWith("..") &&
                    text.Count(ch => ch is '.' or '!' or '?') == 1)
                    text = text[..^1];
                break;
            case "Professional":
                if (text.Length > 0)
                {
                    if (char.IsLower(text[0]))
                        text = char.ToUpper(text[0]) + text[1..];
                    if (!"….!?\n".Contains(text[^1]))
                        text += ".";
                }
                break;
        }

        return new FormatResult(text, false);
    }

    private static string StripEdgePunctuation(string s)
        => s.Trim().Trim('.', ',', '!', '?', ';', ':', ' ');

    private static string TidyWhitespace(string s)
    {
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" +([,.!?;:])", "$1");          // no space before punctuation
        s = Regex.Replace(s, @"([,.!?;:])(?=\p{L})", "$1 ");  // space after punctuation
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = Regex.Replace(s, @"^[ ,.;:]+", "");               // leftovers from removed fillers
        return s.Trim();
    }

    private static string CapitalizeAfterBreaks(string s)
        => Regex.Replace(s, @"(^|\n+)(\p{Ll})", m => m.Groups[1].Value + char.ToUpper(m.Groups[2].Value[0]));
}
