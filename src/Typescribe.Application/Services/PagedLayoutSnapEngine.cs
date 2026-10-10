namespace Typescribe.Application.Services;

/// <summary>
/// Shared physical-page snapping for direct-manipulation clients. The engine consumes the same
/// page guides emitted by PagedLayoutEngine, so desktop movement stays deterministic and does not
/// create a separate coordinate model.
/// </summary>
public static class PagedLayoutSnapEngine
{
    public sealed record MoveResult(
        double XPoints,
        double YPoints,
        bool SnappedX,
        bool SnappedY,
        double? VerticalTargetPoints = null,
        double? HorizontalTargetPoints = null);

    public sealed record SizeResult(
        double WidthPoints,
        double HeightPoints,
        bool SnappedWidth,
        bool SnappedHeight,
        double? VerticalTargetPoints = null,
        double? HorizontalTargetPoints = null);

    public static MoveResult SnapMove(
        PageLayoutPage page,
        PageLayoutRect moving,
        double proposedXPoints,
        double proposedYPoints,
        IEnumerable<PageLayoutRect>? nearbyObjects = null,
        double thresholdPoints = 5)
    {
        ArgumentNullException.ThrowIfNull(page);

        var xTargets = VerticalTargets(page, nearbyObjects).Distinct().ToArray();
        var yTargets = HorizontalTargets(page, nearbyObjects).Distinct().ToArray();

        var x = SnapAxis(
            proposedXPoints,
            moving.WidthPoints,
            xTargets,
            thresholdPoints);
        var y = SnapAxis(
            proposedYPoints,
            moving.HeightPoints,
            yTargets,
            thresholdPoints);

        return new MoveResult(
            x.Value,
            y.Value,
            x.Snapped,
            y.Snapped,
            x.Target,
            y.Target);
    }

    public static SizeResult SnapResize(
        PageLayoutPage page,
        PageLayoutRect moving,
        double proposedWidthPoints,
        double proposedHeightPoints,
        IEnumerable<PageLayoutRect>? nearbyObjects = null,
        double thresholdPoints = 5,
        double minimumSizePoints = 24)
    {
        ArgumentNullException.ThrowIfNull(page);

        var width = Math.Max(minimumSizePoints, proposedWidthPoints);
        var height = Math.Max(minimumSizePoints, proposedHeightPoints);
        var proposedRight = moving.XPoints + width;
        var proposedBottom = moving.YPoints + height;

        var xMatch = ClosestTarget(
            proposedRight,
            VerticalTargets(page, nearbyObjects),
            thresholdPoints);
        var yMatch = ClosestTarget(
            proposedBottom,
            HorizontalTargets(page, nearbyObjects),
            thresholdPoints);

        if (xMatch is { } x)
            width = Math.Max(minimumSizePoints, x.Target - moving.XPoints);
        if (yMatch is { } y)
            height = Math.Max(minimumSizePoints, y.Target - moving.YPoints);

        return new SizeResult(
            width,
            height,
            xMatch is not null,
            yMatch is not null,
            xMatch?.Target,
            yMatch?.Target);
    }

    private static IEnumerable<double> VerticalTargets(
        PageLayoutPage page,
        IEnumerable<PageLayoutRect>? nearbyObjects)
    {
        yield return 0;
        yield return page.WidthPoints / 2d;
        yield return page.WidthPoints;

        foreach (var guide in page.Guides)
            if (guide.Orientation == PageLayoutGuideOrientation.Vertical)
                yield return guide.PositionPoints;

        if (nearbyObjects is null) yield break;
        foreach (var bounds in nearbyObjects)
        {
            yield return bounds.XPoints;
            yield return bounds.XPoints + bounds.WidthPoints / 2d;
            yield return bounds.RightPoints;
        }
    }

    private static IEnumerable<double> HorizontalTargets(
        PageLayoutPage page,
        IEnumerable<PageLayoutRect>? nearbyObjects)
    {
        yield return 0;
        yield return page.HeightPoints / 2d;
        yield return page.HeightPoints;

        foreach (var guide in page.Guides)
            if (guide.Orientation == PageLayoutGuideOrientation.Horizontal)
                yield return guide.PositionPoints;

        if (nearbyObjects is null) yield break;
        foreach (var bounds in nearbyObjects)
        {
            yield return bounds.YPoints;
            yield return bounds.YPoints + bounds.HeightPoints / 2d;
            yield return bounds.BottomPoints;
        }
    }

    private static (double Value, bool Snapped, double? Target) SnapAxis(
        double proposedStart,
        double size,
        IEnumerable<double> targets,
        double threshold)
    {
        var candidates = new[]
        {
            proposedStart,
            proposedStart + size / 2d,
            proposedStart + size
        };

        double? bestCorrection = null;
        double? bestTarget = null;

        foreach (var target in targets)
        {
            foreach (var candidate in candidates)
            {
                var correction = target - candidate;
                if (Math.Abs(correction) > threshold) continue;
                if (bestCorrection is null || Math.Abs(correction) < Math.Abs(bestCorrection.Value))
                {
                    bestCorrection = correction;
                    bestTarget = target;
                }
            }
        }

        return bestCorrection is null
            ? (proposedStart, false, null)
            : (proposedStart + bestCorrection.Value, true, bestTarget);
    }

    private static (double Target, double Distance)? ClosestTarget(
        double proposed,
        IEnumerable<double> targets,
        double threshold)
    {
        (double Target, double Distance)? best = null;
        foreach (var target in targets)
        {
            var distance = Math.Abs(target - proposed);
            if (distance > threshold) continue;
            if (best is null || distance < best.Value.Distance)
                best = (target, distance);
        }
        return best;
    }
}
