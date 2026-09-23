using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    /// <summary>
    /// Who is working on what: your locks (with a nudge to push before unlocking) and teammates'
    /// locks, with force-unlock tucked away in a menu.
    /// </summary>
    class LocksPanel
    {
        private enum Filter { All, Mine, Team }

        private readonly GitWindow window;
        private GitSession Session => window.Session;

        public VisualElement Root { get; }

        private readonly TextField search;
        private readonly Button allButton;
        private readonly Button mineButton;
        private readonly Button teamButton;
        private readonly ScrollView scroll;
        private readonly VisualElement noRemote;
        private Filter filter = Filter.All;

        public LocksPanel(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Column("gfu-panel gfu-locks");

            Root.Add(GitUi.Text("Scenes, models and textures can't be combined like code. Lock a file while you work on it so nobody else overwrites it.", "gfu-intro"));

            var toolbar = GitUi.Row("gfu-toolbar");
            search = GitUi.Field("Find a locked file");
            search.AddToClassList("gfu-search");
            search.RegisterValueChangedCallback(_ => Rebuild());
            toolbar.Add(search);
            var segmented = GitUi.Row("gfu-segmented");
            allButton = GitUi.Button("All", () => SetFilter(Filter.All), "segment");
            mineButton = GitUi.Button("Mine", () => SetFilter(Filter.Mine), "segment");
            teamButton = GitUi.Button("Team", () => SetFilter(Filter.Team), "segment");
            segmented.Add(allButton);
            segmented.Add(mineButton);
            segmented.Add(teamButton);
            toolbar.Add(segmented);
            toolbar.Add(GitUi.Button(null, () =>
            {
                Session.RefreshLocks();
                window.ShowToast("Checking GitHub for locks…", "info");
            }, "ghost", "sync", "Refresh locks from GitHub"));
            Root.Add(toolbar);

            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("gfu-locks__scroll");
            Root.Add(scroll);

            noRemote = GitUi.Column("gfu-empty");
            noRemote.Add(new GitIcon("lock", 32));
            noRemote.Add(GitUi.Text("Locks need GitHub", "gfu-empty__title"));
            noRemote.Add(GitUi.Text("Add this repository's GitHub address in Settings to lock files for your team.", "gfu-empty__body"));
            noRemote.Add(GitUi.Button("Open Settings", () => window.ShowTab(GitTab.Settings), "secondary", "gear"));
            Root.Add(noRemote);

            var tip = GitUi.Row("gfu-tip");
            var info = new GitIcon("info", 16);
            info.AddToClassList("gfu-icon--accent");
            tip.Add(info);
            tip.Add(GitUi.Text("Lock from anywhere: right-click an asset in the Project window and choose Request Lock. Locked files show a padlock on their icon.", "gfu-tip__text"));
            Root.Add(tip);

            UpdateFilterButtons();
        }

        public void OnShow()
        {
            Session.RefreshLocks();
        }

        public void Refresh(GitDataKind kind)
        {
            if ((kind & (GitDataKind.Locks | GitDataKind.Status | GitDataKind.User | GitDataKind.Repository | GitDataKind.Branch | GitDataKind.Tracking)) != 0)
                Rebuild();
        }

        private void SetFilter(Filter value)
        {
            filter = value;
            UpdateFilterButtons();
            Rebuild();
        }

        private void UpdateFilterButtons()
        {
            var locks = Session?.Locks ?? new GitLock[0];
            var mine = locks.Count(l => Session.IsMine(l));
            GitUi.SetButtonText(allButton, "All " + locks.Count);
            GitUi.SetButtonText(mineButton, "Mine " + mine);
            GitUi.SetButtonText(teamButton, "Team " + (locks.Count - mine));
            allButton.EnableInClassList("gfu-btn--selected", filter == Filter.All);
            mineButton.EnableInClassList("gfu-btn--selected", filter == Filter.Mine);
            teamButton.EnableInClassList("gfu-btn--selected", filter == Filter.Team);
        }

        private void Rebuild()
        {
            UpdateFilterButtons();
            scroll.Clear();

            var hasRemote = Session.HasRemote;
            noRemote.style.display = hasRemote ? DisplayStyle.None : DisplayStyle.Flex;
            scroll.style.display = hasRemote ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasRemote)
                return;

            var query = search.value?.Trim();
            var locks = Session.Locks
                .Where(l => string.IsNullOrEmpty(query) || l.Path.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (l.Owner.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(l => l.LockedAt)
                .ToList();
            var mine = locks.Where(l => Session.IsMine(l)).ToList();
            var team = locks.Where(l => !Session.IsMine(l)).ToList();

            var changed = new HashSet<string>(ChangeEntry.Build(Session).Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            var unpushed = new HashSet<string>(Session.Log.Take(Session.Ahead)
                .SelectMany(e => e.Changes ?? new List<GitStatusEntry>())
                .Select(c => GitSession.Normalize(c.Path)), StringComparer.OrdinalIgnoreCase);

            if (filter != Filter.Team)
            {
                scroll.Add(GitUi.SectionLabel("Locked by you · " + mine.Count));
                if (mine.Count == 0)
                    scroll.Add(GitUi.Text("You haven't locked anything.", "gfu-section-empty"));
                foreach (var l in mine)
                {
                    var key = GitSession.Normalize(l.Path.ToString());
                    scroll.Add(MyLockRow(l, changed.Contains(key), unpushed.Contains(key)));
                }
            }

            if (filter != Filter.Mine)
            {
                scroll.Add(GitUi.SectionLabel("Locked by teammates · " + team.Count));
                if (team.Count == 0)
                    scroll.Add(GitUi.Text("Nobody else has anything locked.", "gfu-section-empty"));
                foreach (var l in team)
                    scroll.Add(TeamLockRow(l));
            }
        }

        private VisualElement BaseRow(GitLock l, out VisualElement textColumn)
        {
            var path = GitSession.Normalize(l.Path.ToString());
            var kind = AssetKinds.Classify(path);
            var projectPath = Session.ToAssetPath(path);
            var row = GitUi.Row("gfu-lock-row");
            var img = new Image { image = AssetKinds.Icon(projectPath, kind), scaleMode = ScaleMode.ScaleToFit };
            img.AddToClassList("gfu-lock-row__icon");
            row.Add(img);
            textColumn = GitUi.Column("gfu-lock-row__text");
            var title = GitUi.Row("gfu-lock-row__title");
            title.Add(GitUi.Text(System.IO.Path.GetFileName(path), "gfu-change-row__name"));
            var dir = System.IO.Path.GetDirectoryName(path);
            title.Add(GitUi.Text(string.IsNullOrEmpty(dir) ? string.Empty : GitSession.Normalize(dir), "gfu-change-row__dir"));
            textColumn.Add(title);
            row.Add(textColumn);
            row.tooltip = path;
            row.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount == 2)
                    GitUi.Ping(projectPath);
                else
                    GitUi.Select(projectPath);
            });
            return row;
        }

        private VisualElement MyLockRow(GitLock l, bool hasChanges, bool hasUnpushed)
        {
            var row = BaseRow(l, out var text);
            var since = GitUi.Since(l.LockedAt);
            var state = hasChanges ? "has changes you haven't committed" : hasUnpushed ? "has commits you haven't pushed" : "no changes";
            text.Add(GitUi.Text(Capitalize(since) + (string.IsNullOrEmpty(since) ? string.Empty : " · ") + state, "gfu-lock-row__sub"));
            if (hasChanges || hasUnpushed)
            {
                var note = GitUi.Row("gfu-lock-row__note");
                var warn = new GitIcon("warn", 12);
                warn.AddToClassList("gfu-icon--warn");
                note.Add(warn);
                note.Add(GitUi.Text(hasChanges ? "Commit and push first, then unlock so the team gets your changes." : "Push first, then unlock so the team gets your changes.", "gfu-lock-row__note-text"));
                text.Add(note);
            }
            row.Add(GitUi.Button("Unlock", () => Unlock(l, false, hasChanges || hasUnpushed), "secondary", "unlock"));
            return row;
        }

        private VisualElement TeamLockRow(GitLock l)
        {
            var row = BaseRow(l, out var text);
            var who = GitUi.Row("gfu-lock-row__who");
            who.Add(GitUi.Avatar(l.Owner.Name, 16));
            var since = GitUi.Since(l.LockedAt);
            who.Add(GitUi.Text((string.IsNullOrEmpty(l.Owner.Name) ? "Someone" : l.Owner.Name) + (string.IsNullOrEmpty(since) ? string.Empty : " · " + since), "gfu-lock-row__sub"));
            text.Add(who);
            var path = GitSession.Normalize(l.Path.ToString());
            var projectPath = Session.ToAssetPath(path);
            Button more = null;
            more = GitUi.Button(null, () =>
            {
                var menu = new GenericMenu();
                if (projectPath != null && AssetDatabase.LoadMainAssetAtPath(projectPath) != null)
                    menu.AddItem(new GUIContent("Show in Project"), false, () => GitUi.Ping(projectPath));
                else
                    menu.AddDisabledItem(new GUIContent("Show in Project"));
                menu.AddItem(new GUIContent("Copy file path"), false, () => EditorGUIUtility.systemCopyBuffer = path);
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("Force unlock…"), false, () => Unlock(l, true, false));
                menu.DropDown(more.worldBound);
            }, "ghost", "more", "More actions");
            row.Add(more);
            return row;
        }

        private void Unlock(GitLock l, bool force, bool hasChanges)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            var name = System.IO.Path.GetFileName(l.Path.ToString());

            if (force)
            {
                var owner = string.IsNullOrEmpty(l.Owner.Name) ? "the owner" : l.Owner.Name;
                if (!EditorUtility.DisplayDialog("Force unlock " + name + "?",
                        "Only do this if " + owner + " can't unlock it themselves.\n\nAny changes they haven't pushed yet may be overwritten when someone else commits this file.",
                        "Force unlock", "Cancel"))
                    return;
            }
            else if (hasChanges && !EditorUtility.DisplayDialog("Unlock before sharing your changes?",
                         "Your changes to " + name + " aren't on GitHub yet. Once it's unlocked, a teammate could start editing it and one of you would lose work.",
                         "Unlock anyway", "Cancel"))
            {
                return;
            }

            Session.Run(force ? "Force unlocking…" : "Unlocking…", repo.ReleaseLock(l.Path, force), (ok, ex) =>
            {
                window.ShowToast(ok ? "Unlocked " + name + "." : "Couldn't unlock: " + GitSession.FriendlyError(ex), ok ? "good" : "error");
                Session.RefreshLocks();
            });
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
