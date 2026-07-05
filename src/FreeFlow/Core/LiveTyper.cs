using System.Text.RegularExpressions;

namespace FreeFlow.Core;

/// <summary>
/// Types the streaming transcript into the target app as the user speaks.
/// Holds back the last couple of words (the unstable tail the decoder may still
/// revise); when a revision does land in already-typed text, backspaces the
/// difference and retypes. All calls must come from the UI thread.
/// </summary>
public sealed class LiveTyper
{
    private const int HoldBackWords = 2;

    private string _typed = "";
    private bool _active;

    public bool Active => _active;
    public int TypedLength => _typed.Length;

    public void Begin()
    {
        _typed = "";
        _active = true;
    }

    /// <summary>Feed the latest raw partial transcript; types the newly committed words.</summary>
    public void OnPartial(string partial, AppConfig cfg, string? prefix)
    {
        if (!_active || string.IsNullOrEmpty(partial)) return;

        var words = partial.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int commit = words.Length - HoldBackWords;
        if (commit <= 0) return;

        string desired = string.Join(' ', words.Take(commit));
        desired = ApplyDictionary(desired, cfg);
        if (prefix != null) desired = prefix + desired;

        SyncTo(desired);
    }

    /// <summary>
    /// Called once when the utterance ends. Replaces everything typed live with the
    /// polished final text (or just finishes typing the tail when they match).
    /// </summary>
    public void Finalize(string finalText, string injectMode)
    {
        if (!_active) return;
        _active = false;

        if (finalText.Length == 0)
        {
            SyncTo(""); // erase everything we typed
            _typed = "";
            return;
        }

        // cheap path: final only appends to what's typed (case-sensitive prefix match)
        if (finalText.StartsWith(_typed, StringComparison.Ordinal))
        {
            string tail = finalText[_typed.Length..];
            if (tail.Length > 0)
                TextInjector.TypeText(tail);
        }
        else
        {
            int keep = CommonPrefix(_typed, finalText);
            TextInjector.SendBackspaces(BackspaceCount(_typed[keep..]));
            string replacement = finalText[keep..];
            if (replacement.Length > 0)
            {
                // paste large replacements (fast), type short ones (less clipboard churn)
                if (replacement.Length > 24 && injectMode != "Type")
                    TextInjector.PasteText(replacement);
                else
                    TextInjector.TypeText(replacement);
            }
        }
        _typed = finalText;
    }

    /// <summary>Abort live typing and erase what was typed (e.g. "scratch that").</summary>
    public void Erase()
    {
        _active = false;
        SyncTo("");
    }

    private void SyncTo(string desired)
    {
        if (desired == _typed) return;
        int keep = CommonPrefix(_typed, desired);
        int backspaces = BackspaceCount(_typed[keep..]);
        if (backspaces > 0)
            TextInjector.SendBackspaces(backspaces);
        if (desired.Length > keep)
            TextInjector.TypeText(desired[keep..]);
        _typed = desired;
    }

    private static int CommonPrefix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>One backspace deletes a whole newline pair in most Windows edit controls.</summary>
    private static int BackspaceCount(string removed)
        => removed.Replace("\r\n", "\n").Length;

    private static string ApplyDictionary(string text, AppConfig cfg)
    {
        foreach (var d in cfg.Dictionary.Where(d => d.Spoken.Trim().Length > 0))
        {
            text = Regex.Replace(text, $@"\b{Regex.Escape(d.Spoken.Trim())}\b",
                d.Written.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        return text;
    }
}
