using System;
using System.Drawing;
using System.Windows.Forms;

public class LoveControl : Control
{
    private bool _loved;
    private bool _banned;

    // public event Action<bool> LoveChanged;

    public bool Loved
    {
        get => _loved;
        set
        {
            if (_loved == value)
            {
                Invalidate();
                return;
            }

            _loved = value;

            if (_loved)
                _banned = false;

            Invalidate();
        }
    }

    public bool Banned
    {
        get => _banned;
        set
        {
            if (_banned == value)
            {
                Invalidate();
                return;
            }

            _banned = value;

            if (_banned)
                _loved = false;

            Invalidate();
        }
    }

    public LoveControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Cursor = Cursors.Default;
        Size = new Size(18, 18);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        string heart;

        if (_banned)
            heart = "🕱";
        else if (_loved)
            heart = "♥";
        else
            heart = "♡";

        using (var font = new Font("Segoe UI Symbol", 15f))
        using (var brush = new SolidBrush(ForeColor))
        {
            var size = e.Graphics.MeasureString(heart, font);

            e.Graphics.DrawString(
                heart,
                font,
                brush,
                (Width - size.Width) / 2,
                (Height - size.Height) / 2);
        }
    }
}
