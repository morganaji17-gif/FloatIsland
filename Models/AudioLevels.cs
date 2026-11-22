namespace SonoIsland.Models;

/// <summary>
/// In-memory snapshot of the analysed Spotify audio. No audio is ever stored.
/// Reused every frame — never allocate one of these per frame.
/// </summary>
public sealed class AudioLevels
{
    public const int BarCount = 16;

    public readonly float[] Bars = new float[BarCount];
    public float Bass;      // 0..1 low-frequency energy
    public float Mid;       // 0..1
    public float Treble;    // 0..1
    public float Rms;       // 0..1 overall loudness
    public float Peak;      // 0..1 instantaneous peak
    public float Beat;      // 1 → 0 decay, set on detected peaks
    public bool Active;     // capture running

    public void Reset()
    {
        Array.Clear(Bars, 0, BarCount);
        Bass = Mid = Treble = Rms = Peak = Beat = 0f;
        Active = false;
    }

    public void CopyFrom(AudioLevels o)
    {
        Array.Copy(o.Bars, Bars, BarCount);
        Bass = o.Bass; Mid = o.Mid; Treble = o.Treble;
        Rms = o.Rms; Peak = o.Peak; Beat = o.Beat; Active = o.Active;
    }
}
