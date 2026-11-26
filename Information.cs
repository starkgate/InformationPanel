using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    // Replacement for MusicBee's built-in Track & Artist panel. Rows are free text
    // using <Tag> placeholders (e.g. "by <Artist>"), configurable via the header's
    // "Settings..." menu - modeled after the native panel's own field/artwork
    // options. Multi-value tags (Artist, Genre) are split so every individual
    // value (separated by ';') is its own clickable segment: hovering one
    // underlines just that value, clicking it opens a new tab filtered to it.
    internal class Information : UserControl
    {
        private const int ArtworkMargin = 6;
        private const int MinTextWidth = 60;

        // MusicBee's scrollable container (decompiled, v3.6): vertical scroll
        // value in this public int property, in units of the protected int field
        // (pixels per unit, default 1).
        private const string HostScrollPositionProperty = "#=zqD01yizSbC3X";
        private const string HostScrollUnitField = "#=zZj2UiKUrDgRB";

        private static readonly Regex TagRegex = new Regex("<([^<>]+)>");

        // File properties (not tags - MusicBee's API exposes them separately via
        // NowPlaying_GetFileProperty), usable as <Tag> placeholders in rows too,
        // named after MusicBee's own column names. "Type" is what the native
        // panel's track properties line calls the Format column (e.g. "MP3").
        private static readonly Dictionary<string, FilePropertyType> PropertyTags =
            new Dictionary<string, FilePropertyType>(StringComparer.OrdinalIgnoreCase)
            {
                { "Type", FilePropertyType.Format },
                { "Format", FilePropertyType.Format },
                { "Kind", FilePropertyType.Kind },
                { "Sample Rate", FilePropertyType.SampleRate },
                { "Bitrate", FilePropertyType.Bitrate },
                { "Bit Depth", FilePropertyType.Bitdepth },
                { "Channels", FilePropertyType.Channels },
                { "Duration", FilePropertyType.Duration },
                { "Time", FilePropertyType.Duration },
                { "Size", FilePropertyType.Size },
                { "Date Added", FilePropertyType.DateAdded },
                { "Date Modified", FilePropertyType.DateModified },
                { "Last Played", FilePropertyType.LastPlayed },
                { "Play Count", FilePropertyType.PlayCount },
                { "Skip Count", FilePropertyType.SkipCount },
            };

        private readonly PictureBox artworkBox;
        private readonly SegmentedLabel[] rows = new SegmentedLabel[PanelSettings.RowCount];
        private readonly SegmentedLabel propertiesRow;
        private readonly Font propertiesFont;
        private readonly RatingControl ratingControl;
        private readonly LoveControl loveControl;
        private readonly Label emptyLabel;
        private ContextMenuStrip _fallbackArtworkMenu;

        // Coalesces bursts of notifications (e.g. one TagsChanged per file after
        // rating a whole album) into a single refresh.
        private readonly Timer _refreshTimer = new Timer { Interval = 50 };

        // The container MusicBee created for this panel (OnDockablePanelCreated).
        // It is MusicBee's own scrollable panel: given our content height via
        // MB_SetPanelScrollableArea it shows its native (skinned) scrollbar and
        // scrolls its child controls - i.e. this panel - itself.
        private readonly Control host;
        private bool _nativeScrollFailed;
        private bool _hostScrollResolved;
        private PropertyInfo _hostScrollPosition;
        private FieldInfo _hostScrollUnit;
        private bool _inLayout;

        // The shown files: the playing track, or the selected ones. Tags and
        // properties are read once per refresh (several rows may use the same
        // tag, and with several tracks selected each read covers all of them).
        private string[] _files = new string[0];
        private HashSet<string> _fileSet;
        private string[] _notifiedSelectedFiles;
        private readonly Dictionary<MetaDataType, string> _tagCache = new Dictionary<MetaDataType, string>();
        private readonly Dictionary<FilePropertyType, string> _propertyCache = new Dictionary<FilePropertyType, string>();
        private Dictionary<string, MetaDataType> _fieldNameLookup;
        private string _lastArtworkTrack;

        public Information(Control host)
        {
            this.host = host;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            artworkBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
            };
            artworkBox.DoubleClick += (s, e) => ShowFullSizeArtwork();
            artworkBox.MouseUp += ArtworkBox_MouseUp;
            Controls.Add(artworkBox);

            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new SegmentedLabel();
                rows[i].SegmentClicked += OnSegmentClicked;
                Controls.Add(rows[i]);
            }

            // Native panel fonts: rows use MusicBee's default font (title in
            // bold); the properties line uses the same font 1.5pt smaller.
            Font baseFont = null;
            try { baseFont = mbApi.Setting_GetDefaultFont(); } catch { }
            if (baseFont != null)
                Font = baseFont;

            propertiesFont = new Font(Font.FontFamily, Math.Max(1f, Font.SizeInPoints - 1.5f), Font.Style, GraphicsUnit.Point);
            propertiesRow = new SegmentedLabel { Font = propertiesFont };
            Controls.Add(propertiesRow);

            ratingControl = new RatingControl();
            ratingControl.RatingChanged += OnRatingChanged;
            Controls.Add(ratingControl);

            loveControl = new LoveControl();
            // loveControl.LoveChanged += OnLoveChanged;
            Controls.Add(loveControl);

            emptyLabel = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Visible = false,
            };
            Controls.Add(emptyLabel);

            _refreshTimer.Tick += (s, e) =>
            {
                _refreshTimer.Stop();
                RefreshTrackInfo();
            };

            if (host != null)
            {
                host.Resize += Host_Resize;
            }
            else
            {
                Dock = DockStyle.Fill;
                AutoScroll = true;
            }
            Resize += (s, e) => PerformCustomLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (host != null)
                    host.Resize -= Host_Resize;
                _refreshTimer.Dispose();
                _fallbackArtworkMenu?.Dispose();
                artworkBox.Image?.Dispose();
            }

            base.Dispose(disposing);

            if (disposing)
                propertiesFont.Dispose();
        }

        private void Host_Resize(object sender, EventArgs e) => PerformCustomLayout();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            try
            {
                Color background = SkinColor(ElementComponent.ComponentBackground, SystemColors.Control);
                Color foreground = SkinColor(ElementComponent.ComponentForeground, SystemColors.ControlText);

                // Blend foreground towards the background for literal template text
                // (e.g. "by ", " - ") so it stays legible without competing with the
                // clickable tag values.
                Color dim = Color.FromArgb(
                    (foreground.R + background.R) / 2,
                    (foreground.G + background.G) / 2,
                    (foreground.B + background.B) / 2);

                BackColor = background;
                emptyLabel.BackColor = background;
                emptyLabel.ForeColor = dim;

                foreach (var row in rows.Append(propertiesRow))
                {
                    row.BackColor = background;
                    row.ForeColor = foreground;
                    row.DimColor = dim;
                }

                ratingControl.BackColor = background;
                ratingControl.ForeColor = foreground;

                // Native panel: the properties line is plain foreground; it only
                // looks secondary because of its smaller font.
                propertiesRow.DimColor = foreground;
            }
            catch (Exception ex)
            {
                Logger.Error("Error applying Information theme", ex);
            }
        }

        private static Color SkinColor(ElementComponent component, Color fallback)
        {
            try
            {
                int value = mbApi.Setting_GetSkinElementColour(SkinElement.SkinTrackAndArtistPanel, ElementState.ElementStateDefault, component);
                if (value == 0) return fallback;
                return ColorTranslator.FromWin32(value);
            }
            catch
            {
                return fallback;
            }
        }

        public void RefreshTrackInfo()
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)RefreshTrackInfo);
                return;
            }

            try
            {
                ApplyTheme();

                _files = ResolveFiles();
                _fileSet = null;
                _tagCache.Clear();
                _propertyCache.Clear();

                string trackUrl = CurrentFile;
                if (string.IsNullOrEmpty(trackUrl))
                {
                    ShowEmptyState();
                    return;
                }

                emptyLabel.Visible = false;

                string album = GetTag(MetaDataType.Album);
                string albumArtist = GetTag(MetaDataType.AlbumArtist);
                string[] artists = GetArtists();
                string[] genres = IsMultiSelection
                    ? CommonValues(file => SplitList(mbApi.Library_GetFileTag(file, MetaDataType.Genre)))
                    : SplitList(GetTag(MetaDataType.Genre));
                string[] playlists = GetPlaylists();

                for (int i = 0; i < rows.Length; i++)
                {
                    var segments = ParseTemplate(PanelSettings.Rows[i], artists, genres, playlists, album, albumArtist);
                    rows[i].Segments = segments;
                    rows[i].Visible = segments.Any(s => !string.IsNullOrWhiteSpace(s.Text));
                }

                if (PanelSettings.ShowTrackProperties)
                {
                    propertiesRow.Segments = BuildTrackPropertiesSegments();
                    propertiesRow.Visible = propertiesRow.Segments.Count > 0;
                }
                else
                {
                    propertiesRow.Visible = false;
                }

                if (PanelSettings.ShowRating)
                {
                    ratingControl.Visible = true;
                    string ratingRaw = GetTag(MetaDataType.Rating);
                    // returned in the current culture's format too (e.g. "2,5")
                    if (!double.TryParse(ratingRaw, NumberStyles.Float, CultureInfo.CurrentCulture, out double ratingValue))
                        double.TryParse(ratingRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out ratingValue);
                    ratingControl.Rating = Math.Max(0, Math.Min(5, ratingValue));
                    string loveRaw = GetTag(MetaDataType.RatingLove);
                    
                    loveControl.Loved = loveRaw == "L";
                    loveControl.Banned = loveRaw == "B";
                }
                else
                {
                    ratingControl.Visible = false;
                }
                loveControl.Visible = ratingControl.Visible;

                if (PanelSettings.ArtworkPosition != ArtworkPosition.None && SelectionSharesAlbum())
                {
                    artworkBox.Visible = true;
                    RefreshArtwork(trackUrl);
                }
                else
                {
                    artworkBox.Visible = false;
                }

                PerformCustomLayout();
            }
            catch (Exception ex)
            {
                Logger.Error("Error refreshing Information", ex);
            }
        }

        // Like the native panel's header menu option: show the playing track, or
        // the track(s) selected in the main panel. With several tracks selected,
        // like the native panel, only what all of them have in common is shown:
        // a tag appears only if it has the same value on every selected track
        // (so selecting an album hides the title but keeps album/artist/year),
        // multi-value tags (artists, genres) show the values shared by all, and
        // artwork only shows when they all belong to the same album. A rating
        // set from the panel applies to every selected track.
        private bool UseSelectedTrack => PanelSettings.TrackSource == TrackSource.SelectedTrack;
        private bool IsMultiSelection => UseSelectedTrack && _files.Length > 1;
        private string CurrentFile => _files.Length > 0 ? _files[0] : null;

        private string[] ResolveFiles()
        {
            if (!UseSelectedTrack)
            {
                string url = mbApi.NowPlaying_GetFileUrl();
                return string.IsNullOrEmpty(url) ? new string[0] : new[] { url };
            }

            if (_notifiedSelectedFiles != null)
                return _notifiedSelectedFiles;

            try
            {
                if (mbApi.Library_QueryFilesEx("domain=SelectedFiles", out string[] files) && files != null)
                    return files;
            }
            catch (Exception ex)
            {
                Logger.Error("Error querying selected files", ex);
            }

            return new string[0];
        }

        public void OnSelectedFilesChanged(string[] files)
        {
            _notifiedSelectedFiles = files ?? new string[0];
            if (UseSelectedTrack)
                RequestRefresh();
        }

        // Tags/rating changed on a file - any file in the library, so only
        // refresh when it's one of the shown ones. That includes artwork edits.
        public void OnFileChanged(string file)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action<string>)OnFileChanged, file);
                return;
            }

            if (_fileSet == null)
                _fileSet = new HashSet<string>(_files, StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(file) || _fileSet.Contains(file))
                RequestRefresh(reloadArtwork: true);
        }

        public void RequestRefresh(bool reloadArtwork = false)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action<bool>)RequestRefresh, reloadArtwork);
                return;
            }

            if (reloadArtwork)
                _lastArtworkTrack = null;

            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private string GetTag(MetaDataType field)
        {
            if (!_tagCache.TryGetValue(field, out string value))
            {
                value = ReadTag(field);
                _tagCache[field] = value;
            }
            return value;
        }

        private string ReadTag(MetaDataType field)
        {
            if (!UseSelectedTrack) return mbApi.NowPlaying_GetFileTag(field);
            if (_files.Length == 0) return null;
            if (_files.Length == 1) return mbApi.Library_GetFileTag(_files[0], field);
            return CommonValue(file => mbApi.Library_GetFileTag(file, field));
        }

        private string GetProperty(FilePropertyType property)
        {
            if (!_propertyCache.TryGetValue(property, out string value))
            {
                value = ReadProperty(property);
                _propertyCache[property] = value;
            }
            return value;
        }

        private string ReadProperty(FilePropertyType property)
        {
            if (!UseSelectedTrack) return mbApi.NowPlaying_GetFileProperty(property);
            if (_files.Length == 0) return null;
            if (_files.Length == 1) return mbApi.Library_GetFileProperty(_files[0], property);

            // several selected tracks: their total duration rather than a
            // common one (which they'd practically never have)
            if (property == FilePropertyType.Duration) return TotalDuration();

            return CommonValue(file => mbApi.Library_GetFileProperty(file, property));
        }

        // The value if identical on every file, else null. Stops at the first
        // difference, so differing tags stay cheap even for large selections.
        private string CommonValue(Func<string, string> read)
        {
            string first = read(_files[0]) ?? "";
            for (int i = 1; i < _files.Length; i++)
            {
                if (!string.Equals(read(_files[i]) ?? "", first, StringComparison.Ordinal))
                    return null;
            }
            return first;
        }

        // Values present on every file (order of the first file), for
        // multi-value tags like artists and genres.
        private string[] CommonValues(Func<string, string[]> read)
        {
            if (_files.Length == 0) return new string[0];

            var common = read(_files[0]).ToList();
            for (int i = 1; i < _files.Length && common.Count > 0; i++)
            {
                var values = new HashSet<string>(read(_files[i]), StringComparer.OrdinalIgnoreCase);
                common.RemoveAll(v => !values.Contains(v));
            }
            return common.ToArray();
        }

        private string[] GetArtists()
        {
            if (IsMultiSelection)
            {
                return CommonValues(file => SplitArtists(
                    mbApi.Library_GetFileTag(file, MetaDataType.MultiArtist),
                    () => mbApi.Library_GetFileTag(file, MetaDataType.Artist)));
            }

            return SplitArtists(GetTag(MetaDataType.MultiArtist), () => GetTag(MetaDataType.Artist));
        }

        private string[] GetPlaylists()
        {
            var playlists = new List<string>();
        
            try
            {
                if (_files.Length == 0 || !mbApi.Playlist_QueryPlaylists())
                    return playlists.ToArray();
        
                string playlistUrl;
        
                while (!string.IsNullOrEmpty(playlistUrl = mbApi.Playlist_QueryGetNextPlaylist()))
                {
                    if (mbApi.Playlist_GetType(playlistUrl) == PlaylistFormat.Auto)
                        continue;
                    
                    bool containsAllFiles = true;
        
                    foreach (string file in _files)
                    {
                        if (!mbApi.Playlist_IsInList(playlistUrl, file))
                        {
                            containsAllFiles = false;
                            break;
                        }
                    }
        
                    if (!containsAllFiles)
                        continue;
        
                    string name = mbApi.Playlist_GetName(playlistUrl);
        
                    if (!string.IsNullOrEmpty(name))
                    {
                        name = name.TrimEnd('/', '\\');
                    
                        int separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
                    
                        if (separator >= 0)
                            name = name.Substring(separator + 1);
                    
                        if (name.Length > 0)
                            playlists.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error retrieving playlists", ex);
            }
        
            return playlists.ToArray();
        }

        // MultiArtist holds the individual artists separated by '\0'; without
        // it, fall back to the Artist tag split on ';'.
        private static string[] SplitArtists(string multiArtist, Func<string> artist)
        {
            if (!string.IsNullOrEmpty(multiArtist))
            {
                var parts = multiArtist.Split('\0').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (parts.Length > 0) return parts;
            }
            return SplitList(artist());
        }

        private static string[] SplitList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new string[0];
            return raw.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        // Artwork is only meaningful for several tracks if they're one album.
        private bool SelectionSharesAlbum()
        {
            if (!IsMultiSelection) return true;
            return !string.IsNullOrEmpty(GetTag(MetaDataType.Album)) && GetTag(MetaDataType.AlbumArtist) != null;
        }

        private void ShowEmptyState()
        {
            emptyLabel.Text = UseSelectedTrack ? "No track selected" : "Nothing playing";
            artworkBox.Visible = false;
            artworkBox.Image?.Dispose();
            artworkBox.Image = null;

            foreach (var row in rows)
                row.Visible = false;

            propertiesRow.Visible = false;
            ratingControl.Visible = false;
            emptyLabel.Visible = true;
            _lastArtworkTrack = null;
            PerformCustomLayout();
        }

        // Splits a row template into literal-text and <Tag> segments (see
        // TryResolveTag for the recognized names). Unrecognized tags are left
        // as-is so a typo is visible rather than silently dropped.
        private List<TrackInfoSegment> ParseTemplate(string template, string[] artists, string[] genres, string[] playlists, string album, string albumArtist)
        {
            var segments = new List<TrackInfoSegment>();
            if (string.IsNullOrEmpty(template)) return segments;

            int last = 0;
            foreach (Match m in TagRegex.Matches(template))
            {
                if (m.Index > last)
                    segments.Add(TrackInfoSegment.Static(template.Substring(last, m.Index - last)));

                AppendTagSegments(segments, m.Groups[1].Value.Trim(), artists, genres, playlists, album, albumArtist);

                last = m.Index + m.Length;
            }

            if (last < template.Length)
                segments.Add(TrackInfoSegment.Static(template.Substring(last)));

            return segments;
        }

        private void AppendTagSegments(List<TrackInfoSegment> segments, string tagName, string[] artists, string[] genres, string[] playlists, string album, string albumArtist)
        {
            if (string.Equals(tagName, "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                AppendStaticMultiValue(segments, playlists);
                return;
            }

            if (PropertyTags.TryGetValue(tagName, out FilePropertyType property))
            {
                // file properties aren't filterable tags, so they're plain text
                string propertyValue = GetPropertyText(property);
                if (!string.IsNullOrEmpty(propertyValue))
                    segments.Add(TrackInfoSegment.Static(propertyValue, dim: false));
                return;
            }

            if (!TryResolveTag(tagName, out MetaDataType field))
            {
                segments.Add(TrackInfoSegment.Static($"<{tagName}>"));
                return;
            }

            switch (field)
            {
                case MetaDataType.Artist:
                    AppendMultiValue(segments, artists, MetaDataType.MultiArtist);
                    return;

                case MetaDataType.Genre:
                    AppendMultiValue(segments, genres, MetaDataType.Genre, wrap: true);
                    return;

                case MetaDataType.Album:
                    if (string.IsNullOrEmpty(album)) return;
                    string filterAlbumArtist = !string.IsNullOrEmpty(albumArtist) ? albumArtist : artists.FirstOrDefault();
                    var albumSegment = TrackInfoSegment.Filter(album, MetaDataType.Album, album, MetaDataType.AlbumArtist, filterAlbumArtist);
                    albumSegment.AlbumId = GetTag(MetaDataType.AlbumUniqueId);
                    segments.Add(albumSegment);
                    return;
            }

            string value = GetTag(field);
            if (string.IsNullOrEmpty(value)) return;

            // Like the native panel: title and track number are bold, not filter links.
            if (field == MetaDataType.TrackTitle ||
                field == MetaDataType.TrackNo)
            {
                var segment = TrackInfoSegment.Static(value, dim: false);
                segment.Bold = true;
                segments.Add(segment);
                return;
            }

            segments.Add(TrackInfoSegment.Filter(value, field, value));
        }

        // <Tag> names match MusicBee's own display names (e.g. "Title", "Full
        // Year", "Album Artist" - confirmed via a user report that <Title> and
        // <Full Year> weren't recognized even though the API's enum member names
        // are TrackTitle/Year), via Setting_GetFieldName - not the internal enum
        // member names. Falls back to the enum member name too, so <TrackTitle> or
        // <AlbumArtist> (the API names) still work for anyone used to those.
        private bool TryResolveTag(string tagName, out MetaDataType field)
        {
            if (_fieldNameLookup == null)
            {
                _fieldNameLookup = new Dictionary<string, MetaDataType>(StringComparer.OrdinalIgnoreCase);
                foreach (MetaDataType candidate in Enum.GetValues(typeof(MetaDataType)))
                {
                    try
                    {
                        // first field wins if two share a display name
                        string displayName = mbApi.Setting_GetFieldName(candidate);
                        if (!string.IsNullOrEmpty(displayName) && !_fieldNameLookup.ContainsKey(displayName))
                            _fieldNameLookup[displayName] = candidate;
                    }
                    catch
                    {
                        // some MetaDataType values aren't valid field-name lookups; skip them
                    }
                }
            }

            if (_fieldNameLookup.TryGetValue(tagName, out field))
                return true;

            return Enum.TryParse(tagName, true, out field);
        }

        // Durations come back as MusicBee displays them ("4:26", "1:02:03");
        // summed and formatted the same way (m:ss, or h:mm:ss from an hour).
        private string TotalDuration()
        {
            var total = TimeSpan.Zero;
            foreach (string file in _files)
            {
                if (TryParseDuration(mbApi.Library_GetFileProperty(file, FilePropertyType.Duration), out TimeSpan duration))
                    total += duration;
            }

            if (total <= TimeSpan.Zero) return null;

            int hours = (int)total.TotalHours;
            return hours > 0
                ? $"{hours}:{total.Minutes:00}:{total.Seconds:00}"
                : $"{total.Minutes}:{total.Seconds:00}";
        }

        private static bool TryParseDuration(string text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            long seconds = 0;
            foreach (string part in text.Trim().Split(':'))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    return false;
                seconds = seconds * 60 + n;
            }

            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        private string GetPropertyText(FilePropertyType property)
        {
            string value;
            try
            {
                value = GetProperty(property)?.Trim();
            }
            catch
            {
                return null;
            }

            // The Format property comes back empty (seen on FLAC files), so fall
            // back to the file extension - what the native panel shows (FLAC, MP3...).
            if (string.IsNullOrEmpty(value) && property == FilePropertyType.Format)
            {
                try
                {
                    string[] extensions = _files
                        .Select(f => string.IsNullOrEmpty(f) ? "" : Path.GetExtension(f).TrimStart('.').ToUpperInvariant())
                        .Distinct()
                        .Take(2)
                        .ToArray();
                    value = extensions.Length == 1 ? extensions[0] : null;
                }
                catch
                {
                    value = null;
                }
            }

            if (string.IsNullOrEmpty(value)) return value;

            // Numeric channel counts read better the way the native panel shows them.
            if (property == FilePropertyType.Channels && int.TryParse(value, out int channels))
                return channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels} ch";

            return value;
        }

        // Native panel's "show track properties" line: "<Type> <Sample Rate>,
        // <Bitrate>, <Channels>, <Duration>", skipping any value that's missing.
        private List<TrackInfoSegment> BuildTrackPropertiesSegments()
        {
            string type = GetPropertyText(FilePropertyType.Format);
            string sampleRate = GetPropertyText(FilePropertyType.SampleRate);
            string head = string.Join(" ", new[] { type, sampleRate }.Where(s => !string.IsNullOrEmpty(s)));

            var parts = new[]
            {
                head,
                GetPropertyText(FilePropertyType.Bitrate),
                GetPropertyText(FilePropertyType.Channels),
                GetPropertyText(FilePropertyType.Duration),
            }.Where(s => !string.IsNullOrEmpty(s));

            var segments = new List<TrackInfoSegment>();
            string text = string.Join(", ", parts);
            if (text.Length > 0)
                segments.Add(TrackInfoSegment.Static(text));
            return segments;
        }

        // wrap: a value that doesn't fit the line moves to the next one (the
        // "; " separator stays at the end of the previous line).
        private static void AppendMultiValue(List<TrackInfoSegment> segments, string[] values, MetaDataType field, bool wrap = false)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    segments.Add(TrackInfoSegment.Static(" • "));

                var segment = TrackInfoSegment.Filter(values[i], field, values[i]);
                segment.WrapBefore = wrap && i > 0;
                segments.Add(segment);
            }
        }

        private static void AppendStaticMultiValue(List<TrackInfoSegment> segments, string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    segments.Add(TrackInfoSegment.Static(" • "));
        
                segments.Add(TrackInfoSegment.Static(values[i], dim: false));
            }
        }

        private void RefreshArtwork(string trackUrl)
        {
            if (trackUrl == _lastArtworkTrack) return;
            _lastArtworkTrack = trackUrl;

            try
            {
                mbApi.Library_GetArtworkEx(trackUrl, 0, true, out _, out _, out byte[] imageData);

                // GDI+ needs an image's stream to stay open for the image's whole
                // lifetime (Save/Clipboard can otherwise fail with "A generic error
                // occurred in GDI+"). A MemoryStream holds no unmanaged resources,
                // so it's simply left for the GC along with the image.
                var previous = artworkBox.Image;
                artworkBox.Image = imageData != null && imageData.Length > 0
                    ? Image.FromStream(new MemoryStream(imageData))
                    : null;
                previous?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error("Error loading artwork", ex);
            }
        }

        private void OnRatingChanged(double newRating)
        {
            try
            {
                // MusicBee parses the rating with the current culture: an
                // invariant "2.5" was read as 25 under a ',' decimal culture (and
                // stored as 25/100 = 1.25 stars). 0 = no rating: clear the tag,
                // like the native widget does.
                string value = newRating <= 0 ? "" : newRating.ToString(CultureInfo.CurrentCulture);

                // every shown track: the playing one, or all selected ones
                foreach (string file in _files)
                {
                    if (string.IsNullOrEmpty(file)) continue;
                    mbApi.Library_SetFileTag(file, MetaDataType.Rating, value);
                    mbApi.Library_CommitTagsToFile(file);
                }

                // make the main list and other panels show the new rating now
                mbApi.MB_RefreshPanels();
                RequestRefresh();
            }
            catch (Exception ex)
            {
                Logger.Error("Error setting rating", ex);
            }
        }

        /* private void OnLoveChanged(bool loved)
        {
            try
            {
                foreach (string file in _files)
                {
                    mbApi.Library_SetFileTag(
                        file,
                        MetaDataType.RatingLove,
                        loved ? "L" : "");
        
                    mbApi.Library_CommitTagsToFile(file);
                }
        
                mbApi.MB_RefreshPanels();
                RequestRefresh();
            }
            catch (Exception ex)
            {
                Logger.Error("Error updating Loved status", ex);
            }
        } */

        // ViewShowFullSizeArtwork reads the command dispatcher's third parameter -
        // an IList<Track> - and does tracks[0], so MusicBeeCommandInvoker resolves
        // an actual Track object for the shown file and passes it through: this
        // opens MusicBee's own native full-size artwork window.
        private void ShowFullSizeArtwork()
        {
            string trackUrl = CurrentFile;
            if (!string.IsNullOrEmpty(trackUrl))
                MusicBeeCommandInvoker.InvokeWithTrack("ViewShowFullSizeArtwork", trackUrl);
        }

        // Tries to show MusicBee's own artwork context menu (found by shape, not
        // name - see NativeArtworkMenuService). Falls back to a small built-in menu
        // with the same core actions if that isn't found.
        private void ArtworkBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (artworkBox.Image == null) return;

            var screenPos = artworkBox.PointToScreen(e.Location);

            if (NativeArtworkMenuService.TryShow(artworkBox, screenPos))
                return;

            if (_fallbackArtworkMenu == null)
                _fallbackArtworkMenu = BuildFallbackArtworkMenu();
            _fallbackArtworkMenu.Show(screenPos);
        }

        private ContextMenuStrip BuildFallbackArtworkMenu()
        {
            var menu = new ContextMenuStrip();

            menu.Items.Add("Show Full Size", null, (s, e) => ShowFullSizeArtwork());

            menu.Items.Add("Copy", null, (s, e) =>
            {
                try { Clipboard.SetImage(artworkBox.Image); }
                catch (Exception ex) { Logger.Error("Error copying artwork to clipboard", ex); }
            });

            menu.Items.Add("Save As...", null, (s, e) =>
            {
                try
                {
                    using (var dialog = new SaveFileDialog { Filter = "PNG Image|*.png|JPEG Image|*.jpg" })
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            var format = dialog.FilterIndex == 2 ? System.Drawing.Imaging.ImageFormat.Jpeg : System.Drawing.Imaging.ImageFormat.Png;
                            artworkBox.Image.Save(dialog.FileName, format);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Error saving artwork", ex);
                }
            });

            return menu;
        }

        // Plain click: browse to the value in the Thumbnail Browser panel, if one is
        // open. Ctrl+click, or no Thumbnail Browser panel found, opens a filtered tab.
        private async void OnSegmentClicked(TrackInfoSegment segment)
        {
            try
            {
                bool forceNewTab = (ModifierKeys & Keys.Control) != 0;

                if (!forceNewTab && await ThumbnailBrowserService.TryBrowseToAsync(segment))
                    return;

                bool flyoutWasOpen = SidebarFlyoutService.IsOpen();
                if (MusicBeeCommandInvoker.Invoke("FileNewTab"))
                {
                    // FileNewTab pops out the left sidebar when it's hidden; we
                    // don't want it since the tab gets its content right away.
                    if (!flyoutWasOpen)
                        SidebarFlyoutService.Close();

                    await Task.Delay(150);
                }

                mbApi.MB_OpenFilterInTab(segment.Field1, ComparisonType.Is, segment.Value1, segment.Field2, ComparisonType.Is, segment.Value2);
            }
            catch (Exception ex)
            {
                Logger.Error("Error handling segment click", ex);
            }
        }

        // Like the native panel: artwork above/below the text fills the panel
        // width, and when everything doesn't fit the panel scrolls vertically.
        // Showing/hiding the scrollbar changes the client width (and so the
        // artwork size), so re-run until the size is stable (bounded).
        private void PerformCustomLayout()
        {
            if (_inLayout) return;
            _inLayout = true;
            try
            {
                if (host != null && !_nativeScrollFailed && LayoutInNativeScrollHost())
                    return;

                for (int pass = 0; pass < 3; pass++)
                {
                    Size client = ClientSize;
                    var layout = ComputeLayout(client.Width, client.Height);

                    var minSize = new Size(0, layout.ContentHeight > client.Height ? layout.ContentHeight : 0);
                    if (AutoScrollMinSize != minSize)
                        AutoScrollMinSize = minSize;

                    if (ClientSize != client)
                        continue;

                    ApplyLayout(layout, AutoScrollPosition.Y);
                    break;
                }
            }
            finally
            {
                _inLayout = false;
            }
        }

        // Native scrolling: this panel is sized to its full content height inside
        // MusicBee's container, which scrolls it (moves it up) itself - so keep
        // whatever Top MusicBee has scrolled us to. The container's scrollbar is
        // one of its own child controls at the right edge, so the usable width is
        // up to the leftmost other visible child. Showing/hiding that scrollbar
        // changes the width, hence the bounded re-run. Returns false (and switches
        // to a standard scrollbar) if the container isn't one MusicBee can scroll.
        private bool LayoutInNativeScrollHost()
        {
            // When the content height is right at the panel height, showing the
            // scrollbar narrows the panel, which shrinks the artwork so that it
            // fits and the scrollbar hides again - forever. After two tries,
            // settle on the narrower width (scrollbar shown) and apply it.
            int? forcedWidth = null;
            for (int pass = 0; pass < 3; pass++)
            {
                bool lastPass = pass == 2;
                int width = forcedWidth ?? HostContentWidth();
                int viewportHeight = host.ClientSize.Height;
                var layout = ComputeLayout(width, viewportHeight);

                bool ok;
                try
                {
                    ok = mbApi.MB_SetPanelScrollableArea(host, new Size(0, layout.ContentHeight), false);
                }
                catch (Exception ex)
                {
                    Logger.Error("MB_SetPanelScrollableArea threw", ex);
                    ok = false;
                }

                if (!ok)
                {
                    Logger.Info($"[Scroll] MB_SetPanelScrollableArea rejected container {host.GetType().FullName}; falling back to a standard scrollbar.");
                    _nativeScrollFailed = true;
                    Dock = DockStyle.Fill;
                    AutoScroll = true;
                    return false;
                }

                int newWidth = HostContentWidth();
                if (newWidth != width && !lastPass)
                {
                    if (pass == 1)
                        forcedWidth = Math.Min(width, newWidth);
                    continue;
                }

                // Our Top must mirror the container's scroll position: 0 when
                // everything fits, and never beyond the scrollable range.
                int scrollPixels = HostScrollPixels();
                int top = scrollPixels >= 0 ? -scrollPixels : Math.Min(0, Top);
                int minTop = Math.Min(0, viewportHeight - layout.ContentHeight);
                top = Math.Max(minTop, Math.Min(0, top));

                Bounds = new Rectangle(0, top, width, Math.Max(layout.ContentHeight, viewportHeight));
                ApplyLayout(layout, 0);
                break;
            }
            return true;
        }

        // The container's scroll position in pixels, or -1 if not readable.
        private int HostScrollPixels()
        {
            try
            {
                if (!_hostScrollResolved)
                {
                    _hostScrollResolved = true;
                    var type = host.GetType();

                    var position = type.GetProperty(HostScrollPositionProperty, BindingFlags.Public | BindingFlags.Instance);
                    _hostScrollPosition = position?.PropertyType == typeof(int) ? position : null;

                    var unit = ReflectionHelper.FindField(type, HostScrollUnitField, BindingFlags.NonPublic | BindingFlags.Instance);
                    _hostScrollUnit = unit?.FieldType == typeof(int) ? unit : null;
                }

                if (_hostScrollPosition == null) return -1;

                int unitPixels = _hostScrollUnit == null ? 1 : Math.Max(1, (int)_hostScrollUnit.GetValue(host));
                return Math.Max(0, (int)_hostScrollPosition.GetValue(host)) * unitPixels;
            }
            catch
            {
                return -1;
            }
        }

        private int HostContentWidth()
        {
            int width = host.ClientSize.Width;
            foreach (Control child in host.Controls)
            {
                if (child == this || !child.Visible) continue;
                if (child.Left > 0 && child.Left < width)
                    width = child.Left;
            }
            return width;
        }

        private struct LayoutResult
        {
            public Rectangle Artwork;
            public Rectangle Text;
            public int ContentHeight;
        }

        // Rows that wrap get taller the narrower the text column is.
        private int TextBlockHeight(int textWidth)
        {
            // Native panel spacing (MusicBee v3.6): rows are followed by the
            // rating strip (+3 before, +4 after) then the properties line (+3
            // before, +1 after). SegmentedLabel heights already include the
            // native 2px gap after each row, hence +1 before properties here.
            int height = rows.Where(r => r.Visible).Sum(r => r.GetPreferredHeight(textWidth));
            if (ratingControl.Visible)
                height += Dpi(3) + ratingControl.PreferredStripSize.Height + Dpi(4);
            if (propertiesRow.Visible)
                height += Dpi(1) + propertiesRow.GetPreferredHeight(textWidth);
            return height;
        }

        private LayoutResult ComputeLayout(int width, int viewportHeight)
        {
            int availWidth = Math.Max(0, width - ArtworkMargin * 2);
            ArtworkPosition position = artworkBox.Visible ? PanelSettings.ArtworkPosition : ArtworkPosition.None;
            var result = new LayoutResult();

            switch (position)
            {
                case ArtworkPosition.Above:
                {
                    int textBlockHeight = TextBlockHeight(availWidth);
                    result.Artwork = new Rectangle(ArtworkMargin, ArtworkMargin, availWidth, availWidth);
                    result.Text = new Rectangle(ArtworkMargin, result.Artwork.Bottom + ArtworkMargin, availWidth, textBlockHeight);
                    result.ContentHeight = result.Text.Bottom + ArtworkMargin;
                    break;
                }

                case ArtworkPosition.Below:
                {
                    int textBlockHeight = TextBlockHeight(availWidth);
                    result.Text = new Rectangle(ArtworkMargin, ArtworkMargin, availWidth, textBlockHeight);
                    result.Artwork = new Rectangle(ArtworkMargin, result.Text.Bottom + ArtworkMargin, availWidth, availWidth);
                    result.ContentHeight = result.Artwork.Bottom + ArtworkMargin;
                    break;
                }

                case ArtworkPosition.Left:
                case ArtworkPosition.Right:
                {
                    // beside the text: sized to the visible height, capped so the
                    // text keeps a usable width
                    int availHeight = Math.Max(0, viewportHeight - ArtworkMargin * 2);
                    int maxArtWidth = Math.Max(0, width - ArtworkMargin * 3 - MinTextWidth);
                    int size = Math.Max(0, Math.Min(availHeight, maxArtWidth));
                    int textWidth = Math.Max(0, width - size - ArtworkMargin * (size > 0 ? 3 : 2));
                    int textBlockHeight = TextBlockHeight(textWidth);

                    if (position == ArtworkPosition.Left)
                    {
                        result.Artwork = new Rectangle(ArtworkMargin, ArtworkMargin, size, size);
                        int textLeft = result.Artwork.Right + (size > 0 ? ArtworkMargin : 0);
                        result.Text = new Rectangle(textLeft, ArtworkMargin, textWidth, textBlockHeight);
                    }
                    else
                    {
                        result.Artwork = new Rectangle(width - ArtworkMargin - size, ArtworkMargin, size, size);
                        result.Text = new Rectangle(ArtworkMargin, ArtworkMargin, textWidth, textBlockHeight);
                    }

                    result.ContentHeight = Math.Max(result.Artwork.Bottom, result.Text.Bottom) + ArtworkMargin;
                    break;
                }

                default:
                    result.Artwork = Rectangle.Empty;
                    result.Text = new Rectangle(ArtworkMargin, ArtworkMargin, availWidth, TextBlockHeight(availWidth));
                    result.ContentHeight = result.Text.Bottom + ArtworkMargin;
                    break;
            }

            return result;
        }

        private void ApplyLayout(LayoutResult layout, int scrollOffsetY)
        {
            Rectangle textArea = layout.Text;
            textArea.Offset(0, scrollOffsetY);

            if (artworkBox.Visible)
            {
                Rectangle artwork = layout.Artwork;
                artwork.Offset(0, scrollOffsetY);
                artworkBox.Bounds = artwork;
            }

            int y = textArea.Top;
            foreach (var row in rows)
            {
                if (!row.Visible) continue;
                int height = row.GetPreferredHeight(textArea.Width);
                row.Bounds = new Rectangle(textArea.Left, y, textArea.Width, height);
                y += height;
            }

            if (ratingControl.Visible)
            {
                y += Dpi(3);
            
                // Native stars start 2px left of the text column
                ratingControl.Bounds = new Rectangle(
                    new Point(textArea.Left - Dpi(2), y),
                    ratingControl.PreferredStripSize);
            
                y += ratingControl.Height + Dpi(4);
            }
            
            if (loveControl.Visible)
            {
                loveControl.Bounds = new Rectangle(
                    ratingControl.Right + Dpi(4),
                    ratingControl.Top - Dpi(3),
                    Dpi(18),
                    Dpi(18));
            }

            if (propertiesRow.Visible)
            {
                y += Dpi(1);
                propertiesRow.Bounds = new Rectangle(textArea.Left, y, textArea.Width, propertiesRow.GetPreferredHeight(textArea.Width));
            }
        }

        // MusicBee scales its pixel constants only above 100 DPI.
        private int Dpi(int n) => (int)Math.Round(n * (DeviceDpi > 100 ? DeviceDpi / 96f : 1f));
    }
}
