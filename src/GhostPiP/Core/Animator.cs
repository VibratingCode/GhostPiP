using System.Diagnostics;
using System.Windows.Media;

namespace GhostPiP.Core;

public static class Ease
{
    public static double Linear(double t) => t;
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double OutQuint(double t) => 1 - Math.Pow(1 - t, 5);
    public static double InCubic(double t) => t * t * t;
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Settles with one small, soft overshoot: a mirror coming to rest, not a bounce.</summary>
    public static double Spring(double t) => 1 - Math.Exp(-6.5 * t) * Math.Cos(9.5 * t);
}

/// <summary>Per-frame tweens for things WPF's own animations can't reach: native windows and DWM thumbnails.</summary>
public static class Animator
{
    public sealed class Tween
    {
        internal double Start, Duration;
        internal Action<double> Update = _ => { };
        internal Func<double, double> Easing = Ease.OutCubic;
        internal Action? Done;
        internal bool Cancelled;

        public void Cancel() => Cancelled = true;
    }

    static readonly List<Tween> s_tweens = new();
    static readonly Stopwatch s_clock = Stopwatch.StartNew();
    static bool s_hooked, s_inFrame;

    public static bool ReduceMotion { get; set; }

    public static Tween Run(double milliseconds, Action<double> update, Func<double, double>? easing = null, Action? done = null)
    {
        var tween = new Tween
        {
            Start = s_clock.Elapsed.TotalMilliseconds,
            Duration = ReduceMotion ? 0 : milliseconds,
            Update = update,
            Easing = easing ?? Ease.OutCubic,
            Done = done,
        };
        if (tween.Duration <= 0)
        {
            update(1);
            done?.Invoke();
            return tween;
        }
        update(tween.Easing(0));
        s_tweens.Add(tween);
        if (!s_hooked)
        {
            s_hooked = true;
            CompositionTarget.Rendering += OnFrame;
        }
        return tween;
    }

    static void OnFrame(object? sender, EventArgs e)
    {
        // Resizing a WPF window from inside a tween renders synchronously, which raises this event again.
        if (s_inFrame)
            return;
        s_inFrame = true;
        try
        {
            double now = s_clock.Elapsed.TotalMilliseconds;
            // A snapshot, because callbacks can start new tweens or cancel running ones.
            foreach (var tween in s_tweens.ToArray())
            {
                if (tween.Cancelled)
                {
                    s_tweens.Remove(tween);
                    continue;
                }
                double t = Math.Min(1, (now - tween.Start) / tween.Duration);
                tween.Update(t >= 1 ? 1 : tween.Easing(t));
                if (t >= 1)
                {
                    s_tweens.Remove(tween);
                    if (!tween.Cancelled)
                        tween.Done?.Invoke();
                }
            }
        }
        finally
        {
            s_inFrame = false;
        }
        if (s_tweens.Count == 0)
        {
            s_hooked = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }
}
