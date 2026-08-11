namespace PaperBridge.Application.Reading;

/// <summary>
/// Converts device-specific wheel deltas into a bounded pixel movement. Some
/// high-resolution mice report a large accumulated delta in one event; that
/// value must never be used as a raw scroll offset.
/// </summary>
public static class PdfScrollWheel
{
    public const double StandardWheelDelta = 120d;
    public const double MaximumStepPixels = 84d;

    public static double GetPixelMovement(int wheelDelta, double viewportHeight)
    {
        if (wheelDelta == 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            return 0;
        }

        var fullStep = Math.Clamp(viewportHeight * 0.09, 48d, MaximumStepPixels);
        var fraction = Math.Clamp(Math.Abs(wheelDelta) / StandardWheelDelta, 0.05d, 1d);
        return -Math.Sign(wheelDelta) * fullStep * fraction;
    }
}
