namespace FloatIsland.Controls;

/// <summary>
/// Critically-damped-ish spring integrator using Apple's "Designing Fluid
/// Interfaces" parameterisation (response time in seconds + damping fraction).
///
/// Unlike a <c>DoubleAnimation</c>, a spring can be retargeted mid-flight: the
/// current velocity is carried over, so the motion never jumps or restarts. That is
/// what makes panel resizes and lyric scrolling feel physical instead of scripted.
/// </summary>
internal sealed class Spring
{
    /// <summary>Time to reach the target, in seconds. Smaller = snappier.</summary>
    public double Response { get; init; } = 0.38;

    /// <summary>1.0 = never overshoots; below 1.0 springs past the target.</summary>
    public double Damping { get; init; } = 0.86;

    public double Value { get; private set; }
    public double Target { get; private set; }
    public double Velocity { get; private set; }

    public Spring(double response, double damping, double initial = 0)
    {
        Response = response;
        Damping = damping;
        Value = initial;
        Target = initial;
    }

    public bool Settled =>
        Math.Abs(Value - Target) < 0.02 && Math.Abs(Velocity) < 0.6;

    public void Snap(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    public void Aim(double target) => Target = target;

    /// <summary>Advances the simulation. Returns the new value.</summary>
    public double Step(double dt)
    {
        if (Settled) { Value = Target; Velocity = 0; return Value; }

        // Clamp dt so a stalled message pump cannot explode the integrator.
        if (dt > 0.05) dt = 0.05;
        if (dt <= 0) return Value;

        double omega = 2.0 * Math.PI / Math.Max(0.05, Response);
        double x = Value - Target;
        double accel = -omega * omega * x - 2.0 * Damping * omega * Velocity;

        Velocity += accel * dt;
        Value += Velocity * dt;

        if (Settled) { Value = Target; Velocity = 0; }
        return Value;
    }
}
