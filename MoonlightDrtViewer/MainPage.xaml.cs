using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

using MoonlightDrtViewer.MonkeyWrench;
using System.Text;
using System.Net.Browser;

namespace MoonlightDrtViewer
{
	public partial class MainPage : UserControl
	{
		public static WebServicesSoapClient ws_client = new WebServicesSoapClient ("si.com");
		public static WebServicesSoap ws = ws_client;

		public static WebServiceLogin login = new WebServiceLogin ();

		double commit_text_height = 60;
		double test_text_width = 100.0;
		double test_square_size = 20.0;
		DateTime last_render = DateTime.MinValue;
		bool initialized = false;

		public MainPage ()
		{
			InitializeComponent ();

			Application.Current.UnhandledException += (object sender, ApplicationUnhandledExceptionEventArgs ea) =>
			{
				Console.WriteLine ("Unhandled exception: {0}", ea.ExceptionObject);
				ea.Handled = true;
			};

			WorkQueue.ItemAdded += (WorkItem item) =>
			{
				int pos = WorkQueue.GetQueuePosition (item);
				if (pos >= lstWork.Items.Count) {
					lstWork.Items.Add (item);
				} else {
					lstWork.Items.Insert (pos, item);
				}
				txtStatus.Text = string.Format ("{0} items left in queue", WorkQueue.Size);
			};

			WorkQueue.ExecutionStarted += (WorkItem item) =>
			{
				lstWork.Items.Remove (item);
				txtStatus.Text = string.Format ("{0} items left in queue: {1}", WorkQueue.Size, item.ToString ());
			};

			WorkQueue.ExecutionCompleted += (WorkItem item) =>
			{
				txtStatus.Text = string.Format ("{0} items left in queue", WorkQueue.Size);
			};

			DispatcherTimer timer = new DispatcherTimer ();
			timer.Interval = TimeSpan.FromSeconds (10);
			timer.Tick += CheckISOStorageSize;
			timer.Start ();
			CheckISOStorageSize ();
			
			string pwd, user;
			if (Settings.TryGet<string> ("password", out pwd) && Settings.TryGet ("user", out user)) {
				txtPassword.Password = pwd;
				txtUser.Text = user;
				cmdContinue_Click (cmdContinue, null);
			}
			initialized = true;
		}

		private void CheckForNewResults (object sender, EventArgs ea)
		{
			if (WorkQueue.Size == 0)
				cmdRefresh_Click (sender, ea);
		}

		public static void Log (string msg, params object [] args)
		{
			if (!Deployment.Current.CheckAccess ()) {
				Deployment.Current.Dispatcher.BeginInvoke (() => Log (msg, args));
				return;
			}
			((MainPage) Application.Current.RootVisual).LogInternal (msg, args);
		}

		private void LogInternal (string msg, params object [] args)
		{
			if (!CheckAccess ()) {
				Deployment.Current.Dispatcher.BeginInvoke (() => LogInternal (msg, args));
				return;
			}
			lstLog.Items.Add (string.Format (msg, args));
		}

		private System.Windows.Shapes.Path CreateTestResultPath (Color fill, params Geometry [] geometries)
		{
			System.Windows.Shapes.Path result = new System.Windows.Shapes.Path ();
			result.Fill = new SolidColorBrush (fill);
			if (geometries.Length == 1) {
				result.Data = geometries [0];
			} else {
				GeometryGroup gg = new GeometryGroup ();
				foreach (Geometry g in geometries) {
					if (g != null)
						gg.Children.Add (g);
				}
				result.Data = gg;
			}
			result.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
			result.VerticalAlignment = System.Windows.VerticalAlignment.Top;
			return result;
		}

		static bool rendering = false;

		public void RenderTests (bool force = false)
		{
			try {
				Commits commits;
				int counter;
				bool passing_tests = !(chkOnlyFailedTests.IsChecked.HasValue && chkOnlyFailedTests.IsChecked.Value);
				bool hide_revisions = chkHideRevisions.IsChecked.HasValue && chkHideRevisions.IsChecked.Value;
				string [] ids = string.IsNullOrEmpty (txtID.Text) ? new string [0] : txtID.Text.Split (new char [] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				string [] areas;


				if (!force && last_render.AddSeconds (5) > DateTime.Now) {
					// Only render every 5 seconds
					return;
				}
				last_render = DateTime.Now;
				
				commits = Settings.GetCommits ();
				commits.Sort ();

				cvsTests.Children.Clear ();

				if (Tests.tests == null || Tests.tests.Count == 0)
					return;

				if (rendering)
					return;
				rendering = true;

				if (!cmbAreas.IsDropDownOpen) {
					string current_area = "All";
					if (cmbAreas.SelectedIndex >= 0)
						current_area = (string) cmbAreas.SelectedValue;
					cmbAreas.Items.Clear ();
					cmbAreas.Items.Add ("All");
					string [] all_areas = Tests.areas.ToArray ();
					Array.Sort (all_areas);
					foreach (string area in all_areas) {
						cmbAreas.Items.Add (area);
					}
					cmbAreas.SelectedValue = current_area;
				}
				areas = cmbAreas.SelectedIndex <= 0 ? new string [0] : ((string) cmbAreas.SelectedValue).Split (new char [] { ',' }, StringSplitOptions.RemoveEmptyEntries);

				// add revisions
				if (!hide_revisions) {
					counter = 0;
					foreach (Commit commit in commits) {
						RotateTransform rotate = new RotateTransform ();
						rotate.Angle = 90;
						TextBlock tb = new TextBlock ();
						tb.Text = commit.R.Length > 9 ? (commit.R.Substring (0, 6) + "...") : commit.R;
						tb.SetValue (Canvas.LeftProperty, test_square_size * (counter + 1) + test_text_width);
						tb.SetValue (Canvas.TopProperty, 0.0);
						tb.Width = commit_text_height;
						tb.MaxHeight = test_square_size;
						tb.RenderTransform = rotate;
						//tb.RenderTransformOrigin = new Point (test_square_size * counter + test_text_width, commit_text_height);
						//tb.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
						cvsTests.Children.Add (tb);
						counter++;
					}
				}

				// add test labels
				counter = 0;
				Tests.shown_tests.Clear ();
				for (int tst_idx = 0; tst_idx < Tests.tests.Count; tst_idx++) {
					Test test = Tests.tests [tst_idx];
					double top;
					TextBlock tb;

					// Check if this test should be shown
					bool test_failed = false;
					for (int i = 0; i < commits.Count; i++) {
						TestRevision tr;

						tr = test.FindRevision (commits [i].R);
						if (tr == null)
							continue;

						test_failed = tr.GetComputedStatus () == "Failed" || tr.GetComputedStatus () == "Crashed";
						break;
					}

					if (!passing_tests && !test_failed)
						continue;

					if (areas.Length > 0) {
						bool found = false;
						if (test.Areas != null) {
							foreach (string a in test.Areas) {
								foreach (string b in areas) {
									if (string.Equals (a, b, StringComparison.OrdinalIgnoreCase)) {
										found = true;
										break;
									}
								}
								if (found)
									break;
							}
						}
						if (!found)
							continue;
					}

					if (!string.IsNullOrEmpty (txtID.Text)) {
						bool found = false;
						foreach (string id in ids) {
							if (string.Equals (id, test.ID, StringComparison.OrdinalIgnoreCase)) {
								found = true;
								break;
							}
						}
						if (!found)
							continue;
					}

					Tests.shown_tests.Add (test);

					top = test_square_size * counter + (hide_revisions ? 0 : commit_text_height);

					tb = new TextBlock ();
					tb.Text = test.ID;
					tb.SetValue (Canvas.LeftProperty, 0.0);
					tb.SetValue (Canvas.TopProperty, top);

					cvsTests.Children.Add (tb);

					for (int rev_idx = 0; rev_idx < test.Revisions.Count; rev_idx++) {
						TestRevision tr = test.Revisions [rev_idx];
						int test_index = hide_revisions ? rev_idx : commits.IndexOf (commits.Single (c => c.R == tr.Revision));
						double left = test_square_size * test_index + test_text_width;
						Color color;
						int crashed = 0, failed = 0, known_failures = 0, passed = 0, unknown = 0;
						bool mixed_results = false;

						tr.CountResults (out crashed, out failed, out known_failures, out passed, out unknown, out mixed_results);

						if (crashed > 0) {
							color = Color.FromArgb (255, 125, 0, 0);
						} else if (failed > 0) {
							color = Color.FromArgb (255, 255, 0, 0);
						} else if (known_failures > 0) {
							color = Colors.Orange;
						} else if (unknown > 0) {
							color = Colors.Yellow;
						} else {
							color = Colors.Green;
						}

						RectangleGeometry rect = new RectangleGeometry ();
						rect.Rect = new Rect (left, top, test_square_size, test_square_size);
						cvsTests.Children.Add (CreateTestResultPath (color, rect));
						if (mixed_results) {
							rect = new RectangleGeometry ();
							rect.Rect = new Rect (left + 5, top + 5, test_square_size - 10, test_square_size - 10);
							rect.RadiusX = test_square_size - 10;
							rect.RadiusY = rect.RadiusX;
							cvsTests.Children.Add (CreateTestResultPath (Colors.LightGray, rect));
						}
					}

					counter++;
				}
				cvsTests.Height = counter * test_square_size + commit_text_height + 20;
				cvsTests.Width = commits.Count * test_square_size + test_text_width + 20;
				rendering = false;
			} catch (Exception ex) {
				Log ("Exception in RenderTests: {0}", ex);
				rendering = false;
			}
		}

		private TestRevision FindTestRevision (Point pnt, out Test test, out Commit commit)
		{
			int test_idx, commit_idx;
			Commits commits;
			TestRevision revision = null;
			bool hide_revisions = chkHideRevisions.IsChecked.HasValue && chkHideRevisions.IsChecked.Value;

			test = null;
			commit = null;

			// Find test
			test_idx = (int) ((pnt.Y - (hide_revisions ? 0 : commit_text_height)) / test_square_size);
			commit_idx = (int) ((pnt.X - test_text_width) / test_square_size);

			if (commit_idx < 0)
				return null;

			commits = Settings.GetCommits ();

			if (!(test_idx < 0 || test_idx >= Tests.shown_tests.Count))
				test = Tests.shown_tests [test_idx];
			
			if (hide_revisions) {
				if (test == null)
					return null;
				if (commit_idx >= test.Revisions.Count)
					return null;
				revision = test.Revisions [commit_idx];
			} else {
				if (commit_idx >= commits.Count)
					return null;
				commit = commits [commit_idx];
				if (test != null)
					revision = test.FindRevision (commit.R);
			}

			return revision;
		}

		private void cvsTests_MouseMove (object sender, MouseEventArgs e)
		{
			ShowTestDetails (false, e);
		}

		private void cvsTests_MouseLeftButtonDown (object sender, MouseButtonEventArgs e)
		{
			try {
				chkStickyResults.IsChecked = chkStickyResults.IsChecked.HasValue ? !chkStickyResults.IsChecked.Value : true;
				ShowTestDetails (chkStickyResults.IsChecked.Value, e);
			} catch (Exception ex) {
				Log ("Canvas MouseLeftButtonDown exception: {0}", ex);
			}
		}

		private void ShowTestDetails (bool find_html_reports, MouseEventArgs e)
		{
			try {
				TextBlock tb;
				Test test;
				TestRevision revision;
				Commit commit;

				if (!find_html_reports && chkStickyResults.IsChecked.HasValue && chkStickyResults.IsChecked.Value)
					return;

				revision = FindTestRevision (e.GetPosition (cvsTests), out test, out commit);

				gridTestDetails.Tag = new object [] { revision, test };
				gridTestDetails.Children.Clear ();
				gridTestDetails.RowDefinitions.Clear ();

				if ((test == null || revision == null) && commit != null) {
					gridTestDetails.RowDefinitions.Add (new RowDefinition ());
					gridTestDetails.RowDefinitions [gridTestDetails.RowDefinitions.Count - 1].Height = new GridLength (20);
					tb = new TextBlock ();
					tb.SetValue (Grid.RowProperty, gridTestDetails.RowDefinitions.Count - 1);
					tb.SetValue (Grid.ColumnProperty, 0);
					tb.SetValue (Grid.ColumnSpanProperty, 3);
					tb.Text = string.Format ("Revision: {0} {1} {2}", commit.R, commit.Author, commit.Date);
					gridTestDetails.Children.Add (tb);

					int total_failed = 0, total_passed = 0;
					int total_known_failures = 0, total_crashed = 0;
					int total_mixed_results = 0;
					int total = 0;

					foreach (Test t in Tests.tests) {
						TestRevision tr = t.FindRevision (commit.R);
						int crashed, failed, known_failures, passed, unknown;
						bool mixed_results;

						if (tr == null)
							continue;
						
						tr.CountResults (out crashed, out failed, out known_failures, out passed, out unknown, out mixed_results);

						if (mixed_results)
							total_mixed_results++;

						if (crashed > 0) {
							total_crashed++;
							total_failed++;
						} else if (failed > 0) {
							total_failed++;
						} else if (known_failures > 0) {
							total_known_failures++;
							total_failed++;
						} else if (passed > 0) {
							total_passed++;
						}

						if (crashed > 0 || failed > 0 || known_failures > 0 || passed > 0 || unknown > 0)
							total++;
					}

					string [] results = { 
											string.Format ("{0}/{1} = {2:#.##}%", total_passed, total, 100 * (double) total_passed / (double) total),
											string.Format ("{0}/{1} = {2:#.##}%", total_failed, total, 100 * (double) total_failed / (double) total),
											total_crashed.ToString (),
											total_known_failures.ToString (),
											total_mixed_results.ToString (),
											total.ToString () };
					string [] titles = { 
										   "Passed tests", 
										   "Failed tests", 
										   "Failed tests that crashed", 
										   "Failed tests marked as known failure", 
										   "Tests with mixed results", 
										   "Total number of tests executed" };
					for (int i = 0; i < results.Length; i++) {
						gridTestDetails.RowDefinitions.Add (new RowDefinition ());
						gridTestDetails.RowDefinitions [gridTestDetails.RowDefinitions.Count - 1].Height = new GridLength (20);
						tb = new TextBlock ();
						tb.SetValue (Grid.RowProperty, gridTestDetails.RowDefinitions.Count - 1);
						tb.SetValue (Grid.ColumnProperty, 0);
						tb.SetValue (Grid.ColumnSpanProperty, 3);
						tb.Text = string.Format ("{0}: {1}", titles [i], results [i]);
						gridTestDetails.Children.Add (tb);
					}

				} else if (test != null && revision != null) {
					gridTestDetails.RowDefinitions.Add (new RowDefinition ());
					gridTestDetails.RowDefinitions [gridTestDetails.RowDefinitions.Count - 1].Height = new GridLength (20);
					tb = new TextBlock ();
					tb.SetValue (Grid.RowProperty, gridTestDetails.RowDefinitions.Count - 1);
					tb.SetValue (Grid.ColumnProperty, 0);
					tb.SetValue (Grid.ColumnSpanProperty, 3);
					tb.Text = string.Format ("Test ID: '{1}' Title: '{2}' Commit: {0}", revision.Revision, test.ID, test.Title);
					gridTestDetails.Children.Add (tb);

					if (revision.Results == null)
						return;

					foreach (TestResult res in revision.Results) {
						gridTestDetails.RowDefinitions.Add (new RowDefinition ());
						gridTestDetails.RowDefinitions [gridTestDetails.RowDefinitions.Count - 1].Height = new GridLength (20);

						tb = new TextBlock ();
						tb.SetValue (Grid.RowProperty, gridTestDetails.RowDefinitions.Count - 1);
						tb.SetValue (Grid.ColumnProperty, 0);

						gridTestDetails.Children.Add (tb);
						tb.Text = string.Format ("Result: {0}{3}{4} for lane {1} on host {2}\n",
							res.GetComputedStatus (),
							Settings.GetLanes ().Single (lane => lane.id == res.LaneId).lane,
							Settings.GetHosts ().Single (host => host.id == res.HostId).host,
							!string.IsNullOrEmpty (res.KnownFailure) ? string.Format (" ({0})", res.KnownFailure) : string.Empty,
							res.Crashed ? " (Crashed)" : string.Empty);

						HyperlinkButton link = new HyperlinkButton ();
						link.SetValue (Grid.RowProperty, gridTestDetails.RowDefinitions.Count - 1);
						link.SetValue (Grid.ColumnProperty, 1);
						link.Content = find_html_reports ? "Computing link to html report..." : "Click test square to compute html reports";
						link.Foreground = new SolidColorBrush (Colors.Black);
						gridTestDetails.Children.Add (link);

						if (find_html_reports) {
							int workfile_id = res.WorkFileId;
							ws.BeginGetFilesForWork (login, res.RevisionWorkId, res.CommandId, "index.html", (IAsyncResult async_res) =>
							{
								GetFilesForWorkResponse response = ws.EndGetFilesForWork (async_res);
								HyperlinkButton lnk = link;
								Test tst = test;

								Dispatcher.BeginInvoke (() =>
								{
									if (response.WorkFileIds != null && response.WorkFileIds.Length > 0 && response.WorkFileIds [0].Length > 0) {
										lnk.NavigateUri = new Uri (string.Format ("http://moon.sublimeintervention.com/ViewHtmlReport.aspx?lane_id={0}&host_id={1}&revision_id={2}&workfile_id={3}#id_{4}",
																		res.LaneId,
																		res.HostId,
																		res.RevisionId,
																		response.WorkFileIds [0] [0],
																		tst.ID), UriKind.Absolute);
										link.TargetName = "_blank";
										lnk.Content = "View html report";
									} else {
										lnk.Content = "Html report not found";
									}
								});
							}, null);
						}
					}
				}
			} catch (Exception ex) {
				Log ("Canvas MouseMove exception: {0}", ex);
			}
		}
		
		private void CheckISOStorageSize (object sender = null, EventArgs ea = null)
		{
			System.Windows.Visibility v;
			if (HasEnoughISOStorage ()) {
				v = System.Windows.Visibility.Collapsed;
			} else {
				v = System.Windows.Visibility.Visible;
			}
			if (v != cmdIncreaseISOStorage.Visibility) {
				cmdIncreaseISOStorage.Visibility = v;
				cmdIncreaseISOStorage.Content = string.Format ("Increase ISO storage, current is {0} MB (with {1} MB free), need at least 1GB total and 100MB free", IsolatedStorageFile.GetUserStoreForApplication ().Quota / 1024 / 1024, IsolatedStorageFile.GetUserStoreForApplication ().AvailableFreeSpace / 1024 / 1024);
				cmdContinue.Visibility = v == System.Windows.Visibility.Collapsed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
			}
		}

		public static void TryDeleteISOFile (string md5)
		{
			try {
				IsolatedStorageFile.GetUserStoreForApplication ().DeleteFile (md5);
			} catch (Exception ex) {
				MainPage.Log ("TryDeleteISOFile ({0}) could not delete: {1}", md5, ex);
			}
		}

		private static bool HasEnoughISOStorage ()
		{
			IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication ();
			if (iso.Quota < 1024 * 1024 * 1024)
				return false; /* 1 gb needed at least */
			if (iso.AvailableFreeSpace < 1024 * 1024 * 10)
				return false; /* 10 mb free */
			return true;
		}

		private void Load ()
		{
			WorkQueue.Append (new FetchLogin (), LoginFetched);
			WorkQueue.Append (new FetchLanes (), LanesFetched, () => !string.IsNullOrEmpty(login.Cookie));
			WorkQueue.Append (new FetchHosts (), HostsFetched, () => !string.IsNullOrEmpty (login.Cookie));
		}

		private void LoginFetched (WorkItem item)
		{
			if (!string.IsNullOrEmpty (login.Cookie))
				return;

			txtPassword.Password = string.Empty;
			tabLogin.IsSelected = true;

			lstWork.Items.Clear ();
			WorkQueue.Clear ();
		}

		private void LanesFetched (WorkItem item)
		{
			HashSet<string> repositories;

			lstLanes.Items.Clear ();
			foreach (DBLane lane in Settings.GetLanes ()) {
				lstLanes.Items.Add (lane.lane);
			}

			repositories = new HashSet<string> ();
			foreach (DBLane lane in Settings.GetLanes ()) {
				if (repositories.Contains (lane.repository))
					continue;
				repositories.Add (lane.repository);
			}

			cmbRepositories.Items.Clear ();
			foreach (string repository in repositories) {
				cmbRepositories.Items.Add (repository);
			}
			if (!string.IsNullOrEmpty (Settings.GetSelectedRepository ())) {
				cmbRepositories.SelectedValue = Settings.GetSelectedRepository ();
			} else {
				cmbRepositories.SelectedValue = "git://github.com/mono/moon.git";
			}
			WorkQueue.Append (new FetchRevisions (), RevisionsFetched, () => Settings.GetSelectedRepository () != null);
		}

		private void HostsFetched (WorkItem item)
		{
			lstHosts.Items.Clear ();
			foreach (DBHost host in Settings.GetHosts ()) {
				lstHosts.Items.Add (host.host);
			}
		}

		private void RevisionsFetched (WorkItem item)
		{
			lstRevisions.Items.Clear ();
			if (Settings.GetCommits () != null) {
				foreach (Commit commit in Settings.GetCommits ()) {
					lstRevisions.Items.Add (commit.R + " " + commit.Date.ToString ("yyyy/MM/dd HH:mm:ss"));
				}
			}

			string [] revisions;
			Commits commits = Settings.GetCommits ();

			revisions = new string [5];

			for (int i = 0; i < commits.Count; i++) {
				revisions [i % 5] = commits [i].R;
				if (i % 5 == 4) {
					WorkQueue.Append (new FetchTestResults (revisions), null, () => HasEnoughISOStorage ());
					revisions = new string [5];
				}
			}
			if (commits.Count % 5 > 0) {
				Array.Resize (ref revisions, commits.Count % 5);
				WorkQueue.Append (new FetchTestResults (revisions), null, () => HasEnoughISOStorage ());
			}
		}

		private void cmbRepositories_SelectionChanged (object sender, SelectionChangedEventArgs e)
		{
			lstLanes.SelectedItems.Clear ();

			if (cmbRepositories.SelectedIndex >= 0) {
				Settings.Save ("selected-repository", cmbRepositories.SelectedItem);
				foreach (DBLane lane in Settings.GetSelectedLanes ()) {
					lstLanes.SelectedItems.Add (lane.lane);
				}
			}
		}

		private void cmdClear_Click (object sender, RoutedEventArgs e)
		{
			foreach (string file in IsolatedStorageFile.GetUserStoreForApplication ().GetFileNames ()) {
				IsolatedStorageFile.GetUserStoreForApplication ().DeleteFile (file);
			}
			cmdClearNoFiles_Click (sender, e);
		}

		private void cmdClearNoFiles_Click (object sender, RoutedEventArgs e)
		{
			IsolatedStorageSettings.ApplicationSettings.Clear ();
			IsolatedStorageSettings.ApplicationSettings.Save ();
			lstHosts.Items.Clear ();
			lstLanes.Items.Clear ();
			lstLog.Items.Clear ();
			lstRevisions.Items.Clear ();
			lstWork.Items.Clear ();
			cmbRepositories.Items.Clear ();
			Tests.tests = new List<Test> ();
			if (System.Windows.Browser.HtmlPage.IsEnabled) {
				System.Windows.Browser.HtmlPage.Window.Eval ("location.reload (true);");
			}
			Load ();
		}

		private void cmdIncreaseISOStorage_Click (object sender, RoutedEventArgs e)
		{
			if (IsolatedStorageFile.GetUserStoreForApplication ().Quota < 1024 * 1024 * 1024)
				IsolatedStorageFile.GetUserStoreForApplication ().IncreaseQuotaTo (1024 * 1024 * 1024);
			else if (IsolatedStorageFile.GetUserStoreForApplication ().AvailableFreeSpace < 1024 * 1024 * 100)
				IsolatedStorageFile.GetUserStoreForApplication ().IncreaseQuotaTo (IsolatedStorageFile.GetUserStoreForApplication ().Quota + 1024 * 1024 * 512);
			CheckISOStorageSize ();
		}

		private void cmdRefresh_Click (object sender, EventArgs ea)
		{
			int revs;

			if (!int.TryParse (txtRevisions.Text, out revs)) {
				txtRevisions.Text += " (could not parse this number)";
				return;
			}

			WorkQueue.Append (new FetchRevisions (revs, 0), RevisionsFetched, () => Settings.GetSelectedRepository () != null);
		}

		private void chkStickyTestResults_CheckedChanged (object sender, RoutedEventArgs e)
		{
			if (!initialized)
				return;
			if (chkStickyResults.IsChecked.HasValue && chkStickyResults.IsChecked.Value) {
				gridTestDetails.Background = new SolidColorBrush (Colors.LightGray);
			} else {
				gridTestDetails.Background = null;
			}
		}

		private void chkOnlyFailedTests_CheckedChanged (object sender, RoutedEventArgs e)
		{
			if (!initialized)
				return;
			RenderTests (true);
		}

		private void chkHideRevisions_CheckedChanged (object sender, RoutedEventArgs e)
		{
			if (!initialized)
				return;
			RenderTests (true);
		}

		private void cmdContinue_Click (object sender, RoutedEventArgs e)
		{
			login.Password = txtPassword.Password;
			login.User = txtUser.Text;
			Load ();
			tabGraph.IsSelected = true;

			DispatcherTimer timer = new DispatcherTimer ();
			timer = new DispatcherTimer ();
			timer.Interval = TimeSpan.FromMinutes (10);
			timer.Tick += CheckForNewResults;
			timer.Start ();

			Settings.Save ("password", login.Password);
			Settings.Save ("user", login.User);
		}

		private void cmbAreas_SelectionChanged (object sender, SelectionChangedEventArgs e)
		{
			try {
				RenderTests (true);
			} catch (Exception ex) {
				Log ("Exception in cmbAreas_SelectionChanged: {0}", ex);
			}
		}

		private void txtID_TextChanged (object sender, TextChangedEventArgs e)
		{
			try {
				RenderTests (true);
			} catch (Exception ex) {
				Log ("Exception in txtID_TextChanged: {0}", ex);
			}
		}

	}

	static class WorkQueue
	{
		static bool processing;
		static List<WorkItem> items = new List<WorkItem> ();

		public delegate void ExecutionEventHandler (WorkItem item);
		public static event ExecutionEventHandler ExecutionStarted;
		public static event ExecutionEventHandler ExecutionCompleted;
		public static event ExecutionEventHandler ItemAdded;

		public static int Size
		{
			get { return items.Count; }
		}

		public static int GetQueuePosition (WorkItem item)
		{
			return items.IndexOf (item);
		}

		public static void Clear ()
		{
			items.Clear ();
		}

		public static void Prepend (WorkItem item)
		{
			Add (item, null, null, true);
		}

		public static void Prepend (WorkItem item, ExecutionEventHandler completed_handler)
		{
			Add (item, completed_handler, null, true);
		}

		public static void Append (WorkItem item)
		{
			Append (item, null);
		}

		public static void Append (WorkItem item, ExecutionEventHandler completed_handler)
		{
			Append (item, completed_handler, null);
		}

		public static void Append (WorkItem item, ExecutionEventHandler completed_handler, Func<bool> condition)
		{
			Add (item, completed_handler, condition, false);
		}

		public static void Add (WorkItem item, ExecutionEventHandler completed_handler, Func<bool> condition, bool prepend)
		{
			if (completed_handler != null)
				item.Executed += completed_handler;

			if (condition != null)
				item.Condition = condition;

			if (prepend) {
				items.Insert (0, item);
			} else {
				items.Add (item);
			}
			ItemAdded (item);

			if (!processing)
				ProcessNextItem ();
		}

		public static void ProcessNextItem ()
		{
			WorkItem item;

			if (items.Count == 0 || processing)
				return;

			item = items [0];

			if (item.Condition != null && !item.Condition ()) {
				DispatcherTimer timer = new DispatcherTimer ();
				timer.Interval = TimeSpan.FromSeconds (1);
				timer.Tick += (object sender, EventArgs ea) =>
					{
						timer.Stop ();
						ProcessNextItem ();
					};
				timer.Start ();
				return;
			}

			item.running = true;
			processing = true;
			ExecutionStarted (item);
			Deployment.Current.Dispatcher.BeginInvoke (() => item.Execute ());
		}

		public static void ItemCompleted (WorkItem item)
		{
			processing = false;
			items.Remove (item);
			ExecutionCompleted (item);
			ProcessNextItem ();
		}
	}

	abstract class WorkItem
	{
		public bool running;
		public abstract void Execute ();
		public event WorkQueue.ExecutionEventHandler Executed;
		public Func<bool> Condition;

		protected void Log (string msg, params object [] args)
		{
			MainPage.Log (msg, args);
		}

		protected virtual void ReportCompleted ()
		{
			if (!CheckAccess ()) {
				BeginInvoke (() => ReportCompleted ());
				return;
			}

			if (Executed != null)
				Executed (this);
			WorkQueue.ItemCompleted (this);
		}

		protected bool CheckAccess ()
		{
			return Deployment.Current.Dispatcher.CheckAccess ();
		}

		protected void BeginInvoke (Action action)
		{
			Deployment.Current.Dispatcher.BeginInvoke (action);
		}

		protected WebServicesSoap ws
		{
			get { return MainPage.ws; }
		}

		protected WebServicesSoapClient ws_client
		{
			get { return MainPage.ws_client; }
		}

		protected WebServiceLogin login
		{
			get { return MainPage.login; }
		}
	}

	class FetchLogin : WorkItem
	{
		public override void Execute ()
		{
			ws.BeginLogin (login, LoginCompleted, null);
			Log ("Logging in");
		}

		private void LoginCompleted (IAsyncResult res)
		{
			if (!CheckAccess ()) {
				BeginInvoke (() => LoginCompleted (res));
				return;
			}

			LoginResponse response = ws.EndLogin (res);
			login.Cookie = response.Cookie;
			ReportCompleted ();
			if (string.IsNullOrEmpty (login.Cookie)) {
				Log ("Failed to log in as '{0}'", login.User);
			} else {
				Log ("Logged in as '{0}' with cookie: '{1}'", login.User, login.Cookie);
			}
		}

		public override string ToString ()
		{
			return "Login";
		}
	}

	class FetchFile : WorkItem
	{
		string md5;
		HttpWebRequest rw;

		public FetchFile (string md5)
		{
			this.md5 = md5;
		}

		public override string ToString ()
		{
			return string.Format ("Download file {0}", md5);
		}

		public override void Execute ()
		{
			IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication ();
			if (iso.FileExists (md5)) {
				Log ("File not fetched: {0}: it has already been downloaded.", md5);
				ReportCompleted ();
				return;
			}

			string uri = "http://" + ws_client.Endpoint.Address.Uri.Host + "/WebServices/Download.aspx?";
			uri += "md5=" + md5;
			uri += "&cookie=" + login.Cookie;
			uri += "&user=" + login.User;

			rw = HttpWebRequest.CreateHttp (uri);
			rw.AllowReadStreamBuffering = true;
			rw.BeginGetResponse (Downloading, null);
		}

		private void Downloading (IAsyncResult res)
		{
			if (!CheckAccess ()) {
				BeginInvoke (() => Downloading (res));
				return;
			}

			try {
				HttpWebResponse rsp = (HttpWebResponse) rw.EndGetResponse (res);

				System.Threading.ThreadPool.QueueUserWorkItem ((object v) =>
				{
					byte [] buffer = new byte [1024];
					int read;
					try {
						using (Stream str = rsp.GetResponseStream ()) {
							using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication ()) {
								using (IsolatedStorageFileStream iso_file = iso.CreateFile (this.md5)) {
									using (ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream zip = new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream (iso_file)) {
										while ((read = str.Read (buffer, 0, buffer.Length)) > 0) {
											zip.Write (buffer, 0, read);
										}
										zip.Flush ();
										Log ("File download of '{0}' succeeded (real size: {1} compressed size: {2})", md5, str.Length, iso_file.Length);
									}
								}
							}
						}
						rsp.Close ();
					} catch (Exception ex) {
						Log ("File processing of '{0}' failed: {1}", md5, ex.Message);
					}
					this.ReportCompleted ();
				});
			} catch (WebException wex) {
				Log ("File download of '{0}' failed: {1}", md5, wex.Message);
			}
		}
	}

	class FetchLanes : WorkItem
	{
		public override void Execute ()
		{
			if (Settings.GetLanes () != null) {
				Log ("Lanes loaded from iso storage");
				ReportCompleted ();
				return;
			}

			ws.BeginGetLanes (null, Downloaded, null);
		}

		private void Downloaded (IAsyncResult res)
		{
			if (!CheckAccess ()) {
				BeginInvoke (() => Downloaded (res));
				return;
			}

			Settings.Save ("lanes", ws.EndGetLanes (res));
			Log ("Downloaded {0} lanes", Settings.GetLanes ().Count ());
			ReportCompleted ();
		}

		public override string ToString ()
		{
			return "Download all lanes";
		}
	}

	class FetchHosts : WorkItem
	{
		public override void Execute ()
		{
			if (Settings.GetHosts () != null) {
				Log ("Hosts loaded from iso storage");
				ReportCompleted ();
				return;
			}

			ws.BeginGetHosts (null, Downloaded, null);
		}

		private void Downloaded (IAsyncResult res)
		{
			if (!CheckAccess ()) {
				BeginInvoke (() => Downloaded (res));
				return;
			}

			Settings.Save ("hosts", ws.EndGetHosts (res));
			Log ("Downloaded {0} hosts", Settings.GetHosts ().Count ());
			ReportCompleted ();
		}

		public override string ToString ()
		{
			return "Download all hosts";
		}
	}
	
	class FetchRevisions : WorkItem
	{
		public int count;
		public int offset;
		private int request_count;

		public FetchRevisions (int count = 250, int offset = 0)
		{
			this.count = count;
			this.offset = offset;
		}

		public override void Execute ()
		{
			if (Settings.GetSelectedLanes () == null) {
				ReportCompleted ();
				return;
			}

			foreach (DBLane lane in Settings.GetSelectedLanes ()) {
				request_count++;
				ws.BeginGetRevisions (new GetRevisionsRequest (null, lane.id, null, count, offset), Downloaded, null);
			}
		}

		private void Downloaded (IAsyncResult res)
		{
			Commits commits;
			GetRevisionsResponse revisions;

			if (!CheckAccess ()) {
				BeginInvoke (() => Downloaded (res));
				return;
			}

			if (!Settings.TryGet<Commits> ("commits", out commits)) {
				commits = new Commits ();
				Settings.Save ("commits", commits);
			}

			/* We want to merge revisions with any previous revisions we might have */
			GetRevisionsResponse new_revisions = ws.EndGetRevisions (res).GetRevisionsResult;

			Log ("Downloaded {0} revisions from lane {1}",
				new_revisions.Revisions.Length,
				new_revisions.Revisions.Length > 0 ? Settings.GetLanes ().First (l => new_revisions.Revisions [0].lane_id == l.id).lane : "?");

			if (Settings.TryGet<GetRevisionsResponse> ("revisions", out revisions)) {
				List<DBRevision> revs = new List<DBRevision> ();
				revs.AddRange (revisions.Revisions);
				foreach (DBRevision r in new_revisions.Revisions.Reverse ()) {
					if (revs.Any (v => r.id == v.id))
						continue;
					revs.Insert (0, r);
				}
				revisions.Revisions = revs.ToArray ();
			} else {
				revisions = new_revisions;
			}
			foreach (DBRevision rev in revisions.Revisions) {
				if (commits.Any (commit => commit.R == rev.revision))
					continue;
				commits.Add (new Commit (rev.revision, rev.date, rev.author));
			}
			commits.Sort ();
			Settings.Save ("revisions", revisions);
			Settings.Save ("commits", commits);

			request_count--;
			if (request_count == 0)
				ReportCompleted ();
		}

		public override string ToString ()
		{
			return string.Format ("Download last {0} revisions from offset {1}", count, offset);
		}
	}

	class FetchTestResults : WorkItem
	{
		bool failed = false;
		string [] commits;

		public FetchTestResults (string [] commits)
		{
			this.commits = commits;
		}

		public override void Execute ()
		{
			ws.BeginGetTestResults (null, commits, "drtlist.results.xml", Downloaded, null);
			Log ("Downloading test results for {0} commits...", commits.Length);
		}

		private void Downloaded (IAsyncResult res)
		{
			GetTestResultsResponse response;

			if (!CheckAccess ()) {
				BeginInvoke (() => Downloaded (res));
				return;
			}

			try {
				response = ws.EndGetTestResults (res);
				Settings.Save ("testresults", response);
				Log ("Downloaded {0} test results", response.Results.Length);
			} catch (Exception ex) {
				Log ("Exception while downloading test results (will try again): {0}", ex);
				failed = true;
			}
			ReportCompleted ();
		}

		protected override void ReportCompleted ()
		{
			GetTestResultsResponse response;

			base.ReportCompleted ();

			if (failed) {
				WorkQueue.Prepend (new FetchTestResults (commits));
				return;
			}

			response = Settings.Get<GetTestResultsResponse> ("testresults");

			for (int i = response.Results.Length - 1; i >= 0; i--) {
				DBTestResult tr = response.Results [i];
				WorkQueue.Prepend (new FetchFile (tr.md5), (WorkItem item) =>
				{
					WorkQueue.Prepend (new ProcessFile (tr.md5, tr.revisionwork_id, tr.lane_id, tr.host_id, tr.revision_id, tr.command_id, tr.id));
				});
			}
		}

		public override string ToString ()
		{
			return string.Format ("Download {0} test results", commits.Length);
		}
	}

	class ProcessFile : WorkItem
	{
		public string md5;
		public int revisionwork_id;
		public int lane_id;
		public int host_id;
		public int revision_id;
		public int command_id;
		public int workfile_id;

		public ProcessFile (string md5, int revisionwork_id, int lane_id, int host_id, int revision_id, int command_id, int workfile_id)
		{
			this.md5 = md5;
			this.revisionwork_id = revisionwork_id;
			this.lane_id = lane_id;
			this.host_id = host_id;
			this.revision_id = revision_id;
			this.command_id = command_id;
			this.workfile_id = workfile_id;
		}

		public override void Execute ()
		{
			Tests.ProcessFile (md5, revisionwork_id, lane_id, host_id, revision_id, workfile_id, command_id);
			((MainPage) Application.Current.RootVisual).RenderTests ();
			ReportCompleted ();
		}

		public override string ToString ()
		{
			try {
				return string.Format ("Process drtlist from lane {0} on host {1} of revision {2} and command {3}",
					Settings.GetLanes ().First (l => l.id == lane_id).lane,
					Settings.GetHosts ().First (h => h.id == host_id).host,
					Settings.GetRevisions ().First (r => r.id == revision_id).revision,
					command_id);
			} catch {
				return string.Format ("Process drtlist from lane {0} on host {1} of revision {2} and command {3}",
					lane_id, host_id, revision_id, command_id);
			}
		}
	}

	public class Commits : List<Commit>
	{
		public new void Sort ()
		{
			this.Sort ((a, b) => -DateTime.Compare (a.Date, b.Date));
		}
	}

	public class Commit
	{
		public string R;
		public DateTime Date;
		public string Author;

		public Commit ()
		{
		}

		public Commit (string r, DateTime date, string author)
		{
			this.R = r;
			this.Date = date;
			this.Author = author;
		}
	}

}
