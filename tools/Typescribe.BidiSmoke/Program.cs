using Typescribe.Application.Services;
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

static BidiEditableLine ParseRichLine(string source)
{
    if (!BidiMarkdownLineCodec.TryParse(source, out var parsed))
        throw new InvalidOperationException("Rich BiDi line failed to parse.");
    return parsed;
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

var richFormatting = new CharacterFormatting(
    Bold: true,
    Language: "ur-PK",
    Script: ScriptMode.UrduNastaliq,
    Direction: TextDirectionMode.RightToLeft);
var richSource = RichMarkdownFormattingCodec.WrapInline("اردو", richFormatting) + " test";
var richParsed = ParseRichLine(richSource);
if (!string.Equals(richParsed.Text, "اردو test", StringComparison.Ordinal) ||
    richParsed.Spans.Count != 1 || richParsed.Spans[0].Start != 0 || richParsed.Spans[0].End != 4)
    throw new InvalidOperationException("Rich inline metadata was not projected to the clean logical BiDi line correctly.");

// Typing exactly at the end boundary of a formatted Urdu run should inherit that run's formatting.
var insertedVisible = "اردونئی test";
var insertedSource = BidiMarkdownLineCodec.ApplyEdit(richParsed, insertedVisible);
var insertedParsed = ParseRichLine(insertedSource);
if (!string.Equals(insertedParsed.Text, insertedVisible, StringComparison.Ordinal) ||
    insertedParsed.Spans.Count != 1 || insertedParsed.Spans[0].Start != 0 || insertedParsed.Spans[0].End != 7)
    throw new InvalidOperationException("Rich formatting span did not survive insertion at the RTL run boundary.");

// Replacing the complete formatted word must preserve the same formatting on the replacement.
var replacementVisible = "متن test";
var replacementSource = BidiMarkdownLineCodec.ApplyEdit(richParsed, replacementVisible);
var replacementParsed = ParseRichLine(replacementSource);
if (!string.Equals(replacementParsed.Text, replacementVisible, StringComparison.Ordinal) ||
    replacementParsed.Spans.Count != 1 || replacementParsed.Spans[0].Start != 0 || replacementParsed.Spans[0].End != 3)
    throw new InvalidOperationException("Rich formatting span did not survive exact RTL run replacement.");

// Serializing and reparsing without an edit must preserve the exact logical text and rich semantics.
var serializedSource = BidiMarkdownLineCodec.Serialize(richParsed.Text, richParsed.Spans);
var serializedParsed = ParseRichLine(serializedSource);
if (!string.Equals(serializedParsed.Text, richParsed.Text, StringComparison.Ordinal) || serializedParsed.Spans.Count != 1)
    throw new InvalidOperationException("Rich BiDi line serialization failed to round-trip.");

Console.WriteLine("TypeScribe Unicode BiDi smoke fixtures passed.");
