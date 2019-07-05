using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Core.Monads;
using Core.WinForms.Documents;
using SqlContainment;
using static Core.Monads.MonadFunctions;

namespace GUITester
{
	public partial class Form1 : Form
	{
		const string CONFIGURATION_PATH = @"C:\Enterprise\Projects\ApexSQLFormatTester\configuration\guiConfiguration.json";

		Document document;
		IMaybe<NotificationState> notificationState;
		bool viewWhitespace;
		SqlContainerConfiguration configuration;
		bool changesLocked;
		EditorHost editorHost;
		IMaybe<(int, int)> selected;
		bool decorations;
		int firstLineNumber;

		public Form1() => InitializeComponent();

		void Form1_Load(object sender, EventArgs e)
		{
			if (SqlContainerConfiguration.FromFile(CONFIGURATION_PATH).IfNot(out configuration, out var exception))
			{
				Text = exception.Message;
				return;
			}

			decorations = configuration.HostConfiguration.Decorations != RequirementType.Forbidden;

			notificationState = none<NotificationState>();
			selected = none<(int, int)>();

			document = new Document(this, textEditor, ".sql", "SQL");

			editorHost = new EditorHost(textEditor, document);

			var menus = document.Menus;

			menus.Menu("&File");
			menus.Menu("File", "New", (o, args) =>
			{
				textEditor.ClearModificationGlyphs();
				updateNotificationState();
				document.New();
			}, "^N");
			menus.Menu("File", "Open", (o, args) =>
			{
				textEditor.ClearModificationGlyphs();
				updateNotificationState();
				textEditor.ModificationLocked = true;
				try
				{
					document.Open();
				}
				finally
				{
					textEditor.ModificationLocked = false;
				}
			}, "^O");
			menus.Menu("File", "Save", (o, args) =>
			{
				textEditor.SetToSavedGlyphs();
				document.Save();
			}, "^S");
			menus.Menu("File", "Save As...", (o, args) =>
			{
				textEditor.SetToSavedGlyphs();
				document.SaveAs();
			});

			menus.MenuSeparator("File");
			menus.Menu("File", "Reload", (o, args) =>
			{
				textEditor.SetToUnmodifiedGlyphs();
				textEditor.ModificationLocked = true;
				try
				{
					updateNotificationState();
					if (document.FileName.If(out var fileName))
						document.Open(fileName);
				}
				finally
				{
					textEditor.ModificationLocked = false;
				}
			});
			menus.MenuSeparator("File");
			menus.Menu("File", "Exit", (o, args) => Close(), "%F4");

			document.StandardEditMenu();

			menus.Menu("&Test");
			menus.Menu("Test", "Check", (o, args) => check(), "F5");
			menus.Menu("Test", "Is Formatted", (o, args) => isFormatted(), "F6");
			menus.Menu("&View");
			menus.Menu("View", "&Whitespace", (o, args) =>
			{
				viewWhitespace = !viewWhitespace;
				((ToolStripMenuItem)o).Checked = viewWhitespace;
				textEditor.Invalidate();
			}, "^W");
			document.RenderMainMenu();

			textEditor.SetTabs(32, 64, 96, 128, 160, 192, 224);
			textEditor.Paint += (o, args) =>
			{
				args.Graphics.CompositingQuality = CompositingQuality.HighQuality;
				args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				args.Graphics.SmoothingMode = SmoothingMode.HighQuality;
				if (decorations)
				{
					textEditor.DrawLineNumbers(args.Graphics, Color.CadetBlue, Color.White);
					if (viewWhitespace)
						textEditor.DrawWhitespace(args.Graphics);
					drawCurrentLineBar(args.Graphics);
					textEditor.DrawModificationGlyphs(args.Graphics);
				}

				if (notificationState.If(out var state))
					state.Paint(args.Graphics, textEditor);
				if (selected.If(out var tuple))
				{
					var (start, length) = tuple;
					var rectangle = textEditor.RectangleFrom(args.Graphics, start, length, false);
					textEditor.DrawHighlight(args.Graphics, rectangle, Color.Cyan, DashStyle.DashDotDot);
				}
			};

			textEditor.SetLeftMargin(60);
			textEditor.ReassignHandle();
		}

		void drawCurrentLineBar(Graphics graphics)
		{
			if (textEditor.SelectionLength == 0)
				textEditor.DrawCurrentLineBar(graphics, Color.Black, Color.Transparent);
		}

		void check()
		{
			changesLocked = true;
			notificationState = none<NotificationState>();
			var result =
				from formattedContainer in SqlContainer.Formatted(editorHost.Text, configuration, editorHost)
				from colorized in formattedContainer.Colorize(true, configuration)
				from conformancesChecked in formattedContainer.CheckConformance(configuration.SqlConformanceConfiguration)
				from notificationStateFromNonConformances in NotificationState.FromNonConformances(conformancesChecked, document.FileName)
				select notificationStateFromNonConformances;

			changesLocked = false;

			updateNotificationState(result);
		}

		void updateNotificationState(IResult<NotificationState> notificationStateResult, bool invalidateEditor = true)
		{
			listNotifications.Items.Clear();
			selected = none<(int, int)>();
			if (notificationStateResult.If(out var newNotificationState, out var exception))
			{
				notificationState = newNotificationState.Some();
				listNotifications.Items.AddRange(newNotificationState.Select(nc => (object)nc).ToArray());
			}
			else
				notificationState = new NotificationState(exception).Some();

			if (invalidateEditor)
				textEditor.Invalidate();
		}

		void updateNotificationState(Exception exception, bool invalidateEditor = true)
		{
			listNotifications.Items.Clear();
			selected = none<(int, int)>();
			notificationState = new NotificationState(exception).Some();

			if (invalidateEditor)
				textEditor.Invalidate();
		}

		void updateNotificationState(string message, bool invalidateEditor = true)
		{
			listNotifications.Items.Clear();
			selected = none<(int, int)>();
			notificationState = new NotificationState(message).Some();

			if (invalidateEditor)
				textEditor.Invalidate();
		}

		void updateNotificationState(bool invalidateEditor = true)
		{
			listNotifications.Items.Clear();
			selected = none<(int, int)>();
			notificationState = none<NotificationState>();

			if (invalidateEditor)
				textEditor.Invalidate();
		}

		void textEditor_TextChanged(object sender, EventArgs e)
		{
			colorize();
		}

		void colorize(bool invalidateEditor = true)
		{
			if (changesLocked || (editorHost?.Text?.Length ?? 0) == 0)
				return;

			changesLocked = true;
			document.KeepClean = true;
			textEditor.ModificationLocked = true;

			try
			{
				if (notificationState.If(out var ns))
					ns.ClearException();
				if (SqlContainer.Colorize(editorHost.Text, editorHost, configuration).IfNot(out var exception))
					updateNotificationState(exception, false);

				if (invalidateEditor)
					textEditor.Invalidate();
			}
			finally
			{
				changesLocked = false;
				document.KeepClean = false;
				textEditor.ModificationLocked = false;
			}
		}

		void isFormatted()
		{
			if (SqlContainer.Formatted(editorHost.Text, configuration, editorHost).If(out var sqlContainer, out var exception))
			{
				if (editorHost.Text == sqlContainer.SqlSource)
					updateNotificationState("Formatted");
				else
					updateNotificationState("Not formatted");
			}
			else
				updateNotificationState(exception);
		}

		void listNotifications_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (listNotifications.SelectedIndex > -1)
			{
				var nonConformance = (NonConformance)listNotifications.SelectedItem;
				selected = (nonConformance.Selection.Position, nonConformance.Selection.Length).Some();
				textEditor.StopUpdating();
				textEditor.Select(nonConformance.Selection.Position, nonConformance.Selection.Length);
				textEditor.ResumeUpdating();
				textEditor.ScrollToCaret();
				textEditor.Invalidate();

				colorizeOnNewFirstVisibleLine();
			}
		}

		void textEditor_KeyUp(object sender, KeyEventArgs e)
		{
			switch (e.KeyCode)
			{
				case Keys.Up:
				case Keys.Down:
					textEditor.Invalidate();
					break;
/*				default:
					textEditor.DrawModificationGlyphs();
					break;*/
			}
		}

		void textEditor_SelectionChanged(object sender, EventArgs e)
		{
			colorizeOnNewFirstVisibleLine();
		}

		void colorizeOnNewFirstVisibleLine()
		{
			if (changesLocked)
				return;

			if (textEditor.FirstVisibleLine != firstLineNumber)
			{
				colorize(false);
				firstLineNumber = textEditor.FirstVisibleLine;
			}

			textEditor.Invalidate();
		}

		void Form1_Resize(object sender, EventArgs e)
		{
			if (changesLocked)
				return;

			colorize();
			firstLineNumber = textEditor.FirstVisibleLine;
		}
	}
}