// =========================================================================================
// Tool Name:    Price Action Channel
// Class:        PriceActionChannel
// Category:     Drawing Tool
// Version:      1.5.0
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/price-action-channel
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  3-point PATS trend channel: base line, parallel line and 50% midline, with square or
//               vertical ends, optional extensions and on-chart handles for length, width and rotation.
//
// Changelog:
//   v1.5.0 (2026-11-19) - New "Length Multiplier" setting (default 1.25): after click 3 the channel is lengthened
//                         by that factor, extending its right-hand end. 1.0 keeps it as drawn. Hand-drawn only.
//                         - Click 3 can also lengthen the channel: if it is further right than click 2, the
//                           channel reaches it first, then the multiplier is applied. Never shortens.
//                         - Drawing preview shows the extra length as a faint ghost, so the preview matches
//                           the finished channel.
//   v1.4.1 (2026-11-17) - Fix: clicking or double-clicking the fill no longer briefly selects the channel or
//                         opens its properties (fill excluded from NinjaTrader's hit-test pass).
//   v1.4.0 (2026-11-17) - Channel fill is click-through: select and move the channel by its lines only.
//                         - Hidden midline (Show 50% Midline off) no longer catches clicks.
//                         - Drawing: after click 1 the base line previews as a ray toward the cursor.
//                         - Drawing: after click 2 the full channel (fill, midline) previews with ray guides.
//   v1.3.1 (2026-11-03) - Initial public release. Cleaned and optimized by icPriceAction.
//                         - Direct2D brushes created in OnRenderTargetChanged; fill color updated without reallocation.
//                         - Channel fill geometry cached and rebuilt only when the channel moves on screen.
//                         - Removed per-call allocations in autoscale, culling and selection points.
//                         - Rendering guarded before the first click; fill color brush frozen for thread safety.
//                         - Settings regrouped (1. General / 2. Visuals) with descriptions on every setting.
//   v1.3.0 (date unknown) - New geometry model with Square/Vertical channel ends; line settings moved to
//                           Stroke properties (v1.2.x color/width settings still load).
// =========================================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Input;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.DrawingTools.icPA
{
	public enum PriceActionChannelEndStyle
	{
		/// <summary>Ends are visually square (90 degrees) to the lines at any zoom. The parallel line's
		/// first/last bar can shift slightly as the chart is rescaled.</summary>
		Square,
		/// <summary>Ends are vertical: parallel corners share the base corners' bars, so both lines
		/// always start and end on the same bars.</summary>
		Vertical
	}

	/// <summary>
	/// Custom 3-Point Trend Channel for PATS.
	///
	/// Geometry model:
	///   - Base line runs AnchorBaseStart -> AnchorBaseEnd.
	///   - Parallel line is the line parallel to the base that passes through AnchorChannelOffset.
	///   - Channel ends follow EndStyle: Square (perpendicular in screen space) or Vertical (bar-aligned).
	///     Parallel corners are the base corners offset along that end direction.
	///   AnchorChannelOffset can sit anywhere on the parallel line (Draw.PriceActionChannel works with any
	///   point on it); after a manual edit it is stored at the base start's time so the parallel line
	///   stays put through zooming and autoscaling.
	///
	/// Handles (when selected):
	///   - 4 corner handles (circles): length + width. Movement along the lines changes length,
	///     movement across them moves only that line (width). Angle never changes. Corner follows the cursor.
	///   - 2 rotate handles (blue dots just beyond the midline ends): angle + length, pivoting
	///     around the opposite midline end. Width is locked.
	///   - 2 width handles (squares on the line midpoints): pure width, moves only that line.
	///   - Any line (base, parallel, or the midline when shown): moves the whole channel.
	///   The fill is click-through, so large channels don't block other drawings or order lines.
	///
	/// Building (3 clicks):
	///   - Click 1: base start. The base line previews as a ray through the cursor to the chart edge
	///     (solid up to the cursor, dashed beyond it).
	///   - Click 2: base end (angle + length). The full channel previews with dashed ray guides.
	///   - Click 3: channel width.
	///   After click 3 the right-hand end first reaches the third click if that is further right than click 2,
	///   then the length is multiplied by LengthMultiplier. It never shortens. While drawing, that extra length
	///   previews as a faint ghost.
	///   Channels created through Draw.PriceActionChannel are never lengthened.
	/// </summary>
	public class PriceActionChannel : DrawingTool
	{
		private enum BuildStep
		{
			NotStarted,
			SettingBaseEnd,         // Click 1 placed: drawing base line
			SettingChannelOffset    // Click 2 placed: base line locked, parallel line tracking cursor
		}

		private enum DragMode
		{
			None,
			BaseStart,
			BaseEnd,
			ParallelStart,
			ParallelEnd,
			RotateFromStart,
			RotateFromEnd,
			WidthFromParallelMid,
			WidthFromBaseMid,
			MovingChannel
		}

		private struct ChannelGeometry
		{
			public Vector2 BaseStart, BaseEnd;
			public Vector2 ParallelStart, ParallelEnd;
			public Vector2 MidStart, MidEnd;
			public Vector2 BaseMid, ParallelMid;
			public Vector2 RotateStart, RotateEnd;
			public Vector2 Unit;     // unit vector along the base line
			public Vector2 Off;      // unit direction from base corners to parallel corners (depends on EndStyle)
			public float Width;      // signed distance base -> parallel, measured along Off (pixels)
			public float Slope;      // pixels of Y per pixel of X along the base line
		}

		private const int AnchorRadius = 4;
		private const float HitRadius = 8f;
		private const float RotateHandleGap = 14f;   // rotate handles sit this far beyond the midline ends
		private const float MinSpanPx = 3f;           // prevents collapsing the channel to zero length

		private ChartAnchor anchorBaseStart;
		private ChartAnchor anchorBaseEnd;
		private ChartAnchor anchorChannelOffset;

		private System.Windows.Media.Brush fillColor;

		// Drag snapshot (pixel space, captured on mouse down)
		private Vector2 dsMouse;
		private Vector2 dsBaseStart;
		private Vector2 dsBaseEnd;
		private Vector2 dsParallelStart;
		private Vector2 dsRotateStart;
		private Vector2 dsRotateEnd;
		private Vector2 dsUnit;
		private Vector2 dsOff;
		private float dsWidth;

		private DragMode currentDragMode = DragMode.None;
		private BuildStep currentBuildStep = BuildStep.NotStarted;

		// Direct2D device resources: created in OnRenderTargetChanged, disposed there and in State.Terminated
		private SharpDX.Direct2D1.RenderTarget cachedRenderTarget;
		private SharpDX.Direct2D1.SolidColorBrush dxFillBrush;
		private SharpDX.Direct2D1.SolidColorBrush dxHandleFill;
		private SharpDX.Direct2D1.SolidColorBrush dxHandleBorder;
		private SharpDX.Direct2D1.SolidColorBrush dxWidthHandleFill;
		private SharpDX.Direct2D1.SolidColorBrush dxAccent;
		private SharpDX.Direct2D1.StrokeStyle dxGuideStyle;   // dashed style for the ray guides while drawing
		private SharpDX.Direct2D1.SolidColorBrush dxGhostBrush; // faint line color for the extra-length preview
		private Color4 cachedFillColor;
		private Color4 cachedGhostColor;
		private const float GhostAlpha = 0.35f;

		// Cached fill geometry (factory resource): rebuilt only when the channel's screen corners change
		private SharpDX.Direct2D1.PathGeometry fillGeometry;
		private Vector2 fillGeoA, fillGeoB, fillGeoC, fillGeoD;

		// Autoscale range is frozen while dragging so the chart doesn't rescale under the cursor
		private double lastMinValue = double.MaxValue;
		private double lastMaxValue = double.MinValue;

		public override object Icon { get { return NinjaTrader.Gui.Tools.Icons.DrawTrendChannel; } }

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "Price Action Channel";
				Description = "Draws a 3-point PATS trend channel: clicks 1 and 2 set the base line, click 3 sets the parallel line. "
					+ "Includes a 50% midline, square or vertical channel ends, optional left/right extensions and right-margin drawing. "
					+ "When selected, drag the corners for length/width, the blue dots to rotate, the squares for width, or any line to move the channel. "
					+ "The fill is click-through, so other drawings and order lines inside the channel stay clickable. "
					+ "Click 3 can push the end further right, then the length is multiplied by Length Multiplier (default 1.25x, 1 = off), shown as a faint ghost while drawing.\r\n"
					+ "Developed by Istvan Cebrian | https://icpriceaction.com\r"
					+ "Support this free tool: https://buymeacoffee.com/icpriceaction";

				anchorBaseStart = new ChartAnchor { IsEditing = true, DrawingTool = this, DisplayName = "Base Start" };
				anchorBaseEnd = new ChartAnchor { IsEditing = false, DrawingTool = this, DisplayName = "Base End" };
				anchorChannelOffset = new ChartAnchor { IsEditing = false, DrawingTool = this, DisplayName = "Channel Offset" };

				// PATS Defaults
				LineStroke = new Stroke(MakeBrush(0xFF, 0x8E, 0x8E, 0x8E), DashStyleHelper.Solid, 1.5f);
				MidlineStroke = new Stroke(MakeBrush(0xFF, 0x8E, 0x8E, 0x8E), DashStyleHelper.Dash, 1f);
				FillColor = MakeBrush(0xFF, 0xC6, 0xC6, 0xC6);
				FillOpacity = 10;
				ShowMidline = true;
				ExtendLeft = false;
				ExtendRight = false;
				EndStyle = PriceActionChannelEndStyle.Square;
				LengthMultiplier = 1.25;
			}
			else if (State == State.Terminated)
			{
				DisposeDeviceResources();
				DisposeFillGeometry();
				Dispose();
			}
		}

		private static System.Windows.Media.SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
		{
			var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
			brush.Freeze();
			return brush;
		}

		#region Anchors
		[Display(Order = 1)]
		public ChartAnchor AnchorBaseStart { get { return anchorBaseStart; } set { anchorBaseStart = value; } }

		[Display(Order = 2)]
		public ChartAnchor AnchorBaseEnd { get { return anchorBaseEnd; } set { anchorBaseEnd = value; } }

		[Display(Order = 3)]
		public ChartAnchor AnchorChannelOffset { get { return anchorChannelOffset; } set { anchorChannelOffset = value; } }

		public override IEnumerable<ChartAnchor> Anchors
		{
			get { return new[] { AnchorBaseStart, AnchorBaseEnd, AnchorChannelOffset }; }
		}

		public override bool SupportsAlerts { get { return false; } }
		#endregion

		#region Properties
		// ---- 1. General ----
		[Display(Name = "Channel Ends", Description = "Square: ends at 90 degrees on screen. Vertical: both lines start and end on the same bars.", GroupName = "1. General", Order = 1)]
		public PriceActionChannelEndStyle EndStyle { get; set; }

		[Display(Name = "Extend Lines Left", Description = "Extends the base line, parallel line and midline to the left edge of the chart.", GroupName = "1. General", Order = 2)]
		public bool ExtendLeft { get; set; }

		[Display(Name = "Extend Lines Right", Description = "Extends the base line, parallel line and midline to the right edge of the chart.", GroupName = "1. General", Order = 3)]
		public bool ExtendRight { get; set; }

		[Range(1.0, 10.0)]
		[Display(Name = "Length Multiplier", Description = "After click 3 the channel's length is multiplied by this, extending its right-hand end. 1 keeps it as drawn, 1.25 adds a quarter, 2 doubles it. If click 3 is further right than click 2, the channel first reaches click 3 and is then multiplied. Never shortens; channels drawn by indicators are never changed.", GroupName = "1. General", Order = 4)]
		public double LengthMultiplier { get; set; }

		// ---- 2. Visuals ----
		[Display(Name = "Lines", Description = "Color, dash style and width of the base and parallel lines.", GroupName = "2. Visuals", Order = 5)]
		public Stroke LineStroke { get; set; }

		[XmlIgnore]
		[Display(Name = "Channel Fill Color", Description = "Color used to shade the area between the base and parallel lines.", GroupName = "2. Visuals", Order = 6)]
		public System.Windows.Media.Brush FillColor
		{
			get { return fillColor; }
			set
			{
				fillColor = value;
				// Rendering reads this brush, so keep it frozen (thread-safe)
				if (fillColor != null && !fillColor.IsFrozen && fillColor.CanFreeze)
				{
					fillColor = fillColor.Clone();
					fillColor.Freeze();
				}
			}
		}

		[Browsable(false)]
		public string FillColorSerializable
		{
			get { return Serialize.BrushToString(FillColor); }
			set { FillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name = "Fill Opacity (%)", Description = "Opacity of the channel fill. 0 turns the fill off.", GroupName = "2. Visuals", Order = 7)]
		public int FillOpacity { get; set; }

		[Display(Name = "Show 50% Midline", Description = "Draws a line halfway between the base and parallel lines.", GroupName = "2. Visuals", Order = 8)]
		public bool ShowMidline { get; set; }

		[Display(Name = "Midline", Description = "Color, dash style and width of the 50% midline.", GroupName = "2. Visuals", Order = 9)]
		public Stroke MidlineStroke { get; set; }

		// ---- Legacy v1.2.x settings: read from old workspaces/templates, never written back ----
		[Browsable(false)]
		[XmlElement("LineColorSerializable")]
		public string LegacyLineColor
		{
			get { return null; }
			set { if (!string.IsNullOrEmpty(value) && LineStroke != null) LineStroke.Brush = Serialize.StringToBrush(value); }
		}

		[Browsable(false)]
		[XmlElement("LineWidth")]
		public string LegacyLineWidth
		{
			get { return null; }
			set
			{
				float w;
				if (LineStroke != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out w))
					LineStroke.Width = w;
			}
		}

		[Browsable(false)]
		[XmlElement("MidlineColorSerializable")]
		public string LegacyMidlineColor
		{
			get { return null; }
			set { if (!string.IsNullOrEmpty(value) && MidlineStroke != null) MidlineStroke.Brush = Serialize.StringToBrush(value); }
		}
		#endregion

		#region Geometry
		private static float LineY(Vector2 pointOnLine, float slope, float x)
		{
			return pointOnLine.Y + (slope * (x - pointOnLine.X));
		}

		private static float Cross(Vector2 a, Vector2 b)
		{
			return (a.X * b.Y) - (a.Y * b.X);
		}

		// Direction the channel ends run in: screen-perpendicular (Square) or straight down (Vertical).
		// Falls back to perpendicular for a near-vertical base line, where vertical ends are undefined.
		private Vector2 OffsetDirection(Vector2 unit)
		{
			if (EndStyle == PriceActionChannelEndStyle.Vertical && Math.Abs(unit.X) > 0.05f)
				return new Vector2(0f, 1f);
			return new Vector2(-unit.Y, unit.X);
		}

		// Signed distance, measured along 'off', from the line (through linePoint, along unit) to p
		private static float AcrossDistance(Vector2 p, Vector2 linePoint, Vector2 unit, Vector2 off)
		{
			float denom = Cross(unit, off);
			if (Math.Abs(denom) < 0.0001f)
				return 0f;
			return Cross(unit, p - linePoint) / denom;
		}

		private ChannelGeometry CalculateGeometry(System.Windows.Point p1, System.Windows.Point p2, System.Windows.Point p3)
		{
			var g = new ChannelGeometry();
			g.BaseStart = new Vector2((float)p1.X, (float)p1.Y);
			g.BaseEnd = new Vector2((float)p2.X, (float)p2.Y);

			Vector2 d = g.BaseEnd - g.BaseStart;
			float len = d.Length();
			g.Unit = len > 0.001f ? d * (1f / len) : new Vector2(1f, 0f);
			g.Off = OffsetDirection(g.Unit);
			g.Slope = Math.Abs(d.X) > 0.5f ? d.Y / d.X : 0f;

			Vector2 offsetPoint = new Vector2((float)p3.X, (float)p3.Y);
			g.Width = AcrossDistance(offsetPoint, g.BaseStart, g.Unit, g.Off);

			Vector2 off = g.Off * g.Width;
			Vector2 half = off * 0.5f;

			g.ParallelStart = g.BaseStart + off;
			g.ParallelEnd = g.BaseEnd + off;
			g.MidStart = g.BaseStart + half;
			g.MidEnd = g.BaseEnd + half;
			g.BaseMid = (g.BaseStart + g.BaseEnd) * 0.5f;
			g.ParallelMid = (g.ParallelStart + g.ParallelEnd) * 0.5f;

			g.RotateStart = g.MidStart - (g.Unit * RotateHandleGap);
			g.RotateEnd = g.MidEnd + (g.Unit * RotateHandleGap);
			return g;
		}

		private ChannelGeometry GetGeometry(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
		{
			return CalculateGeometry(
				AnchorBaseStart.GetPoint(chartControl, chartPanel, chartScale),
				AnchorBaseEnd.GetPoint(chartControl, chartPanel, chartScale),
				AnchorChannelOffset.GetPoint(chartControl, chartPanel, chartScale));
		}

		// Returns a copy with the lines pushed to the panel edges (render, hit test and culling only)
		private ChannelGeometry ApplyExtensions(ChannelGeometry g, ChartPanel chartPanel)
		{
			if ((!ExtendLeft && !ExtendRight) || Math.Abs(g.BaseEnd.X - g.BaseStart.X) < 0.5f)
				return g;

			float xLeft = chartPanel.X;
			float xRight = chartPanel.X + chartPanel.W;

			ExtendLine(ref g.BaseStart, ref g.BaseEnd, g.Slope, xLeft, xRight);
			ExtendLine(ref g.ParallelStart, ref g.ParallelEnd, g.Slope, xLeft, xRight);
			ExtendLine(ref g.MidStart, ref g.MidEnd, g.Slope, xLeft, xRight);
			return g;
		}

		private void ExtendLine(ref Vector2 a, ref Vector2 b, float slope, float xLeft, float xRight)
		{
			bool aIsRight = a.X >= b.X;
			Vector2 right = aIsRight ? a : b;
			Vector2 left = aIsRight ? b : a;

			if (ExtendRight && xRight > right.X)
				right = new Vector2(xRight, LineY(left, slope, xRight));
			if (ExtendLeft && xLeft < left.X)
				left = new Vector2(xLeft, LineY(right, slope, xLeft));

			if (aIsRight) { a = right; b = left; }
			else { a = left; b = right; }
		}

		// Writes a new channel back to the anchors. The channel offset anchor is stored on the parallel
		// line at the base start's time, which keeps the parallel line stable through zoom/autoscale.
		private void ApplyAnchors(Vector2 baseStart, Vector2 baseEnd, Vector2 pointOnParallel, ChartControl chartControl, ChartScale chartScale)
		{
			System.Windows.Point offsetPt;
			float dx = baseEnd.X - baseStart.X;
			if (Math.Abs(dx) > 0.5f)
			{
				float slope = (baseEnd.Y - baseStart.Y) / dx;
				float verticalOffset = pointOnParallel.Y - LineY(baseStart, slope, pointOnParallel.X);
				offsetPt = new System.Windows.Point(baseStart.X, baseStart.Y + verticalOffset);
			}
			else
			{
				offsetPt = new System.Windows.Point(pointOnParallel.X, pointOnParallel.Y);
			}

			AnchorBaseStart.UpdateFromPoint(new System.Windows.Point(baseStart.X, baseStart.Y), chartControl, chartScale);
			AnchorBaseEnd.UpdateFromPoint(new System.Windows.Point(baseEnd.X, baseEnd.Y), chartControl, chartScale);
			AnchorChannelOffset.UpdateFromPoint(offsetPt, chartControl, chartScale);
		}

		private static bool TryRotateTarget(Vector2 pivot, Vector2 handle, out Vector2 newMidEnd)
		{
			newMidEnd = pivot;
			Vector2 v = handle - pivot;
			float len = v.Length();
			if (len <= RotateHandleGap + MinSpanPx)
				return false;

			newMidEnd = pivot + (v * ((len - RotateHandleGap) / len));
			return Math.Abs(newMidEnd.X - pivot.X) >= MinSpanPx;
		}

		// The channel with its right-hand end lengthened along the lines (angle and width unchanged). First the
		// parallel line's right end reaches reachX (the third click's X, which lies on the parallel line; pass
		// float.MinValue when there is none) if that is further right, then, when applyMultiplier is set, the length
		// is multiplied by LengthMultiplier. Never shortens. Bar spacing is linear in X, so multiplying the X span
		// multiplies the length in bars too. Returns false, leaving ext = g, when nothing changes.
		private bool TryExtendLength(ChannelGeometry g, float reachX, bool applyMultiplier, out ChannelGeometry ext)
		{
			ext = g;
			if (Math.Abs(g.BaseEnd.X - g.BaseStart.X) < MinSpanPx)
				return false;

			bool endIsRight = g.BaseEnd.X >= g.BaseStart.X;
			float leftX = endIsRight ? g.BaseStart.X : g.BaseEnd.X;
			float rightX = endIsRight ? g.BaseEnd.X : g.BaseStart.X;

			// 1. Click 3 can push the end further right. The cursor is on the parallel line, and with Square ends
			//    the parallel corner sits sideways from the base corner (Off.X * Width), so convert the cursor's X
			//    into the base corner's X. Vertical ends have Off.X = 0, so nothing changes there.
			float reachBaseX = reachX - (g.Off.X * g.Width);
			float reachedX = reachBaseX > rightX ? reachBaseX : rightX;

			// 2. The multiplier applies to that length
			float targetX = reachedX;
			if (applyMultiplier && LengthMultiplier > 1.0001)
				targetX = leftX + ((reachedX - leftX) * (float)LengthMultiplier);

			if (targetX <= rightX + 0.5f)
				return false;

			Vector2 newRight = new Vector2(targetX, LineY(g.BaseStart, g.Slope, targetX));
			Vector2 newStart = endIsRight ? g.BaseStart : newRight;
			Vector2 newEnd = endIsRight ? newRight : g.BaseEnd;

			// Any point on the parallel line keeps the same width
			ext = CalculateGeometry(ToPoint(newStart), ToPoint(newEnd), ToPoint(g.ParallelStart));
			return true;
		}

		// Price distance from the base line to the parallel line, interpolating the base line in time
		private double ParallelPriceOffset()
		{
			long t1 = AnchorBaseStart.Time.Ticks;
			long t2 = AnchorBaseEnd.Time.Ticks;
			long t3 = AnchorChannelOffset.Time.Ticks;

			if (t1 == t2)
				return AnchorChannelOffset.Price - AnchorBaseStart.Price;

			double frac = (double)(t3 - t1) / (t2 - t1);
			double basePriceAtOffset = AnchorBaseStart.Price + (frac * (AnchorBaseEnd.Price - AnchorBaseStart.Price));
			return AnchorChannelOffset.Price - basePriceAtOffset;
		}
		#endregion

		#region Autoscale & Culling
		public override void OnCalculateMinMax()
		{
			if (DrawingState == DrawingState.Editing || DrawingState == DrawingState.Moving)
			{
				MinValue = lastMinValue;
				MaxValue = lastMaxValue;
				return;
			}

			MinValue = double.MaxValue;
			MaxValue = double.MinValue;

			if (!IsVisible || DrawingState == DrawingState.Building)
				return;

			// Base corners plus the same corners shifted by the parallel offset (no allocations)
			double off = ParallelPriceOffset();
			double startPrice = AnchorBaseStart.Price;
			double endPrice = AnchorBaseEnd.Price;

			MinValue = Math.Min(Math.Min(startPrice, endPrice), Math.Min(startPrice + off, endPrice + off));
			MaxValue = Math.Max(Math.Max(startPrice, endPrice), Math.Max(startPrice + off, endPrice + off));

			lastMinValue = MinValue;
			lastMaxValue = MaxValue;
		}

		private static void Include(Vector2 p, ref float minX, ref float maxX, ref float minY, ref float maxY)
		{
			if (p.X < minX) minX = p.X;
			if (p.X > maxX) maxX = p.X;
			if (p.Y < minY) minY = p.Y;
			if (p.Y > maxY) maxY = p.Y;
		}

		public override bool IsVisibleOnChart(ChartControl chartControl, ChartScale chartScale, DateTime firstTimeOnChart, DateTime lastTimeOnChart)
		{
			if (DrawingState != DrawingState.Normal)
				return true;

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			ChannelGeometry e = ApplyExtensions(GetGeometry(chartControl, chartPanel, chartScale), chartPanel);

			float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
			Include(e.BaseStart, ref minX, ref maxX, ref minY, ref maxY);
			Include(e.BaseEnd, ref minX, ref maxX, ref minY, ref maxY);
			Include(e.ParallelStart, ref minX, ref maxX, ref minY, ref maxY);
			Include(e.ParallelEnd, ref minX, ref maxX, ref minY, ref maxY);
			Include(e.RotateStart, ref minX, ref maxX, ref minY, ref maxY);
			Include(e.RotateEnd, ref minX, ref maxX, ref minY, ref maxY);

			float pad = HitRadius + AnchorRadius;
			return maxX >= chartPanel.X - pad && minX <= chartPanel.X + chartPanel.W + pad
				&& maxY >= chartPanel.Y - pad && minY <= chartPanel.Y + chartPanel.H + pad;
		}
		#endregion

		#region Hit Testing
		private static bool Near(Vector2 a, Vector2 b)
		{
			return (a - b).Length() <= HitRadius;
		}

		private DragMode HitTest(ChannelGeometry g, Vector2 pt, ChartPanel chartPanel)
		{
			// 1. Corners: length + width
			if (Near(pt, g.BaseStart)) return DragMode.BaseStart;
			if (Near(pt, g.BaseEnd)) return DragMode.BaseEnd;
			if (Near(pt, g.ParallelStart)) return DragMode.ParallelStart;
			if (Near(pt, g.ParallelEnd)) return DragMode.ParallelEnd;

			// 2. Rotate handles (only exist while selected, since they sit outside the channel)
			if (IsSelected)
			{
				if (Near(pt, g.RotateStart)) return DragMode.RotateFromStart;
				if (Near(pt, g.RotateEnd)) return DragMode.RotateFromEnd;
			}

			// 3. Width handles
			if (Near(pt, g.ParallelMid)) return DragMode.WidthFromParallelMid;
			if (Near(pt, g.BaseMid)) return DragMode.WidthFromBaseMid;

			// 4. Body
			if (IsPointOverChannel(g, pt, chartPanel)) return DragMode.MovingChannel;

			return DragMode.None;
		}

		// Lines only: the fill is click-through so a large channel never blocks other drawings or order lines.
		// The midline only counts when it is actually shown.
		private bool IsPointOverChannel(ChannelGeometry g, Vector2 pt, ChartPanel chartPanel)
		{
			ChannelGeometry e = ApplyExtensions(g, chartPanel);

			if (DistanceToLineSegment(pt, e.BaseStart, e.BaseEnd) <= HitRadius ||
			    DistanceToLineSegment(pt, e.ParallelStart, e.ParallelEnd) <= HitRadius)
				return true;

			return ShowMidline && DistanceToLineSegment(pt, e.MidStart, e.MidEnd) <= HitRadius;
		}

		private static float DistanceToLineSegment(Vector2 p, Vector2 a, Vector2 b)
		{
			Vector2 ab = b - a;
			float lenSq = ab.LengthSquared();
			if (lenSq < 0.0001f) return (p - a).Length();

			float t = Math.Max(0f, Math.Min(1f, Vector2.Dot(p - a, ab) / lenSq));
			Vector2 projection = a + (t * ab);
			return (p - projection).Length();
		}

		private static Cursor CursorFor(DragMode mode)
		{
			switch (mode)
			{
				case DragMode.BaseStart:
				case DragMode.BaseEnd:
				case DragMode.ParallelStart:
				case DragMode.ParallelEnd:
					return Cursors.Cross;
				case DragMode.RotateFromStart:
				case DragMode.RotateFromEnd:
					return Cursors.Hand;
				case DragMode.WidthFromBaseMid:
				case DragMode.WidthFromParallelMid:
					return Cursors.SizeNS;
				case DragMode.MovingChannel:
					return Cursors.SizeAll;
				default:
					return null;
			}
		}
		#endregion

		#region Interaction Logic
		private static System.Windows.Point ToPoint(Vector2 v)
		{
			return new System.Windows.Point(v.X, v.Y);
		}

		public override System.Windows.Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
		{
			if (DrawingState == DrawingState.Building)
				return new System.Windows.Point[0];

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			ChannelGeometry g = GetGeometry(chartControl, chartPanel, chartScale);

			if (IsSelected)
			{
				return new[]
				{
					ToPoint(g.BaseStart), ToPoint(g.BaseEnd), ToPoint(g.ParallelStart), ToPoint(g.ParallelEnd),
					ToPoint(g.BaseMid), ToPoint(g.ParallelMid), ToPoint(g.RotateStart), ToPoint(g.RotateEnd)
				};
			}

			return new[]
			{
				ToPoint(g.BaseStart), ToPoint(g.BaseEnd), ToPoint(g.ParallelStart), ToPoint(g.ParallelEnd),
				ToPoint(g.BaseMid), ToPoint(g.ParallelMid)
			};
		}

		public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, System.Windows.Point point)
		{
			if (DrawingState == DrawingState.Building)
				return Cursors.Pen;

			if (currentDragMode != DragMode.None)
				return CursorFor(currentDragMode);

			ChannelGeometry g = GetGeometry(chartControl, chartPanel, chartScale);
			DragMode hover = HitTest(g, new Vector2((float)point.X, (float)point.Y), chartPanel);

			if (hover == DragMode.None)
				return null;

			return IsLocked ? Cursors.Arrow : CursorFor(hover);
		}

		public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);

			switch (DrawingState)
			{
				case DrawingState.Building:
					if (currentBuildStep == BuildStep.NotStarted)
					{
						// Click 1: AnchorBaseStart set
						dataPoint.CopyDataValues(AnchorBaseStart);
						dataPoint.CopyDataValues(AnchorBaseEnd);
						dataPoint.CopyDataValues(AnchorChannelOffset);

						AnchorBaseStart.IsEditing = false;
						AnchorBaseEnd.IsEditing = true;
						currentBuildStep = BuildStep.SettingBaseEnd;
					}
					else if (currentBuildStep == BuildStep.SettingBaseEnd)
					{
						// Ignore a second click on (or right next to) the first: it would make a zero-length channel
						System.Windows.Point startPt = AnchorBaseStart.GetPoint(chartControl, chartPanel, chartScale);
						if ((mousePt - startPt).Length < MinSpanPx)
							break;

						// Click 2: AnchorBaseEnd set
						dataPoint.CopyDataValues(AnchorBaseEnd);
						dataPoint.CopyDataValues(AnchorChannelOffset);

						AnchorBaseEnd.IsEditing = false;
						AnchorChannelOffset.IsEditing = true;
						currentBuildStep = BuildStep.SettingChannelOffset;
					}
					else if (currentBuildStep == BuildStep.SettingChannelOffset)
					{
						// Click 3: AnchorChannelOffset locked
						dataPoint.CopyDataValues(AnchorChannelOffset);

						// Lengthen the right-hand end to the further of: length x Length Multiplier, or this click
						// (hand-drawn channels only; never shortens)
						ChannelGeometry drawn = GetGeometry(chartControl, chartPanel, chartScale);
						ChannelGeometry lengthened;
						if (TryExtendLength(drawn, (float)mousePt.X, true, out lengthened))
						{
							foreach (ChartAnchor anchor in Anchors)
								anchor.IsEditing = true;
							ApplyAnchors(lengthened.BaseStart, lengthened.BaseEnd, lengthened.ParallelStart, chartControl, chartScale);
							foreach (ChartAnchor anchor in Anchors)
								anchor.IsEditing = false;
						}

						AnchorChannelOffset.IsEditing = false;

						currentBuildStep = BuildStep.NotStarted;
						DrawingState = DrawingState.Normal;
						IsSelected = true;
					}
					break;

				case DrawingState.Normal:
					ChannelGeometry g = GetGeometry(chartControl, chartPanel, chartScale);
					Vector2 pt = new Vector2((float)mousePt.X, (float)mousePt.Y);
					DragMode hit = HitTest(g, pt, chartPanel);

					if (hit == DragMode.None)
					{
						IsSelected = false;
						currentDragMode = DragMode.None;
						break;
					}

					IsSelected = true;
					if (IsLocked)
						break;

					dsMouse = pt;
					dsBaseStart = g.BaseStart;
					dsBaseEnd = g.BaseEnd;
					dsParallelStart = g.ParallelStart;
					dsRotateStart = g.RotateStart;
					dsRotateEnd = g.RotateEnd;
					dsUnit = g.Unit;
					dsOff = g.Off;
					dsWidth = g.Width;

					// Every drag rewrites all three anchors
					foreach (ChartAnchor anchor in Anchors)
						anchor.IsEditing = true;

					currentDragMode = hit;
					DrawingState = hit == DragMode.MovingChannel ? DrawingState.Moving : DrawingState.Editing;
					break;
			}
		}

		public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (IsLocked) return;

			if (DrawingState == DrawingState.Building)
			{
				if (currentBuildStep == BuildStep.SettingBaseEnd)
					dataPoint.CopyDataValues(AnchorBaseEnd);
				else if (currentBuildStep == BuildStep.SettingChannelOffset)
					dataPoint.CopyDataValues(AnchorChannelOffset);
				return;
			}

			if (DrawingState != DrawingState.Editing && DrawingState != DrawingState.Moving)
				return;

			System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
			Vector2 m = new Vector2((float)mousePt.X, (float)mousePt.Y);
			Vector2 delta = m - dsMouse;

			switch (currentDragMode)
			{
				// ---- Corners: length (along) + width (across). Angle locked. Corner follows cursor. ----
				case DragMode.BaseStart:
				{
					// Base line slides across to pass through the cursor; parallel line stays put
					Vector2 nb2 = dsBaseEnd + (dsOff * AcrossDistance(m, dsBaseStart, dsUnit, dsOff));
					if ((nb2 - m).Length() < MinSpanPx) break;
					ApplyAnchors(m, nb2, dsParallelStart, chartControl, chartScale);
					break;
				}
				case DragMode.BaseEnd:
				{
					Vector2 nb1 = dsBaseStart + (dsOff * AcrossDistance(m, dsBaseEnd, dsUnit, dsOff));
					if ((m - nb1).Length() < MinSpanPx) break;
					ApplyAnchors(nb1, m, dsParallelStart, chartControl, chartScale);
					break;
				}
				case DragMode.ParallelStart:
				{
					// Parallel line slides across to the cursor; base line stays put, its start follows along
					Vector2 nb1 = m - (dsOff * AcrossDistance(m, dsBaseStart, dsUnit, dsOff));
					if ((dsBaseEnd - nb1).Length() < MinSpanPx) break;
					ApplyAnchors(nb1, dsBaseEnd, m, chartControl, chartScale);
					break;
				}
				case DragMode.ParallelEnd:
				{
					Vector2 nb2 = m - (dsOff * AcrossDistance(m, dsBaseStart, dsUnit, dsOff));
					if ((nb2 - dsBaseStart).Length() < MinSpanPx) break;
					ApplyAnchors(dsBaseStart, nb2, m, chartControl, chartScale);
					break;
				}

				// ---- Rotate handles: angle + length around the opposite midline end. Width locked. ----
				case DragMode.RotateFromEnd:
				{
					Vector2 pivot = dsBaseStart + (dsOff * (dsWidth * 0.5f));
					Vector2 newMidEnd;
					if (!TryRotateTarget(pivot, dsRotateEnd + delta, out newMidEnd)) break;

					Vector2 dir = newMidEnd - pivot;
					dir = dir * (1f / dir.Length());
					Vector2 halfOff = OffsetDirection(dir) * (dsWidth * 0.5f);

					ApplyAnchors(pivot - halfOff, newMidEnd - halfOff, pivot + halfOff, chartControl, chartScale);
					break;
				}
				case DragMode.RotateFromStart:
				{
					Vector2 pivot = dsBaseEnd + (dsOff * (dsWidth * 0.5f));
					Vector2 newMidStart;
					if (!TryRotateTarget(pivot, dsRotateStart + delta, out newMidStart)) break;

					Vector2 dir = pivot - newMidStart;
					dir = dir * (1f / dir.Length());
					Vector2 halfOff = OffsetDirection(dir) * (dsWidth * 0.5f);

					ApplyAnchors(newMidStart - halfOff, pivot - halfOff, newMidStart + halfOff, chartControl, chartScale);
					break;
				}

				// ---- Width handles: pure width, moves only the grabbed line. ----
				case DragMode.WidthFromParallelMid:
				{
					Vector2 shift = dsOff * AcrossDistance(m, dsMouse, dsUnit, dsOff);
					ApplyAnchors(dsBaseStart, dsBaseEnd, dsParallelStart + shift, chartControl, chartScale);
					break;
				}
				case DragMode.WidthFromBaseMid:
				{
					Vector2 shift = dsOff * AcrossDistance(m, dsMouse, dsUnit, dsOff);
					ApplyAnchors(dsBaseStart + shift, dsBaseEnd + shift, dsParallelStart, chartControl, chartScale);
					break;
				}

				// ---- Body: move everything ----
				case DragMode.MovingChannel:
					ApplyAnchors(dsBaseStart + delta, dsBaseEnd + delta, dsParallelStart + delta, chartControl, chartScale);
					break;
			}
		}

		public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (DrawingState == DrawingState.Editing || DrawingState == DrawingState.Moving)
			{
				foreach (ChartAnchor anchor in Anchors)
					anchor.IsEditing = false;

				currentDragMode = DragMode.None;
				DrawingState = DrawingState.Normal;
			}
		}
		#endregion

		#region Direct2D Resources
		public override void OnRenderTargetChanged()
		{
			DisposeDeviceResources();
			if (RenderTarget != null)
				CreateDeviceResources(RenderTarget);
		}

		private void CreateDeviceResources(SharpDX.Direct2D1.RenderTarget rt)
		{
			cachedRenderTarget = rt;
			dxHandleFill = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(1f, 1f, 1f, 1f));
			dxHandleBorder = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.2f, 0.2f, 0.2f, 1f));
			dxWidthHandleFill = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f, 0.95f, 0.95f, 1f));
			dxAccent = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.1f, 0.5f, 0.9f, 1f));
			dxGuideStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
				new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });

			cachedFillColor = ToSharpDXColor(FillColor, FillOpacity / 100f);
			dxFillBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, cachedFillColor);

			cachedGhostColor = ToSharpDXColor(LineStroke != null ? LineStroke.Brush : null, GhostAlpha);
			dxGhostBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, cachedGhostColor);
		}

		private void DisposeDeviceResources()
		{
			DisposeBrush(ref dxFillBrush);
			DisposeBrush(ref dxHandleFill);
			DisposeBrush(ref dxHandleBorder);
			DisposeBrush(ref dxWidthHandleFill);
			DisposeBrush(ref dxAccent);
			DisposeBrush(ref dxGhostBrush);
			if (dxGuideStyle != null)
			{
				dxGuideStyle.Dispose();
				dxGuideStyle = null;
			}
			cachedRenderTarget = null;
		}

		private void DisposeFillGeometry()
		{
			if (fillGeometry != null)
			{
				fillGeometry.Dispose();
				fillGeometry = null;
			}
		}

		private static void DisposeBrush(ref SharpDX.Direct2D1.SolidColorBrush brush)
		{
			if (brush != null)
			{
				brush.Dispose();
				brush = null;
			}
		}

		// Returns the cached fill geometry, rebuilding it only when the corners moved on screen
		private SharpDX.Direct2D1.PathGeometry GetFillGeometry(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
		{
			if (fillGeometry != null && a == fillGeoA && b == fillGeoB && c == fillGeoC && d == fillGeoD)
				return fillGeometry;

			DisposeFillGeometry();
			fillGeometry = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory);
			using (GeometrySink sink = fillGeometry.Open())
			{
				sink.BeginFigure(a, FigureBegin.Filled);
				sink.AddLine(b);
				sink.AddLine(c);
				sink.AddLine(d);
				sink.EndFigure(FigureEnd.Closed);
				sink.Close();
			}

			fillGeoA = a;
			fillGeoB = b;
			fillGeoC = c;
			fillGeoD = d;
			return fillGeometry;
		}

		// Respects the brush's own alpha and opacity (as set in the color picker), multiplied by 'alpha'
		private static Color4 ToSharpDXColor(System.Windows.Media.Brush wpfBrush, float alpha)
		{
			var scb = wpfBrush as System.Windows.Media.SolidColorBrush;
			if (scb != null)
			{
				float a = alpha * (scb.Color.A / 255f) * (float)scb.Opacity;
				return new Color4(scb.Color.R / 255f, scb.Color.G / 255f, scb.Color.B / 255f, a);
			}
			return new Color4(0.5f, 0.5f, 0.5f, alpha);
		}
		#endregion

		#region Direct2D Rendering
		public override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (AnchorBaseStart == null || AnchorBaseEnd == null || AnchorChannelOffset == null || LineStroke == null) return;

			// Nothing to draw until the first click has placed the anchors
			if (DrawingState == DrawingState.Building && currentBuildStep == BuildStep.NotStarted) return;

			SharpDX.Direct2D1.RenderTarget rt = RenderTarget;
			if (rt == null) return;

			// Safety net only: normally OnRenderTargetChanged has already created these for this render target
			if (!ReferenceEquals(rt, cachedRenderTarget) || dxFillBrush == null)
			{
				DisposeDeviceResources();
				CreateDeviceResources(rt);
			}

			// Fill color/opacity changed in the properties: update the existing brush, no reallocation
			Color4 fill = ToSharpDXColor(FillColor, FillOpacity / 100f);
			if (fill != cachedFillColor)
			{
				dxFillBrush.Color = fill;
				cachedFillColor = fill;
			}

			// Line color changed in the properties: update the ghost brush, no reallocation
			Color4 ghost = ToSharpDXColor(LineStroke.Brush, GhostAlpha);
			if (ghost != cachedGhostColor)
			{
				dxGhostBrush.Color = ghost;
				cachedGhostColor = ghost;
			}

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			ChannelGeometry g = GetGeometry(chartControl, chartPanel, chartScale);

			LineStroke.RenderTarget = rt;

			// STAGE 1 (after click 1): base line to the cursor, faint ghost of the extra length
			// (Length Multiplier), then a dashed ray guide on to the chart edge
			if (DrawingState == DrawingState.Building && currentBuildStep == BuildStep.SettingBaseEnd)
			{
				ChannelGeometry x;
				TryExtendLength(g, float.MinValue, true, out x);

				rt.DrawLine(g.BaseStart, g.BaseEnd, LineStroke.BrushDX, LineStroke.Width, LineStroke.StrokeStyle);
				DrawGhost(rt, g.BaseStart, x.BaseStart);
				DrawGhost(rt, g.BaseEnd, x.BaseEnd);
				DrawRayGuide(rt, x.BaseEnd, g.BaseEnd - g.BaseStart, chartPanel);
				return;
			}

			// STAGE 2 (after click 2): full channel preview tracking the cursor (the third click).
			//   r = reached channel: solid, out to the cursor when it is further right than click 2
			//   x = final channel:   r x Length Multiplier, the extra shown as faint ghost lines
			// The fill covers the final channel, then dashed ray guides continue to the chart edge.
			if (DrawingState == DrawingState.Building && currentBuildStep == BuildStep.SettingChannelOffset)
			{
				float cursorX = (float)AnchorChannelOffset.GetPoint(chartControl, chartPanel, chartScale).X;
				ChannelGeometry r, x;
				TryExtendLength(g, cursorX, false, out r);
				TryExtendLength(g, cursorX, true, out x);

				if (FillOpacity > 0 && !IsInHitTest)
					rt.FillGeometry(GetFillGeometry(x.BaseStart, x.BaseEnd, x.ParallelEnd, x.ParallelStart), dxFillBrush);

				rt.DrawLine(r.BaseStart, r.BaseEnd, LineStroke.BrushDX, LineStroke.Width, LineStroke.StrokeStyle);
				rt.DrawLine(r.ParallelStart, r.ParallelEnd, LineStroke.BrushDX, LineStroke.Width, LineStroke.StrokeStyle);
				DrawGhost(rt, r.BaseStart, x.BaseStart);
				DrawGhost(rt, r.BaseEnd, x.BaseEnd);
				DrawGhost(rt, r.ParallelStart, x.ParallelStart);
				DrawGhost(rt, r.ParallelEnd, x.ParallelEnd);

				if (ShowMidline && MidlineStroke != null)
				{
					MidlineStroke.RenderTarget = rt;
					rt.DrawLine(r.MidStart, r.MidEnd, MidlineStroke.BrushDX, MidlineStroke.Width, MidlineStroke.StrokeStyle);
					DrawGhost(rt, r.MidStart, x.MidStart);
					DrawGhost(rt, r.MidEnd, x.MidEnd);
				}

				Vector2 dir = g.BaseEnd - g.BaseStart;
				DrawRayGuide(rt, x.BaseEnd, dir, chartPanel);
				DrawRayGuide(rt, x.ParallelEnd, dir, chartPanel);
				return;
			}

			// STAGE 3: completed channel (extensions affect drawing only; handles stay on the real anchors)
			ChannelGeometry e = ApplyExtensions(g, chartPanel);

			// 1. Fill (skipped in NT's hit-test pass so clicks and double-clicks on the fill pass through)
			if (FillOpacity > 0 && !IsInHitTest)
				rt.FillGeometry(GetFillGeometry(e.BaseStart, e.BaseEnd, e.ParallelEnd, e.ParallelStart), dxFillBrush);

			// 2. Base & parallel lines (no end connectors)
			rt.DrawLine(e.BaseStart, e.BaseEnd, LineStroke.BrushDX, LineStroke.Width, LineStroke.StrokeStyle);
			rt.DrawLine(e.ParallelStart, e.ParallelEnd, LineStroke.BrushDX, LineStroke.Width, LineStroke.StrokeStyle);

			// 3. 50% midline
			if (ShowMidline && MidlineStroke != null)
			{
				MidlineStroke.RenderTarget = rt;
				rt.DrawLine(e.MidStart, e.MidEnd, MidlineStroke.BrushDX, MidlineStroke.Width, MidlineStroke.StrokeStyle);
			}

			// 4. Handles
			if (IsSelected)
			{
				// Rotate handles: thin stem from the midline end + filled accent dot
				rt.DrawLine(g.MidStart, g.RotateStart, dxAccent, 1f);
				rt.DrawLine(g.MidEnd, g.RotateEnd, dxAccent, 1f);
				DrawCircleHandle(rt, g.RotateStart, dxAccent, dxHandleFill);
				DrawCircleHandle(rt, g.RotateEnd, dxAccent, dxHandleFill);

				// Corner handles: length + width
				DrawCircleHandle(rt, g.BaseStart, dxHandleFill, dxHandleBorder);
				DrawCircleHandle(rt, g.BaseEnd, dxHandleFill, dxHandleBorder);
				DrawCircleHandle(rt, g.ParallelStart, dxHandleFill, dxHandleBorder);
				DrawCircleHandle(rt, g.ParallelEnd, dxHandleFill, dxHandleBorder);

				// Width handles: squares
				DrawSquareHandle(rt, g.BaseMid, dxWidthHandleFill, dxAccent);
				DrawSquareHandle(rt, g.ParallelMid, dxWidthHandleFill, dxAccent);
			}
		}

		// Thin dashed guide from 'from' along 'dir' to the edge of the chart panel (building preview only)
		private void DrawRayGuide(SharpDX.Direct2D1.RenderTarget rt, Vector2 from, Vector2 dir, ChartPanel chartPanel)
		{
			if (dxGuideStyle == null || dir.LengthSquared() < 0.0001f)
				return;

			float xMin = chartPanel.X, xMax = chartPanel.X + chartPanel.W;
			float yMin = chartPanel.Y, yMax = chartPanel.Y + chartPanel.H;

			// Distance (in multiples of dir) until the ray leaves the panel
			float tx = dir.X > 0f ? (xMax - from.X) / dir.X : dir.X < 0f ? (xMin - from.X) / dir.X : float.MaxValue;
			float ty = dir.Y > 0f ? (yMax - from.Y) / dir.Y : dir.Y < 0f ? (yMin - from.Y) / dir.Y : float.MaxValue;
			float t = Math.Min(tx, ty);
			if (t <= 0f || t == float.MaxValue)
				return;

			rt.DrawLine(from, from + (dir * t), LineStroke.BrushDX, 1f, dxGuideStyle);
		}

		// Faint segment in the line color showing length that Length Multiplier will add (building preview only)
		private void DrawGhost(SharpDX.Direct2D1.RenderTarget rt, Vector2 from, Vector2 to)
		{
			if (dxGhostBrush == null || (to - from).LengthSquared() < 1f)
				return;
			rt.DrawLine(from, to, dxGhostBrush, LineStroke.Width, LineStroke.StrokeStyle);
		}

		private void DrawCircleHandle(SharpDX.Direct2D1.RenderTarget rt, Vector2 center,
			SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush border)
		{
			var ellipse = new SharpDX.Direct2D1.Ellipse(center, AnchorRadius, AnchorRadius);
			rt.FillEllipse(ellipse, fill);
			rt.DrawEllipse(ellipse, border, 1.5f);
		}

		private void DrawSquareHandle(SharpDX.Direct2D1.RenderTarget rt, Vector2 center,
			SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush border)
		{
			var rect = new SharpDX.RectangleF(center.X - AnchorRadius, center.Y - AnchorRadius, AnchorRadius * 2, AnchorRadius * 2);
			rt.FillRectangle(rect, fill);
			rt.DrawRectangle(rect, border, 1.5f);
		}
		#endregion
	}
}

namespace NinjaTrader.NinjaScript.DrawingTools
{
	public static partial class Draw
	{
		private static icPA.PriceActionChannel PriceActionChannelCore(NinjaScriptBase owner, string tag, bool isAutoScale,
			int baseStartBarsAgo, DateTime baseStartTime, double baseStartY,
			int baseEndBarsAgo, DateTime baseEndTime, double baseEndY,
			int offsetBarsAgo, DateTime offsetTime, double offsetY,
			bool isGlobal, string templateName)
		{
			if (owner == null)
				throw new ArgumentException("owner");
			if (string.IsNullOrWhiteSpace(tag))
				throw new ArgumentException("tag cannot be null or empty", "tag");
			if (isGlobal && tag[0] != GlobalDrawingToolManager.GlobalDrawingToolTagPrefix)
				tag = GlobalDrawingToolManager.GlobalDrawingToolTagPrefix + tag;

			var channel = DrawingTool.GetByTagOrNew(owner, typeof(icPA.PriceActionChannel), tag, templateName) as icPA.PriceActionChannel;
			if (channel == null)
				return null;

			DrawingTool.SetDrawingToolCommonValues(channel, tag, isAutoScale, owner, isGlobal);

			ChartAnchor baseStart = DrawingTool.CreateChartAnchor(owner, baseStartBarsAgo, baseStartTime, baseStartY);
			ChartAnchor baseEnd = DrawingTool.CreateChartAnchor(owner, baseEndBarsAgo, baseEndTime, baseEndY);
			ChartAnchor offset = DrawingTool.CreateChartAnchor(owner, offsetBarsAgo, offsetTime, offsetY);

			baseStart.CopyDataValues(channel.AnchorBaseStart);
			baseEnd.CopyDataValues(channel.AnchorBaseEnd);
			offset.CopyDataValues(channel.AnchorChannelOffset);

			foreach (ChartAnchor anchor in channel.Anchors)
				anchor.IsEditing = false;

			channel.SetState(State.Active);
			return channel;
		}

		/// <summary>
		/// Draws a Price Action Channel. The base line runs from point 1 to point 2; the parallel line
		/// passes through point 3 (any point on it).
		/// </summary>
		public static icPA.PriceActionChannel PriceActionChannel(NinjaScriptBase owner, string tag, bool isAutoScale,
			int baseStartBarsAgo, double baseStartY, int baseEndBarsAgo, double baseEndY, int offsetBarsAgo, double offsetY)
		{
			return PriceActionChannelCore(owner, tag, isAutoScale,
				baseStartBarsAgo, Core.Globals.MinDate, baseStartY,
				baseEndBarsAgo, Core.Globals.MinDate, baseEndY,
				offsetBarsAgo, Core.Globals.MinDate, offsetY,
				false, null);
		}

		public static icPA.PriceActionChannel PriceActionChannel(NinjaScriptBase owner, string tag, bool isAutoScale,
			DateTime baseStartTime, double baseStartY, DateTime baseEndTime, double baseEndY, DateTime offsetTime, double offsetY)
		{
			return PriceActionChannelCore(owner, tag, isAutoScale,
				int.MinValue, baseStartTime, baseStartY,
				int.MinValue, baseEndTime, baseEndY,
				int.MinValue, offsetTime, offsetY,
				false, null);
		}

		public static icPA.PriceActionChannel PriceActionChannel(NinjaScriptBase owner, string tag, bool isAutoScale,
			int baseStartBarsAgo, double baseStartY, int baseEndBarsAgo, double baseEndY, int offsetBarsAgo, double offsetY,
			bool isGlobal, string templateName)
		{
			return PriceActionChannelCore(owner, tag, isAutoScale,
				baseStartBarsAgo, Core.Globals.MinDate, baseStartY,
				baseEndBarsAgo, Core.Globals.MinDate, baseEndY,
				offsetBarsAgo, Core.Globals.MinDate, offsetY,
				isGlobal, templateName);
		}

		public static icPA.PriceActionChannel PriceActionChannel(NinjaScriptBase owner, string tag, bool isAutoScale,
			DateTime baseStartTime, double baseStartY, DateTime baseEndTime, double baseEndY, DateTime offsetTime, double offsetY,
			bool isGlobal, string templateName)
		{
			return PriceActionChannelCore(owner, tag, isAutoScale,
				int.MinValue, baseStartTime, baseStartY,
				int.MinValue, baseEndTime, baseEndY,
				int.MinValue, offsetTime, offsetY,
				isGlobal, templateName);
		}
	}
}