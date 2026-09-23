using System;
using Unity.Editor.Tasks;
using Unity.Editor.Tasks.Logging;
using Unity.VersionControl.Git.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.VersionControl.Git.UI
{
    [Serializable]
    class SettingsView : Subview
    {
        private const string GitRepositoryTitle = "Repository Configuration";

        private const string RepositorySelectionTitle = "Repository Selection";
        private const string RepositorySelectionLabel = "Active repository";
        private const string RepositorySelectionRefresh = "Rescan";
        private const string RepositorySelectionBrowse = "Browse...";
        private const string RepositorySelectionAutoDiscover = "<auto-discover>";

        private const string GitRepositoryRemoteLabel = "Remote";
        private const string GitRepositorySave = "Save Repository";
        private const string InitializeGitAttributesLabel = "Setup .gitattributes";
        private const string SetupUnityMergeLabel = "Setup Unity Yaml Merge";

        private const string GeneralSettingsTitle = "General";

        private const string WebTimeoutLabel = "Timeout of web requests";
        private const string GitTimeoutLabel = "Timeout of git commands";

        private const string DebugSettingsTitle = "Debug";
        private const string EnableTraceLoggingLabel = "Enable Trace Logging";

        private const string UISceneHierarchySettingsTitle = "UI - Scene Hierarchy";
        private const string UIProjectViewSettingsTitle = "UI - Project Window";
        private const string IconsEnabledToggleLabel = "Show Git Status Icons";
        private const string HierarchyIconsIndentToggleLabel = "Align to end of label";
        private const string HierarchyIconsIndentToggleTooltip = "You probably don't want this";
        private static GUIContent hierarchyIconsIndentToggleContent;
        private static GUIContent HierarchyIconsIndentToggleContent => hierarchyIconsIndentToggleContent ?? (hierarchyIconsIndentToggleContent = new GUIContent(HierarchyIconsIndentToggleLabel, HierarchyIconsIndentToggleTooltip));

        private const string HierarchyIconsOffsetLabel = "Offset";
        private const string HierarchyIconsOffsetRightTooltip = "Offset from the right edge of the hierarchy window. Increase this value to move icons away from the edge.";
        private const string HierarchyIconsOffsetLeftTooltip = "Offset from the left edge of the hierarchy window.";
        private static GUIContent hierarchyIconsOffsetRightContent;
        private static GUIContent HierarchyIconsOffsetRightContent => hierarchyIconsOffsetRightContent ?? (hierarchyIconsOffsetRightContent = new GUIContent(HierarchyIconsOffsetLabel, HierarchyIconsOffsetRightTooltip));
        private static GUIContent hierarchyIconsOffsetLeftContent;
        private static GUIContent HierarchyIconsOffsetLeftContent => hierarchyIconsOffsetLeftContent ?? (hierarchyIconsOffsetLeftContent = new GUIContent(HierarchyIconsOffsetLabel, HierarchyIconsOffsetLeftTooltip));

        private const string HierarchyIconsAlignmentLabel = "Align icons to";
        private const string HierarchyIconsAlignmentTooltip = "Align the icons to the left or right of the hiearchy entry. Note that the icons will visually overlap the scene object buttons when aligned on the left, but the buttons will still work.";
        private static GUIContent hierarchyIconsAlignmentContent;
        private static GUIContent HierarchyIconsAlignmentContent => hierarchyIconsAlignmentContent ?? (hierarchyIconsAlignmentContent = new GUIContent(HierarchyIconsAlignmentLabel, HierarchyIconsAlignmentTooltip));

        private const string DefaultRepositoryRemoteName = "origin";

        [NonSerialized] private bool currentRemoteHasUpdate;
        [NonSerialized] private bool isBusy;
        [NonSerialized] private System.Collections.Generic.List<RepositoryEntry> discoveredRepositories;

        // Repository-switch overlay state. While switchInProgress is true the whole settings
        // view is greyed out and a centered progress bar is drawn, and the view keeps
        // repainting so it catches the async git re-query the moment it completes.
        [NonSerialized] private bool switchInProgress;
        [NonSerialized] private string switchTargetPath;
        [NonSerialized] private double switchStartTime;
        [NonSerialized] private float switchProgress;
        private const double SwitchTimeoutSeconds = 60d;

        [SerializeField] private GitPathView gitPathView = new GitPathView();
        [SerializeField] private bool hasRemote;
        [SerializeField] private CacheUpdateEvent lastCurrentRemoteChangedEvent;
        [SerializeField] private string newRepositoryRemoteUrl;
        [SerializeField] private string repositoryRemoteName;
        [SerializeField] private string repositoryRemoteUrl;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private UserSettingsView userSettingsView = new UserSettingsView();

        [SerializeField] private bool repositorySettingsHidden;
        [SerializeField] private bool repositorySelectionHidden;
        [SerializeField] private bool generalSettingsHidden;
        [SerializeField] private bool debugSettingsHidden;
        [SerializeField] private bool uiSceneSettingsHidden;
        [SerializeField] private bool uiProjectSettingsHidden;

        public override void InitializeView(IView parent)
        {
            base.InitializeView(parent);
            gitPathView.InitializeView(this);
            userSettingsView.InitializeView(this);
        }

        public override void OnEnable()
        {
            base.OnEnable();
            gitPathView.OnEnable();
            userSettingsView.OnEnable();
            AttachHandlers(Repository);

            if (Repository != null)
            {
                ValidateCachedData(Repository);
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            gitPathView.OnDisable();
            userSettingsView.OnDisable();
            DetachHandlers(Repository);
        }

        public override void OnDataUpdate()
        {
            base.OnDataUpdate();
            userSettingsView.OnDataUpdate();
            gitPathView.OnDataUpdate();

            MaybeUpdateData();
            UpdateSwitchProgress();
        }

        public override void Refresh()
        {
            base.Refresh();
            gitPathView.Refresh();
            userSettingsView.Refresh();
            Refresh(CacheType.RepositoryInfo);
        }

        public override void OnGUI()
        {
            // While a repository switch is in flight, grey out and disable all the settings so
            // the user can't kick off a second switch or edit stale data mid-swap.
            EditorGUI.BeginDisabledGroup(switchInProgress);
            scroll = GUILayout.BeginScrollView(scroll);
            {
                userSettingsView.OnGUI();

                GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                OnRepositorySelectionGUI();
                GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                if (Repository != null)
                {
                    OnRepositorySettingsGUI();
                    GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                }

                gitPathView.OnGUI();
                OnGeneralSettingsGui();
                OnLoggingSettingsGui();
                OnUISettingsGui();
            }

            GUILayout.EndScrollView();
            EditorGUI.EndDisabledGroup();

            DoProgressGUI();

            if (switchInProgress)
                DoSwitchOverlayGUI();
        }

        /// <summary>
        /// Draws a dimming layer over the whole view plus a centered "Switching repository…"
        /// panel with a progress bar, so the long-running git re-query reads as a modal
        /// operation instead of a thin bar hidden at the bottom of the window.
        /// </summary>
        private void DoSwitchOverlayGUI()
        {
            var full = new Rect(0, 0, Position.width, Position.height);

            // Dim everything behind the panel.
            EditorGUI.DrawRect(full, new Color(0f, 0f, 0f, 0.55f));

            // Centered panel.
            const float panelWidth = 340f;
            const float panelHeight = 92f;
            var panel = new Rect(
                (Position.width - panelWidth) * 0.5f,
                (Position.height - panelHeight) * 0.5f,
                panelWidth, panelHeight);

            EditorGUI.DrawRect(panel, new Color(0.16f, 0.16f, 0.16f, 0.98f));
            EditorGUI.DrawRect(new Rect(panel.x, panel.y, panel.width, 1), new Color(1f, 1f, 1f, 0.15f));

            var inner = new Rect(panel.x + 14, panel.y + 12, panel.width - 28, panel.height - 24);

            var titleRect = new Rect(inner.x, inner.y, inner.width, 18);
            var title = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
            GUI.Label(titleRect, "Switching repository…", title);

            var targetName = string.IsNullOrEmpty(switchTargetPath)
                ? "<auto-discover>"
                : System.IO.Path.GetFileName(switchTargetPath.TrimEnd('/', '\\'));
            var subRect = new Rect(inner.x, titleRect.yMax + 2, inner.width, 14);
            GUI.Label(subRect, targetName, EditorStyles.miniLabel);

            var barRect = new Rect(inner.x, subRect.yMax + 8, inner.width, 18);
            EditorGUI.ProgressBar(barRect, switchProgress, string.Empty);
        }

        private void AttachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentRemoteChanged += RepositoryOnCurrentRemoteChanged;
        }

        private void RepositoryOnCurrentRemoteChanged(CacheUpdateEvent cacheUpdateEvent)
        {
            if (!lastCurrentRemoteChangedEvent.Equals(cacheUpdateEvent))
            {
                lastCurrentRemoteChangedEvent = cacheUpdateEvent;
                currentRemoteHasUpdate = true;
                Redraw();
            }
        }

        private void DetachHandlers(IRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            repository.CurrentRemoteChanged -= RepositoryOnCurrentRemoteChanged;
        }

        private void ValidateCachedData(IRepository repository)
        {
            repository.CheckAndRaiseEventsIfCacheNewer(CacheType.RepositoryInfo, lastCurrentRemoteChangedEvent);
        }

        private void MaybeUpdateData()
        {
            if (Repository == null)
                return;

            if (currentRemoteHasUpdate)
            {
                currentRemoteHasUpdate = false;
                var currentRemote = Repository.CurrentRemote;
                hasRemote = currentRemote.HasValue && !String.IsNullOrEmpty(currentRemote.Value.Url);
                if (!hasRemote)
                {
                    repositoryRemoteName = DefaultRepositoryRemoteName;
                    newRepositoryRemoteUrl = repositoryRemoteUrl = string.Empty;
                }
                else
                {
                    repositoryRemoteName = currentRemote.Value.Name;
                    newRepositoryRemoteUrl = repositoryRemoteUrl = currentRemote.Value.Url;
                }
            }
        }

        private void OnRepositorySelectionGUI()
        {
            repositorySelectionHidden = !Controls.FoldoutScope(!repositorySelectionHidden, RepositorySelectionTitle, () =>
            {
                var selected = EnvironmentCache.Instance.SelectedRepositoryPath;

                // The button label must reflect the user's choice immediately, not the
                // window's cached repository (which lags behind the async git re-query after a
                // switch). Priority: the persisted selection, else the live environment's
                // resolved repository path, else auto-discover.
                string currentLabel;
                if (!string.IsNullOrEmpty(selected))
                {
                    currentLabel = selected;
                }
                else
                {
                    var resolved = Environment?.RepositoryPath.ToString();
                    currentLabel = string.IsNullOrEmpty(resolved) ? RepositorySelectionAutoDiscover : resolved;
                }

                EditorGUI.BeginDisabledGroup(IsBusy);
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        EditorGUILayout.PrefixLabel(RepositorySelectionLabel);

                        if (GUILayout.Button(new GUIContent(currentLabel), EditorStyles.popup))
                        {
                            ShowRepositoryDropdown(selected);
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    {
                        if (GUILayout.Button(RepositorySelectionRefresh, GUILayout.ExpandWidth(false)))
                        {
                            discoveredRepositories = null;
                        }

                        if (GUILayout.Button(RepositorySelectionBrowse, GUILayout.ExpandWidth(false)))
                        {
                            var start = !string.IsNullOrEmpty(selected)
                                ? selected
                                : Environment.UnityProjectPath.ToString();
                            var picked = EditorUtility.OpenFolderPanel("Select a git repository", start, string.Empty);
                            if (!string.IsNullOrEmpty(picked))
                            {
                                SelectRepository(picked);
                            }
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.EndDisabledGroup();
            });
        }

        private void ShowRepositoryDropdown(string selectedPath)
        {
            if (discoveredRepositories == null)
            {
                discoveredRepositories = RepositorySelector.Scan(Environment.UnityProjectPath.ToString());
            }

            var menu = new GenericMenu();

            // Auto-discover option: clears the override and lets Git for Unity find the repo by
            // walking up from the project root (the original behaviour).
            menu.AddItem(new GUIContent(RepositorySelectionAutoDiscover), string.IsNullOrEmpty(selectedPath),
                () => SelectRepository(null));

            if (discoveredRepositories.Count > 0)
                menu.AddSeparator(string.Empty);

            foreach (var entry in discoveredRepositories)
            {
                var repoPath = entry.Path;
                var isSelected = !string.IsNullOrEmpty(selectedPath)
                    && string.Equals(
                        System.IO.Path.GetFullPath(selectedPath).TrimEnd('/', '\\'),
                        System.IO.Path.GetFullPath(repoPath).TrimEnd('/', '\\'),
                        StringComparison.OrdinalIgnoreCase);

                // GenericMenu treats '/' as a submenu separator, which turned the relative
                // paths into nested menus. Swap it for a visually similar divider so every repo
                // shows as a single flat entry.
                var label = entry.DisplayName.Replace('/', '\u2215'); // U+2215 DIVISION SLASH

                menu.AddItem(new GUIContent(label), isSelected, () => SelectRepository(repoPath));
            }

            menu.ShowAsContext();
        }

        private void SelectRepository(string path)
        {
            try
            {
                // Begin the switch: raise the overlay immediately so the user gets instant
                // feedback, then perform the actual repository swap.
                switchInProgress = true;
                switchTargetPath = string.IsNullOrEmpty(path) ? null : path;
                switchStartTime = EditorApplication.timeSinceStartup;
                switchProgress = 0f;
                isBusy = true;

                EnvironmentCache.Instance.SetSelectedRepository(path);

                // Rebuild the application manager and environment against the newly selected
                // repository. After this, EntryPoint.ApplicationManager lazily recreates itself
                // bound to the freshly-resolved environment.
                EntryPoint.Restart();

                // The per-repo info caches (branch, remote, status, log, locks) are shared
                // singletons persisted under Library/gfu with long timeouts, so after a switch
                // they still hold the PREVIOUS repository's data. Invalidate them once so the
                // new repository re-queries git.
                var env = EntryPoint.ApplicationManager.Environment;
                env.CacheContainer.InvalidateAll();

                // Re-bind the whole window (header + active view) to the new repository. This
                // re-attaches the change-event handlers so the header repaints when the git
                // query completes.
                discoveredRepositories = null;
                var window = Window.GetWindow();
                if (window != null)
                    window.InitializeWindow(EntryPoint.ApplicationManager);

                // Refresh only the fast, LOCAL caches needed for the header (branch + remote).
                // Deliberately avoid GitLocks / fetch / ahead-behind here: those can hit the
                // network and were a big part of the multi-second stall. They lazy-load when
                // the user opens the relevant tab. This is one coordinated pass, so the
                // progress bar no longer flickers several times.
                var repository = EntryPoint.ApplicationManager.Environment.Repository;
                if (repository != null)
                {
                    repository.Refresh(CacheType.RepositoryInfo);
                    repository.Refresh(CacheType.Branches);
                    repository.Refresh(CacheType.GitStatus);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to switch repository to {0}", path ?? "<auto-discover>");
                switchInProgress = false;
            }
            finally
            {
                isBusy = false;
                Redraw();
            }
        }

        /// <summary>
        /// Detects when a repository switch has finished (the live environment now resolves to
        /// the requested repo and its git data has been re-queried) and lowers the overlay.
        /// Also advances a smooth indeterminate progress value and enforces a timeout so the
        /// overlay can never get permanently stuck.
        /// </summary>
        private void UpdateSwitchProgress()
        {
            if (!switchInProgress)
                return;

            var elapsed = EditorApplication.timeSinceStartup - switchStartTime;

            // Smooth, ease-out indeterminate progress that never quite reaches 1 until done.
            switchProgress = Mathf.Clamp01((float)(1.0 - Math.Exp(-elapsed / 6.0)));

            var env = Manager != null ? Manager.Environment : null;
            var resolvedPath = env != null ? env.RepositoryPath.ToString() : null;

            bool targetReached;
            if (string.IsNullOrEmpty(switchTargetPath))
            {
                // Auto-discover: any resolved repo (or explicit none) counts as settled.
                targetReached = true;
            }
            else
            {
                targetReached = !string.IsNullOrEmpty(resolvedPath)
                    && string.Equals(
                        System.IO.Path.GetFullPath(resolvedPath).TrimEnd('/', '\\'),
                        System.IO.Path.GetFullPath(switchTargetPath).TrimEnd('/', '\\'),
                        StringComparison.OrdinalIgnoreCase);
            }

            // Consider the switch complete once the environment points at the target repo and
            // either the background git work has drained OR a short grace period has passed.
            // The header only needs the (fast, local) RepositoryInfo query; a large repo's
            // status/log can keep the manager "busy" much longer, so we don't wait on that —
            // otherwise the overlay would linger for the full status scan.
            var busy = (Manager != null && Manager.IsBusy) || (Repository != null && Repository.IsBusy);
            if (targetReached && (!busy || elapsed > 2.0) && elapsed > 0.4)
            {
                switchInProgress = false;
                switchProgress = 1f;
            }
            else if (elapsed > SwitchTimeoutSeconds)
            {
                // Safety valve: never leave the UI locked behind the overlay.
                switchInProgress = false;
                Logger.Warning("Repository switch to {0} timed out after {1:0}s; clearing overlay.",
                    switchTargetPath ?? "<auto-discover>", elapsed);
            }

            // Keep repainting while the overlay is up so it tracks the async work.
            Redraw();
        }

        private void OnRepositorySettingsGUI()
        {
            repositorySettingsHidden = !Controls.FoldoutScope(!repositorySettingsHidden, GitRepositoryTitle, () =>
            {
                EditorGUI.BeginDisabledGroup(IsBusy);
                {
                    newRepositoryRemoteUrl = EditorGUILayout.TextField(GitRepositoryRemoteLabel + ": " + repositoryRemoteName, newRepositoryRemoteUrl);
                    var needsSaving = newRepositoryRemoteUrl != repositoryRemoteUrl && !String.IsNullOrEmpty(newRepositoryRemoteUrl);

                    EditorGUI.BeginDisabledGroup(!needsSaving);
                    {
                        if (GUILayout.Button(GitRepositorySave, GUILayout.ExpandWidth(false)))
                        {
                            try
                            {
                                isBusy = true;
                                Repository.SetupRemote(repositoryRemoteName, newRepositoryRemoteUrl)
                                    .FinallyInUI((_, __) =>
                                    {
                                        isBusy = false;
                                        Redraw();
                                    })
                                    .Start();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex);
                            }
                        }
                    }
                    EditorGUI.EndDisabledGroup();

                    if (GUILayout.Button(InitializeGitAttributesLabel, GUILayout.ExpandWidth(false)))
                    {
                        bool doit = true;
                        var gitAttrs = Repository.LocalPath.Combine(".gitattributes");
                        if (gitAttrs.FileExists())
                        {
                            doit = EditorUtility.DisplayDialog("Overwrite .gitattributes",
                                "A .gitattributes file already exists. Are you sure you want to overwrite it?",
                                "Overwrite", "Cancel");
                        }

                        if (doit)
                        {
                            try
                            {
                                isBusy = true;
                                SPath unityYamlMergeExec = this.Environment.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + Environment.ExecutableExtension);
                                Repository.UpdateGitAttributes()
                                    .Then(Repository.UpdateMergeSettings(unityYamlMergeExec))
                                    .FinallyInUI((_, __) =>
                                    {
                                        isBusy = false;
                                        Redraw();
                                    }).Start();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex);
                            }
                        }
                    }

                    if (GUILayout.Button(SetupUnityMergeLabel, GUILayout.ExpandWidth(false)))
                    {
                        try
                        {
                            isBusy = true;
                            SPath unityYamlMergeExec = this.Environment.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + Environment.ExecutableExtension);
                            Repository.UpdateMergeSettings(unityYamlMergeExec).FinallyInUI((_, __) => {
                                isBusy = false;
                                Redraw();
                            }).Start();

                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex);
                        }
                    }
                }
                EditorGUI.EndDisabledGroup();
            });
        }

        private void OnLoggingSettingsGui()
        {
            debugSettingsHidden = !Controls.FoldoutScope(!debugSettingsHidden, DebugSettingsTitle, () =>
            {
                Controls.DoControl(LogHelper.TracingEnabled,
                    value => EditorGUILayout.Toggle(EnableTraceLoggingLabel, value),
                    value =>
                    {
                        LogHelper.TracingEnabled = value;
                        Manager.UserSettings.Set(Constants.TraceLoggingKey, value);
                    });
            });
        }

        private void OnGeneralSettingsGui()
        {
            generalSettingsHidden = !Controls.FoldoutScope(!generalSettingsHidden, GeneralSettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.WebTimeout,
                    value => EditorGUILayout.IntField(WebTimeoutLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.WebTimeout = value;
                        Manager.UserSettings.Set(Constants.WebTimeoutKey, value);
                    });

                Controls.DoControl(ApplicationConfiguration.GitTimeout,
                    value => EditorGUILayout.IntField(GitTimeoutLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.GitTimeout = value;
                        Manager.UserSettings.Set(Constants.GitTimeoutKey, value);
                    });
            });
        }

        private void OnUISettingsGui()
        {
            bool dirty = false;

            uiSceneSettingsHidden = !Controls.FoldoutScope(!uiSceneSettingsHidden, UISceneHierarchySettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.HierarchyIconsEnabled,
                    value => EditorGUILayout.Toggle(IconsEnabledToggleLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.HierarchyIconsEnabled = value;
                        Manager.UserSettings.Set(Constants.HierarchyIconsEnabledKey, value);
                        dirty = true;
                    });

                Controls.DoControl(ApplicationConfiguration.HierarchyIconsAlignment,
                    value => (ApplicationConfiguration.HierarchyIconAlignment) EditorGUILayout.EnumPopup(HierarchyIconsAlignmentContent, value),
                    value =>
                    {
                        ApplicationConfiguration.HierarchyIconsAlignment = value;
                        Manager.UserSettings.Set(Constants.HierarchyIconsAlignmentKey, value);
                        dirty = true;
                    });

                if (ApplicationConfiguration.HierarchyIconsAlignment == ApplicationConfiguration.HierarchyIconAlignment.Right)
                {
                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsIndented,
                        value => EditorGUILayout.Toggle(HierarchyIconsIndentToggleContent, value),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsIndented = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsIndentedKey, value);
                            dirty = true;
                        });

                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsOffsetRight,
                        value => EditorGUILayout.IntSlider(HierarchyIconsOffsetRightContent, value, 0, 200),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsOffsetRight = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsOffsetRightKey, value);
                            dirty = true;
                        });
                }
                else
                {
                    Controls.DoControl(ApplicationConfiguration.HierarchyIconsOffsetLeft,
                        value => EditorGUILayout.IntSlider(HierarchyIconsOffsetLeftContent, value, -16, 16),
                        value =>
                        {
                            ApplicationConfiguration.HierarchyIconsOffsetLeft = value;
                            Manager.UserSettings.Set(Constants.HierarchyIconsOffsetLeftKey, value);
                            dirty = true;
                        });
                }
            });

            if (dirty)
            {
                EditorApplication.RepaintHierarchyWindow();
            }

            dirty = false;

            uiProjectSettingsHidden = !Controls.FoldoutScope(!uiProjectSettingsHidden, UIProjectViewSettingsTitle, () =>
            {
                Controls.DoControl(ApplicationConfiguration.ProjectIconsEnabled,
                    value => EditorGUILayout.Toggle(IconsEnabledToggleLabel, value),
                    value =>
                    {
                        ApplicationConfiguration.ProjectIconsEnabled = value;
                        Manager.UserSettings.Set(Constants.ProjectIconsEnabledKey, value);
                        dirty = true;
                    });

                if (dirty)
                {
                    EditorApplication.RepaintProjectWindow();
                }
            });
        }

        public override bool IsBusy => isBusy || userSettingsView.IsBusy || gitPathView.IsBusy;
    }
}
