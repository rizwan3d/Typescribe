using Typescribe.Domain.Models;

static void ExpectDirection(string name, string text, TextDirectionMode expected)
{
    var actual = UnicodeScriptClassifier.DetectDirection(text);
    if (actual != expected)
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}. Text: {text}");
}

static void ExpectScript(string name, string text, string? language, ScriptMode expected)
{
    var actual = UnicodeScriptClassifier.DetectScript(text, language);
    if (actual != expected)
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}. Text: {text}");
}

var fixtures = new[]
{
    "اردو میں ایک سطر 2026 TypeScribe",
    "العربية مع English 123",
    "فارسی و English 123",
    "English first پھر اردو",
    "**اردو**",
    "1234 — !?"
};
var originals = fixtures.ToArray();

ExpectDirection("Urdu", fixtures[0], TextDirectionMode.RightToLeft);
ExpectDirection("Arabic", fixtures[1], TextDirectionMode.RightToLeft);
ExpectDirection("Persian", fixtures[2], TextDirectionMode.RightToLeft);
ExpectDirection("English-first mixed", fixtures[3], TextDirectionMode.LeftToRight);
ExpectDirection("Markdown-neutral prefix", fixtures[4], TextDirectionMode.RightToLeft);
ExpectDirection("Neutral-only", fixtures[5], TextDirectionMode.Auto);

ExpectScript("Urdu language", "یہ ایک جملہ ہے", "ur-PK", ScriptMode.UrduNastaliq);
ExpectScript("Urdu distinct characters", "ٹیسٹ اردو", null, ScriptMode.UrduNastaliq);
ExpectScript("Persian language", "فارسی", "fa-IR", ScriptMode.Persian);
ExpectScript("Arabic language", "العربية", "ar", ScriptMode.Arabic);
ExpectScript("Hebrew", "שלום", "he", ScriptMode.Hebrew);

if (!UnicodeScriptClassifier.ContainsArabicScript("abc العربية xyz"))
    throw new InvalidOperationException("Arabic-script coverage detection failed.");
if (!UnicodeScriptClassifier.IsRtlLanguage("ur-PK") || !UnicodeScriptClassifier.IsRtlLanguage("fa-IR"))
    throw new InvalidOperationException("RTL language classification failed.");

for (var index = 0; index < fixtures.Length; index++)
{
    if (!string.Equals(fixtures[index], originals[index], StringComparison.Ordinal))
        throw new InvalidOperationException("BiDi classification mutated logical Unicode source text.");
}

Console.WriteLine("TypeScribe Unicode BiDi smoke fixtures passed.");
