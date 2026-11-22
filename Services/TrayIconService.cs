using System.Windows.Forms;

namespace SonoIsland.Services;

/// <summary>
/// Manages the system tray icon and context menu for Sono Island.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("Show Island", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon() ?? CreateDefaultIcon(),
            Text = "Sono Island",
            Visible = true,
            ContextMenuStrip = _menu
        };

        // Double-click on the tray icon → toggle visibility
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the tooltip text shown on hover over the tray icon.
    /// </summary>
    public void SetTooltip(string text)
    {
        if (_disposed) return;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>The application icon shipped next to the executable, if present.</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            return System.IO.File.Exists(path) ? new System.Drawing.Icon(path) : null;
        }
        catch { return null; }
    }

    private static System.Drawing.Icon CreateDefaultIcon()
    {
        // Generate a minimal 16×16 music-note icon programmatically
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.FromArgb(5, 5, 5));

        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(29, 185, 84), 1.5f);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(29, 185, 84));

        // Draw a simple note
        g.FillEllipse(brush, 3, 7, 4, 4); // note head
        g.DrawLine(pen, 5, 7, 5, 2);       // stem
        g.DrawBezier(pen, new System.Drawing.Point(5, 2),
                          new System.Drawing.Point(7, 2),
                          new System.Drawing.Point(8, 5),
                          new System.Drawing.Point(5, 6));

        var hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
