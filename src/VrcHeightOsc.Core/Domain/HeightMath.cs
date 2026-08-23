namespace VrcHeightOsc.Core.Domain;

public static class HeightMath
{
    public const double MinOscHeight = 0.01d;
    public const double MaxOscHeight = 10000.0d;

    /// <summary>
    /// Python's clamp returns the lower bound for invalid values.  Preserve
    /// that behavior for NaN/infinity instead of allowing them onto OSC.
    /// </summary>
    public static double Clamp(double value, double lower, double upper)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return lower;
        }

        return Math.Max(lower, Math.Min(upper, value));
    }

    public static double? ApplyRuleHeightLimits(
        RuleDefinition rule,
        double? currentHeight,
        double targetHeight)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.LimitEnabled)
        {
            return Clamp(targetHeight, MinOscHeight, MaxOscHeight);
        }

        var lower = Math.Min(rule.LimitMin, rule.LimitMax);
        var upper = Math.Max(rule.LimitMin, rule.LimitMax);

        var current = currentHeight ?? targetHeight;

        if (rule.LimitBehavior.Equals("clamp", StringComparison.Ordinal))
        {
            return Clamp(targetHeight, lower, upper);
        }

        if (current >= lower && current <= upper)
        {
            return Clamp(targetHeight, lower, upper);
        }

        if (current < lower)
        {
            if (rule.LimitBehavior.Equals("block_outside", StringComparison.Ordinal))
            {
                return null;
            }

            if (rule.LimitBehavior.Equals("toward_range", StringComparison.Ordinal))
            {
                if (targetHeight <= current)
                {
                    return null;
                }

                return Clamp(targetHeight, lower, upper);
            }
        }

        if (current > upper)
        {
            if (rule.LimitBehavior.Equals("block_outside", StringComparison.Ordinal))
            {
                return null;
            }

            if (rule.LimitBehavior.Equals("toward_range", StringComparison.Ordinal))
            {
                if (targetHeight >= current)
                {
                    return null;
                }

                return Clamp(targetHeight, lower, upper);
            }
        }

        return Clamp(targetHeight, lower, upper);
    }

    public static double MapFollowHeight(RuleDefinition rule, double value)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var inputMin = rule.FollowInputMin;
        var inputMax = rule.FollowInputMax;

        var t = Math.Abs(inputMax - inputMin) < 0.000001d
            ? 0.0d
            : (value - inputMin) / (inputMax - inputMin);

        t = Clamp(t, 0.0d, 1.0d);
        return rule.FollowHeightMin + (rule.FollowHeightMax - rule.FollowHeightMin) * t;
    }
}
