using System.Text;
using System.Text.Json;

namespace FreeFlow.Core;

/// <summary>
/// Optional AI features (command mode, dictation polish) via any OpenAI-compatible
/// endpoint — Ollama, LM Studio, llama.cpp server, or a hosted key. Everything else
/// in FreeFlow works without this.
/// </summary>
public static class AiProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static Task<string?> RewriteAsync(AppConfig cfg, string instruction, string text, CancellationToken ct = default)
        => ChatAsync(cfg,
            "You edit text. Apply the user's instruction to the text and output ONLY the edited text — no explanations, no quotes, no markdown fences.",
            $"Instruction: {instruction}\n\nText:\n{text}", ct);

    public static Task<string?> PolishAsync(AppConfig cfg, string text, CancellationToken ct = default)
        => ChatAsync(cfg,
            "You clean up dictated speech. Fix grammar, punctuation and obvious speech-to-text errors while preserving the speaker's words and meaning. Never add new content. Output ONLY the cleaned text.",
            text, ct);

    public static async Task<(bool Ok, string Message)> TestAsync(AppConfig cfg)
    {
        try
        {
            var reply = await ChatAsync(cfg, "Reply with the single word: pong", "ping", CancellationToken.None);
            return string.IsNullOrWhiteSpace(reply)
                ? (false, "Endpoint responded but returned no text.")
                : (true, $"OK — model replied: {reply.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string?> ChatAsync(AppConfig cfg, string system, string user, CancellationToken ct)
    {
        string url = cfg.AiBaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new
        {
            model = cfg.AiModel,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.2,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(cfg.AiApiKey))
            req.Headers.Add("Authorization", $"Bearer {cfg.AiApiKey}");

        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI endpoint returned {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        string? content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return content?.Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
