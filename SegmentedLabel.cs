using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    // One piece of text within a SegmentedLabel. Non-clickable segments (prefixes
    // like "by " and separators like "; ") are drawn in a dimmed color. Clickable
    // segments underline on hover and raise SegmentClicked when clicked.
    internal class TrackInfoSegment
    {
        public string Text;
        public bool Clickable;
        public bool Dim;
        public bool Bold;
        public MetaDataType Field1;
        public string Value1;
        public MetaDataType Field2;
        public string Value2;
        // For album segments: MusicBee's album unique id, used to pick the exact
        // album (right album artist) in the Thumbnail Browser.
        public string AlbumId;
        // The line may wrap before this segment if it doesn't fit.
        public bool WrapBefore;

        internal Rectangle Bounds;

        public TrackInfoSegment(string text)
        {
            Text = text;
        }

        public static TrackInfoSegment Static(string text, bool dim = true) => new TrackInfoSegment(text) { Dim = dim };

        public static TrackInfoSegment Filter(string text, MetaDataType field1, string value1, MetaDataType? field2 = null, string value2 = null)
        {
            return new TrackInfoSegment(text)
            {
                Clickable = true,
                Field1 = field1,
                Value1 = value1,
                Field2 = field2 ?? field1,
                Value2 = value2 ?? value1,
            };
        }
    }

    // A line of text made of multiple segments, some clickable. Hovering a
    // clickable segment underlines just that segment and shows a hand cursor;
    // clicking it raises SegmentClicked. When it doesn't fit its width, it wraps
    // onto more lines before WrapBefore segments; its owner sizes it via
    // GetPreferredHeight.
    internal class SegmentedLabel : Control
    {
        private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        // Segment bounds exclude GDI's trailing overhang (see LayoutSegments), so
        // drawing must not clip to them.
        private const TextFormatFlags DrawFlags = TextFlags | TextFormatFlags.NoClipping;

        private List<TrackInfoSegment> _segments = new List<TrackInfoSegment>();
        private Font _boldFont;
        private int _overhang = -1;
        private int _boldOverhang = -1;
        private int _lineHeight;
        private bool _hasWrapPoints;
        private int _layoutWidth = -1;
        private int _measuredWidth = -1;
        private int _measuredHeight;
        private int _hoveredIndex = -1;

        public event Action<TrackInfoSegment> SegmentClicked;

        public Color DimColor { get; set; } = SystemColors.GrayText;

        public SegmentedLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Plain Control doesn't support Color.Transparent; Information.ApplyTheme()
            // assigns the real skin background before this control is ever shown.
            UpdateLineHeight();
        }

        public List<TrackInfoSegment> Segments
        {
            get => _segments;
            set
            {
                _segments = value ?? new List<TrackInfoSegment>();
                _hoveredIndex = -1;
                _hasWrapPoints = _segments.Exists(s => s.WrapBefore);
                _measuredWidth = -1;
                ApplyLayout();
                Invalidate();
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            _boldFont?.Dispose();
            _boldFont = null;
            _overhang = _boldOverhang = -1;
            base.OnFontChanged(e);
            UpdateLineHeight();
            _measuredWidth = -1;
            ApplyLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _boldFont?.Dispose();
                _boldFont = null;
            }
            base.Dispose(disposing);
        }

        private Font BoldFont => _boldFont ?? (_boldFont = new Font(Font, FontStyle.Bold));

        private Font FontFor(TrackInfoSegment seg) => seg.Bold ? BoldFont : Font;

        // Only wrapping makes the layout depend on the width.
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_hasWrapPoints && Width != _layoutWidth)
                ApplyLayout();
        }

        // Height of one line, including the native 2px gap after it.
        private void UpdateLineHeight()
        {
            var max = new Size(short.MaxValue, short.MaxValue);
            _lineHeight = Math.Max(
                TextRenderer.MeasureText("Ag", Font, max, TextFlags).Height,
                TextRenderer.MeasureText("Ag", BoldFont, max, TextFlags).Height) + 2;
        }

        // The height needed to show every segment at the given width.
        public int GetPreferredHeight(int width)
        {
            if (!_hasWrapPoints) return _lineHeight;

            if (width != _measuredWidth)
            {
                _measuredWidth = width;
                _measuredHeight = LayoutSegments(width, apply: false) * _lineHeight;
            }
            return _measuredHeight;
        }

        private void ApplyLayout()
        {
            _layoutWidth = Width;
            LayoutSegments(Width, apply: true);
        }

        // Places the segments in lines no wider than `width`, breaking only
        // before WrapBefore segments (a single segment too wide on its own
        // still overflows). Returns the line count; with apply, also stores
        // each segment's bounds.
        private int LayoutSegments(int width, bool apply)
        {
            // GDI's measured width = text advance + a fixed trailing overhang.
            // Measuring segments one by one adds that overhang after every
            // segment (visible as extra space around short separators like "; ").
            // So each segment ends where the whole run of same-font text up to
            // and including it ends, minus that overhang.
            var runText = new System.Text.StringBuilder();
            Font runFont = null;
            int runStart = 0;
            int x = 0;
            int line = 0;
            foreach (var seg in _segments)
            {
                Font font = FontFor(seg);
                if (!ReferenceEquals(font, runFont))
                {
                    runFont = font;
                    runStart = x;
                    runText.Clear();
                }

                runText.Append(seg.Text);
                int end = runStart + RunWidth(runText, seg.Bold);

                if (seg.WrapBefore && x > 0 && end > width)
                {
                    line++;
                    x = runStart = 0;
                    runText.Clear().Append(seg.Text);
                    end = RunWidth(runText, seg.Bold);
                }

                if (apply)
                    seg.Bounds = new Rectangle(x, line * _lineHeight, Math.Max(0, end - x), _lineHeight);
                x = end;
            }
            return line + 1;
        }

        private int RunWidth(System.Text.StringBuilder runText, bool bold)
        {
            return Math.Max(0, MeasureWidth(runText.ToString(), bold ? BoldFont : Font) - TrailingOverhang(bold));
        }

        private static int MeasureWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(short.MaxValue, short.MaxValue), TextFlags).Width;
        }

        private int TrailingOverhang(bool bold)
        {
            if (bold)
                return _boldOverhang >= 0 ? _boldOverhang : (_boldOverhang = MeasureOverhang(BoldFont));
            return _overhang >= 0 ? _overhang : (_overhang = MeasureOverhang(Font));
        }

        // measure("aa") = 2 * advance(a) + overhang, measure("a") = advance(a) + overhang
        private static int MeasureOverhang(Font font)
        {
            return Math.Max(0, 2 * MeasureWidth("a", font) - MeasureWidth("aa", font));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.Clear(BackColor);

            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                Color color = seg.Dim ? DimColor : ForeColor;
                TextRenderer.DrawText(e.Graphics, seg.Text, FontFor(seg), seg.Bounds, color, DrawFlags);

                if (seg.Clickable && i == _hoveredIndex)
                {
                    int y = seg.Bounds.Bottom - 2;
                    using (var pen = new Pen(color))
                        e.Graphics.DrawLine(pen, seg.Bounds.Left, y, seg.Bounds.Right, y);
                }
            }
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].Clickable && _segments[i].Bounds.Contains(p))
                    return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int newIndex = HitTest(e.Location);
            if (newIndex != _hoveredIndex)
            {
                _hoveredIndex = newIndex;
                Cursor = newIndex >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_hoveredIndex != -1)
            {
                _hoveredIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);

            if (e.Button != MouseButtons.Left) return;

            int index = HitTest(e.Location);
            if (index >= 0)
            {
                SegmentClicked?.Invoke(_segments[index]);
            }
        }
    }
}
