using System;
using System.Collections.Generic;
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
using System.Windows.Threading;

using MoonlightDrtViewer.MonkeyWrench;

namespace MoonlightDrtViewer
{
	public static class Settings
	{
		#region API
		public static bool Has (string setting)
		{
			return IsolatedStorageSettings.ApplicationSettings.Contains (setting);
		}

		public static bool TryGet<T> (string setting, out T value) where T : class
		{
			return IsolatedStorageSettings.ApplicationSettings.TryGetValue<T> (setting, out value);
		}

		public static T Get<T> (string setting) where T : class
		{
			return (T) IsolatedStorageSettings.ApplicationSettings [setting];
		}

		public static void Save (string setting, object value)
		{
			IsolatedStorageSettings.ApplicationSettings [setting] = value;
			if (!Deployment.Current.Dispatcher.CheckAccess ()) {
				Deployment.Current.Dispatcher.BeginInvoke (() => IsolatedStorageSettings.ApplicationSettings.Save ());
			} else {
				IsolatedStorageSettings.ApplicationSettings.Save ();
			}
		}
		#endregion

		#region Properties
		public static IEnumerable<DBLane> GetSelectedLanes ()
		{
			string selected_repository = GetSelectedRepository ();
			IEnumerable<DBLane> lanes;

			if (selected_repository == null)
				return null;

			lanes = GetLanes ();

			if (lanes == null)
				return null;

			return lanes.Where (v => v.repository == selected_repository && !v.lane.StartsWith ("trunk-build"));
		}

		public static IEnumerable<DBHost> GetHosts ()
		{
			if (!Settings.Has ("hosts"))
				return null;
			return Settings.Get<GetHostsResponse> ("hosts").Hosts;
		}

		public static IEnumerable<DBLane> GetLanes ()
		{
			if (!Settings.Has ("lanes"))
				return null;
			return Settings.Get<GetLanesResponse> ("lanes").Lanes;
		}

		public static IEnumerable<DBRevision> GetRevisions ()
		{
			if (!Settings.Has ("revisions"))
				return null;
			return Settings.Get<GetRevisionsResponse> ("revisions").Revisions;
		}

		public static Commits GetCommits ()
		{
			if (!Settings.Has ("commits"))
				return null;
			return Settings.Get<Commits> ("commits");
		}

		public static string GetSelectedRepository ()
		{
			if (!Settings.Has ("selected-repository"))
				return null;
			return Settings.Get<string> ("selected-repository");
		}
		#endregion
	}
}
