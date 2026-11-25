using System.Text;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

/// <summary>
/// Converts LaTeX math expressions into clean typographical Unicode text
/// for renderers that do not run MathJax (such as the QuestPDF fallback).
/// </summary>
public static partial class MathFormatter
{
    private static readonly Dictionary<string, string> MathSymbols = new()
    {
        // Logic & Arrows
        { @"\implies", "⟹" },
        { @"\iff", "⟺" },
        { @"\rightarrow", "→" },
        { @"\to", "→" },
        { @"\leftarrow", "←" },
        { @"\leftrightarrow", "↔" },
        { @"\Rightarrow", "⇒" },
        { @"\Leftarrow", "⇐" },
        { @"\Leftrightarrow", "⇔" },
        { @"\uparrow", "↑" },
        { @"\downarrow", "↓" },

        // Relations & Operators
        { @"\leq", "≤" },
        { @"\le", "≤" },
        { @"\geq", "≥" },
        { @"\ge", "≥" },
        { @"\neq", "≠" },
        { @"\ne", "≠" },
        { @"\approx", "≈" },
        { @"\equiv", "≡" },
        { @"\sim", "~" },
        { @"\pm", "±" },
        { @"\mp", "∓" },
        { @"\times", "×" },
        { @"\div", "÷" },
        { @"\cdot", "·" },
        { @"\circ", "∘" },
        { @"\infty", "∞" },
        { @"\partial", "∂" },
        { @"\nabla", "∇" },
        { @"\forall", "∀" },
        { @"\exists", "∃" },
        { @"\in", "∈" },
        { @"\notin", "∉" },
        { @"\subset", "⊂" },
        { @"\subseteq", "⊆" },
        { @"\cap", "∩" },
        { @"\cup", "∪" },
        { @"\land", "∧" },
        { @"\lor", "∨" },
        { @"\neg", "¬" },
        { @"\sum", "∑" },
        { @"\prod", "∏" },
        { @"\int", "∫" },

        // Greek lowercase
        { @"\alpha", "α" }, { @"\beta", "β" }, { @"\gamma", "γ" }, { @"\delta", "δ" },
        { @"\epsilon", "ε" }, { @"\varepsilon", "ε" }, { @"\zeta", "ζ" }, { @"\eta", "η" },
        { @"\theta", "θ" }, { @"\vartheta", "ϑ" }, { @"\iota", "ι" }, { @"\kappa", "κ" },
        { @"\lambda", "λ" }, { @"\mu", "μ" }, { @"\nu", "ν" }, { @"\xi", "ξ" },
        { @"\pi", "π" }, { @"\varpi", "ϖ" }, { @"\rho", "ρ" }, { @"\varrho", "ϱ" },
        { @"\sigma", "σ" }, { @"\varsigma", "ς" }, { @"\tau", "τ" }, { @"\upsilon", "υ" },
        { @"\phi", "φ" }, { @"\varphi", "ϕ" }, { @"\chi", "χ" }, { @"\psi", "ψ" },
        { @"\omega", "ω" },

        // Greek uppercase
        { @"\Gamma", "Γ" }, { @"\Delta", "Δ" }, { @"\Theta", "Θ" }, { @"\Lambda", "Λ" },
        { @"\Xi", "Ξ" }, { @"\Pi", "Π" }, { @"\Sigma", "Σ" }, { @"\Upsilon", "Υ" },
        { @"\Phi", "Φ" }, { @"\Psi", "Ψ" }, { @"\Omega", "Ω" },

        // Spacing commands
        { @"\,", " " }, { @"\;", " " }, { @"\:", " " }, { @"\!", "" },
        { @"\quad", "  " }, { @"\qquad", "    " }, { @"\\", "\n" }
    };

    private static readonly KeyValuePair<string, string>[] OrderedMathSymbols =
        MathSymbols.OrderByDescending(k => k.Key.Length).ToArray();

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        { '0', '⁰' }, { '1', '¹' }, { '2', '²' }, { '3', '³' }, { '4', '⁴' },
        { '5', '⁵' }, { '6', '⁶' }, { '7', '⁷' }, { '8', '⁸' }, { '9', '⁹' },
        { '+', '⁺' }, { '-', '⁻' }, { '=', '⁼' }, { '(', '⁽' }, { ')', '⁾' },
        { 'n', 'ⁿ' }, { 'i', 'ⁱ' }, { 'x', 'ˣ' }
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        { '0', '₀' }, { '1', '₁' }, { '2', '₂' }, { '3', '₃' }, { '4', '₄' },
        { '5', '₅' }, { '6', '₆' }, { '7', '₇' }, { '8', '₈' }, { '9', '₉' },
        { '+', '₊' }, { '-', '₋' }, { '=', '₌' }, { '(', '₍' }, { ')', '₎' },
        { 'a', 'ₐ' }, { 'e', 'ₑ' }, { 'h', 'ₕ' }, { 'i', 'ᵢ' }, { 'j', 'ⱼ' },
        { 'k', 'ₖ' }, { 'l', 'ₗ' }, { 'm', 'ₘ' }, { 'n', 'ₙ' }, { 'o', 'ₒ' },
        { 'p', 'ₚ' }, { 'r', 'ᵣ' }, { 's', 'ₛ' }, { 't', 'ₜ' }, { 'u', 'ᵤ' },
        { 'v', 'ᵥ' }, { 'x', 'ₓ' }
    };

    /// <summary>
    /// Formats a LaTeX math formula into clean Unicode / optional Markdown text, supporting nested braces.
    /// </summary>
    public static string FormatFormula(string latex, bool useMarkdownEmphasis = true)
    {
        if (string.IsNullOrWhiteSpace(latex)) return string.Empty;

        var result = latex.Trim();

        // 1. Formatting commands (supports nested braces like \mathbf{x_{1}})
        result = ReplaceSingleArgCommand(result, @"\text", inner => inner);
        result = ReplaceSingleArgCommand(result, @"\mathrm", inner => inner);
        result = ReplaceSingleArgCommand(result, @"\mathbf", inner => useMarkdownEmphasis ? $"**{inner}**" : inner);
        result = ReplaceSingleArgCommand(result, @"\mathit", inner => useMarkdownEmphasis ? $"*{inner}*" : inner);

        // 2. Fractions and roots (handles nested braces cleanly)
        result = ReplaceFracCommands(result);
        result = ReplaceSqrtCommands(result);

        // 3. Greek and mathematical symbols (longest first)
        foreach (var (cmd, symbol) in OrderedMathSymbols)
            result = result.Replace(cmd, symbol);

        // 4. Superscripts & subscripts
        result = SuperscriptBracedRegex().Replace(result, m => ToSuperscript(m.Groups[1].Value));
        result = SubscriptBracedRegex().Replace(result, m => ToSubscript(m.Groups[1].Value));
        result = SuperscriptSingleRegex().Replace(result, m => ToSuperscript(m.Groups[1].Value));
        result = SubscriptSingleRegex().Replace(result, m => ToSubscript(m.Groups[1].Value));

        // 5. Remove leftover grouping braces and collapse extra horizontal spaces
        result = result.Replace("{", "").Replace("}", "");
        result = MultiSpaceRegex().Replace(result, " ");

        return result.Trim();
    }

    /// <summary>
    /// Replaces inline ($...$) and display ($$...$$) LaTeX delimiters inside a plain text segment
    /// without misinterpreting currency values (e.g., "$50 and $20").
    /// </summary>
    public static string ReplaceInlineAndDisplayMath(string text, bool useMarkdownEmphasis = false)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('$'))
            return text;

        var output = DisplayMathRegex().Replace(text, m => FormatFormula(m.Groups[1].Value, useMarkdownEmphasis));
        output = InlineMathRegex().Replace(output, m => FormatFormula(m.Groups[1].Value, useMarkdownEmphasis));
        return output;
    }

    private static string ReplaceSingleArgCommand(string input, string command, Func<string, string> formatter)
    {
        var prefix = command + "{";
        int idx = 0;
        while ((idx = input.IndexOf(prefix, idx, StringComparison.Ordinal)) >= 0)
        {
            if (TryReadBalancedBraces(input, idx + command.Length, out var content, out var endIndex))
            {
                var replacement = formatter(content);
                input = string.Concat(input.AsSpan(0, idx), replacement, input.AsSpan(endIndex));
                idx += replacement.Length;
            }
            else
            {
                idx += prefix.Length;
            }
        }
        return input;
    }

    private static string ReplaceFracCommands(string input)
    {
        const string cmd = @"\frac";
        int idx = 0;
        while ((idx = input.IndexOf(cmd, idx, StringComparison.Ordinal)) >= 0)
        {
            int pos = idx + cmd.Length;
            if (TryReadBalancedBraces(input, pos, out var num, out var afterNum) &&
                TryReadBalancedBraces(input, afterNum, out var den, out var afterDen))
            {
                var formatted = $"({FormatFormula(num, false)} / {FormatFormula(den, false)})";
                input = string.Concat(input.AsSpan(0, idx), formatted, input.AsSpan(afterDen));
                idx += formatted.Length;
            }
            else
            {
                idx += cmd.Length;
            }
        }
        return input;
    }

    private static string ReplaceSqrtCommands(string input)
    {
        const string cmd = @"\sqrt";
        int idx = 0;
        while ((idx = input.IndexOf(cmd, idx, StringComparison.Ordinal)) >= 0)
        {
            int pos = idx + cmd.Length;
            if (pos < input.Length && input[pos] == '[')
            {
                var closeBracket = input.IndexOf(']', pos + 1);
                if (closeBracket > pos) pos = closeBracket + 1;
            }

            if (TryReadBalancedBraces(input, pos, out var inner, out var endIndex))
            {
                var formatted = $"√({FormatFormula(inner, false)})";
                input = string.Concat(input.AsSpan(0, idx), formatted, input.AsSpan(endIndex));
                idx += formatted.Length;
            }
            else
            {
                idx += cmd.Length;
            }
        }
        return input;
    }

    private static bool TryReadBalancedBraces(string input, int openBraceIndex, out string content, out int nextIndex)
    {
        content = string.Empty;
        nextIndex = openBraceIndex;

        while (openBraceIndex < input.Length && char.IsWhiteSpace(input[openBraceIndex]))
            openBraceIndex++;

        if (openBraceIndex >= input.Length || input[openBraceIndex] != '{')
            return false;

        int depth = 0;
        int start = openBraceIndex + 1;
        for (int i = openBraceIndex; i < input.Length; i++)
        {
            if (input[i] == '{') depth++;
            else if (input[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    content = input[start..i];
                    nextIndex = i + 1;
                    return true;
                }
            }
        }
        return false;
    }

    private static string ToSuperscript(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(SuperscriptMap.TryGetValue(c, out var mapped) ? mapped : c);
        return sb.ToString();
    }

    private static string ToSubscript(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(SubscriptMap.TryGetValue(c, out var mapped) ? mapped : c);
        return sb.ToString();
    }

    [GeneratedRegex(@"(?s)\$\$(.+?)\$\$")]
    public static partial Regex DisplayMathRegex();

    // Pandoc/GFM-style inline math: no space after opening $, no space before closing $,
    // no unescaped inner $, and closing $ cannot be immediately followed by a word char (protects "$50 and $20").
    [GeneratedRegex(@"(?<!\\)(?<!\$)(?<!\w)\$(?!\$)(?!\s)([^$\r\n]+?)(?<!\s)(?<!\\)(?<!\$)\$(?!\$)(?!\w)")]
    public static partial Regex InlineMathRegex();

    [GeneratedRegex(@"_\{([0-9a-zA-Z\+\-]+)\}")]
    private static partial Regex SubscriptBracedRegex();

    [GeneratedRegex(@"\^\{([0-9a-zA-Z\+\-]+)\}")]
    private static partial Regex SuperscriptBracedRegex();

    [GeneratedRegex(@"_([0-9a-zA-Z])")]
    private static partial Regex SubscriptSingleRegex();

    [GeneratedRegex(@"\^([0-9a-zA-Z])")]
    private static partial Regex SuperscriptSingleRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultiSpaceRegex();
}
