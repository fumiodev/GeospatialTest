using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.Samples.Geospatial;
using Cysharp.Threading.Tasks;

/// <summary>
/// Controller for Geospatial sample.
/// </summary>
public class GeoSpatialControllerWithPlayback : MonoBehaviour
{
	[Header("AR Components")]
	[SerializeField] private ARSessionOrigin SessionOrigin; // The ARSessionOrigin used in the sample.
	[SerializeField] private ARSession Session; // The ARSession used in the sample.
	[SerializeField] private ARAnchorManager AnchorManager; // The ARAnchorManager used in the sample.
	[SerializeField] private AREarthManager EarthManager; // The AREarthManager used in the sample.
	[SerializeField] private ARCoreExtensions ARCoreExtensions; // The ARCoreExtensions used in the sample.

	[Header("UI Elements")]
	[SerializeField] private Button GetStartedButton;
	[SerializeField] private Button LearnMoreButton;
	[SerializeField] private Button ClearAllButton; // UI element for clearing all anchors, including history.
	[SerializeField] private Button SetAnchorButton; // UI element for adding a new anchor at current location.
	[SerializeField] private GameObject GeospatialPrefab; // A 3D object that presents an Geospatial Anchor.
	[SerializeField] private GameObject PrivacyPromptCanvas; // UI element showing privacy prompt.
	[SerializeField] private GameObject ARViewCanvas; // UI element containing all AR view contents.
	[SerializeField] private GameObject InfoPanel; // UI element to display information at runtime.
	[SerializeField] private Text InfoText; // Text displaying <see cref="GeospatialPose"/> information at runtime.
	[SerializeField] private Text SnackBarText; // Text displaying in a snack bar at the bottom of the screen.
	[SerializeField] private Text DebugText; // Text displaying debug information, only activated in debug build.

	// Help message shows while localizing.
	private const string LocalizingMessage = "Localizing your device to set anchor.";

	/// <summary>
	/// Help message shows when <see cref="AREarthManager.EarthTrackingState"/> is not tracking
	/// or the pose accuracies are beyond thresholds.
	/// </summary>
	private const string LocalizationInstructionMessage =
		"Point your camera at buildings, stores, and signs near you.";

	// Help message shows when location fails or hits timeout.
	private const string LocalizationFailureMessage =
		"Localization not possible.\n" +
		"Close and open the app to restart the session.";

	// Help message shows when location success.
	private const string LocalizationSuccessMessage = "Localization completed.";

	// The timeout period waiting for localization to be completed.
	private const float TimeoutSeconds = 180;

	// Indicates how long a information text will display on the screen before terminating.
	private const float ErrorDisplaySeconds = 3;

	// The key name used in PlayerPrefs which indicates whether the privacy prompt has
	// displayed at least one time.
	private const string HasDisplayedPrivacyPromptKey = "HasDisplayedGeospatialPrivacyPrompt";

	// The key name used in PlayerPrefs which stores geospatial anchor history data.
	// The earliest one will be deleted once it hits storage limit.
	private const string PersistentGeospatialAnchorsStorageKey = "PersistentGeospatialAnchors";

	// The limitation of how many Geospatial Anchors can be stored in local storage.
	private const int StorageLimit = 5;

	// Accuracy threshold for heading degree that can be treated as localization completed.
	private const double HeadingAccuracyThreshold = 25;

	// Accuracy threshold for altitude and longitude that can be treated as localization completed.
	private const double HorizontalAccuracyThreshold = 20;

	private bool m_IsInARView = false;
	private bool m_IsReturning = false;
	private bool m_IsLocalizing = false;
	private bool m_EnablingGeospatial = false;
	private bool m_ShouldResolvingHistory = false;
	private float m_LocalizationPassedTime = 0f;
	private float m_ConfigurePrepareTime = 3f;
	private GeospatialAnchorHistoryCollection m_HistoryCollection = null;
	private List<GameObject> m_AnchorObjects = new List<GameObject>();

	private void Awake()
	{
		// Lock screen to portrait.
		Screen.autorotateToLandscapeLeft = false;
		Screen.autorotateToLandscapeRight = false;
		Screen.autorotateToPortraitUpsideDown = false;
		Screen.orientation = ScreenOrientation.Portrait;

		// Enable geospatial sample to target 60fps camera capture frame rate
		// on supported devices.
		// Note, Application.targetFrameRate is ignored when QualitySettings.vSyncCount != 0.
		Application.targetFrameRate = 60;

		if (SessionOrigin == null)
		{
			Debug.LogError("Cannot find ARSessionOrigin.");
		}

		if (Session == null)
		{
			Debug.LogError("Cannot find ARSession.");
		}

		if (ARCoreExtensions == null)
		{
			Debug.LogError("Cannot find ARCoreExtensions.");
		}

		// Set OnClick
		GetStartedButton.onClick.AddListener(OnGetStartedClicked);
		LearnMoreButton.onClick.AddListener(OnLearnMoreClicked);
		ClearAllButton.onClick.AddListener(OnClearAllClicked);
		SetAnchorButton.onClick.AddListener(OnSetAnchorClicked);
	}

	private void OnEnable()
	{
		SwitchToARView(PlayerPrefs.HasKey(HasDisplayedPrivacyPromptKey));

		m_IsReturning = false;
		m_EnablingGeospatial = false;
		InfoPanel.SetActive(false);
		SetAnchorButton.gameObject.SetActive(false);
		ClearAllButton.gameObject.SetActive(false);
		DebugText.gameObject.SetActive(Debug.isDebugBuild && EarthManager != null);

		m_LocalizationPassedTime = 0f;
		m_IsLocalizing = true;
		SnackBarText.text = LocalizingMessage;

#if UNITY_IOS
		Debug.Log("Start location services.");
		Input.location.Start();
#endif
		LoadGeospatialAnchorHistory();
		m_ShouldResolvingHistory = m_HistoryCollection.Collection.Count > 0;
	}

	private void OnDisable()
	{
#if UNITY_IOS
		Debug.Log("Stop location services.");
		Input.location.Stop();
#endif
		foreach (var anchor in m_AnchorObjects)
		{
			Destroy(anchor);
		}

		m_AnchorObjects.Clear();
		SaveGeospatialAnchorHistory();
	}

	private void Update()
	{
		if (!m_IsInARView)
		{
			return;
		}

		UpdateDebugInfo();

		// Check session error status.
		LifecycleUpdate();
		if (m_IsReturning)
		{
			return;
		}

		if (ARSession.state != ARSessionState.SessionInitializing &&
			ARSession.state != ARSessionState.SessionTracking)
		{
			return;
		}

		// Check feature support and enable Geospatial API when it's supported.
		var featureSupport = EarthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);
		switch (featureSupport)
		{
			case FeatureSupported.Unknown:
				return;
			case FeatureSupported.Unsupported:
				QuitAppWithReasonAsync("Geospatial API is not supported by this devices.").Forget();
				return;
			case FeatureSupported.Supported:
				if (ARCoreExtensions.ARCoreExtensionsConfig.GeospatialMode ==
					GeospatialMode.Disabled)
				{
					Debug.Log("Geospatial sample switched to GeospatialMode.Enabled.");
					ARCoreExtensions.ARCoreExtensionsConfig.GeospatialMode =
						GeospatialMode.Enabled;
					m_ConfigurePrepareTime = 3.0f;
					m_EnablingGeospatial = true;
					return;
				}
				break;
		}

		// Waiting for new configuration taking effect.
		if (m_EnablingGeospatial)
		{
			m_ConfigurePrepareTime -= Time.deltaTime;
			if (m_ConfigurePrepareTime < 0)
			{
				m_EnablingGeospatial = false;
			}
			else
			{
				return;
			}
		}

		// Check earth state.
		var earthState = EarthManager.EarthState;
		if (earthState != EarthState.Enabled)
		{
			QuitAppWithReasonAsync($"Geospatial sample encountered an EarthState error: {earthState}").Forget();
			return;
		}

		// Check earth localization.
#if UNITY_IOS
		bool isSessionReady = ARSession.state == ARSessionState.SessionTracking &&
			Input.location.status == LocationServiceStatus.Running;
#else
		bool isSessionReady = ARSession.state == ARSessionState.SessionTracking;
#endif
		var earthTrackingState = EarthManager.EarthTrackingState;
		var pose = earthTrackingState == TrackingState.Tracking ?
			EarthManager.CameraGeospatialPose : new GeospatialPose();
		if (!isSessionReady || earthTrackingState != TrackingState.Tracking ||
			pose.HeadingAccuracy > HeadingAccuracyThreshold ||
			pose.HorizontalAccuracy > HorizontalAccuracyThreshold)
		{
			// Lost localization during the session.
			if (!m_IsLocalizing)
			{
				m_IsLocalizing = true;
				m_LocalizationPassedTime = 0f;
				SetAnchorButton.gameObject.SetActive(false);
				ClearAllButton.gameObject.SetActive(false);
				foreach (var go in m_AnchorObjects)
				{
					go.SetActive(false);
				}
			}

			if (m_LocalizationPassedTime > TimeoutSeconds)
			{
				Debug.LogError("Geospatial sample localization passed timeout.");
				QuitAppWithReasonAsync(LocalizationFailureMessage).Forget();
			}
			else
			{
				m_LocalizationPassedTime += Time.deltaTime;
				SnackBarText.text = LocalizationInstructionMessage;
			}
		}
		else if (m_IsLocalizing)
		{
			// Finished localization.
			m_IsLocalizing = false;
			m_LocalizationPassedTime = 0f;
			SetAnchorButton.gameObject.SetActive(true);
			ClearAllButton.gameObject.SetActive(m_AnchorObjects.Count > 0);
			SnackBarText.text = LocalizationSuccessMessage;
			foreach (var go in m_AnchorObjects)
			{
				go.SetActive(true);
			}

			ResolveHistory();
		}

		InfoPanel.SetActive(true);
		if (earthTrackingState == TrackingState.Tracking)
		{
			var sb = new System.Text.StringBuilder();
			sb.AppendFormat("Latitude/Longitude: {0}°, {1}°", pose.Latitude.ToString("F6"), pose.Longitude.ToString("F6"));
			sb.AppendLine();
			sb.AppendFormat("Horizontal Accuracy: {0}m", pose.HorizontalAccuracy.ToString("F6"));
			sb.AppendLine();
			sb.AppendFormat("Altitude: {0}m", pose.Altitude.ToString("F2"));
			sb.AppendLine();
			sb.AppendFormat("Vertical Accuracy: {0}m", pose.VerticalAccuracy.ToString("F2"));
			sb.AppendLine();
			sb.AppendFormat("Heading: {0}°", pose.Heading.ToString("F1"));
			sb.AppendLine();
			sb.AppendFormat("Heading Accuracy: {0}°", pose.HeadingAccuracy.ToString("F1"));
			sb.AppendLine();
			InfoText.text = sb.ToString();
		}
		else
		{
			InfoText.text = "GEOSPATIAL POSE: not tracking";
		}
	}

	#region On click method
	// Callback handling "Get Started" button click event in Privacy Prompt.
	private void OnGetStartedClicked()
	{
		PlayerPrefs.SetInt(HasDisplayedPrivacyPromptKey, 1);
		PlayerPrefs.Save();
		SwitchToARView(true);
	}

	// Callback handling "Learn More" Button click event in Privacy Prompt.
	private void OnLearnMoreClicked()
	{
		Application.OpenURL("https://developers.google.com/ar/data-privacy");
	}

	// Callback handling "Clear All" button click event in AR View.
	private void OnClearAllClicked()
	{
		foreach (var anchor in m_AnchorObjects)
		{
			Destroy(anchor);
		}

		m_AnchorObjects.Clear();
		m_HistoryCollection.Collection.Clear();
		SnackBarText.text = "Anchor(s) cleared!";
		ClearAllButton.gameObject.SetActive(false);
		SaveGeospatialAnchorHistory();
	}

	// Callback handling "Set Anchor" button click event in AR View.
	private void OnSetAnchorClicked()
	{
		var pose = EarthManager.CameraGeospatialPose;
		GeospatialAnchorHistory history = new GeospatialAnchorHistory(
			pose.Latitude, pose.Longitude, pose.Altitude, pose.Heading);
		if (PlaceGeospatialAnchor(history))
		{
			m_HistoryCollection.Collection.Add(history);
			SnackBarText.text = $"{m_AnchorObjects.Count} Anchor(s) Set!";
		}
		else
		{
			SnackBarText.text = "Failed to set an anchor!";
		}

		ClearAllButton.gameObject.SetActive(m_HistoryCollection.Collection.Count > 0);
		SaveGeospatialAnchorHistory();
	}
	#endregion // On click method

	private bool PlaceGeospatialAnchor(GeospatialAnchorHistory history)
	{
		Quaternion quaternion =
			Quaternion.AngleAxis(180f - (float)history.Heading, Vector3.up);
		var anchor = AnchorManager.AddAnchor(
			history.Latitude, history.Longitude, history.Altitude, quaternion);
		if (anchor != null)
		{
			GameObject anchorGO = Instantiate(GeospatialPrefab, anchor.transform);
			m_AnchorObjects.Add(anchorGO);
			return true;
		}

		return false;
	}

	private void ResolveHistory()
	{
		if (!m_ShouldResolvingHistory)
		{
			return;
		}

		m_ShouldResolvingHistory = false;
		foreach (var history in m_HistoryCollection.Collection)
		{
			PlaceGeospatialAnchor(history);
		}

		ClearAllButton.gameObject.SetActive(m_HistoryCollection.Collection.Count > 0);
		SnackBarText.text = string.Format("{0} anchor(s) set from history.",
			m_AnchorObjects.Count);
	}

	private void LoadGeospatialAnchorHistory()
	{
		if (PlayerPrefs.HasKey(PersistentGeospatialAnchorsStorageKey))
		{
			m_HistoryCollection = JsonUtility.FromJson<GeospatialAnchorHistoryCollection>(
				PlayerPrefs.GetString(PersistentGeospatialAnchorsStorageKey));

			// Remove all records created more than 24 hours and update stored history.
			DateTime current = DateTime.Now;
			m_HistoryCollection.Collection.RemoveAll(
				data => current.Subtract(data.CreatedTime).Days > 0);
			PlayerPrefs.SetString(PersistentGeospatialAnchorsStorageKey,
				JsonUtility.ToJson(m_HistoryCollection));
			PlayerPrefs.Save();
		}
		else
		{
			m_HistoryCollection = new GeospatialAnchorHistoryCollection();
		}
	}

	private void SaveGeospatialAnchorHistory()
	{
		// Sort the data from latest record to earliest record.
		m_HistoryCollection.Collection.Sort((left, right) =>
			right.CreatedTime.CompareTo(left.CreatedTime));

		// Remove the earliest data if the capacity exceeds storage limit.
		if (m_HistoryCollection.Collection.Count > StorageLimit)
		{
			m_HistoryCollection.Collection.RemoveRange(
				StorageLimit, m_HistoryCollection.Collection.Count - StorageLimit);
		}

		PlayerPrefs.SetString(
			PersistentGeospatialAnchorsStorageKey, JsonUtility.ToJson(m_HistoryCollection));
		PlayerPrefs.Save();
	}

	private void SwitchToARView(bool enable)
	{
		m_IsInARView = enable;
		SessionOrigin.gameObject.SetActive(enable);
		Session.gameObject.SetActive(enable);
		ARCoreExtensions.gameObject.SetActive(enable);
		ARViewCanvas.SetActive(enable);
		PrivacyPromptCanvas.SetActive(!enable);
	}

	private void LifecycleUpdate()
	{
		// Pressing 'back' button quits the app.
		if (Input.GetKeyUp(KeyCode.Escape))
		{
			Application.Quit();
		}

		if (m_IsReturning)
		{
			return;
		}

		// Only allow the screen to sleep when not tracking.
		var sleepTimeout = SleepTimeout.NeverSleep;
		if (ARSession.state != ARSessionState.SessionTracking)
		{
			sleepTimeout = SleepTimeout.SystemSetting;
		}

		Screen.sleepTimeout = sleepTimeout;

		// Quit the app if ARSession is in an error status.
		string returningReason = string.Empty;
		if (ARSession.state != ARSessionState.CheckingAvailability &&
			ARSession.state != ARSessionState.Ready &&
			ARSession.state != ARSessionState.SessionInitializing &&
			ARSession.state != ARSessionState.SessionTracking)
		{
			returningReason = string.Format(
				"Geospatial sample encountered an ARSession error state {0}.\n" +
				"Please start the app again.",
				ARSession.state);
		}
#if UNITY_IOS
		else if (Input.location.status == LocationServiceStatus.Failed)
		{
			returningReason =
				"Geospatial sample failed to start location service.\n" +
				"Please start the app again and grant precise location permission.";
		}
#endif
		else if (SessionOrigin == null || Session == null || ARCoreExtensions == null)
		{
			returningReason = string.Format(
				"Geospatial sample failed with missing AR Components.");
		}

		QuitAppWithReasonAsync(returningReason).Forget();
	}

	private async UniTask QuitAppWithReasonAsync(string reason)
	{
		if (string.IsNullOrEmpty(reason))
		{
			return;
		}

		SetAnchorButton.gameObject.SetActive(false);
		ClearAllButton.gameObject.SetActive(false);
		InfoPanel.SetActive(false);

		Debug.LogError(reason);
		SnackBarText.text = reason;
		m_IsReturning = true;
		await UniTask.Delay((int)ErrorDisplaySeconds * 1000);
		Application.Quit();
	}

	private void UpdateDebugInfo()
	{
		if (!Debug.isDebugBuild || EarthManager == null)
		{
			return;
		}

		var pose = EarthManager.EarthState == EarthState.Enabled &&
			EarthManager.EarthTrackingState == TrackingState.Tracking ?
			EarthManager.CameraGeospatialPose : new GeospatialPose();
		var supported = EarthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);
		var sb = new System.Text.StringBuilder();
		sb.Append($"IsReturning: {m_IsReturning}"); sb.AppendLine();
		sb.Append($"IsLocalizing: {m_IsLocalizing}\n"); sb.AppendLine();
		sb.Append($"SessionState: {ARSession.state}\n"); sb.AppendLine();
		sb.Append($"LocationServiceStatus: {Input.location.status}\n"); sb.AppendLine();
		sb.Append($"FeatureSupported: {supported}\n"); sb.AppendLine();
		sb.Append($"EarthState: {EarthManager.EarthState}\n"); sb.AppendLine();
		sb.Append($"EarthTrackingState: {EarthManager.EarthTrackingState}\n"); sb.AppendLine();
		sb.Append($"  LAT/LNG: {pose.Latitude:F6}, {pose.Longitude:F6}\n"); sb.AppendLine();
		sb.Append($"  HorizontalAcc: {pose.HorizontalAccuracy:F6}\n"); sb.AppendLine();
		sb.Append($"  ALT: {pose.Altitude:F2}\n"); sb.AppendLine();
		sb.Append($"  VerticalAcc: {pose.VerticalAccuracy:F2}\n"); sb.AppendLine();
		sb.Append($"  Heading: {pose.Heading:F2}\n"); sb.AppendLine();
		sb.Append($"  HeadingAcc: {pose.HeadingAccuracy:F2}"); sb.AppendLine();

		DebugText.text = sb.ToString();
	}
}

