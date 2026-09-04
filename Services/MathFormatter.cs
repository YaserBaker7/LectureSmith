using System.Text;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

/// <summary>
/// Converts LaTeX math expressions into clean typographical Unicode text
/// for formats that do not have native LaTeX/MathJax rendering engines (e.g. PDF and Avalonia Preview).
/// </summary>
public static class MathFormatter
{
    private static readonly Regex DisplayMathRegex =
        new(@"(?s)\$\$(.+?)\$\$", RegexOptions.Compiled);

    private static readonly Regex InlineMathRegex =
        new(@"(?<!\$)\$(?!\$)(.+?)(?<!\$)\$(?!\$)", RegexOptions.Compiled);

    private static readonly Regex TextCommandRegex =
        new(@"\\text\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex MathBfCommandRegex =
        new(@"\\mathbf\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex MathItCommandRegex =
        new(@"\\mathit\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex MathRmCommandRegex =
        new(@"\\mathrm\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex FracCommandRegex =
        new(@"\\frac\{([^}]+)\}\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex SqrtCommandRegex =
        new(@"\\sqrt(?:\[[^\]]+\])?\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly Regex SubscriptBracedRegex =
        new(@"_\{([0-9a-zA-Z\+\-]+)\}", RegexOptions.Compiled);

    private static readonly Regex SuperscriptBracedRegex =
        new(@"\^\{([0-9a-zA-Z\+\-]+)\}", RegexOptions.Compiled);

    private static readonly Regex SubscriptSingleRegex =
        new(@"_([0-9a-zA-Z])", RegexOptions.Compiled);

    private static readonly Regex SuperscriptSingleRegex =
        new(@"\^([0-9a-zA-Z])", RegexOptions.Compiled);

    // Common LaTeX math symbols dictionary
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
        { @"\alpha", "α" },
        { @"\beta", "β" },
        { @"\gamma", "γ" },
        { @"\delta", "δ" },
        { @"\epsilon", "ε" },
        { @"\varepsilon", "ε" },
        { @"\zeta", "ζ" },
        { @"\eta", "η" },
        { @"\theta", "θ" },
        { @"\vartheta", "ϑ" },
        { @"\iota", "ι" },
        { @"\kappa", "κ" },
        { @"\lambda", "λ" },
        { @"\mu", "μ" },
        { @"\nu", "ν" },
        { @"\xi", "ξ" },
        { @"\pi", "π" },
        { @"\varpi", "ϖ" },
        { @"\rho", "ρ" },
        { @"\varrho", "ϱ" },
        { @"\sigma", "σ" },
        { @"\varsigma", "ς" },
        { @"\tau", "τ" },
        { @"\upsilon", "υ" },
        { @"\phi", "φ" },
        { @"\varphi", "ϕ" },
        { @"\chi", "χ" },
        { @"\psi", "ψ" },
        { @"\omega", "ω" },

        // Greek uppercase
        { @"\Gamma", "Γ" },
        { @"\Delta", "Δ" },
        { @"\Theta", "Θ" },
        { @"\Lambda", "Λ" },
        { @"\Xi", "Ξ" },
        { @"\Pi", "Π" },
        { @"\Sigma", "Σ" },
        { @"\Upsilon", "Υ" },
        { @"\Phi", "Φ" },
        { @"\Psi", "Ψ" },
        { @"\Omega", "Ω" },

        // Spacing commands
        { @"\,", " " },
        { @"\;", " " },
        { @"\:", " " },
        { @"\!", "" },
        { @"\quad", "  " },
        { @"\qquad", "    " },
        { @"\\", "\n" }
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
    /// Formats a single LaTeX math formula into clean Unicode / Markdown text.
    /// E.g. "\text{Skewness} = 0.41 \implies \mathbf{Right-Skewed}" -> "Skewness = 0.41 ⟹ **Right-Skewed**"
    /// </summary>
    public static string FormatFormula(string latex, bool useMarkdownEmphasis = true)
    {
        if (string.IsNullOrWhiteSpace(latex)) return string.Empty;

        var result = latex.Trim();

        // 1. Convert formatting commands
        result = TextCommandRegex.Replace(result, m => m.Groups[1].Value);
        result = MathBfCommandRegex.Replace(result, m => useMarkdownEmphasis ? $"**{m.Groups[1].Value}**" : m.Groups[1].Value);
        result = MathItCommandRegex.Replace(result, m => useMarkdownEmphasis ? $"*{m.Groups[1].Value}*" : m.Groups[1].Value);
        result = MathRmCommandRegex.Replace(result, m => m.Groups[1].Value);

        // 2. Fractions and roots
        result = FracCommandRegex.Replace(result, m => $"({m.Groups[1].Value} / {m.Groups[2].Value})");
        result = SqrtCommandRegex.Replace(result, m => $"√({m.Groups[1].Value})");

        // 3. Greek and mathematical symbols (longest first to avoid partial replacements)
        foreach (var (cmd, symbol) in OrderedMathSymbols)
        {
            result = result.Replace(cmd, symbol);
        }

        // 4. Superscripts & subscripts
        result = SuperscriptBracedRegex.Replace(result, m => ToSuperscript(m.Groups[1].Value));
        result = SubscriptBracedRegex.Replace(result, m => ToSubscript(m.Groups[1].Value));
        result = SuperscriptSingleRegex.Replace(result, m => ToSuperscript(m.Groups[1].Value));
        result = SubscriptSingleRegex.Replace(result, m => ToSubscript(m.Groups[1].Value));

        // 5. Clean remaining curly braces from grouped parameters
        result = result.Replace("{", "").Replace("}", "");

        // 6. Clean multiple spaces
        result = Regex.Replace(result, @"[ \t]+", " ");

        return result.Trim();
    }

    /// <summary>
    /// Replaces all inline ($...$) and display ($$...$$) LaTeX math blocks in markdown
    /// with formatted Unicode typography suitable for Markdown viewers and plain-text displays.
    /// </summary>
    public static string FormatMarkdownForDisplay(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        // First convert display math ($$...$$) into formatted blockquotes
        var output = DisplayMathRegex.Replace(markdown, m =>
        {
            var formula = FormatFormula(m.Groups[1].Value, useMarkdownEmphasis: true);
            return $"\n> 📐 **Formula:** {formula}\n";
        });

        // Then convert inline math ($...$)
        output = InlineMathRegex.Replace(output, m =>
        {
            var formula = FormatFormula(m.Groups[1].Value, useMarkdownEmphasis: true);
            return formula;
        });

        return output;
    }

    private static string ToSuperscript(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (SuperscriptMap.TryGetValue(c, out var mapped))
                sb.Append(mapped);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ToSubscript(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (SubscriptMap.TryGetValue(c, out var mapped))
                sb.Append(mapped);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
