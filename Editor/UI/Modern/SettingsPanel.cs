using System;
using System.IO;
using Unity.Editor.Tasks.Logging;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    /// <summary>
    /// Settings ordered for people: who you are, which project, whether the project is healthy,
    /// display options, and the plumbing folded under Advanced.
    /// </summary>
    class SettingsPanel
    {
        private readonly GitWindow window;
        private GitSession Session => window.Session;

        public VisualElement Root { get; }

        private readonly TextField nameField;
        private readonly TextField emailField;
        private readonly Button saveUser;
        private readonly Label repoLabel;
        private readonly TextField remoteField;
        private readonly VisualElement healthList;
        private readonly Toggle projectIcons;
        private readonly Toggle hierarchyIcons;
        private readonly Toggle autoFetch;
        private readonly TextField remoteNameField;
        private readonly TextField remoteUrlField;
        private readonly Button saveRemote;
        private readonly IntegerField gitTimeout;
        private readonly IntegerField webTimeout;
        private readonly Toggle traceLogging;
        private readonly Toggle overrideBlocks;
        private readonly Label checksCount;

        public SettingsPanel(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Column("gfu-panel gfu-settings");

            var bar = GitUi.Row("gfu-settings__bar");
            bar.Add(GitUi.Button("Back", window.CloseSettings, "ghost", "chevron-left"));
            bar.Add(GitUi.Text("Settings", "gfu-settings__title"));
            Root.Add(bar);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("gfu-settings__scroll");
            Root.Add(scroll);

            // You
            nameField = LabeledField("Name", out var nameRow);
            emailField = LabeledField("Email", out var emailRow);
            nameField.RegisterValueChangedCallback(_ => UpdateSaveUser());
            emailField.RegisterValueChangedCallback(_ => UpdateSaveUser());
            saveUser = GitUi.Button("Save", SaveUser, "secondary");
            var you = Card(scroll, "You", "Your name appears on commits and locks so the team knows who changed what.");
            you.Add(nameRow);
            you.Add(emailRow);
            you.Add(GitUi.Row("gfu-card__actions", GitUi.Spacer(), saveUser));

            // Project
            var project = Card(scroll, "Project", "Which folder this window works with. Packages with their own Git history appear here too.");
            var repoRow = GitUi.Row("gfu-field-row");
            repoRow.Add(GitUi.Text("Repository", "gfu-field-row__label"));
            var repoButton = new Button(window.ShowRepositoryMenu);
            repoButton.AddToClassList("gfu-dropdown");
            repoButton.Add(new GitIcon("repo", 14));
            repoLabel = GitUi.Text(string.Empty, "gfu-dropdown__label");
            repoButton.Add(repoLabel);
            repoButton.Add(new GitIcon("chevron-down", 14));
            repoRow.Add(repoButton);
            project.Add(repoRow);
            remoteField = LabeledField("GitHub", out var remoteRow);
            remoteField.isReadOnly = true;
            remoteRow.Add(GitUi.Button(null, () =>
            {
                EditorGUIUtility.systemCopyBuffer = Session.RemoteUrl ?? string.Empty;
                window.ShowToast("Copied the GitHub address.", "info");
            }, "ghost", "copy", "Copy address"));
            remoteRow.Add(GitUi.Button(null, () =>
            {
                var url = GitUi.WebUrl(Session.RemoteUrl);
                if (!string.IsNullOrEmpty(url))
                    Application.OpenURL(url);
            }, "ghost", "external", "Open on GitHub"));
            project.Add(remoteRow);

            // Health
            var health = Card(scroll, "Project health", "Checked each time you open Settings.");
            healthList = GitUi.Column("gfu-health");
            health.Add(healthList);
            health.Add(GitUi.Row("gfu-card__actions", GitUi.Button("Repair setup", RepairSetup, "secondary", "shield",
                "Writes a Unity-friendly .gitattributes (if missing) and configures Unity's smart merge for scenes and prefabs.")));

            // Display
            var display = Card(scroll, "Display", null);
            projectIcons = SwitchRow(display, "Show Git status on icons in the Project window", ApplicationConfiguration.ProjectIconsEnabled, value =>
            {
                ApplicationConfiguration.ProjectIconsEnabled = value;
                Session.Manager?.UserSettings.Set(Constants.ProjectIconsEnabledKey, value);
                EditorApplication.RepaintProjectWindow();
            });
            hierarchyIcons = SwitchRow(display, "Show Git status in the Hierarchy", ApplicationConfiguration.HierarchyIconsEnabled, value =>
            {
                ApplicationConfiguration.HierarchyIconsEnabled = value;
                Session.Manager?.UserSettings.Set(Constants.HierarchyIconsEnabledKey, value);
                EditorApplication.RepaintHierarchyWindow();
            });
            autoFetch = SwitchRow(display, "Check GitHub for team updates every few minutes", EditorPrefs.GetBool(GitSession.AutoFetchPrefKey, true),
                value => EditorPrefs.SetBool(GitSession.AutoFetchPrefKey, value));

            // Advanced
            var advanced = new Foldout { text = "Advanced", value = false };
            advanced.AddToClassList("gfu-advanced");
            advanced.Add(GitUi.Text("Remote, timeouts, logging and pre-commit overrides. You rarely need these.", "gfu-card__subtitle"));

            advanced.Add(GitUi.Text("Remote", "gfu-advanced__heading"));
            remoteNameField = LabeledField("Name", out var remoteNameRow);
            remoteUrlField = LabeledField("URL", out var remoteUrlRow);
            remoteNameField.RegisterValueChangedCallback(_ => UpdateSaveRemote());
            remoteUrlField.RegisterValueChangedCallback(_ => UpdateSaveRemote());
            saveRemote = GitUi.Button("Save remote", SaveRemote, "secondary");
            advanced.Add(remoteNameRow);
            advanced.Add(remoteUrlRow);
            advanced.Add(GitUi.Row("gfu-card__actions", GitUi.Spacer(), saveRemote));

            advanced.Add(GitUi.Text("Timeouts", "gfu-advanced__heading"));
            gitTimeout = IntRow(advanced, "Git commands (ms)", ApplicationConfiguration.GitTimeout, value =>
            {
                ApplicationConfiguration.GitTimeout = value;
                Session.Manager?.UserSettings.Set(Constants.GitTimeoutKey, value);
            });
            webTimeout = IntRow(advanced, "Web requests (ms)", ApplicationConfiguration.WebTimeout, value =>
            {
                ApplicationConfiguration.WebTimeout = value;
                Session.Manager?.UserSettings.Set(Constants.WebTimeoutKey, value);
            });

            advanced.Add(GitUi.Text("Pre-commit checks", "gfu-advanced__heading"));
            checksCount = GitUi.Text(string.Empty, "gfu-hint");
            advanced.Add(checksCount);
            overrideBlocks = SwitchRow(advanced, "Let me commit even when a check blocks (studio override)", PreCommit.PreCommitCheckRunner.OverrideBlocks,
                value => PreCommit.PreCommitCheckRunner.OverrideBlocks = value);
            advanced.Add(GitUi.Row("gfu-card__actions", GitUi.Button("Look for checks again", () =>
            {
                PreCommit.PreCommitCheckRunner.ClearCache();
                OnShow();
            }, "secondary")));

            advanced.Add(GitUi.Text("Troubleshooting", "gfu-advanced__heading"));
            traceLogging = SwitchRow(advanced, "Detailed logging", LogHelper.TracingEnabled, value =>
            {
                LogHelper.TracingEnabled = value;
                Session.Manager?.UserSettings.Set(Constants.TraceLoggingKey, value);
            });
            advanced.Add(GitUi.Row("gfu-card__actions",
                GitUi.Button("Open legacy window", () => Menus.ShowWindow(EntryPoint.ApplicationManager), "secondary", null,
                    "The previous Git window, including Git installation settings."),
                GitUi.Button("Show log file", () =>
                {
                    var log = Session.Environment?.LogPath.ToString();
                    if (!string.IsNullOrEmpty(log))
                        EditorUtility.RevealInFinder(log);
                }, "ghost")));

            var advancedCard = GitUi.Column("gfu-card");
            advancedCard.Add(advanced);
            scroll.Add(advancedCard);
        }

        public void OnShow()
        {
            Session.User?.CheckAndRaiseEventsIfCacheNewer(CacheType.GitUser, default(CacheUpdateEvent));
            LoadUser();
            LoadRemote();
            RebuildHealth();
            projectIcons.SetValueWithoutNotify(ApplicationConfiguration.ProjectIconsEnabled);
            hierarchyIcons.SetValueWithoutNotify(ApplicationConfiguration.HierarchyIconsEnabled);
            autoFetch.SetValueWithoutNotify(EditorPrefs.GetBool(GitSession.AutoFetchPrefKey, true));
            gitTimeout.SetValueWithoutNotify(ApplicationConfiguration.GitTimeout);
            webTimeout.SetValueWithoutNotify(ApplicationConfiguration.WebTimeout);
            traceLogging.SetValueWithoutNotify(LogHelper.TracingEnabled);
            overrideBlocks.SetValueWithoutNotify(PreCommit.PreCommitCheckRunner.OverrideBlocks);
        }

        public void Refresh(GitDataKind kind)
        {
            if ((kind & GitDataKind.User) != 0)
                LoadUser();
            if ((kind & (GitDataKind.Branch | GitDataKind.Repository)) != 0)
                LoadRemote();
        }

        // ---------------------------------------------------------------- you

        private void LoadUser()
        {
            nameField.SetValueWithoutNotify(Session.UserName);
            emailField.SetValueWithoutNotify(Session.UserEmail);
            UpdateSaveUser();
        }

        private void UpdateSaveUser()
        {
            var name = nameField.value?.Trim();
            var email = emailField.value?.Trim();
            saveUser.SetEnabled(!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(email) &&
                                (name != Session.UserName || email != Session.UserEmail));
        }

        private void SaveUser()
        {
            var user = Session.User;
            if (user == null)
                return;
            user.SetNameAndEmail(nameField.value.Trim(), emailField.value.Trim());
            saveUser.SetEnabled(false);
            window.ShowToast("Saved. New commits will use this name.", "good");
        }

        // ---------------------------------------------------------------- project

        private void LoadRemote()
        {
            var repoPath = Session.RepositoryPath;
            var projectPath = Session.ProjectPath;
            if (string.IsNullOrEmpty(repoPath))
                repoLabel.text = "None";
            else if (string.Equals(GitSession.Normalize(repoPath).TrimEnd('/'), GitSession.Normalize(projectPath ?? string.Empty).TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                repoLabel.text = "This project";
            else
                repoLabel.text = Session.ToProjectPath(".")?.TrimEnd('/', '.') ?? repoPath;

            remoteField.SetValueWithoutNotify(GitUi.ShortRemote(Session.RemoteUrl) ?? "Not connected to GitHub");
            remoteNameField.SetValueWithoutNotify(Session.RemoteName ?? "origin");
            remoteUrlField.SetValueWithoutNotify(Session.RemoteUrl ?? string.Empty);
            UpdateSaveRemote();
        }

        private void UpdateSaveRemote()
        {
            var name = remoteNameField.value?.Trim();
            var url = remoteUrlField.value?.Trim();
            saveRemote.SetEnabled(Session.HasRepository && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) &&
                                  (name != (Session.RemoteName ?? "origin") || url != (Session.RemoteUrl ?? string.Empty)));
        }

        private void SaveRemote()
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            Session.Run("Saving remote…", repo.SetupRemote(remoteNameField.value.Trim(), remoteUrlField.value.Trim()), (ok, ex) =>
                window.ShowToast(ok ? "Saved the remote." : "Couldn't save the remote: " + GitSession.FriendlyError(ex), ok ? "good" : "error"));
        }

        // ---------------------------------------------------------------- health

        private void RebuildHealth()
        {
            healthList.Clear();
            var repoPath = Session.RepositoryPath;
            var env = Session.Environment;

            var hasAttributes = !string.IsNullOrEmpty(repoPath) && File.Exists(Path.Combine(repoPath, ".gitattributes"));
            HealthRow(hasAttributes, "Unity files are set up for Git",
                hasAttributes ? ".gitattributes found · big files go through Git LFS" : "No .gitattributes yet. Repair setup adds one.");

            var mergeState = SmartMergeConfigured(repoPath);
            HealthRow(mergeState ?? false, "Smart merge for scenes and prefabs",
                mergeState == true ? "Unity YAML Merge is configured" : mergeState == false ? "Not configured. Repair setup turns it on." : "Couldn't read the Git config for this repository.",
                unknown: mergeState == null);

            var gitPath = env != null && env.GitExecutablePath.IsInitialized ? env.GitExecutablePath.ToString() : null;
            HealthRow(gitPath != null, "Git is installed", gitPath ?? "Git wasn't found. Open the legacy window to pick an installation.");

            var checks = 0;
            try { checks = PreCommit.PreCommitCheckRunner.DiscoverChecks().Count; }
            catch (Exception) { }
            checksCount.text = GitUi.Plural(checks, "check") + " found in this project.";
            HealthRow(true, checks > 0 ? GitUi.Plural(checks, "pre-commit check") + " active" : "No pre-commit checks",
                checks > 0 ? "They run each time you commit." : "Your team can add checks that run before each commit.", unknown: checks == 0);
        }

        /// <summary>true / false when known, null when the config can't be read (e.g. a worktree).</summary>
        private static bool? SmartMergeConfigured(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
                return null;
            try
            {
                var config = Path.Combine(repoPath, ".git", "config");
                if (!File.Exists(config))
                    return null;
                var text = File.ReadAllText(config);
                return text.IndexOf("unityyamlmerge", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void HealthRow(bool ok, string title, string subtitle, bool unknown = false)
        {
            var row = GitUi.Row("gfu-health__row");
            var icon = new GitIcon(unknown ? "info" : ok ? "check" : "warn", 15);
            icon.AddToClassList(unknown ? "gfu-icon--muted" : ok ? "gfu-icon--good" : "gfu-icon--warn");
            row.Add(icon);
            var col = GitUi.Column("gfu-health__text");
            col.Add(GitUi.Text(title, "gfu-health__title"));
            col.Add(GitUi.Text(subtitle, "gfu-health__sub"));
            row.Add(col);
            healthList.Add(row);
        }

        private void RepairSetup()
        {
            var repo = Session.Repository;
            var env = Session.Environment;
            if (repo == null || env == null)
                return;
            var mergeTool = env.UnityApplicationContents.ToSPath().Combine("Tools", "UnityYAMLMerge" + env.ExecutableExtension);
            var hasAttributes = repo.LocalPath.Combine(".gitattributes").FileExists();
            var task = hasAttributes
                ? repo.UpdateMergeSettings(mergeTool)
                : repo.UpdateGitAttributes().Then(repo.UpdateMergeSettings(mergeTool));
            Session.Run("Repairing setup…", task, (ok, ex) =>
            {
                RebuildHealth();
                window.ShowToast(ok ? "Project setup repaired." : "Couldn't repair setup: " + GitSession.FriendlyError(ex), ok ? "good" : "error");
            });
        }

        // ---------------------------------------------------------------- helpers

        private static VisualElement Card(VisualElement parent, string title, string subtitle)
        {
            var card = GitUi.Column("gfu-card");
            card.Add(GitUi.Text(title, "gfu-card__title"));
            if (!string.IsNullOrEmpty(subtitle))
                card.Add(GitUi.Text(subtitle, "gfu-card__subtitle"));
            parent.Add(card);
            return card;
        }

        private static TextField LabeledField(string label, out VisualElement row)
        {
            row = GitUi.Row("gfu-field-row");
            row.Add(GitUi.Text(label, "gfu-field-row__label"));
            var field = GitUi.Field(string.Empty);
            field.AddToClassList("gfu-grow");
            row.Add(field);
            return field;
        }

        private static Toggle SwitchRow(VisualElement parent, string label, bool value, Action<bool> onChange)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.AddToClassList("gfu-toggle");
            toggle.AddToClassList("gfu-toggle--row");
            toggle.RegisterValueChangedCallback(evt => onChange(evt.newValue));
            parent.Add(toggle);
            return toggle;
        }

        private static IntegerField IntRow(VisualElement parent, string label, int value, Action<int> onChange)
        {
            var field = new IntegerField(label) { value = value, isDelayed = true };
            field.AddToClassList("gfu-int-field");
            field.RegisterValueChangedCallback(evt => onChange(Mathf.Max(0, evt.newValue)));
            parent.Add(field);
            return field;
        }
    }
}
