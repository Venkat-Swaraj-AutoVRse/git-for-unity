using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    enum GitTab
    {
        Changes,
        History,
        Locks,
        Settings,
    }

    /// <summary>
    /// The artist-first Git window, built with UI Toolkit. One sync button replaces
    /// Fetch / Pull / Push, changes are grouped by asset type, locks are shown on every file and
    /// settings are ordered for people rather than plumbing. The IMGUI <see cref="Window"/>
    /// remains available as the legacy window.
    /// </summary>
    class GitWindow : EditorWindow
    {
        private const string Title = "Git";
        private const string StyleSheetPath = "Packages/com.spoiledcat.git.ui/Editor/UI/Modern/GitWindow.uss";

        [SerializeField] private GitTab activeTab = GitTab.Changes;
        [SerializeField] private GitTab tabBeforeSettings = GitTab.Changes;

        [NonSerialized] private GitSession session;
        [NonSerialized] private VisualElement root;
        [NonSerialized] private VisualElement repoView;
        [NonSerialized] private VisualElement emptyView;
        [NonSerialized] private VisualElement header;
        [NonSerialized] private Label repoTitle;
        [NonSerialized] private Label repoSubtitle;
        [NonSerialized] private Button branchButton;
        [NonSerialized] private Label branchLabel;
        [NonSerialized] private SyncButton syncButton;
        [NonSerialized] private VisualElement tabBar;
        [NonSerialized] private readonly Dictionary<GitTab, TabButton> tabButtons = new Dictionary<GitTab, TabButton>();
        [NonSerialized] private VisualElement content;
        [NonSerialized] private VisualElement toast;
        [NonSerialized] private Label toastLabel;
        [NonSerialized] private IVisualElementScheduledItem toastHide;
        [NonSerialized] private IVisualElementScheduledItem ticker;

        [NonSerialized] private ChangesPanel changesPanel;
        [NonSerialized] private HistoryPanel historyPanel;
        [NonSerialized] private LocksPanel locksPanel;
        [NonSerialized] private SettingsPanel settingsPanel;
        [NonSerialized] private BranchPicker branchPicker;
        [NonSerialized] private List<RepositoryEntry> discoveredRepositories;
        [NonSerialized] private bool getLatestAfterCommit;

        public GitSession Session => session;
        public bool GetLatestAfterCommit => getLatestAfterCommit;

        public static GitWindow ShowWindow()
        {
            var inspector = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
            var window = GetWindow<GitWindow>(inspector);
            window.titleContent = new GUIContent(Title, Styles.SmallLogo);
            window.minSize = new Vector2(360, 420);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Title, Styles.SmallLogo);
            if (session == null)
                session = new GitSession();
            session.Changed += OnSessionChanged;
        }

        private void OnDisable()
        {
            if (session != null)
            {
                session.Changed -= OnSessionChanged;
                session.Detach();
            }
            ticker?.Pause();
        }

        private void CreateGUI()
        {
            root = rootVisualElement;
            root.Clear();
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null)
                root.styleSheets.Add(sheet);
            root.AddToClassList("gfu-root");
            root.EnableInClassList("gfu-light", !EditorGUIUtility.isProSkin);

            repoView = GitUi.Column("gfu-repo-view");
            repoView.Add(BuildHeader());
            content = GitUi.Column("gfu-content");
            repoView.Add(content);
            root.Add(repoView);

            emptyView = BuildEmptyState();
            root.Add(emptyView);

            changesPanel = new ChangesPanel(this);
            historyPanel = new HistoryPanel(this);
            locksPanel = new LocksPanel(this);
            settingsPanel = new SettingsPanel(this);
            content.Add(changesPanel.Root);
            content.Add(historyPanel.Root);
            content.Add(locksPanel.Root);
            content.Add(settingsPanel.Root);

            branchPicker = new BranchPicker(this);
            root.Add(branchPicker.Root);

            toast = GitUi.Row("gfu-toast");
            toastLabel = GitUi.Text(string.Empty, "gfu-toast__label");
            toast.Add(toastLabel);
            toast.Add(GitUi.Button(null, HideToast, "ghost", "close", "Dismiss"));
            toast.style.display = DisplayStyle.None;
            root.Add(toast);

            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            ApplyTab();
            session.MarkDirty(GitDataKind.All);
            session.Tick();
            ticker = root.schedule.Execute(OnTick).Every(200);
        }

        private void OnTick()
        {
            session.Tick();
            UpdateSyncButton();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape && branchPicker != null && branchPicker.IsOpen)
            {
                branchPicker.Close();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.F5)
            {
                RefreshAll();
                evt.StopPropagation();
            }
        }

        // ---------------------------------------------------------------- header

        private VisualElement BuildHeader()
        {
            header = GitUi.Column("gfu-header");

            var top = GitUi.Row("gfu-header__top");
            var repoButton = new Button(ShowRepositoryMenu);
            repoButton.AddToClassList("gfu-repo-button");
            repoButton.tooltip = "Switch between the project and any package with its own Git history";
            repoButton.Add(new GitIcon("repo", 20));
            var repoText = GitUi.Column("gfu-repo-button__text");
            repoTitle = GitUi.Text("Repository", "gfu-repo-button__title");
            repoSubtitle = GitUi.Text(string.Empty, "gfu-repo-button__subtitle");
            repoText.Add(repoTitle);
            repoText.Add(repoSubtitle);
            repoButton.Add(repoText);
            repoButton.Add(new GitIcon("chevron-down", 16));
            top.Add(repoButton);
            top.Add(GitUi.Button(null, () => ShowTab(GitTab.Settings), "ghost", "gear", "Settings"));
            header.Add(top);

            var actions = GitUi.Row("gfu-header__actions");
            branchButton = new Button(ToggleBranchPicker);
            branchButton.AddToClassList("gfu-branch-button");
            branchButton.tooltip = "Switch or create a branch";
            branchButton.Add(new GitIcon("branch", 16));
            var branchText = GitUi.Column("gfu-branch-button__text");
            branchText.Add(GitUi.Text("Branch", "gfu-caption"));
            branchLabel = GitUi.Text("—", "gfu-branch-button__name");
            branchText.Add(branchLabel);
            branchButton.Add(branchText);
            branchButton.Add(new GitIcon("chevron-down", 16));
            actions.Add(branchButton);

            syncButton = new SyncButton(OnSyncClicked);
            actions.Add(syncButton);
            header.Add(actions);

            tabBar = GitUi.Row("gfu-tabs");
            foreach (var tab in new[] { GitTab.Changes, GitTab.History, GitTab.Locks })
            {
                var button = new TabButton(tab.ToString(), () => ShowTab(tab));
                tabButtons[tab] = button;
                tabBar.Add(button);
            }
            header.Add(tabBar);
            return header;
        }

        private VisualElement BuildEmptyState()
        {
            var e = GitUi.Column("gfu-empty gfu-empty--window");
            e.Add(new GitIcon("repo", 40));
            e.Add(GitUi.Text("No Git repository here yet", "gfu-empty__title"));
            e.Add(GitUi.Text("This project isn't connected to Git. Pick a repository inside the project, or set one up from the legacy window.", "gfu-empty__body"));
            var row = GitUi.Row("gfu-empty__actions");
            row.Add(GitUi.Button("Choose repository", ShowRepositoryMenu, "primary", "repo"));
            row.Add(GitUi.Button("Set up in legacy window", () => Menus.ShowWindow(EntryPoint.ApplicationManager), "secondary"));
            e.Add(row);
            return e;
        }

        private void UpdateHeader()
        {
            var hasRepo = session.HasRepository;
            repoView.style.display = hasRepo ? DisplayStyle.Flex : DisplayStyle.None;
            emptyView.style.display = hasRepo ? DisplayStyle.None : DisplayStyle.Flex;
            if (!hasRepo)
                return;

            var repoPath = session.RepositoryPath ?? string.Empty;
            var projectPath = session.ProjectPath ?? string.Empty;
            var isProject = string.Equals(GitSession.Normalize(repoPath).TrimEnd('/'), GitSession.Normalize(projectPath).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            var folder = Path.GetFileName(repoPath.TrimEnd('/', '\\'));
            repoTitle.text = Prettify(folder);

            var owner = RemoteOwner(session.RemoteUrl);
            var where = isProject ? "Project" : "Package";
            if (!isProject)
            {
                var rel = session.ToProjectPath(".") ?? string.Empty;
                if (!string.IsNullOrEmpty(rel) && rel != ".")
                    where = "Package · " + rel.TrimEnd('/', '.');
            }
            repoSubtitle.text = owner != null ? where + " · " + owner + " on GitHub" : session.HasRemote ? where : where + " · not on GitHub yet";

            branchLabel.text = string.IsNullOrEmpty(session.BranchName) ? "—" : session.BranchName;

            tabButtons[GitTab.Changes].SetCount(changesPanel.Count);
            tabButtons[GitTab.Locks].SetCount(session.Locks.Count);
        }

        private static string Prettify(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return "Repository";
            return folder.Replace('-', ' ').Replace('_', ' ');
        }

        private static string RemoteOwner(string remoteUrl)
        {
            var shortUrl = GitUi.ShortRemote(remoteUrl);
            if (string.IsNullOrEmpty(shortUrl))
                return null;
            var parts = shortUrl.Split('/');
            return parts.Length >= 3 ? parts[1] : null;
        }

        // ---------------------------------------------------------------- sync

        private enum SyncAction { None, Fetch, GetLatest, Push, Sync, Settings }

        private SyncAction currentSyncAction;

        private void UpdateSyncButton()
        {
            if (syncButton == null || !session.HasRepository)
                return;

            if (session.IsOperationRunning)
            {
                var detail = session.OperationDetail;
                var percent = session.OperationProgress;
                var label = session.OperationLabel;
                if (!session.OperationIsBackground && percent > 0.01f && percent < 0.999f)
                    label = label.TrimEnd('…') + "…  " + Mathf.RoundToInt(percent * 100) + "%";
                syncButton.Set(SyncButton.Tone.Busy, "sync", label, string.IsNullOrEmpty(detail) ? "Working with GitHub" : detail, percent);
                currentSyncAction = SyncAction.None;
                return;
            }

            if (!string.IsNullOrEmpty(session.LastError))
            {
                syncButton.Set(SyncButton.Tone.Error, "offline", "Sync problem", session.LastError + " Click to retry.");
                currentSyncAction = SyncAction.Fetch;
                return;
            }

            if (!session.HasRemote)
            {
                syncButton.Set(SyncButton.Tone.Secondary, "offline", "Not on GitHub", "Add a GitHub address in Settings");
                currentSyncAction = SyncAction.Settings;
                return;
            }

            var ahead = session.Ahead;
            var behind = session.Behind;
            if (!session.IsTracking && !string.IsNullOrEmpty(session.BranchName))
            {
                syncButton.Set(SyncButton.Tone.Primary, "upload", "Publish branch", "Share " + session.BranchName + " with the team");
                currentSyncAction = SyncAction.Push;
            }
            else if (behind > 0 && ahead > 0)
            {
                syncButton.Set(SyncButton.Tone.Primary, "sync", "Sync", "Get " + GitUi.Plural(behind, "update") + ", then push your " + ahead);
                currentSyncAction = SyncAction.Sync;
            }
            else if (behind > 0)
            {
                syncButton.Set(SyncButton.Tone.Primary, "download", "Get latest", GitUi.Plural(behind, "update") + " from your team");
                currentSyncAction = SyncAction.GetLatest;
            }
            else if (ahead > 0)
            {
                syncButton.Set(SyncButton.Tone.Primary, "upload", "Push to team", GitUi.Plural(ahead, "commit") + " only you have");
                currentSyncAction = SyncAction.Push;
            }
            else
            {
                var ago = GitUi.Ago(session.LastChecked);
                syncButton.Set(SyncButton.Tone.Secondary, "check", "Up to date", ago != null ? "Checked for updates " + ago : "Click to check for updates");
                currentSyncAction = SyncAction.Fetch;
            }
        }

        private void OnSyncClicked()
        {
            switch (currentSyncAction)
            {
                case SyncAction.Fetch:
                    session.ClearError();
                    session.Fetch(false, (ok, ex) =>
                    {
                        if (ok && session.Behind == 0)
                            ShowToast("You're up to date.", "good");
                    });
                    break;
                case SyncAction.GetLatest:
                    GetLatest(false);
                    break;
                case SyncAction.Push:
                    Push();
                    break;
                case SyncAction.Sync:
                    GetLatest(true);
                    break;
                case SyncAction.Settings:
                    ShowTab(GitTab.Settings);
                    break;
            }
        }

        /// <summary>Gets the team's work. Uncommitted changes must be committed first so nothing is overwritten.</summary>
        public void GetLatest(bool pushAfterwards)
        {
            if (!session.HasRepository || !session.HasRemote)
                return;

            var uncommitted = changesPanel.Count;
            if (uncommitted > 0)
            {
                var updates = session.Behind > 0 ? "the " + GitUi.Plural(session.Behind, "update") : "the latest";
                var commitFirst = EditorUtility.DisplayDialog(
                    "Commit your work before getting the latest",
                    "You have " + GitUi.Plural(uncommitted, "uncommitted change") + ". Commit them first to keep them safe, and we'll get " + updates + " from your team straight after.",
                    "Commit first", "Cancel");
                if (commitFirst)
                {
                    getLatestAfterCommit = true;
                    ShowTab(GitTab.Changes);
                    changesPanel.FocusSummary();
                    changesPanel.Refresh(GitDataKind.Operation);
                }
                return;
            }

            RunGetLatest(pushAfterwards);
        }

        /// <summary>Called by the Changes panel once a commit (and optional push) has finished.</summary>
        public void OnCommitFinished(bool success)
        {
            if (!getLatestAfterCommit)
                return;
            getLatestAfterCommit = false;
            if (success && session.HasRemote)
                RunGetLatest(true);
        }

        public void CancelGetLatestAfterCommit()
        {
            getLatestAfterCommit = false;
            changesPanel.Refresh(GitDataKind.Operation);
        }

        private void RunGetLatest(bool pushAfterwards)
        {
            var behind = session.Behind;
            var started = session.Run("Getting latest…", session.Repository.Pull(), (ok, ex) =>
            {
                if (ok)
                {
                    AssetDatabase.Refresh();
                    ShowToast(behind > 0 ? "Got " + GitUi.Plural(behind, "update") + " from your team." : "You have the latest from your team.", "good");
                    if (pushAfterwards && (session.Ahead > 0 || !session.IsTracking))
                        Push();
                    else if (pushAfterwards)
                        session.Repository.Refresh(CacheType.GitAheadBehind);
                }
                else
                {
                    ShowToast("Couldn't get the latest: " + GitSession.FriendlyError(ex), "error", sticky: true);
                }
            });
            if (!started)
                ShowToast("Still busy with the last action. Try again in a moment.", "info");
        }

        public void Push()
        {
            if (!session.HasRepository || !session.HasRemote)
                return;
            var ahead = session.Ahead;
            var started = session.Run("Pushing to the team…", session.Repository.Push(), (ok, ex) =>
            {
                if (ok)
                    ShowToast(ahead > 0 ? "Pushed " + GitUi.Plural(ahead, "commit") + " to the team." : "Your branch is now on GitHub.", "good");
                else
                    ShowToast("Couldn't push: " + GitSession.FriendlyError(ex), "error", sticky: true);
            });
            if (!started)
                ShowToast("Still busy with the last action. Try again in a moment.", "info");
        }

        public void RefreshAll()
        {
            session.Refresh();
            ShowToast("Refreshing…", "info");
        }

        // ---------------------------------------------------------------- tabs

        public void ShowTab(GitTab tab)
        {
            if (tab == GitTab.Settings && activeTab != GitTab.Settings)
                tabBeforeSettings = activeTab;
            activeTab = tab;
            branchPicker?.Close();
            ApplyTab();
        }

        public void CloseSettings()
        {
            ShowTab(tabBeforeSettings == GitTab.Settings ? GitTab.Changes : tabBeforeSettings);
        }

        private void ApplyTab()
        {
            if (content == null)
                return;
            var inSettings = activeTab == GitTab.Settings;
            header.EnableInClassList("gfu-header--settings", inSettings);
            header.style.display = inSettings ? DisplayStyle.None : DisplayStyle.Flex;
            changesPanel.Root.style.display = activeTab == GitTab.Changes ? DisplayStyle.Flex : DisplayStyle.None;
            historyPanel.Root.style.display = activeTab == GitTab.History ? DisplayStyle.Flex : DisplayStyle.None;
            locksPanel.Root.style.display = activeTab == GitTab.Locks ? DisplayStyle.Flex : DisplayStyle.None;
            settingsPanel.Root.style.display = inSettings ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var pair in tabButtons)
                pair.Value.SetActive(pair.Key == activeTab);

            if (activeTab == GitTab.Locks)
                locksPanel.OnShow();
            else if (activeTab == GitTab.Settings)
                settingsPanel.OnShow();
            else if (activeTab == GitTab.History)
                historyPanel.OnShow();
        }

        private void OnSessionChanged(GitDataKind kind)
        {
            if (root == null)
                return;
            if ((kind & GitDataKind.Repository) != 0)
            {
                discoveredRepositories = null;
                getLatestAfterCommit = false;
                branchPicker?.Close();
            }
            changesPanel.Refresh(kind);
            historyPanel.Refresh(kind);
            locksPanel.Refresh(kind);
            settingsPanel.Refresh(kind);
            branchPicker.Refresh(kind);
            UpdateHeader();
            UpdateSyncButton();
        }

        // ---------------------------------------------------------------- repositories

        public void ShowRepositoryMenu()
        {
            var projectPath = session.ProjectPath;
            if (discoveredRepositories == null && !string.IsNullOrEmpty(projectPath))
                discoveredRepositories = RepositorySelector.Scan(projectPath);

            var menu = new GenericMenu();
            var current = GitSession.Normalize(session.RepositoryPath ?? string.Empty).TrimEnd('/');
            var selected = EnvironmentCache.Instance.SelectedRepositoryPath;

            menu.AddItem(new GUIContent("Automatic (the project's repository)"), string.IsNullOrEmpty(selected), () => SwitchRepository(null));
            if (discoveredRepositories != null && discoveredRepositories.Count > 0)
            {
                menu.AddSeparator(string.Empty);
                foreach (var entry in discoveredRepositories)
                {
                    var path = entry.Path;
                    var isCurrent = string.Equals(GitSession.Normalize(path).TrimEnd('/'), current, StringComparison.OrdinalIgnoreCase);
                    var label = entry.DisplayName == "<project root>" ? "Project" : entry.DisplayName;
                    // GenericMenu treats '/' as a submenu separator; keep every repo as one flat entry.
                    menu.AddItem(new GUIContent(label.Replace('/', '∕')), isCurrent, () => SwitchRepository(path));
                }
            }
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Browse for a folder…"), false, () =>
            {
                var picked = EditorUtility.OpenFolderPanel("Choose a Git repository", projectPath ?? string.Empty, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                    SwitchRepository(picked);
            });
            menu.AddItem(new GUIContent("Look for repositories again"), false, () =>
            {
                discoveredRepositories = null;
                ShowRepositoryMenu();
            });
            menu.ShowAsContext();
        }

        private void SwitchRepository(string path)
        {
            try
            {
                RepositorySwitcher.Switch(path);
                ShowToast("Switched to " + (string.IsNullOrEmpty(path) ? "the project's repository" : Prettify(Path.GetFileName(path.TrimEnd('/', '\\')))) + ".", "info");
            }
            catch (Exception ex)
            {
                ShowToast("Couldn't switch repository: " + ex.Message, "error", sticky: true);
            }
        }

        // ---------------------------------------------------------------- branch picker

        private void ToggleBranchPicker()
        {
            if (branchPicker.IsOpen)
            {
                branchPicker.Close();
                return;
            }
            var top = branchButton.worldBound.yMax - root.worldBound.y + 4;
            var left = branchButton.worldBound.xMin - root.worldBound.x;
            branchPicker.Open(left, top);
        }

        public void SetBranchButtonOpen(bool open)
        {
            branchButton.EnableInClassList("gfu-branch-button--open", open);
        }

        // ---------------------------------------------------------------- toast

        public void ShowToast(string message, string tone = "info", bool sticky = false)
        {
            if (toast == null)
                return;
            toastLabel.text = message;
            foreach (var t in new[] { "info", "good", "error", "warn" })
                toast.EnableInClassList("gfu-toast--" + t, t == tone);
            toast.style.display = DisplayStyle.Flex;
            toastHide?.Pause();
            if (!sticky)
                toastHide = toast.schedule.Execute(HideToast).StartingIn(tone == "error" ? 8000 : 4000);
        }

        public void HideToast()
        {
            if (toast != null)
                toast.style.display = DisplayStyle.None;
        }

        // ---------------------------------------------------------------- helpers

        private class TabButton : Button
        {
            private readonly Label count;

            public TabButton(string text, Action onClick) : base(onClick)
            {
                AddToClassList("gfu-tab");
                var label = new Label(text);
                label.AddToClassList("gfu-tab__label");
                Add(label);
                count = new Label();
                count.AddToClassList("gfu-tab__count");
                Add(count);
                SetCount(0);
            }

            public void SetCount(int value)
            {
                count.text = value > 999 ? "999+" : value.ToString();
                count.style.display = value > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            public void SetActive(bool active)
            {
                EnableInClassList("gfu-tab--active", active);
            }
        }
    }

    /// <summary>The single button that always shows the next sync step.</summary>
    class SyncButton : Button
    {
        public enum Tone { Primary, Secondary, Busy, Error }

        private readonly GitIcon icon;
        private readonly Label title;
        private readonly Label subtitle;
        private readonly VisualElement progressTrack;
        private readonly VisualElement progressFill;

        public SyncButton(Action onClick) : base(onClick)
        {
            AddToClassList("gfu-sync");
            icon = new GitIcon("check", 18);
            icon.AddToClassList("gfu-sync__icon");
            Add(icon);
            var text = GitUi.Column("gfu-sync__text");
            title = GitUi.Text(string.Empty, "gfu-sync__title");
            subtitle = GitUi.Text(string.Empty, "gfu-sync__subtitle");
            text.Add(title);
            text.Add(subtitle);
            Add(text);
            progressTrack = GitUi.Div("gfu-sync__track");
            progressFill = GitUi.Div("gfu-sync__fill");
            progressTrack.Add(progressFill);
            Add(progressTrack);
        }

        public void Set(Tone tone, string iconName, string titleText, string subtitleText, float progress = -1f)
        {
            EnableInClassList("gfu-sync--primary", tone == Tone.Primary);
            EnableInClassList("gfu-sync--secondary", tone == Tone.Secondary);
            EnableInClassList("gfu-sync--busy", tone == Tone.Busy);
            EnableInClassList("gfu-sync--error", tone == Tone.Error);
            icon.IconName = iconName;
            if (title.text != titleText)
                title.text = titleText;
            if (subtitle.text != subtitleText)
                subtitle.text = subtitleText;
            tooltip = subtitleText;
            var showProgress = tone == Tone.Busy;
            progressTrack.style.display = showProgress ? DisplayStyle.Flex : DisplayStyle.None;
            if (showProgress)
            {
                // Unknown progress shows as a slim indeterminate sliver rather than an empty bar.
                var value = progress > 0.01f ? Mathf.Clamp01(progress) : 0.15f;
                progressFill.style.width = Length.Percent(value * 100f);
            }
            SetEnabled(tone != Tone.Busy);
        }
    }

    /// <summary>Switches Git for Unity to another repository and re-binds any open windows.</summary>
    static class RepositorySwitcher
    {
        public static void Switch(string path)
        {
            EnvironmentCache.Instance.SetSelectedRepository(string.IsNullOrEmpty(path) ? null : path);

            // Rebuild the application manager and environment against the new repository.
            EntryPoint.Restart();

            // Per-repository caches are shared singletons; invalidate them so the new repository
            // is re-queried instead of showing the previous one's data.
            var env = EntryPoint.ApplicationManager.Environment;
            env.CacheContainer.InvalidateAll();

            var legacy = Window.GetWindow();
            if (legacy != null)
                legacy.InitializeWindow(EntryPoint.ApplicationManager);

            var repository = env.Repository;
            if (repository != null)
            {
                repository.Refresh(CacheType.RepositoryInfo);
                repository.Refresh(CacheType.Branches);
                repository.Refresh(CacheType.GitStatus);
            }
        }
    }
}
