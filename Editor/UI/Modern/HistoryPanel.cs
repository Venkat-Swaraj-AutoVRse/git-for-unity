using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    /// <summary>
    /// A readable timeline: updates waiting on GitHub first, then commits grouped by day with
    /// people's initials, and the selected commit's files underneath.
    /// </summary>
    class HistoryPanel
    {
        private const int RowHeight = 42;

        private readonly GitWindow window;
        private GitSession Session => window.Session;

        public VisualElement Root { get; }

        private readonly TextField search;
        private readonly VisualElement incoming;
        private readonly Label incomingTitle;
        private readonly ListView list;
        private readonly TwoPaneSplitView split;
        private readonly VisualElement details;
        private readonly Label detailSummary;
        private readonly Label detailBody;
        private readonly Label detailMeta;
        private readonly Label detailId;
        private readonly VisualElement detailFiles;
        private readonly Button undoButton;
        private readonly VisualElement emptyState;

        private readonly List<HistoryItem> items = new List<HistoryItem>();
        private GitLogEntry? selected;
        // set while a "Load more" request is in flight, so the row can't be clicked twice
        private bool loadingMore;

        public HistoryPanel(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Column("gfu-panel gfu-history");

            var toolbar = GitUi.Row("gfu-toolbar");
            search = GitUi.Field("Search commits, people or files");
            search.AddToClassList("gfu-search");
            search.RegisterValueChangedCallback(_ => RebuildItems());
            toolbar.Add(search);
            Root.Add(toolbar);

            incoming = GitUi.Row("gfu-callout gfu-history__incoming");
            var down = new GitIcon("download", 18);
            down.AddToClassList("gfu-icon--accent");
            incoming.Add(down);
            var incomingText = GitUi.Column("gfu-callout__text");
            incomingTitle = GitUi.Text(string.Empty, "gfu-callout__title");
            incomingText.Add(incomingTitle);
            incomingText.Add(GitUi.Text("They're on GitHub but not on your computer yet.", "gfu-callout__body"));
            incoming.Add(incomingText);
            incoming.Add(GitUi.Button("Get latest", () => window.GetLatest(false), "primary", "download"));
            Root.Add(incoming);

            split = new TwoPaneSplitView(1, 220, TwoPaneSplitViewOrientation.Vertical);
            split.AddToClassList("gfu-history__split");

            list = new ListView(items, RowHeight, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            list.AddToClassList("gfu-list");
            list.selectionChanged += OnSelectionChanged;
            split.Add(list);

            details = GitUi.Column("gfu-history__details");
            var detailScroll = new ScrollView(ScrollViewMode.Vertical);
            detailScroll.AddToClassList("gfu-history__details-scroll");
            detailSummary = GitUi.Text(string.Empty, "gfu-history__summary");
            detailBody = GitUi.Text(string.Empty, "gfu-history__body");
            detailMeta = GitUi.Text(string.Empty, "gfu-hint");
            var idRow = GitUi.Row("gfu-history__id-row");
            detailId = GitUi.Text(string.Empty, "gfu-mono");
            idRow.Add(detailMeta);
            idRow.Add(GitUi.Spacer());
            idRow.Add(detailId);
            idRow.Add(GitUi.Button(null, () =>
            {
                if (selected.HasValue)
                {
                    EditorGUIUtility.systemCopyBuffer = selected.Value.CommitID;
                    window.ShowToast("Copied commit ID.", "info");
                }
            }, "ghost", "copy", "Copy commit ID"));
            detailScroll.Add(detailSummary);
            detailScroll.Add(detailBody);
            detailScroll.Add(idRow);
            detailFiles = GitUi.Column("gfu-history__files");
            detailScroll.Add(detailFiles);
            var actions = GitUi.Row("gfu-history__actions");
            undoButton = GitUi.Button("Undo this commit…", UndoCommit, "secondary", "undo",
                "Adds a new commit that reverses this one. The original stays in History.");
            actions.Add(undoButton);
            detailScroll.Add(actions);
            details.Add(detailScroll);
            split.Add(details);
            Root.Add(split);

            emptyState = GitUi.Column("gfu-empty");
            emptyState.Add(new GitIcon("history", 32));
            emptyState.Add(GitUi.Text("No history yet", "gfu-empty__title"));
            emptyState.Add(GitUi.Text("Commits you and your team make appear here.", "gfu-empty__body"));
            Root.Add(emptyState);

            ShowDetails(null);
        }

        public void OnShow()
        {
            Session.Repository?.Refresh(CacheType.GitLog);
        }

        public void Refresh(GitDataKind kind)
        {
            if ((kind & (GitDataKind.Log | GitDataKind.Tracking | GitDataKind.User | GitDataKind.Repository)) != 0)
                RebuildItems();

            var behind = Session.Behind;
            incoming.style.display = behind > 0 && Session.HasRemote ? DisplayStyle.Flex : DisplayStyle.None;
            incomingTitle.text = GitUi.Plural(behind, "update") + " from your team";
            undoButton.SetEnabled(!Session.IsBusy);
        }

        private void RebuildItems()
        {
            items.Clear();
            var log = Session.Log;
            var filter = search.value?.Trim();
            var ahead = Session.Ahead;
            string lastDay = null;

            for (var i = 0; i < log.Count; i++)
            {
                var entry = log[i];
                if (!string.IsNullOrEmpty(filter) && !Matches(entry, filter))
                    continue;
                var day = GitUi.DayLabel(entry.Time);
                if (day != lastDay)
                {
                    items.Add(new HistoryItem { IsHeader = true, Day = day });
                    lastDay = day;
                }
                // The current branch's log is newest first, so the first `ahead` commits are the unpushed ones.
                items.Add(new HistoryItem { Entry = entry, NotPushed = Session.HasRemote && i < ahead });
            }

            // Search only covers what's loaded, so keep offering older commits while filtering too.
            loadingMore = false;
            if (Session.Repository?.HasMoreLog ?? false)
                items.Add(new HistoryItem { IsLoadMore = true });

            list.RefreshItems();
            var hasLog = log.Count > 0;
            split.style.display = hasLog ? DisplayStyle.Flex : DisplayStyle.None;
            emptyState.style.display = hasLog ? DisplayStyle.None : DisplayStyle.Flex;

            if (selected.HasValue)
            {
                var id = selected.Value.CommitID;
                var match = log.FirstOrDefault(l => l.CommitID == id);
                ShowDetails(match.CommitID == id ? match : (GitLogEntry?)null);
            }
        }

        private void LoadMore()
        {
            var repo = Session.Repository;
            if (repo == null || loadingMore)
                return;
            loadingMore = true;
            list.RefreshItems();
            repo.LoadMoreLog();
        }

        private static bool Matches(GitLogEntry entry, string filter)
        {
            bool Has(string s) => !string.IsNullOrEmpty(s) && s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            return Has(entry.Summary) || Has(entry.Description) || Has(entry.AuthorName) || Has(entry.CommitID) ||
                   (entry.Changes != null && entry.Changes.Any(c => Has(c.Path)));
        }

        private VisualElement MakeRow()
        {
            var row = new HistoryRow(LoadMore);
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (!(row.userData is HistoryItem item) || item.IsHeader || item.IsLoadMore)
                    return;
                var e = item.Entry;
                evt.menu.AppendAction("Copy commit ID", _ => EditorGUIUtility.systemCopyBuffer = e.CommitID);
                evt.menu.AppendAction("Copy summary", _ => EditorGUIUtility.systemCopyBuffer = e.Summary);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Undo this commit…", _ =>
                {
                    selected = e;
                    UndoCommit();
                }, Session.IsBusy ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
            }));
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var row = (HistoryRow)element;
            var item = items[index];
            row.userData = item;
            if (item.IsHeader)
                row.BindHeader(item.Day);
            else if (item.IsLoadMore)
                row.BindLoadMore(ApplicationConfiguration.HistoryPageSize, loadingMore);
            else
                row.BindCommit(item.Entry, item.NotPushed, IsMe(item.Entry));
        }

        private bool IsMe(GitLogEntry entry)
        {
            return (!string.IsNullOrEmpty(Session.UserEmail) && string.Equals(entry.AuthorEmail, Session.UserEmail, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrEmpty(Session.UserName) && string.Equals(entry.AuthorName, Session.UserName, StringComparison.OrdinalIgnoreCase));
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            var item = selection.OfType<HistoryItem>().FirstOrDefault();
            if (item == null)
                return;
            if (item.IsHeader || item.IsLoadMore)
            {
                list.ClearSelection();
                return;
            }
            ShowDetails(item.Entry);
        }

        private void ShowDetails(GitLogEntry? entry)
        {
            selected = entry;
            details.style.display = entry.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            detailFiles.Clear();
            if (!entry.HasValue)
                return;

            var e = entry.Value;
            detailSummary.text = e.Summary;
            detailBody.text = e.Description;
            detailBody.style.display = string.IsNullOrWhiteSpace(e.Description) ? DisplayStyle.None : DisplayStyle.Flex;
            detailMeta.text = (IsMe(e) ? "You" : e.AuthorName) + " · " + GitUi.TimeLabel(e.Time);
            detailId.text = e.ShortID;

            var changes = e.Changes ?? new List<GitStatusEntry>();
            var assets = changes.Where(c => !GitSession.Normalize(c.Path).EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList();
            var metaOnly = changes.Count - assets.Count;
            detailFiles.Add(GitUi.SectionLabel(GitUi.Plural(assets.Count, "file") + (metaOnly > 0 ? " · plus " + GitUi.Plural(metaOnly, ".meta file") : string.Empty)));

            const int maxShown = 200;
            foreach (var change in assets.Take(maxShown))
                detailFiles.Add(FileRow(e, change));
            if (assets.Count > maxShown)
                detailFiles.Add(GitUi.Text("+ " + (assets.Count - maxShown) + " more", "gfu-hint"));
        }

        private VisualElement FileRow(GitLogEntry commit, GitStatusEntry change)
        {
            var path = GitSession.Normalize(change.Path);
            var kind = AssetKinds.Classify(path);
            var projectPath = Session.ToProjectPath(path);
            var row = GitUi.Row("gfu-file-row");
            var img = new Image { image = AssetKinds.Icon(projectPath, kind), scaleMode = ScaleMode.ScaleToFit };
            img.AddToClassList("gfu-change-row__icon");
            row.Add(img);
            var name = GitUi.Text(System.IO.Path.GetFileName(path), "gfu-change-row__name");
            name.tooltip = path;
            row.Add(name);
            var dir = System.IO.Path.GetDirectoryName(path);
            row.Add(GitUi.Text(string.IsNullOrEmpty(dir) ? string.Empty : GitSession.Normalize(dir), "gfu-change-row__dir"));
            row.Add(GitUi.Spacer());
            var entry = new ChangeEntry { Status = change.Status };
            row.Add(GitUi.Pill(entry.StatusLabel, entry.StatusTone));
            row.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount == 2)
                    GitUi.Ping(projectPath);
                else
                    GitUi.Select(projectPath);
            });
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Show in Project", _ => GitUi.Ping(projectPath),
                    projectPath != null && AssetDatabase.LoadMainAssetAtPath(projectPath) != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Copy path", _ => EditorGUIUtility.systemCopyBuffer = path);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Restore this version of the file…", _ => RestoreVersion(commit, path),
                    change.Status == GitFileStatus.Deleted || Session.IsBusy ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
            }));
            return row;
        }

        private void RestoreVersion(GitLogEntry commit, string path)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            var name = System.IO.Path.GetFileName(path);
            if (!EditorUtility.DisplayDialog("Restore an older version?",
                    "Replace your copy of " + name + " with the version from \"" + commit.Summary + "\" (" + GitUi.TimeLabel(commit.Time) + ")?\n\n" +
                    "The file will show up in Changes, so you can review it and commit it, or undo it.",
                    "Restore", "Cancel"))
                return;
            var files = new List<string> { path };
            if (!path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && (commit.Changes?.Any(c => GitSession.Normalize(c.Path) == path + ".meta") ?? false))
                files.Add(path + ".meta");
            Session.Run("Restoring " + name + "…", repo.CheckoutVersion(commit.CommitID, files), (ok, ex) =>
            {
                if (ok)
                {
                    AssetDatabase.Refresh();
                    window.ShowToast("Restored " + name + ". Commit it to share the change.", "good");
                }
                else
                {
                    window.ShowToast("Couldn't restore: " + GitSession.FriendlyError(ex), "error", sticky: true);
                }
            });
        }

        private void UndoCommit()
        {
            var repo = Session.Repository;
            if (repo == null || !selected.HasValue)
                return;
            var e = selected.Value;
            if (!EditorUtility.DisplayDialog("Undo this commit?",
                    "\"" + e.Summary + "\"\n\nThis adds a new commit that reverses its changes. The original stays in History, so nothing is lost.",
                    "Undo commit", "Cancel"))
                return;
            Session.Run("Undoing commit…", repo.Revert(e.CommitID), (ok, ex) =>
            {
                if (ok)
                {
                    AssetDatabase.Refresh();
                    window.ShowToast("Undid \"" + e.Summary + "\". Push to share it with the team.", "good");
                }
                else
                {
                    window.ShowToast("Couldn't undo the commit: " + GitSession.FriendlyError(ex), "error", sticky: true);
                }
            });
        }

        private class HistoryItem
        {
            public bool IsHeader;
            public bool IsLoadMore;
            public string Day;
            public GitLogEntry Entry;
            public bool NotPushed;
        }

        private class HistoryRow : VisualElement
        {
            private readonly Label day;
            private readonly Label avatar;
            private readonly VisualElement text;
            private readonly Label summary;
            private readonly Label meta;
            private readonly Label notPushed;
            private readonly Label files;
            private readonly Button loadMore;

            public HistoryRow(Action onLoadMore)
            {
                AddToClassList("gfu-history-row");
                day = GitUi.Text(string.Empty, "gfu-section-label gfu-history-row__day");
                Add(day);
                avatar = (Label)GitUi.Avatar(string.Empty, 24);
                Add(avatar);
                text = GitUi.Column("gfu-history-row__text");
                summary = GitUi.Text(string.Empty, "gfu-history-row__summary");
                meta = GitUi.Text(string.Empty, "gfu-history-row__meta");
                text.Add(summary);
                text.Add(meta);
                Add(text);
                notPushed = GitUi.Pill("Not pushed", "warn");
                notPushed.tooltip = "Only on your computer. Push to share it.";
                Add(notPushed);
                files = GitUi.Text(string.Empty, "gfu-hint");
                Add(files);
                // real text so the helper creates its label; Button.text would draw behind the icon
                loadMore = GitUi.Button("Load older commits", onLoadMore, "secondary", "chevron-down");
                loadMore.AddToClassList("gfu-history-row__more");
                Add(loadMore);
            }

            private void ShowOnly(params VisualElement[] visible)
            {
                foreach (var child in new VisualElement[] { day, avatar, text, notPushed, files, loadMore })
                    child.style.display = Array.IndexOf(visible, child) >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            public void BindLoadMore(int pageSize, bool loading)
            {
                EnableInClassList("gfu-history-row--header", false);
                EnableInClassList("gfu-history-row--more", true);
                ShowOnly(loadMore);
                GitUi.SetButtonText(loadMore, loading ? "Loading…" : "Load " + GitUi.Plural(pageSize, "older commit"));
                loadMore.SetEnabled(!loading);
                tooltip = "Page size can be changed in Settings > Display.";
            }

            public void BindHeader(string label)
            {
                EnableInClassList("gfu-history-row--header", true);
                EnableInClassList("gfu-history-row--more", false);
                day.text = label.ToUpperInvariant();
                ShowOnly(day);
                tooltip = null;
            }

            public void BindCommit(GitLogEntry e, bool isNotPushed, bool isMe)
            {
                EnableInClassList("gfu-history-row--header", false);
                EnableInClassList("gfu-history-row--more", false);
                ShowOnly(avatar, text, files);
                GitUi.SetAvatar(avatar, e.AuthorName);
                summary.text = string.IsNullOrEmpty(e.Summary) ? "(no summary)" : e.Summary;
                meta.text = (isMe ? "You" : e.AuthorName) + " · " + e.Time.ToLocalTime().ToString("HH:mm");
                notPushed.style.display = isNotPushed ? DisplayStyle.Flex : DisplayStyle.None;
                var count = e.Changes?.Count(c => !GitSession.Normalize(c.Path).EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) ?? 0;
                files.text = count > 0 ? GitUi.Plural(count, "file") : string.Empty;
                tooltip = e.Summary + "\n" + e.AuthorName + " · " + GitUi.TimeLabel(e.Time) + " · " + e.ShortID;
            }
        }
    }
}
