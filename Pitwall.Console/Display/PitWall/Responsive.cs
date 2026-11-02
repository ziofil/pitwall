using Spectre.Console.Rendering;

namespace Pitwall.Console;

/// <summary>
/// Chooses what to draw from the width available at render time, so a page can add side panels
/// on wide terminals and drop them on narrow ones.
/// </summary>
public sealed class Responsive(Func<int, IRenderable> build) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        build(maxWidth).Render(options, maxWidth);
}
