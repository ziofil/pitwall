using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// Chooses what to draw from the space available at render time, so a page can add side panels
/// on wide terminals and drop them on narrow ones. The height is the rows available when the
/// content is placed in a layout, or <c>null</c> when it isn't limited.
/// </summary>
public sealed class Responsive(Func<int, int?, IRenderable> build) : IRenderable
{
    public Responsive(Func<int, IRenderable> build)
        : this((width, _) => build(width)) { }

    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        build(maxWidth, options.Height).Render(options, maxWidth);
}
