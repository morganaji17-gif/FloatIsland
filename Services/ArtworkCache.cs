using System;
using System.IO;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FloatIsland.Services;

public sealed class ArtworkCache : IDisposable
{
    private readonly Dictionary<string, WeakReference<BitmapImage>> _cache = new();
    private readonly object _lock = new();
    private const int MaxEntries = 20;

    public async Task<BitmapImage?> LoadAsync(IRandomAccessStreamReference? reference, string? cacheKey)
    {
        if (reference == null) return null;

        if (!string.IsNullOrEmpty(cacheKey))
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(cacheKey, out var existing) &&
                    existing.TryGetTarget(out var hit) &&
                    hit != null)
                    return hit;
                _cache.Remove(cacheKey);
            }
        }

        byte[]? buffer = null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            uint size;
            try { size = stream.Size > 2 * 1024 * 1024 ? 2 * 1024 * 1024 : (uint)stream.Size; }
            catch
            {
                // Some streams report their size as unavailable
                // Fall back to a modest buffer and read what we can
                size = 512 * 1024; // 512 KB cap — covers all album art
            }

            if (size == 0)
            {
                Log.Debug("ArtworkCache: stream has zero size, skipping");
                return null;
            }

            // Cap buffer size to prevent OOM from malformed streams
            if (size > 2 * 1024 * 1024) size = 2 * 1024 * 1024;

            buffer = new byte[size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            reader.ReadBytes(buffer);
        }
        catch (Exception ex)
        {
            Log.Debug($"ArtworkCache stream read: {ex.Message}");
            return null;
        }

        if (buffer == null || buffer.Length == 0) return null;

        BitmapImage? bmp = null;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(buffer);
            bmp.DecodePixelWidth = 150; // keep memory footprint small
            bmp.EndInit();
            bmp.Freeze();
        }
        catch (Exception ex)
        {
            Log.Debug($"ArtworkCache decode: {ex.Message}");
            return null;
        }

        if (!string.IsNullOrEmpty(cacheKey))
        {
            lock (_lock)
            {
                if (_cache.Count >= MaxEntries)
                {
                    // Purge dead entries first
                    var dead = _cache.Where(kv => !kv.Value.TryGetTarget(out _))
                                     .Select(kv => kv.Key).ToList();
                    foreach (var k in dead) _cache.Remove(k);

                    // Still over limit → evict oldest
                    while (_cache.Count >= MaxEntries && _cache.Count > 0)
                        _cache.Remove(_cache.Keys.First());
                }
                _cache[cacheKey] = new WeakReference<BitmapImage>(bmp);
            }
        }

        return bmp;
    }

    public void Dispose() => Clear();
    public void Clear() { lock (_lock) _cache.Clear(); }
}
