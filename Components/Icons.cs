using Microsoft.AspNetCore.Components;

namespace MtnAttendance.Components;

/// <summary>
/// Pictogrammes des maquettes en SVG inline : ils héritent de currentColor,
/// donc une seule règle CSS suffit pour les décliner (ok / warn / bad).
/// </summary>
public static class Icons
{
    private const string Attrs =
        "fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";

    public static MarkupString Check => Svg(40, "<path d=\"M5 13l5 5L19 7\" />");

    public static MarkupString Clock => Svg(40,
        "<circle cx=\"12\" cy=\"12\" r=\"9\" /><path d=\"M12 7v5l3 2\" />");

    public static MarkupString Warning => Svg(40,
        "<path d=\"M12 4L2.5 20h19L12 4z\" /><path d=\"M12 10v4\" /><path d=\"M12 17.5v.01\" />");

    public static MarkupString Lock => Svg(44,
        "<rect x=\"4\" y=\"10\" width=\"16\" height=\"11\" rx=\"2\" /><path d=\"M8 10V7a4 4 0 0 1 8 0v3\" />");

    public static MarkupString Pin => Svg(18,
        "<path d=\"M12 21s7-6.2 7-11a7 7 0 1 0-14 0c0 4.8 7 11 7 11z\" /><circle cx=\"12\" cy=\"10\" r=\"2.5\" />");

    private static MarkupString Svg(int size, string body) => new(
        $"<svg width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" {Attrs} aria-hidden=\"true\">{body}</svg>");
}
