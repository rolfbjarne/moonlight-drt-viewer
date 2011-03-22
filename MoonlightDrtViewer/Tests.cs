using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Xml;

using MoonlightDrtViewer.MonkeyWrench;

namespace MoonlightDrtViewer
{
	public static class Tests
	{
		public static List<Test> tests = new List<Test> ();
		public static HashSet<string> areas = new HashSet<string> (StringComparer.OrdinalIgnoreCase);
		public static List<Test> shown_tests = new List<Test> ();

		public static Test FindOrCreateTest (string id, string title, string [] areas)
		{
			Test result = null;
			for (int i = 0; i < tests.Count; i++) {
				if (tests [i].ID == id) {
					result = tests [i];
					break;
				}
			}

			if (result == null) {
				result = new Test ();
				result.ID = id;
				result.Title = title;
				result.Areas = areas;

				bool inserted = false;
				for (int i = 0; i < tests.Count; i++) {
					if (string.CompareOrdinal (tests [i].ID, id) > 0) {
						tests.Insert (i, result);
						inserted = true;
						break;
					}
				}
				if (!inserted)
					tests.Add (result);
			}
			return result;
		}

		private static void CompressFile (string md5)
		{
			try {
				byte [] buffer = new byte [1024];
				int read;

				using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication ()) {
					using (IsolatedStorageFileStream iso_in = new IsolatedStorageFileStream (md5, FileMode.Open, FileAccess.Read, FileShare.Read, iso)) {
						using (IsolatedStorageFileStream iso_out = iso.CreateFile (md5 + ".bz")) {
							using (ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream zip_out = new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream (iso_out)) {
								while ((read = iso_in.Read (buffer, 0, buffer.Length)) > 0) {
									zip_out.Write (buffer, 0, read);
								}
								zip_out.Flush ();
							}
						}
					}
				}
				IsolatedStorageFile.GetUserStoreForApplication ().DeleteFile (md5);
				IsolatedStorageFile.GetUserStoreForApplication ().MoveFile (md5 + ".bz", md5);
				MainPage.Log ("Compressed {0}", md5);
			} catch (Exception ex) {
				MainPage.Log ("Exception while compressing {0}: {1}", md5, ex);
			}
		}

		public static void ProcessFile (string md5, int revisionwork_id, int lane_id, int host_id, int revision_id, int workfile_id, int command_id)
		{
			List<string> areas = new List<string> ();
			try {
				bool is_compressed = false;
				byte [] buffer = new byte [1024];
				try {
					using (IsolatedStorageFileStream str = new IsolatedStorageFileStream (md5, FileMode.Open, FileAccess.Read, FileShare.Read, IsolatedStorageFile.GetUserStoreForApplication ())) {
						using (ICSharpCode.SharpZipLib.BZip2.BZip2InputStream zip_in = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream (str)) {
							zip_in.Read (buffer, 0, buffer.Length);
							is_compressed = true;
						}
					}
				} catch (Exception) {
					System.Threading.ThreadPool.QueueUserWorkItem ((object obj) => CompressFile (md5));
				}

				using (IsolatedStorageFileStream iso_str = new IsolatedStorageFileStream (md5, FileMode.Open, FileAccess.Read, FileShare.Read, IsolatedStorageFile.GetUserStoreForApplication ())) {
					Stream str;

					if (is_compressed) {
						str = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream (iso_str);
					} else {
						str = iso_str;
					}

					using (str) {
						XmlReaderSettings settings = new XmlReaderSettings ();
						settings.IgnoreComments = true;
						settings.IgnoreProcessingInstructions = true;
						settings.IgnoreWhitespace = true;
						XmlReader reader = XmlReader.Create (str, settings);
						while (true) {
							/* Go to next test definition */
							if (!reader.Read ())
								break;
							if (!(reader.Name == "TestDefinition" && reader.NodeType == XmlNodeType.Element && reader.Depth == 3)) {
								continue;
							}

							/* We're not at a test definition */
							string id = reader.GetAttribute ("ID");
							string title = null;
							string status = null;
							string known_failure = null;
							bool crashed = false;

							/* Read parameters until we reach end of */
							string params_name = null;
							areas.Clear ();

							while (reader.Read ()) {
								if (reader.Name == "TestDefinition" && reader.NodeType == XmlNodeType.EndElement && reader.Depth == 3) {
									/* End of test definition */
									break;
								}

								if (reader.Name == "Parameter" && reader.NodeType == XmlNodeType.Element && reader.Depth == 4) {
									// TestDefinition/Parameter[@Value]
									if (reader ["Name"] == "Title") {
										title = reader ["Value"];
									} else if (reader ["Name"] == "TestedFeatureAreas") {
										params_name = "TestedFeatureAreas";
										if (!string.IsNullOrEmpty (reader ["Value"]))
											areas.Add (reader ["Value"]);
									} else {
										params_name = null;
									}
								}

								if (reader.Name == "Parameter" && reader.NodeType == XmlNodeType.EndElement && reader.Depth == 4) {
									params_name = null;
								}

								if (reader.Name == "Parameters" && reader.Depth == 4) {
									// TestDefinition/Parameters["Name"]
									if (reader.NodeType == XmlNodeType.Element) {
										params_name = reader ["Name"];
									} else if (reader.NodeType == XmlNodeType.EndElement) {
										params_name = null;
									}
								}

								if (reader.Name == "Parameter" && reader.NodeType == XmlNodeType.Element && reader.Depth == 5) {
									if (params_name == "Result") {
										if (reader ["Name"] == "Status") {
											// TestDefinition/Parameters[@Name="Result"]/Status
											status = reader ["Value"];
										} else if (reader ["Name"] == "Crashed") {
											crashed = bool.Parse (reader ["Value"]);
										}
									} else if (params_name == "Moonlight") {
										if (reader ["Name"] == "KnownFailure") {
											// TestDefinition/Parameters[@Name="Moonlight"]/KnownFailure
											known_failure = reader ["Value"];
										}
									}
								}

								if (reader.Name == "Value" && reader.NodeType == XmlNodeType.Element && reader.Depth == 5) {
									if (params_name == "TestedFeatureAreas") {
										areas.Add (reader ["Name"]);
									}
								}
							}

							foreach (string a in areas)
								Tests.areas.Add (a);

							FindOrCreateTest (id, title, areas.ToArray ()).AddResult (revisionwork_id, lane_id, host_id, revision_id, status, known_failure, crashed, workfile_id, command_id);
							Console.WriteLine ("Found test '{0}' with Title: '{1}' Status: '{2}' KnownFailure: '{3}'", id, title, status, known_failure);
						}
					}
				}
			} catch (Exception ex) {
				MainPage.Log ("ProcessFile ({0}) failed with: {1}", md5, ex);
				// we try to delete any file we can't process
				// this should cause it to be re-downloaded next time
				MainPage.TryDeleteISOFile (md5);
			}
		}
	}

	public class Test
	{
		public string ID;
		public string Title;
		public string [] Areas;
		public List<TestRevision> Revisions = new List<TestRevision> ();

		public TestRevision FindRevision (string commit)
		{
			for (int i = 0; i < Revisions.Count; i++) {
				if (Revisions [i].Revision == commit)
					return Revisions [i];
			}
			return null;
		}

		public void AddResult (int revisionwork_id, int lane_id, int host_id, int revision_id, string status, string known_failure, bool crashed, int workfile_id, int command_id)
		{
			TestRevision r;
			DBRevision dbr = Settings.GetRevisions ().SingleOrDefault (rev => rev.id == revision_id);
			string revision = dbr == null ? revision_id.ToString () : dbr.revision;

			r = FindRevision (revision);
			if (r == null) {
				r = new TestRevision ();
				r.RevisionId = revision_id;
				r.Revision = revision;
				Revisions.Add (r);
			}

			// Remove any existing results we've already parsed
			List<TestResult> to_be_removed = new List<TestResult> (r.Results.Where (tres => tres.WorkFileId == workfile_id));
			foreach (TestResult tres in to_be_removed ) {
				r.Results.Remove (tres);
			}

			TestResult tr = new TestResult ();
			tr.Status = status;
			tr.KnownFailure = known_failure;
			tr.LaneId = lane_id;
			tr.HostId = host_id;
			tr.RevisionId = revision_id;
			tr.RevisionWorkId = revisionwork_id;
			tr.Crashed = crashed;
			tr.WorkFileId = workfile_id;
			tr.CommandId = command_id;
			r.Results.Add (tr);
		}
	}

	public class TestRevision
	{
		public int RevisionId;
		public string Revision;
		public List<TestResult> Results = new List<TestResult> ();

		public string GetComputedStatus ()
		{
			string result = "NotRun";
			foreach (TestResult tr in Results) {
				string status = tr.GetComputedStatus ();
				if (status == "Failed")
					result = status;
				else if (status == "KnownFailure" && result != "Failed")
					result = status;
				else if (result == "NotRun" && tr.Status != null)
					result = tr.Status;
			}
			return result;
		}

		public void CountResults (out int crashed, out int failed, out int known_failures, out int passed, out int unknown, out bool mixed_results)
		{
			crashed = 0;
			failed = 0;
			known_failures = 0;
			passed = 0;
			unknown = 0;
			mixed_results = false;

			foreach (TestResult res in Results) {
				if (res.Crashed) {
					crashed++;
					mixed_results = crashed != Results.Count;
				} else {
					switch (res.Status) {
					case "Passed":
						passed++;
						mixed_results = passed != Results.Count;
						break;
					case "Failed":
						if (string.IsNullOrEmpty (res.KnownFailure)) {
							failed++;
							mixed_results = failed != Results.Count;
						} else {
							known_failures++;
							mixed_results = known_failures != Results.Count;
						}
						break;
					default:
						unknown++;
						break;
					}
				}
			}
		}

	}

	public class TestResult
	{
		public int RevisionWorkId;
		public int LaneId;
		public int HostId;
		public int RevisionId;
		public int WorkFileId;
		public int CommandId;
		public string Status;
		public string KnownFailure;
		public bool Crashed;

		public string GetComputedStatus ()
		{
			return (!string.IsNullOrEmpty (KnownFailure) && Status == "Failed") ? "KnownFailure" : Status;
		}
	}
}
