using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using System;
using Object = UnityEngine.Object;

namespace DevLocker.Animations
{
	/// <summary>
	/// Better way of editing and reviewing animations in an FBX.
	/// - Can edit multiple animations from different FBXs without the needing to apply settings.
	/// - Shows time line of events and each event separately for easier edit.
	/// - Copy / Paste events.
	/// - FBX Animations overview that shows warnings and errors.
	/// - Easy way to search and navigate animations in fbx / folder. Search animations by events.
	/// Drawbacks:
	/// - Needs to be used with inspector preview to watch actual preview.
	/// </summary>
	[InitializeOnLoad]
	public class QuickClipEditor : EditorWindow
	{
		[MenuItem("Tools/Quick Clip Editor")]
		static void ShowWindow()
		{
			GetWindow<QuickClipEditor>("Quick Clip Editor");
		}

		private GameObject _currentModel;
		private string _modelClipsFilter = string.Empty;
		private string _modelEventsFilter = string.Empty;
		private Vector2 _modelClipsScrollPos;

		private AnimationClip _currentClip;
		private ModelImporterProxy _importer;
		private ModelImporterClipAnimation _importerClip;
		private List<AnimationEvent> _events;
		private Vector2 _eventsScrollPos;
		private AnimationEvent _selectedEvent;
		private AnimationEvent _selectedEventPending;
		private bool _showEventNumberParam = true;
		private bool _jumpBetweenFiles = false;


		private static List<ModelImporterProxy> _pendingChanges = new List<ModelImporterProxy>();
		private float _origLabelWidth = 0.0f;
		private Color _origBGColor;

		private bool _showClipInfo = true;
		private bool _showClipSettings = true;
		private bool _showClipEvents = true;

		private bool _editorLocked = false;

		private bool _forceRefresh = false;
		private static System.Object _copyData = null;

		// Sometimes styles get corrupted. This forces them to never serialize and re-create every time assembly is reloaded.
		[NonSerialized] private static GUIStyle FOLDOUT_STYLE;
		[NonSerialized] private static GUIStyle EVENT_SLIDER_THUMB_STYLE;

		private readonly Color EVENT_COLOR = new Color(0.823f, 0.337f, 0.337f);
		private readonly Color EVENT_COLOR_SELECTED = new Color(0.243f, 0.373f, 0.588f);
		private readonly Color EVENT_BG_COLOR_SELECTED = new Color(0.7f, 0.7f, 1f);

		private readonly GUIContent SELECT_BUTTON = new GUIContent("S", "Select");

		private readonly GUIContent APPLY_ALL_BUTTON = new GUIContent("Apply All", "Apply changes to all modified models");
		private readonly GUIContent REVERT_ALL_BUTTON = new GUIContent("Revert All", "Revert all unapplied changes");
		private readonly GUIContent OVERVIEW_BUTTON = new GUIContent("Overview", "Select the main model asset for overview of all clips");
		private readonly GUIContent PREV_BUTTON = new GUIContent("Prev", "Select the previous clip in the model.\nCheck the checkbox to jump between files.");
		private readonly GUIContent NEXT_BUTTON = new GUIContent("Next", "Select the next clip in the model.\nCheck the checkbox to jump between files.");
		private readonly GUIContent JUMP_BETWEEN_FILES_TOGGLE = new GUIContent("", "Should next/previous jump between files.");

		private readonly GUIContent EVENT_BUTTON_COPY = new GUIContent("C", "Copy this event");
		private readonly GUIContent EVENT_BUTTON_PASTE = new GUIContent("P", "Paste on this event");
		private readonly GUIContent EVENT_BUTTON_SET_PREVIEW_TIME = new GUIContent("S", "Set current time from Inspector preview");
		private readonly GUIContent EVENT_BUTTON_REMOVE = new GUIContent("X", "Remove event");

		private readonly GUIContent EVENT_BUTTON_NUMBERS = new GUIContent("Numbers", "Show/hide number parameters of events");

		static QuickClipEditor()
		{
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
		{
			if (playModeStateChange == PlayModeStateChange.ExitingEditMode && _pendingChanges.Count > 0) {
				var proceed = EditorUtility.DisplayDialog("Quick Clip Editor", "Entering Play Mode will cause Quick Clip Editor to loose all its changes. Are you sure?", "Proceed", "Cancel");
				if (!proceed) {
					GetWindow<QuickClipEditor>().Focus();
					EditorApplication.isPlaying = false;
				}
			}
		}

		private void OnEnable()
		{
			Select(null);
		}

		private void OnSelectionChange()
		{
			Repaint();
		}

		private void OnInspectorUpdate()
		{
			// Updates the events preview time seeker.
			if (Selection.activeObject is AnimationClip) {
				Repaint();
			}
		}

		private static void RefreshAllInstances()
		{
			var windows = Resources.FindObjectsOfTypeAll<QuickClipEditor>();
			foreach (var window in windows) {
				window._forceRefresh = true;
				window.Repaint();
			}
		}

		// Hidden Unity function, used to draw lock and other buttons at the top of the window.
		private void ShowButton(Rect rect)
		{
			var lockButtonStyle = GUI.skin.GetStyle("IN LockButton");

			_editorLocked = GUI.Toggle(rect, _editorLocked, GUIContent.none, lockButtonStyle);


			rect.x -= rect.width - 2.0f;
			rect.y += -2.0f;

			if (GUI.Button(rect, "+", GUI.skin.label)) {
				QuickClipEditor window = CreateInstance<QuickClipEditor>();
				window.titleContent = titleContent;
				window.Show();
			}
		}



		void OnGUI()
		{
			if (_forceRefresh) {
				Refresh();
				GUI.FocusControl(null);
				_forceRefresh = false;
			}


			if (Selection.activeObject != _currentClip && Selection.activeObject != _currentModel && !_editorLocked) {
				Select(Selection.activeObject);
			}

			if (_eventDragStarted && Event.current.type == EventType.MouseUp) {
				_eventDragStarted = false;
			}

			_origLabelWidth = EditorGUIUtility.labelWidth;
			_origBGColor = GUI.backgroundColor;

			DrawSelectedClip();

			DrawChanges();

			if (_currentModel && _importer != null && _importer.ImportAnimation) {
				DrawModelOverview();
				return;
			}

			if (_currentClip) {
				DrawSection(ref _showClipInfo, DrawClipInfo, "Info");
			}

			if (_importerClip == null) {
				EditorGUILayout.HelpBox("Please select animation clip or some model.", MessageType.Error, true);
				return;
			}

			DrawSection(ref _showClipSettings, DrawClipSettings, "Settings", GetClipSettingsProblems(_importer, _importerClip));

			DrawSection(ref _showClipEvents, DrawClipEvents, "Events");

			if (GUI.changed) {
				if (!_pendingChanges.Any(proxy => proxy.assetPath == _importer.assetPath)) {
					_pendingChanges.Add(_importer);
				}

				//Debug.Log("Changed!");
			}

			// Check if another window is already changing this importer.
			if (!_pendingChanges.Contains(_importer) && _pendingChanges.Any(proxy => proxy.assetPath == _importer.assetPath)) {
				Refresh();
			}
		}

		private void Select(Object selection)
		{
			WriteBackSelectedEvents();

			var lastImporterPath = _importer != null ? _importer.assetPath : string.Empty;

			_currentClip = null;
			_currentModel = null;

			_importer = null;
			_importerClip = null;
			_events = new List<AnimationEvent>();

			_eventsScrollPos = Vector2.zero;

			GUI.FocusControl(null);


			// HACK: Model browsing mode.
			if (selection is GameObject && AssetDatabase.Contains(selection) && AssetDatabase.IsForeignAsset(selection) && AssetDatabase.IsMainAsset(selection)) {
				_currentModel = (GameObject)selection;

				_importer = GetModelImporter(AssetDatabase.GetAssetPath(_currentModel));

				// Using assetPath, instead of importer, because importers are lost if no changes were made.
				if (lastImporterPath != _importer.assetPath) {
					_modelClipsFilter = string.Empty;
					_modelEventsFilter = string.Empty;
				}

				return;
			}

			_currentClip = selection as AnimationClip;

			Refresh();


			// Using assetPath, instead of importer, because importers are lost if no changes were made.
			if (_importer != null && lastImporterPath != _importer.assetPath) {
				_modelClipsFilter = string.Empty;
				_modelEventsFilter = string.Empty;
			}
		}

		private void Refresh()
		{
			if (_currentClip == null)
				return;

			_selectedEvent = null;
			_eventDragStarted = false;

			_importer = GetModelImporter(AssetDatabase.GetAssetPath(_currentClip));

			if (_importer != null) {
				_importerClip = _importer.GetClipAnimation(_currentClip);
				_events = _importerClip != null ? new List<AnimationEvent>(_importerClip.events) : new List<AnimationEvent>();
				_events.Sort((a, b) => a.time.CompareTo(b.time));
			}
		}

		private ModelImporterProxy GetModelImporter(string assetPath)
		{
			var modelOrClip = AssetDatabase.LoadMainAssetAtPath(assetPath);

			if (modelOrClip is GameObject && PrefabUtility.IsPartOfModelPrefab(modelOrClip)) {

				var importer = _pendingChanges.FirstOrDefault(c => c.assetPath == assetPath);

				if (importer == null) {
					// NOTE: ModelImporter is not a copy. The same instance is returned every time.
					importer = new ModelImporterProxy((ModelImporter)AssetImporter.GetAtPath(assetPath));
				}

				// For freshly imported model, clipAnimations will be empty.
				if (importer.ClipAnimations.Count == 0) {
					importer.ResetClipsToDefaults();
				}

				return importer;

			} else if (modelOrClip is AnimationClip) {

				// Standalone clips work a bit differently from model animations.
				var importer = _pendingChanges.FirstOrDefault(c => c.assetPath == assetPath);
				if (importer == null) {
					importer = new ModelImporterProxy((AnimationClip) modelOrClip);
				}

				return importer;
			}

			return null;
		}

		private void WriteBackSelectedEvents()
		{
			if (_importerClip != null) {
				_events.Sort((a, b) => a.time.CompareTo(b.time));
				_importerClip.events = _events.ToArray();
			}
		}




		private void DrawSelectedClip()
		{
			var wasChanged = GUI.changed;

			EditorGUI.BeginDisabledGroup(!_editorLocked);
			var changedClip = EditorGUILayout.ObjectField("Selected Clip", _currentClip, typeof(AnimationClip), false) as AnimationClip;
			if (changedClip != null && changedClip != _currentClip) {
				Select(changedClip);
			}
			EditorGUI.EndDisabledGroup();

			GUI.changed = wasChanged;
		}

		private void DrawChanges()
		{
			var wasChanged = GUI.changed;

			EditorGUILayout.BeginHorizontal();

			var style = new GUIStyle(EditorStyles.boldLabel);
			style.alignment = TextAnchor.MiddleLeft;
			EditorGUILayout.LabelField("Changes pending: " + _pendingChanges.Count, style, GUILayout.Width(150.0f), GUILayout.Height(22.0f));


			EditorGUI.BeginDisabledGroup(_pendingChanges.Count == 0);
			{
				GUI.backgroundColor = (_pendingChanges.Count == 0) ? _origBGColor : Color.red;

				var clearChanges = false;
				if (GUILayout.Button(APPLY_ALL_BUTTON)) {

					WriteBackSelectedEvents();

					foreach (var importer in _pendingChanges) {
						importer.WriteChanges();
					}
					clearChanges = true;
				}
				GUI.backgroundColor = _origBGColor;

				if (GUILayout.Button(REVERT_ALL_BUTTON) && EditorUtility.DisplayDialog("Revert All", "All your changes will be reverted! Are you sure?", "Ok", "Cancel")) {
					clearChanges = true;
				}


				if (clearChanges) {
					_pendingChanges.Clear();

					RefreshAllInstances();
				}
			}
			EditorGUI.EndDisabledGroup();

			EditorGUI.BeginDisabledGroup(_currentClip == null || AssetDatabase.IsMainAsset(_currentClip));
			if (GUILayout.Button(OVERVIEW_BUTTON, GUILayout.ExpandWidth(false))) {
				var model = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(_currentClip));
				if (_editorLocked) {
					Select(model);
				} else {
					Selection.activeObject = model;
				}

				GUI.FocusControl(null);	// Prevents error on focused delayed field.

				GUIUtility.ExitGUI();
			}
			EditorGUI.EndDisabledGroup();

			
			EditorGUI.BeginDisabledGroup(_currentClip == null);
			if (GUILayout.Button(PREV_BUTTON, GUILayout.ExpandWidth(false))) {
				SelectNextOrPrev(false);
			}
			if (GUILayout.Button(NEXT_BUTTON, GUILayout.ExpandWidth(false))) {
				SelectNextOrPrev(true);
			}
			EditorGUI.EndDisabledGroup();

			_jumpBetweenFiles = EditorGUILayout.Toggle(JUMP_BETWEEN_FILES_TOGGLE, _jumpBetweenFiles, GUILayout.Width(16.0f));

			EditorGUILayout.EndHorizontal();

			GUI.changed = wasChanged;
		}

		private void SelectNextOrPrev(bool next)
		{
			var clips = _jumpBetweenFiles ? GetAllClipsInFolder(_currentClip) : GetAllClips(_currentClip);
			var index = clips.FindIndex(c => c == _currentClip);
			index = next ? (index + 1) % clips.Count : (index + clips.Count - 1) % clips.Count;
			var clip = clips[index];

			// TODO: Why is this needed? Can't I Select(clip) in any way? Check the other places this is used as well.
			if (_editorLocked) {
				Select(clip);
			} else {
				Selection.activeObject = clip;
			}
		}


		private void DrawSection(ref bool foldOut, Action sectionDrawingHandler, string title, List<ClipProblem> problems = null)
		{
			// OnEnable might not be ready with the EditorStyles on recompile, so this is a better place to initialize on demand.
			if (FOLDOUT_STYLE == null) {
				FOLDOUT_STYLE = new GUIStyle(EditorStyles.foldout);
				FOLDOUT_STYLE.fontStyle = FontStyle.Bold;
			}


			// Foldout sets GUI.change to true, but we want to ignore that.
			bool wasChanged = GUI.changed;
			foldOut = EditorGUILayout.Foldout(foldOut, title, FOLDOUT_STYLE);
			GUI.changed = wasChanged;
			if (foldOut) {
				EditorGUI.indentLevel++;
				sectionDrawingHandler();
				EditorGUI.indentLevel--;
			}

			if (problems != null) {
				DrawProblems(problems);
			}
		}

		private void DrawModelOverview()
		{
			// Doesn't work?!
			//AnimationClip[] clips = AnimationUtility.GetAnimationClips(_currentModel);
			var clips = GetAllClips(_currentModel);


			EditorGUILayout.LabelField("Model Animations Overview:", EditorStyles.boldLabel);

			EditorGUILayout.BeginHorizontal();
			_modelClipsFilter = EditorGUILayout.TextField("Clips Filter", _modelClipsFilter);
			if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
				_modelClipsFilter = string.Empty;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.BeginHorizontal();
			_modelEventsFilter = EditorGUILayout.TextField("Event Filter", _modelEventsFilter);
			if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
				_modelEventsFilter = string.Empty;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();

			EditorGUIUtility.labelWidth = 0;


			_modelClipsScrollPos = EditorGUILayout.BeginScrollView(_modelClipsScrollPos, false, false);

			foreach (var pair in _importer.ClipAnimationsSorted) {

				var importerClipName = pair.Key;
				var importerClip = pair.Value;

				if (!string.IsNullOrWhiteSpace(_modelClipsFilter) && importerClipName.IndexOf(_modelClipsFilter, StringComparison.OrdinalIgnoreCase) == -1)
					continue;

				if (!string.IsNullOrWhiteSpace(_modelEventsFilter)) {

					var show = importerClip.events.Any(ev => 
						ev.functionName.IndexOf(_modelEventsFilter, StringComparison.OrdinalIgnoreCase) != -1 ||
						ev.stringParameter.IndexOf(_modelEventsFilter, StringComparison.OrdinalIgnoreCase) != -1 ||
						(ev.objectReferenceParameter && ev.objectReferenceParameter.name.IndexOf(_modelEventsFilter, StringComparison.OrdinalIgnoreCase) != -1)
					);

					if (!show)
						continue;
				}

				var clip = clips.FirstOrDefault(c => c.name == importerClipName);

				EditorGUILayout.BeginHorizontal();


				//
				// Select button
				//

				EditorGUI.BeginDisabledGroup(!clip);    // Could be invalid importer clip entry.
				if (GUILayout.Button(SELECT_BUTTON, GUILayout.ExpandWidth(false), GUILayout.Height(EditorGUIUtility.singleLineHeight - 1))) {
					if (_editorLocked) {
						Select(clip);
					} else {
						Selection.activeObject = clip;
					}
				}
				EditorGUI.EndDisabledGroup();

				//
				// Clip field
				//
				var problems = GetClipSettingsProblems(_importer, importerClip);

				var problemColor = _origBGColor;
				if (problems.Any(p => p.Type == MessageType.Error)) {
					problemColor = Color.red;
				} else if (problems.Any(p => p.Type == MessageType.Warning)) {
					problemColor = Color.yellow;
				}

				GUI.backgroundColor = problemColor;


				if (clip != null) {
					EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false);
				} else {
					EditorGUILayout.TextField(problems.FirstOrDefault(p => p.Type == MessageType.Error).Message);
				}
				GUI.backgroundColor = _origBGColor;


				//
				// Events count
				//
				var eventsRect = EditorGUILayout.GetControlRect(false, GUILayout.Width(82.0f));

				var eventsLabelRect = eventsRect;
				eventsLabelRect.width = 52.0f;
				EditorGUI.LabelField(eventsLabelRect, "Events:");

				var eventsCountRect = eventsRect;
				eventsCountRect.x += eventsLabelRect.width;
				eventsCountRect.width = eventsRect.width - eventsLabelRect.width;
				EditorGUI.BeginDisabledGroup(true);
				EditorGUI.IntField(eventsCountRect, importerClip.events.Length);
				EditorGUI.EndDisabledGroup();

				EditorGUILayout.EndHorizontal();
			}


			EditorGUIUtility.labelWidth = _origLabelWidth;


			EditorGUILayout.EndScrollView();
		}

		private void DrawClipInfo()
		{
			if (_currentClip == null)
				return;

			EditorGUILayout.LabelField($"Length: {_currentClip.length.ToString("0.000")}; FPS: {_currentClip.frameRate}");
		}

		private void DrawClipSettings()
		{
			DrawClipNamesSetting();

			DrawRangeSettings();


			_importerClip.loop = EditorGUILayout.Toggle("Loop", _importerClip.loop);
			if (_importerClip.loop) {
				EditorGUI.indentLevel++;

				_importerClip.loopPose = EditorGUILayout.Toggle("Loop Pose", _importerClip.loopPose);
				_importerClip.cycleOffset = EditorGUILayout.FloatField("Cycle Offset", _importerClip.cycleOffset);

				EditorGUI.indentLevel--;
			}
		}

		private List<ClipProblem> GetClipSettingsProblems(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			var problems = new List<ClipProblem>();

			if (!AreNamesMatching(importer, importerClip)) {
				var problem = new ClipProblem(MessageType.Warning, $"Clip and take names are different.");
				problems.Add(problem);
			}

			if (!IsSourceTakeValid(importer, importerClip)) {
				var problem = new ClipProblem(MessageType.Error, $"Source take \"{importerClip.takeName}\" is missing (probably animation was deleted). Set a new source take!");
				problems.Add(problem);
			}

			if (!AreRangesMatching(importer, importerClip)) {
				var problem = new ClipProblem(MessageType.Warning, $"Clip start/end frame do not match the take ones.");
				problems.Add(problem);
			}

			if (!IsEndRangeValid(importer, importerClip)) {
				var problem = new ClipProblem(MessageType.Error, $"End frame is larger than the take one.");
				problems.Add(problem);
			}

			if (!EventsAreOK(importer, importerClip)) {
				var problem = new ClipProblem(MessageType.Warning, $"Event function name must not contain any white spaces.");
				problems.Add(problem);
			}

			return problems;
		}

		private void DrawClipNamesSetting()
		{
			EditorGUILayout.BeginHorizontal();
			{
				if (!AreNamesMatching(_importer, _importerClip)) {
					GUI.backgroundColor = Color.yellow;
				}


				var origIndent = EditorGUI.indentLevel;

				EditorGUILayout.LabelField("Name:", GUILayout.Width(60.0f));
				EditorGUI.indentLevel = 0;
				_importerClip.name = EditorGUILayout.TextField(_importerClip.name);

				GUI.backgroundColor = _origBGColor;
				EditorGUILayout.LabelField("Take:", GUILayout.Width(40.0f));

				// TODO: Make a modal dialog that lets you search and select source take (cause pop up is horrible with lots of anims).
				int takeIndex = Array.IndexOf(_importer.ImportedTakeNames, _importerClip.takeName);
				takeIndex = EditorGUILayout.Popup(takeIndex, _importer.ImportedTakeNames);
				if (takeIndex >= 0) {
					_importerClip.takeName = _importer.ImportedTakeNames[takeIndex];
				}

				EditorGUI.indentLevel = origIndent;

			}
			EditorGUILayout.EndHorizontal();
		}

		private void DrawRangeSettings()
		{
			var takeInfo = _importer.GetTakeInfo(_importerClip);
			int takeStartFrame;
			int takeStopFrame;
			_importer.GetTakeStartStopFrames(_importerClip, out takeStartFrame, out takeStopFrame);


			EditorGUILayout.BeginHorizontal();
			{
				if (!AreRangesMatching(_importer, _importerClip)) {
					GUI.backgroundColor = Color.yellow;
				}

				EditorGUIUtility.labelWidth = 125;
				var value = EditorGUILayout.IntField("Clip Frame Start:", Mathf.RoundToInt(_importerClip.firstFrame), GUILayout.ExpandWidth(false));
				_importerClip.firstFrame = Mathf.Min(Mathf.Max(0.0f, value), _importerClip.lastFrame);


				if (!IsEndRangeValid(_importer, _importerClip)) {
					GUI.backgroundColor = Color.red;
				}

				EditorGUIUtility.labelWidth = 45;
				value = EditorGUILayout.IntField("End:", Mathf.RoundToInt(_importerClip.lastFrame), GUILayout.ExpandWidth(false));

				if (_importerClip.lastFrame <= Mathf.Max(takeStopFrame, takeInfo.sampleRate)) {
					// Tries to imitate the original range UI. If the takeStopFrame is below 30 FPS (sampleRate), allowed range is between 0 and 30.
					_importerClip.lastFrame = Mathf.Min(Mathf.Max(_importerClip.firstFrame, value), Mathf.Max(takeStopFrame, takeInfo.sampleRate));
				} else {
					_importerClip.lastFrame = Mathf.Max(_importerClip.firstFrame, value);
				}

				GUI.backgroundColor = _origBGColor;
				if (GUILayout.Button("Reset", GUILayout.Height(EditorGUIUtility.singleLineHeight - 1), GUILayout.ExpandWidth(false))) {
					_importerClip.firstFrame = takeStartFrame;
					_importerClip.lastFrame = takeStopFrame;
				}
			}
			EditorGUILayout.EndHorizontal();


			EditorGUILayout.BeginHorizontal();
			{
				EditorGUI.BeginDisabledGroup(true);

				EditorGUIUtility.labelWidth = 125;
				EditorGUILayout.IntField("Take Frame Start:", takeStartFrame, GUILayout.ExpandWidth(false));

				EditorGUIUtility.labelWidth = 45;
				EditorGUILayout.IntField("End:", takeStopFrame, GUILayout.ExpandWidth(false));

				EditorGUI.EndDisabledGroup();
			}
			EditorGUILayout.EndHorizontal();

			EditorGUIUtility.labelWidth = _origLabelWidth;
		}

		private void DrawClipEvents()
		{
			float previewTime = -1.0f;
			if (Selection.activeObject == _currentClip) {
				GetPreviewTime(out previewTime);
			}

			DrawClipEventsTimeline(previewTime);

			DrawClipEventsEntries(previewTime);


			EditorGUILayout.BeginHorizontal();
			{
				if (GUILayout.Button("Add")) {
					var ev = new AnimationEvent() { functionName = "OnAnimEvent" };
					if (previewTime >= 0.0f) {
						ev.time = previewTime;
					}
					_events.Add(ev);
					_selectedEventPending = ev;

					// Other editor instances might be showing the same data.
					WriteBackSelectedEvents();
					RefreshAllInstances();
				}

				var wasChanged = GUI.changed;
				if (GUILayout.Button("Copy Events", GUILayout.ExpandWidth(false))) {
					_copyData = _events.Select(CloneEvent).ToList();
				}
				GUI.changed = wasChanged;

				EditorGUI.BeginDisabledGroup(!(_copyData is List<AnimationEvent>));
				if (GUILayout.Button("Paste Events", GUILayout.ExpandWidth(false))) {
					var pasteEvents = (List<AnimationEvent>)_copyData;

					_events.AddRange(pasteEvents);
				}
				EditorGUI.EndDisabledGroup();


				wasChanged = GUI.changed;
				if (GUILayout.Button(EVENT_BUTTON_NUMBERS, GUILayout.ExpandWidth(false))) {
					_showEventNumberParam = !_showEventNumberParam;
				}
				GUI.changed = wasChanged;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUIUtility.labelWidth = _origLabelWidth;
		}


		private bool _eventDragStarted = false;
		private void DrawClipEventsTimeline(float previewTime)
		{
			float INDENT = 16.0f * EditorGUI.indentLevel;


			EditorGUIUtility.labelWidth = 0;
			var origIndent = EditorGUI.indentLevel;
			EditorGUI.indentLevel = 0;

			var workingRect = EditorGUILayout.GetControlRect(false);
			workingRect.x += INDENT + 62.0f;		// Try to align with the event sliders.
			workingRect.width -= INDENT + 124.0f;	// Try to align with the event sliders.


			var timelineRect = workingRect;
			var seekerRect = new Rect() { y = timelineRect.y, height = timelineRect.height };
			seekerRect.width = 3.0f;


			GUI.Box(timelineRect, "");

			//
			// Time hints
			//
			var timeHintRect = seekerRect;
			timeHintRect.width = 1.0f;
			timeHintRect.height *= 0.75f;
			timeHintRect.y += timelineRect.height - timeHintRect.height;
			var timeHintColor = Color.grey;
			timeHintColor.a = 0.6f;

			var timeHintTextStyle = new GUIStyle(EditorStyles.miniLabel);
			timeHintTextStyle.normal.textColor = timeHintColor;
			var timeHintTextRect = seekerRect;
			timeHintTextRect.width = 28.0f;

			for (float f = 0.0f; f < 0.99f; f += 0.25f) {
				timeHintRect.x = timelineRect.x;
				timeHintRect.x += timelineRect.width * f;
				timeHintRect.x -= 1.0f;   // Center

				if (f > 0.0f) {
					EditorGUI.DrawRect(timeHintRect, timeHintColor);
				}
				timeHintTextRect.x = timeHintRect.x + 3.0f;
				EditorGUI.LabelField(timeHintTextRect, f.ToString("0.00"), timeHintTextStyle);
			}

			timeHintTextRect.x = timelineRect.x + timelineRect.width - timeHintTextRect.width;
			if (timeHintTextRect.x > timelineRect.x + timelineRect.width * 0.75f + timeHintTextRect.width + 6.0f) {
				EditorGUI.LabelField(timeHintTextRect, 1.0f.ToString("0.00"), timeHintTextStyle);
			}

			//
			// Event seekers
			//
			var clickEvent = Event.current.type == EventType.MouseDown;
			foreach (var ev in _events) {
				seekerRect.x = timelineRect.x;
				seekerRect.x += timelineRect.width * ev.time;
				seekerRect.x -= 1.0f;   // Center

				EditorGUI.DrawRect(seekerRect, ev != _selectedEvent ? EVENT_COLOR : EVENT_COLOR_SELECTED);

				HandleEventSeekerDrag(ev, seekerRect, timelineRect);
			}

			// If clicked in the timeline, but not on an event, de-select.
			if (clickEvent && Event.current.type != EventType.Used && timelineRect.Contains(Event.current.mousePosition)) {
				_selectedEvent = null;
				GUIUtility.ExitGUI();
			}

			//
			// Preview seeker
			//
			seekerRect.x = timelineRect.x;
			seekerRect.x += timelineRect.width * previewTime;

			seekerRect.x -= 1.0f; // Center

			seekerRect.y += 4f;
			seekerRect.height -= 4f;

			EditorGUI.DrawRect(seekerRect, Color.white);


			EditorGUIUtility.labelWidth = _origLabelWidth;
			EditorGUI.indentLevel = origIndent;
		}

		private void HandleEventSeekerDrag(AnimationEvent ev, Rect seekerRect, Rect timelineRect)
		{
			if (Event.current.type == EventType.MouseDown && seekerRect.Contains(Event.current.mousePosition)) {
				_selectedEventPending = ev;
				_eventDragStarted = true;
				Event.current.Use();
			}

			if (Event.current.type == EventType.MouseDrag && _eventDragStarted && ev == _selectedEvent) {
				ev.time = Mathf.Clamp01(Mathf.Max(Event.current.mousePosition.x - timelineRect.x, 0.0f) / timelineRect.width);
				GUI.changed = true;
			}
		}

		private void DrawClipEventsEntries(float previewTime)
		{
			EditorGUILayout.BeginVertical();
			var wasChanged = GUI.changed;
			_eventsScrollPos = EditorGUILayout.BeginScrollView(_eventsScrollPos, false, false);
			GUI.changed = wasChanged;

			for (int i = 0; i < _events.Count; ++i) {
				var remove = DrawClipEventEntry(_events[i], previewTime);

				if (remove) {
					_events.RemoveAt(i);
					--i;

					// Other editor instances might be showing the same data.
					WriteBackSelectedEvents();
					RefreshAllInstances();
				}

				EditorGUILayout.Space();
			}

			EditorGUILayout.EndScrollView();
			EditorGUILayout.EndVertical();

			// In case it doesn't exist anymore.
			if (Event.current.type == EventType.Repaint) {
				_selectedEventPending = null;
			}
		}

		private static Texture2D CreatePixelTexture(Color color)
		{
			var texture = new Texture2D(1, 1);
			texture.SetPixel(0, 0, color);
			texture.wrapMode = TextureWrapMode.Repeat;
			texture.Apply();

			return texture;
		}

		private static Texture2D CreateColorizedTexture(Texture2D texture, Color color)
		{
			var colorizedTexture = new Texture2D(texture.width, texture.height);
			Graphics.CopyTexture(texture, 0, 0, colorizedTexture, 0, 0);

			var colorizedPixels = colorizedTexture.GetPixels();
			for (int i = 0; i < colorizedPixels.Length; ++i) {
				colorizedPixels[i] = colorizedPixels[i] + color;
			}

			colorizedTexture.SetPixels(colorizedPixels);
			colorizedTexture.Apply();

			return colorizedTexture;
		}

		private bool DrawClipEventEntry(AnimationEvent ev, float previewTime)
		{
			if (EVENT_SLIDER_THUMB_STYLE == null || EVENT_SLIDER_THUMB_STYLE.normal.background == null) {
				EVENT_SLIDER_THUMB_STYLE = new GUIStyle(EditorStyles.miniButton);
				EVENT_SLIDER_THUMB_STYLE.border = new RectOffset();
				EVENT_SLIDER_THUMB_STYLE.padding = new RectOffset();
				EVENT_SLIDER_THUMB_STYLE.fixedWidth = 3.0f;

				// These need to be checked every time, cause the textures might get lost on reloading or something.
				EVENT_SLIDER_THUMB_STYLE.normal.background = CreatePixelTexture(EVENT_COLOR);
				EVENT_SLIDER_THUMB_STYLE.focused.background = CreatePixelTexture(EVENT_COLOR_SELECTED);
				EVENT_SLIDER_THUMB_STYLE.active.background = CreatePixelTexture(EVENT_COLOR_SELECTED);
			}

			const float LABEL_WIDTH = 70.0f;


			if (_selectedEvent == ev) {
				GUI.backgroundColor = EVENT_BG_COLOR_SELECTED;
			}

			EditorGUILayout.BeginVertical(EditorStyles.helpBox);    // Background

			GUI.backgroundColor = _origBGColor;


			var initiallyChanged = GUI.changed;

			EditorGUILayout.BeginHorizontal();
			{
				EditorGUIUtility.labelWidth = LABEL_WIDTH;

				if (IsBadEventFunctionName(ev)) {
					GUI.backgroundColor = Color.yellow;
				}

				// This is delayed, because typed name could be invalid and "problem" control will pop up with a warning,
				// making this field lose focus.
				ev.functionName = EditorGUILayout.DelayedTextField("Function:", ev.functionName);

				GUI.backgroundColor = _origBGColor;


				var wasChanged = GUI.changed;
				if (GUILayout.Button(EVENT_BUTTON_COPY, GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
					_copyData = CloneEvent(ev);
					_selectedEvent = ev;	// Cause GUI.changed won't be applied here.
				}
				GUI.changed = wasChanged;

				EditorGUI.BeginDisabledGroup(!(_copyData is AnimationEvent));
				if (GUILayout.Button(EVENT_BUTTON_PASTE, GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
					var pasteEv = (AnimationEvent)_copyData;
					CopyEvent(pasteEv, ev);
				}
				EditorGUI.EndDisabledGroup();

				EditorGUI.BeginDisabledGroup(previewTime < 0.0f);
				if (GUILayout.Button(EVENT_BUTTON_SET_PREVIEW_TIME, GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
					ev.time = previewTime;
				}
				EditorGUI.EndDisabledGroup();

				if (GUILayout.Button(EVENT_BUTTON_REMOVE, GUILayout.ExpandWidth(false), GUILayout.Height(14.0f))) {
					return true;
				}
			}
			EditorGUILayout.EndHorizontal();



			EditorGUILayout.BeginHorizontal();
			{
				EditorGUILayout.LabelField("Params:", GUILayout.Width(LABEL_WIDTH - 4.0f));

				var origIndentLevel = EditorGUI.indentLevel;
				EditorGUI.indentLevel = 0;

				ev.stringParameter = EditorGUILayout.TextField(ev.stringParameter);
				if (_showEventNumberParam) {
					ev.floatParameter = EditorGUILayout.FloatField(ev.floatParameter, GUILayout.Width(40));
					ev.intParameter = EditorGUILayout.IntField(ev.intParameter, GUILayout.Width(30));
				}
				ev.objectReferenceParameter = EditorGUILayout.ObjectField(ev.objectReferenceParameter, typeof(Object), false);

				EditorGUI.indentLevel = origIndentLevel;
			}
			EditorGUILayout.EndHorizontal();



			EditorGUILayout.BeginHorizontal();
			{
				EditorGUIUtility.labelWidth = LABEL_WIDTH;

				var sliderRect = EditorGUILayout.GetControlRect(true);

				var origSliderThumb = GUI.skin.horizontalSliderThumb;
				GUI.skin.horizontalSliderThumb = EVENT_SLIDER_THUMB_STYLE;

				ev.time = EditorGUI.Slider(sliderRect, "Time:", ev.time, 0.0f, 1.0f);

				GUI.skin.horizontalSliderThumb = origSliderThumb;


				const float SLIDER_NUMBERS_FIELD_WIDTH = 50.0f;
				const float SLIDER_PADDING_LEFT = 8.0f;
				const float SLIDER_PADDING_RIGHT = -2.0f;   // Some calculations are wrong, don't care.

				float sliderOnlyWidth = sliderRect.width - EditorGUIUtility.labelWidth - SLIDER_NUMBERS_FIELD_WIDTH - SLIDER_PADDING_LEFT - SLIDER_PADDING_RIGHT;

				var previewRect = new Rect() { y = sliderRect.y, height = sliderRect.height };
				previewRect.x = EditorGUIUtility.labelWidth + SLIDER_PADDING_LEFT;
				previewRect.x += sliderOnlyWidth * previewTime;

				previewRect.width = 3.0f;
				previewRect.x -= 1.0f; // Center

				previewRect.y += 4f;
				previewRect.height -= 4f;

				EditorGUI.DrawRect(previewRect, Color.white);

			}
			EditorGUILayout.EndHorizontal();




			EditorGUILayout.EndVertical();  // Background

			if (Event.current.type == EventType.MouseDown) {
				var lastRect = GUILayoutUtility.GetLastRect();
				if (lastRect.Contains(Event.current.mousePosition)) {
					_selectedEvent = ev;
				}
			}

			if (initiallyChanged != GUI.changed) {
				_selectedEvent = ev;
			}

			if (Event.current.type == EventType.Repaint && _selectedEventPending == ev) {
				var lastRect = GUILayoutUtility.GetLastRect();
				_eventsScrollPos = lastRect.position;

				_selectedEvent = _selectedEventPending;
				_selectedEventPending = null;
			}

			return false;
		}

		private static AnimationEvent CloneEvent(AnimationEvent sourceEvent)
		{
			var copyEvent = new AnimationEvent();
			CopyEvent(sourceEvent, copyEvent);

			return copyEvent;
		}

		private static void CopyEvent(AnimationEvent sourceEvent, AnimationEvent destinationEvent)
		{
			destinationEvent.functionName = sourceEvent.functionName;
			destinationEvent.stringParameter = sourceEvent.stringParameter;
			destinationEvent.intParameter = sourceEvent.intParameter;
			destinationEvent.floatParameter = sourceEvent.floatParameter;
			destinationEvent.objectReferenceParameter = sourceEvent.objectReferenceParameter;
			destinationEvent.time = sourceEvent.time;
			destinationEvent.messageOptions = sourceEvent.messageOptions;
		}

		private List<AnimationClip> GetAllClips(Object asset)
		{
			if (asset is AnimationClip && AssetDatabase.IsMainAsset(asset))
				return new List<AnimationClip>() { (AnimationClip) asset };

			return AssetDatabase.LoadAllAssetRepresentationsAtPath(AssetDatabase.GetAssetPath(asset)).OfType<AnimationClip>().ToList();
		}

		private List<AnimationClip> GetAllClipsInFolder(Object asset)
		{
			var folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset));
			var paths = AssetDatabase.FindAssets("t:AnimationClip", new string[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Distinct();

			var clips = new List<AnimationClip>();
			foreach (var path in paths) {

				// No sub-folders
				if (System.IO.Path.GetDirectoryName(path) != folder)
					continue;

				var obj = AssetDatabase.LoadMainAssetAtPath(path);
				if (obj is AnimationClip) {
					clips.Add((AnimationClip) obj);
					continue;
				}

				var subClips = AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
					.OfType<AnimationClip>()
					.Where(clip => clip.name != "Bind_Pose" || asset == clip);
				clips.AddRange(subClips);
			}

			return clips;
		}

		#region Preview Time

		private static Dictionary<Animator, float> _previewAnimatorTimes = new Dictionary<Animator, float>();
		private static Animator _previewAnimatorUsed = null;
		private static double _lastPreviewsRefreshedTime = 0;

		// Tries to get the preview (seek) time from the last touched by the user preview window.
		private static bool GetPreviewTime(out float time)
		{
			time = -1.0f;

			// Periodically refresh preview animators, as the user might have opened new inspectors.
			// NOTE: if inspector is docked but hidden, his animator won't be found. So caching won't work.
			var timeToRefresh = 1f < EditorApplication.timeSinceStartup - _lastPreviewsRefreshedTime;
			var focusedWindowIsPreview = focusedWindow ? focusedWindow.GetType().Name == "PreviewWindow" : false;

			if (_previewAnimatorTimes.Count == 0 || (timeToRefresh && focusedWindowIsPreview)) {

				// Fetch the internal Animators used for the previews. They all have names ending with "AnimatorPreview". (check EditorUtility.InstantiateForAnimatorPreview()).
				// NOTE: Multiple preview Animators can be found if user has multiple inspectors opened.
				var previewAnimators = Resources.FindObjectsOfTypeAll<Animator>()
					.Where(animator => animator.name.EndsWith("AnimatorPreview"))
					.Where(animator => animator.gameObject.hideFlags == HideFlags.HideAndDontSave);

				foreach(var animator in previewAnimators) {
					// Animator might be present, since this is a refresh, not a complete wipe out.
					if (!_previewAnimatorTimes.ContainsKey(animator)) {
						_previewAnimatorTimes.Add(animator, -1.0f);
					}
				}

				if (_previewAnimatorTimes.Count == 0) {
					return false;
				}

				_lastPreviewsRefreshedTime = EditorApplication.timeSinceStartup;
			}


			
			// Find which Animator was changed from last time.
			foreach(var pair in _previewAnimatorTimes) {

				// Inspector/preview was closed, reset list of animators. Selecting another clip also destroys the animator.
				if (pair.Key == null) {
					_previewAnimatorTimes.Clear();
					return GetPreviewTime(out time);
				}


				// Get the preview time.
				float currentTime = pair.Key.GetCurrentAnimatorStateInfo(0).normalizedTime;

				if (!Mathf.Approximately(currentTime, pair.Value)) {
					_previewAnimatorUsed = pair.Key;
					time = currentTime;
					_previewAnimatorTimes[pair.Key] = currentTime;
					return true;
				}
			}

			// No changes - return the same time from the last used Animator.
			time = _previewAnimatorTimes[_previewAnimatorUsed];
			return true;
		}

		#endregion

		#region Problems

		private struct ClipProblem
		{
			public MessageType Type;
			public string Message;

			public ClipProblem(MessageType type, string message)
			{
				Type = type;
				Message = message;
			}
		}

		private static bool AreNamesMatching(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			return importerClip.name == importerClip.takeName || importer.ImportedTakeNames.Length == 1;
		}

		private static bool IsSourceTakeValid(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			return Array.IndexOf(importer.ImportedTakeNames, importerClip.takeName) >= 0;
		}

		private static bool AreRangesMatching(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			if (importer.ImportedTakeInfos.Length <= 1)
				return true;

			int takeStartFrame;
			int takeStopFrame;
			importer.GetTakeStartStopFrames(importerClip, out takeStartFrame, out takeStopFrame);

			return Mathf.RoundToInt(importerClip.firstFrame) == takeStartFrame && Mathf.RoundToInt(importerClip.lastFrame) == takeStopFrame;
		}


		private static bool IsEndRangeValid(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			int takeStartFrame;
			int takeStopFrame;
			importer.GetTakeStartStopFrames(importerClip, out takeStartFrame, out takeStopFrame);

			return importerClip.lastFrame <= takeStopFrame;
		}

		private static bool EventsAreOK(ModelImporterProxy importer, ModelImporterClipAnimation importerClip)
		{
			return !importerClip.events.Any(IsBadEventFunctionName);
		}

		private static bool IsBadEventFunctionName(AnimationEvent ev)
		{
			return ev.functionName == "NewEvent" ||		// Default created events, probably was not configured and not needed.
			       string.IsNullOrEmpty(ev.functionName) ||
			       ev.functionName.Any(char.IsWhiteSpace);
		}


		private static void DrawProblems(List<ClipProblem> problems)
		{
			DrawProblems(problems, MessageType.Info);
			DrawProblems(problems, MessageType.Warning);
			DrawProblems(problems, MessageType.Error);
		}

		private static void DrawProblems(List<ClipProblem> problems, MessageType type)
		{
			var messages = problems
				.Where(p => p.Type == type)
				.Select(p => p.Message);

			var text = string.Join("\n", messages);

			if (!string.IsNullOrEmpty(text)) {
				EditorGUILayout.HelpBox(text, type);
			}
		}

		#endregion


		// This wraps the ModelImporter class, as it is a UnityEngine.Object instance and is shared in the environment.
		// This allows "Revert changes" support.
		// This also wraps single clips assets (outside of models), as they don't have a ModelImporter or ModelImporterClipAnimation.
		// This allows for the rest of the logic to remain the same.
		private class ModelImporterProxy
		{
			public string assetPath { get; }

			// The key is the original name, needed for when the clip name is changed by the user.
			public List<KeyValuePair<string, ModelImporterClipAnimation>> ClipAnimations { get; private set; } = new List<KeyValuePair<string, ModelImporterClipAnimation>>();

			// The sorted version will be used to be drawn in overview. This is different collection, because we don't want to change the order of the clips (avoiding undesired changes).
			public List<KeyValuePair<string, ModelImporterClipAnimation>> ClipAnimationsSorted { get; private set; } = new List<KeyValuePair<string, ModelImporterClipAnimation>>();


			public TakeInfo[] ImportedTakeInfos { get; }
			public string[] ImportedTakeNames { get; }

			public bool ImportAnimation => _importer.importAnimation;

			private readonly ModelImporter _importer;

			// Single out-of-model clip asset.
			private readonly AnimationClip _singleClip;
			private readonly AnimationClipSettings _singleClipSettings;


			public ModelImporterProxy(ModelImporter importer)
			{
				_importer = importer;
				assetPath = _importer.assetPath;

				// NOTE: _importer.clipAnimations is a copy.
				foreach (var clip in _importer.clipAnimations) {
					ClipAnimations.Add(new KeyValuePair<string, ModelImporterClipAnimation>(clip.name, clip));
				}
				UpdateSortedAnimationClips();

				ImportedTakeInfos = _importer.importedTakeInfos;
				ImportedTakeNames = _importer.importedTakeInfos.Select(take => take.name).ToArray();
			}


			public ModelImporterProxy(AnimationClip singleClip)
			{
				_singleClip = singleClip;
				_singleClipSettings = AnimationUtility.GetAnimationClipSettings(_singleClip);
				assetPath = AssetDatabase.GetAssetPath(_singleClip);

				// Fake ModelImporterClipAnimation to keep everything else working the same way.
				// NOTE: Sync these with WriteChanges()
				var importerAnimation = new ModelImporterClipAnimation();
				importerAnimation.name = _singleClip.name;
				importerAnimation.takeName = _singleClip.name;

				importerAnimation.events = AnimationUtility.GetAnimationEvents(_singleClip).Select(CloneEvent).ToArray();

				importerAnimation.firstFrame = _singleClipSettings.startTime * _singleClip.frameRate;
				importerAnimation.lastFrame = _singleClipSettings.stopTime * _singleClip.frameRate;
				importerAnimation.loop = _singleClipSettings.loopTime;
				importerAnimation.loopPose = _singleClipSettings.loopBlend;
				importerAnimation.cycleOffset = _singleClipSettings.cycleOffset;

				ClipAnimations.Add(new KeyValuePair<string, ModelImporterClipAnimation>(_singleClip.name, importerAnimation));

				UpdateSortedAnimationClips();


				var take = new TakeInfo
				{
					name = _singleClip.name,
					sampleRate = _singleClip.frameRate,
					startTime = _singleClipSettings.startTime,
					stopTime = _singleClipSettings.stopTime
				};

				ImportedTakeInfos = new TakeInfo[] { take };
				ImportedTakeNames = ImportedTakeInfos.Select(t => t.name).ToArray();
			}

			public void ResetClipsToDefaults()
			{
				var clipAnimations = new List<ModelImporterClipAnimation>(_importer.defaultClipAnimations);

				ClipAnimations.Clear();
				foreach (var clip in clipAnimations) {
					ClipAnimations.Add(new KeyValuePair<string, ModelImporterClipAnimation>(clip.name, clip));
				}
				UpdateSortedAnimationClips();
			}

			public TakeInfo GetTakeInfo(ModelImporterClipAnimation importerClip)
			{
				return ImportedTakeInfos.FirstOrDefault(t => t.name == importerClip.takeName);
			}

			public void GetTakeStartStopFrames(ModelImporterClipAnimation importerClip, out int takeStartFrame, out int takeStopFrame)
			{
				var takeInfo = GetTakeInfo(importerClip);
				takeStartFrame = Mathf.RoundToInt(takeInfo.startTime * takeInfo.sampleRate);
				takeStopFrame = Mathf.RoundToInt(takeInfo.stopTime * takeInfo.sampleRate);
			}

			public ModelImporterClipAnimation GetClipAnimation(AnimationClip clip)
			{
				var importerClip = ClipAnimations.FirstOrDefault(p => p.Key == clip.name).Value;

				return importerClip;
			}

			public void WriteChanges()
			{
				if (_importer != null) {
					_importer.clipAnimations = ClipAnimations.Select(p => p.Value).ToArray();

					_importer.SaveAndReimport();

					//AssetDatabase.WriteImportSettingsIfDirty(importer.assetPath);
					//AssetDatabase.ImportAsset(importer.assetPath, ImportAssetOptions.ForceUpdate);
				} else {
					var importerAnimation = ClipAnimations[0].Value;
					AnimationUtility.SetAnimationEvents(_singleClip, importerAnimation.events);

					_singleClipSettings.startTime = importerAnimation.firstFrame / _singleClip.frameRate;
					_singleClipSettings.stopTime = importerAnimation.lastFrame / _singleClip.frameRate;
					_singleClipSettings.loopTime = importerAnimation.loop;
					_singleClipSettings.loopBlend = importerAnimation.loopPose;
					_singleClipSettings.cycleOffset = importerAnimation.cycleOffset;

					AnimationUtility.SetAnimationClipSettings(_singleClip, _singleClipSettings);

					EditorUtility.SetDirty(_singleClip);
					AssetDatabase.SaveAssets();
				}
			}


			private void UpdateSortedAnimationClips()
			{
				ClipAnimationsSorted.Clear();
				ClipAnimationsSorted.AddRange(ClipAnimations);
				ClipAnimationsSorted.Sort((p1, p2) => String.Compare(p1.Key, p2.Key, StringComparison.Ordinal));
			}
		}
	}

}
