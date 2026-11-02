using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// A single line of markup that never wraps: text beyond the available width is cut off with an
/// ellipsis. With a background colour it fills the full width, like a title bar or status bar.
/// Dense pit wall pages stay aligned this way when the terminal is narrow.
/// </summary>
public sealed class SingleLine(string markup, Color? background = null) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        if (background is not null)
            return new Measurement(maxWidth, maxWidth);
        var natural = ((IRenderable)new Markup(markup)).Measure(options, 10_000);
        return new Measurement(Math.Min(natural.Max, maxWidth), Math.Min(natural.Max, maxWidth));
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        if (maxWidth <= 0)
            yield break;

        // Lay the markup out on one very long line, then crop it to the width we have
        var segments = ((IRenderable)new Markup(markup))
            .Render(options, 10_000)
            .TakeWhile(x => !x.IsLineBreak)
            .ToList();
        var total = segments.Sum(x => x.Text.Length);
        var limit = total > maxWidth ? maxWidth - 1 : maxWidth;

        var used = 0;
        foreach (var segment in segments)
        {
            if (used >= limit)
                break;
            var text =
                segment.Text.Length > limit - used ? segment.Text[..(limit - used)] : segment.Text;
            yield return new Segment(text, WithBackground(segment.Style));
            used += text.Length;
        }

        if (total > maxWidth)
        {
            yield return new Segment("…", WithBackground(Style.Plain));
            used++;
        }

        if (background is not null && used < maxWidth)
        {
            yield return new Segment(
                new string(' ', maxWidth - used),
                new Style(background: background)
            );
        }
    }

    private Style WithBackground(Style style) =>
        background is { } colour && style.Background == Color.Default
            ? style.Combine(new Style(background: colour))
            : style;
}
