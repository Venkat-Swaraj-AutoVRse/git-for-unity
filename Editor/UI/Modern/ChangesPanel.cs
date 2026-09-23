using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Editor.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    using IO;

    /// <summary>
    /// Changed files grouped by asset type, with .meta files folded into their asset, locks shown
    /// inline and a commit box that pushes straight after committing.
    /// </summary>
    class ChangesPanel
    {
        private const string PushAfterCommitPrefKey = "GitForUnity.Modern.PushAfterCommit";
        private const string GroupByFolderPrefKey = "GitForUnity.Modern.GroupByFolder";
        private const string SummaryDraftKey = "GitForUnity.Modern.CommitSummary";
        private const string BodyDraftKey = "GitForUnity.Modern.CommitBody";
        private const int RowHeight = 30;

        private readonly GitWindow window;
        private GitSession Session => window.Session;

        public VisualElement Root { get; }

        private readonly VisualElement listArea;
        private readonly VisualElement bannerHost;
        private readonly Toggle selectAll;
        private readonly Label selectionLabel;
        private readonly ListView list;
        private readonly VisualElement emptyState;
        private readonly VisualElement emptyCard;
        private readonly Label emptyBody;
        private readonly VisualElement commitArea;
        private readonly VisualElement pendingPullBanner;
        private readonly TextField summary;
        private readonly TextField body;
        private readonly VisualElement checksRow;
        private readonly Label checksLabel;
        private readonly Toggle pushToggle;
        private readonly Button commitButton;
        private readonly Label commitFootnote;
        private readonly TextField search;
        private readonly Button groupByType;
        private readonly Button groupByFolder;

        private List<ChangeEntry> entries = new List<ChangeEntry>();
        private readonly List<ChangeItem> items = new List<ChangeItem>();
        private readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> dismissedLockWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string repositoryKey;

        public int Count => entries.Count;

        public ChangesPanel(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Column("gfu-panel gfu-changes");

            // toolbar
            var toolbar = GitUi.Row("gfu-toolbar");
            search = GitUi.Field("Filter changed files");
            search.AddToClassList("gfu-search");
            search.RegisterValueChangedCallback(_ => RebuildItems());
            toolbar.Add(search);
            var segmented = GitUi.Row("gfu-segmented");
            groupByType = GitUi.Button("Type", () => SetGroupByFolder(false), "segment");
            groupByFolder = GitUi.Button("Folder", () => SetGroupByFolder(true), "segment");
            groupByType.tooltip = "Group changes by asset type";
            groupByFolder.tooltip = "Group changes by folder";
            segmented.Add(groupByType);
            segmented.Add(groupByFolder);
            toolbar.Add(segmented);
            toolbar.Add(GitUi.Button(null, ShowMoreMenu, "ghost", "more", "More actions"));
            Root.Add(toolbar);

            bannerHost = GitUi.Column("gfu-banner-host");
            Root.Add(bannerHost);

            // list
            listArea = GitUi.Column("gfu-list-area");
            var selectRow = GitUi.Row("gfu-select-row");
            selectAll = new Toggle();
            selectAll.AddToClassList("gfu-checkbox");
            selectAll.tooltip = "Select all";
            selectAll.RegisterValueChangedCallback(evt => SetAll(evt.newValue));
            selectRow.Add(selectAll);
            selectionLabel = GitUi.Text(string.Empty, "gfu-select-row__label");
            selectRow.Add(selectionLabel);
            selectRow.Add(GitUi.Spacer());
            var metaHint = GitUi.Text(".meta files are included automatically", "gfu-hint");
            metaHint.tooltip = "Unity stores import settings in a .meta file next to each asset. They're committed together with the asset, so you never have to pick them yourself.";
            selectRow.Add(metaHint);
            listArea.Add(selectRow);

            list = new ListView(items, RowHeight, MakeRow, BindRow)
            {
                // Ctrl/Cmd-click and Shift-click are handled by the ListView itself
                selectionType = SelectionType.Multiple,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            list.AddToClassList("gfu-list");
            list.selectionChanged += OnSelectionChanged;
            list.RegisterCallback<KeyDownEvent>(evt =>
            {
                // Space checks/unchecks every selected file, like clicking one of their checkboxes
                if (evt.keyCode != KeyCode.Space)
                    return;
                var picked = SelectedEntries();
                if (picked.Length == 0)
                    return;
                SetIncluded(picked, !picked.All(IsIncluded));
                evt.StopPropagation();
            });
            list.itemsChosen += OnItemsChosen;
            listArea.Add(list);
            Root.Add(listArea);

            // empty state
            emptyState = GitUi.Column("gfu-empty");
            var badge = GitUi.Div("gfu-empty__badge");
            badge.Add(new GitIcon("check", 28));
            emptyState.Add(badge);
            emptyState.Add(GitUi.Text("All your work is committed", "gfu-empty__title"));
            emptyBody = GitUi.Text("Anything you edit in Unity shows up here automatically.", "gfu-empty__body");
            emptyState.Add(emptyBody);
            emptyCard = GitUi.Column("gfu-empty__card");
            emptyState.Add(emptyCard);
            Root.Add(emptyState);

            // commit area
            commitArea = GitUi.Column("gfu-commit");
            pendingPullBanner = GitUi.Banner("info", "After you commit, we'll get the latest from your team.", null,
                GitUi.Button("Don't get latest", () => window.CancelGetLatestAfterCommit(), "ghost"));
            commitArea.Add(pendingPullBanner);
            commitArea.Add(GitUi.Text("What did you change?", "gfu-field-label"));
            summary = GitUi.Field("e.g. Added pine trees to the Forest scene");
            summary.AddToClassList("gfu-commit__summary");
            summary.value = SessionState.GetString(SummaryDraftKey, string.Empty);
            summary.RegisterValueChangedCallback(evt =>
            {
                SessionState.SetString(SummaryDraftKey, evt.newValue);
                UpdateCommitButton();
            });
            summary.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) && (evt.ctrlKey || evt.commandKey))
                {
                    Commit();
                    evt.StopPropagation();
                }
            });
            commitArea.Add(summary);
            body = GitUi.Field("Details for your team (optional)", multiline: true);
            body.value = SessionState.GetString(BodyDraftKey, string.Empty);
            body.RegisterValueChangedCallback(evt => SessionState.SetString(BodyDraftKey, evt.newValue));
            commitArea.Add(body);

            checksRow = GitUi.Row("gfu-commit__checks");
            var shield = new GitIcon("shield", 14);
            shield.AddToClassList("gfu-icon--good");
            checksRow.Add(shield);
            checksLabel = GitUi.Text(string.Empty, "gfu-hint");
            checksRow.Add(checksLabel);
            commitArea.Add(checksRow);

            pushToggle = new Toggle("Push to the team right after committing");
            pushToggle.AddToClassList("gfu-toggle");
            pushToggle.value = EditorPrefs.GetBool(PushAfterCommitPrefKey, true);
            pushToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(PushAfterCommitPrefKey, evt.newValue);
                UpdateCommitButton();
            });
            commitArea.Add(pushToggle);

            commitButton = GitUi.Button("Commit", Commit, "primary", "upload");
            commitButton.AddToClassList("gfu-commit__button");
            commitArea.Add(commitButton);
            commitFootnote = GitUi.Text(string.Empty, "gfu-commit__footnote");
            commitArea.Add(commitFootnote);
            Root.Add(commitArea);

            UpdateGroupButtons();
        }

        public void FocusSummary()
        {
            summary.schedule.Execute(() => summary.Focus()).StartingIn(50);
        }

        public void Refresh(GitDataKind kind)
        {
            var key = Session.RepositoryPath;
            if (key != repositoryKey)
            {
                repositoryKey = key;
                excluded.Clear();
                seen.Clear();
                dismissedLockWarnings.Clear();
            }

            if ((kind & (GitDataKind.Status | GitDataKind.Locks | GitDataKind.User | GitDataKind.Repository)) != 0)
            {
                entries = ChangeEntry.Build(Session);
                foreach (var e in entries)
                {
                    if (seen.Add(e.Key) && (e.LockedByOther || e.IsConflict))
                        excluded.Add(e.Key); // don't commit over a teammate's lock by default
                }
                excluded.RemoveWhere(k => entries.All(e => !string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase)));
                RebuildItems();
                RebuildBanners();
            }

            var hasChanges = entries.Count > 0;

            // "Commit first, then get latest" was chosen but the changes went away some other way
            // (e.g. undone): nothing is left to commit, so drop the pending get-latest.
            if (!hasChanges && window.GetLatestAfterCommit && !Session.IsBusy)
                window.CancelGetLatestAfterCommit();

            listArea.style.display = hasChanges ? DisplayStyle.Flex : DisplayStyle.None;
            commitArea.style.display = hasChanges ? DisplayStyle.Flex : DisplayStyle.None;
            emptyState.style.display = hasChanges ? DisplayStyle.None : DisplayStyle.Flex;
            pendingPullBanner.style.display = window.GetLatestAfterCommit ? DisplayStyle.Flex : DisplayStyle.None;
            pushToggle.style.display = Session.HasRemote ? DisplayStyle.Flex : DisplayStyle.None;

            if (!hasChanges)
                UpdateEmptyState();

            var checks = 0;
            try { checks = PreCommit.PreCommitCheckRunner.DiscoverChecks().Count; }
            catch (Exception) { }
            checksRow.style.display = checks > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            checksLabel.text = GitUi.Plural(checks, "pre-commit check") + " will run when you commit";

            var who = string.IsNullOrEmpty(Session.UserName) ? string.Empty : " · as " + Session.UserName;
            commitFootnote.text = "to " + (string.IsNullOrEmpty(Session.BranchName) ? "this branch" : Session.BranchName) + who;
            UpdateCommitButton();
        }

        // ---------------------------------------------------------------- items

        private void RebuildItems()
        {
            // rows move when the list is rebuilt, so remember the selection by file, not index
            var selectedKeys = new HashSet<string>(SelectedEntries().Select(e => e.Key));
            items.Clear();
            var filter = search.value?.Trim();
            var filtered = string.IsNullOrEmpty(filter)
                ? entries
                : entries.Where(e => e.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var byFolder = EditorPrefs.GetBool(GroupByFolderPrefKey, false);
            var groups = byFolder
                ? filtered.GroupBy(e => e.Directory).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Key = "dir:" + g.Key, Label = string.IsNullOrEmpty(g.Key) ? "(top level)" : g.Key, Kind = AssetKind.Folder, Items = g.ToList() })
                : filtered.GroupBy(e => e.Kind).OrderBy(g => g.Key)
                    .Select(g => new { Key = "kind:" + g.Key, Label = AssetKinds.GroupLabel(g.Key), Kind = g.Key, Items = g.ToList() });

            foreach (var g in groups)
            {
                var header = new ChangeItem
                {
                    IsHeader = true,
                    GroupKey = g.Key,
                    GroupLabel = g.Label,
                    Kind = g.Kind,
                    GroupEntries = g.Items,
                };
                items.Add(header);
                if (collapsed.Contains(g.Key))
                    continue;
                foreach (var e in g.Items)
                    items.Add(new ChangeItem { Entry = e, GroupKey = g.Key, Kind = e.Kind });
            }
            list.SetSelectionWithoutNotify(Enumerable.Range(0, items.Count)
                .Where(i => !items[i].IsHeader && selectedKeys.Contains(items[i].Entry.Key)));
            list.RefreshItems();
            UpdateSelectionSummary();
        }

        private bool IsIncluded(ChangeEntry e) => !excluded.Contains(e.Key);

        private void SetIncluded(ChangeEntry e, bool value)
        {
            if (value)
                excluded.Remove(e.Key);
            else
                excluded.Add(e.Key);
        }

        private void SetAll(bool value)
        {
            foreach (var e in entries)
                SetIncluded(e, value);
            list.RefreshItems();
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            var selected = entries.Count(IsIncluded);
            selectionLabel.text = selected + " of " + GitUi.Plural(entries.Count, "change") + " selected";
            selectAll.SetValueWithoutNotify(selected > 0);
            selectAll.showMixedValue = selected > 0 && selected < entries.Count;
            UpdateCommitButton();
        }

        private void UpdateCommitButton()
        {
            var selected = entries.Count(IsIncluded);
            var push = pushToggle.value && Session.HasRemote;
            GitUi.SetButtonText(commitButton, selected == 0
                ? "Choose files to commit"
                : "Commit " + GitUi.Plural(selected, "file") + (push ? " and push" : string.Empty));
            var hasSummary = !string.IsNullOrWhiteSpace(summary.value);
            commitButton.SetEnabled(selected > 0 && hasSummary && !Session.IsBusy);
            commitButton.tooltip = !hasSummary ? "Describe what you changed first" : Session.IsBusy ? "Waiting for the current action to finish" : "Ctrl+Enter in the summary also commits";
        }

        private void SetGroupByFolder(bool value)
        {
            EditorPrefs.SetBool(GroupByFolderPrefKey, value);
            UpdateGroupButtons();
            RebuildItems();
        }

        private void UpdateGroupButtons()
        {
            var byFolder = EditorPrefs.GetBool(GroupByFolderPrefKey, false);
            groupByType.EnableInClassList("gfu-btn--selected", !byFolder);
            groupByFolder.EnableInClassList("gfu-btn--selected", byFolder);
        }

        // ---------------------------------------------------------------- rows

        private VisualElement MakeRow()
        {
            var row = new ChangeRow();
            row.Include.RegisterValueChangedCallback(evt =>
            {
                if (!(row.userData is ChangeItem item))
                    return;
                if (item.IsHeader)
                {
                    foreach (var e in item.GroupEntries)
                        SetIncluded(e, evt.newValue);
                }
                else
                {
                    // ticking a checkbox on one of several selected rows applies to all of them
                    foreach (var e in TargetsFor(item.Entry))
                        SetIncluded(e, evt.newValue);
                }
                list.RefreshItems();
                UpdateSelectionSummary();
            });
            row.Chevron.RegisterCallback<ClickEvent>(evt =>
            {
                if (row.userData is ChangeItem item && item.IsHeader)
                {
                    ToggleGroup(item.GroupKey);
                    evt.StopPropagation();
                }
            });
            row.RegisterCallback<ClickEvent>(evt =>
            {
                if (row.userData is ChangeItem item && item.IsHeader && !(evt.target is Toggle) && !IsInside<Toggle>(evt.target as VisualElement, row))
                    ToggleGroup(item.GroupKey);
            });
            row.LockButton.clicked += () =>
            {
                if (row.userData is ChangeItem item && item.Entry != null)
                    ToggleLock(item.Entry);
            };
            row.UndoButton.clicked += () =>
            {
                if (row.userData is ChangeItem item && item.Entry != null)
                    Undo(new[] { item.Entry });
            };
            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (row.userData is ChangeItem item && item.Entry != null)
                    BuildContextMenu(evt.menu, item.Entry, TargetsFor(item.Entry));
            }));
            return row;
        }

        private static bool IsInside<T>(VisualElement target, VisualElement stop) where T : VisualElement
        {
            for (var e = target; e != null && e != stop; e = e.parent)
                if (e is T)
                    return true;
            return false;
        }

        private void BindRow(VisualElement element, int index)
        {
            var row = (ChangeRow)element;
            var item = items[index];
            row.userData = item;

            if (item.IsHeader)
            {
                var count = item.GroupEntries.Count;
                var selected = item.GroupEntries.Count(IsIncluded);
                row.BindHeader(item.GroupLabel, count, selected, collapsed.Contains(item.GroupKey),
                    item.Kind == AssetKind.Folder ? AssetKinds.Icon(null, AssetKind.Folder) : AssetKinds.Icon(null, item.Kind));
                return;
            }

            var e = item.Entry;
            row.BindFile(e, IsIncluded(e), AssetKinds.Icon(e.ProjectPath, e.Kind), CanLock(e));
        }

        private bool CanLock(ChangeEntry e)
        {
            return Session.HasRemote && !e.LockedByOther && e.Status != GitFileStatus.Added && e.Status != GitFileStatus.Untracked && !e.SettingsOnly && e.Kind != AssetKind.Folder;
        }

        /// <summary>The files (not group headers) currently selected in the list, in list order.</summary>
        private ChangeEntry[] SelectedEntries()
        {
            return list.selectedIndices
                .Where(i => i >= 0 && i < items.Count && !items[i].IsHeader)
                .OrderBy(i => i)
                .Select(i => items[i].Entry)
                .ToArray();
        }

        /// <summary>
        /// What an action on <paramref name="clicked"/> should apply to: the whole selection when the
        /// row is part of it, otherwise just that row.
        /// </summary>
        private ChangeEntry[] TargetsFor(ChangeEntry clicked)
        {
            var picked = SelectedEntries();
            return picked.Contains(clicked) ? picked : new[] { clicked };
        }

        private void SetIncluded(IEnumerable<ChangeEntry> targets, bool value)
        {
            foreach (var e in targets)
                SetIncluded(e, value);
            list.RefreshItems();
            UpdateSelectionSummary();
        }

        private void ToggleGroup(string key)
        {
            if (!collapsed.Remove(key))
                collapsed.Add(key);
            RebuildItems();
        }

        private void OnSelectionChanged(IEnumerable<object> selection)
        {
            var picked = list.selectedIndices.Where(i => i >= 0 && i < items.Count).ToList();
            var files = picked.Where(i => !items[i].IsHeader).ToList();
            if (files.Count != picked.Count)
            {
                // headers only expand/collapse their group; drop them from the selection
                if (files.Count == 0)
                {
                    list.ClearSelection();
                    return;
                }
                list.SetSelectionWithoutNotify(files);
            }

            var objects = files
                .Select(i => items[i].Entry.ProjectPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(AssetDatabase.LoadMainAssetAtPath)
                .Where(o => o != null)
                .ToArray();
            if (objects.Length > 0)
                Selection.objects = objects;
        }

        private void OnItemsChosen(IEnumerable<object> chosen)
        {
            var item = chosen.OfType<ChangeItem>().FirstOrDefault();
            if (item?.Entry?.ProjectPath == null)
                return;
            var obj = AssetDatabase.LoadMainAssetAtPath(item.Entry.ProjectPath);
            if (obj != null)
                AssetDatabase.OpenAsset(obj);
        }

        private void BuildContextMenu(DropdownMenu menu, ChangeEntry e, ChangeEntry[] targets)
        {
            if (targets.Length > 1)
            {
                BuildMultiContextMenu(menu, targets);
                return;
            }

            var exists = e.ProjectPath != null && AssetDatabase.LoadMainAssetAtPath(e.ProjectPath) != null;
            menu.AppendAction("Show in Project", _ => GitUi.Ping(e.ProjectPath), exists ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Open", _ => OnItemsChosen(new object[] { new ChangeItem { Entry = e } }), exists ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendSeparator();
            if (e.LockedByMe)
                menu.AppendAction("Unlock", _ => ToggleLock(e));
            else if (!e.LockedByOther)
                menu.AppendAction("Lock so nobody else edits it", _ => ToggleLock(e), Session.HasRemote && !e.SettingsOnly ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Undo my changes…", _ => Undo(new[] { e }));
            menu.AppendSeparator();
            menu.AppendAction("Copy path", _ => EditorGUIUtility.systemCopyBuffer = e.Key);
            menu.AppendAction("Show in Explorer", _ =>
            {
                var full = System.IO.Path.Combine(Session.RepositoryPath ?? string.Empty, e.Key);
                EditorUtility.RevealInFinder(System.IO.File.Exists(full) || System.IO.Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full));
            });
        }

        private void BuildMultiContextMenu(DropdownMenu menu, ChangeEntry[] targets)
        {
            var what = GitUi.Plural(targets.Length, "file");
            var enabled = DropdownMenuAction.Status.Normal;
            var disabled = DropdownMenuAction.Status.Disabled;

            menu.AppendAction("Include " + what + " in the commit", _ => SetIncluded(targets, true),
                targets.All(IsIncluded) ? disabled : enabled);
            menu.AppendAction("Leave " + what + " out of the commit", _ => SetIncluded(targets, false),
                targets.Any(IsIncluded) ? enabled : disabled);
            menu.AppendSeparator();

            var lockable = targets.Where(t => CanLock(t) && !t.LockedByMe).ToArray();
            var mine = targets.Where(t => t.LockedByMe).ToArray();
            menu.AppendAction(lockable.Length > 0 ? "Lock " + GitUi.Plural(lockable.Length, "file") + " so nobody else edits them" : "Lock so nobody else edits them",
                _ => SetLocks(lockable, true), lockable.Length > 0 ? enabled : disabled);
            if (mine.Length > 0)
                menu.AppendAction("Unlock " + GitUi.Plural(mine.Length, "file"), _ => SetLocks(mine, false));
            menu.AppendAction("Undo my changes to " + what + "…", _ => Undo(targets));
            menu.AppendSeparator();
            menu.AppendAction("Copy paths", _ => EditorGUIUtility.systemCopyBuffer = string.Join("\n", targets.Select(t => t.Key)));
        }

        /// <summary>
        /// Locks or unlocks several files as one operation (the window runs one git operation at a
        /// time), stopping at the first failure.
        /// </summary>
        private void SetLocks(ChangeEntry[] targets, bool lockThem)
        {
            var repo = Session.Repository;
            if (repo == null || targets.Length == 0)
                return;
            if (targets.Length == 1)
            {
                ToggleLock(targets[0]);
                return;
            }

            ITask chain = null;
            foreach (var t in targets)
            {
                var path = t.Key.ToSPath();
                var next = lockThem ? repo.RequestLock(path) : repo.ReleaseLock(path, false);
                chain = chain == null ? next : chain.Then(next);
            }

            var what = GitUi.Plural(targets.Length, "file");
            Session.Run((lockThem ? "Locking " : "Unlocking ") + what + "…", chain, (ok, ex) =>
            {
                if (ok)
                    window.ShowToast((lockThem ? "Locked " : "Unlocked ") + what + ".", "good");
                else
                    window.ShowToast("Couldn't " + (lockThem ? "lock" : "unlock") + " all of them: " + GitSession.FriendlyError(ex), "error");
                Session.RefreshLocks();
            });
        }

        private void ShowMoreMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Select all"), false, () => SetAll(true));
            menu.AddItem(new GUIContent("Select none"), false, () => SetAll(false));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Expand all groups"), false, () => { collapsed.Clear(); RebuildItems(); });
            menu.AddItem(new GUIContent("Collapse all groups"), false, () =>
            {
                foreach (var item in items.Where(i => i.IsHeader))
                    collapsed.Add(item.GroupKey);
                RebuildItems();
            });
            menu.AddSeparator(string.Empty);
            if (entries.Count > 0)
                menu.AddItem(new GUIContent("Undo changes to selected files…"), false, () => Undo(entries.Where(IsIncluded).ToArray()));
            else
                menu.AddDisabledItem(new GUIContent("Undo changes to selected files…"));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Refresh"), false, window.RefreshAll);
            menu.ShowAsContext();
        }

        // ---------------------------------------------------------------- banners & empty state

        private void RebuildBanners()
        {
            bannerHost.Clear();

            var conflicts = entries.Where(e => e.IsConflict).ToList();
            if (conflicts.Count > 0)
            {
                var names = conflicts.Count == 1 ? conflicts[0].DisplayName : GitUi.Plural(conflicts.Count, "file");
                bannerHost.Add(GitUi.Banner("error",
                    names + (conflicts.Count == 1 ? " was" : " were") + " changed by you and a teammate",
                    "Unity files like scenes and models can't be combined automatically. Picking a version from this window is coming soon; until then, resolve them from the legacy window or GitHub Desktop.",
                    GitUi.Button("Open legacy window", () => Menus.ShowWindow(EntryPoint.ApplicationManager), "secondary")));
            }

            var lockedByOthers = entries.Where(e => e.LockedByOther && !dismissedLockWarnings.Contains(e.Key)).ToList();
            if (lockedByOthers.Count > 0)
            {
                string title;
                if (lockedByOthers.Count == 1)
                    title = lockedByOthers[0].DisplayName + " is locked by " + lockedByOthers[0].LockOwner;
                else
                    title = GitUi.Plural(lockedByOthers.Count, "file") + " you changed are locked by teammates";
                var undo = GitUi.Button("Undo my changes", () => Undo(lockedByOthers.ToArray()), "secondary", "undo");
                var keep = GitUi.Button("Keep for later", () =>
                {
                    foreach (var e in lockedByOthers)
                        dismissedLockWarnings.Add(e.Key);
                    RebuildBanners();
                }, "ghost");
                bannerHost.Add(GitUi.Banner("warn", title,
                    (lockedByOthers.Count == 1 ? "They may be editing it right now. We've left it" : "They may be editing them right now. We've left them") +
                    " out of this commit so nobody's work gets overwritten.",
                    undo, keep));
            }
        }

        private void UpdateEmptyState()
        {
            emptyCard.Clear();
            var ahead = Session.Ahead;
            var behind = Session.Behind;
            emptyBody.text = "Anything you edit in Unity shows up here automatically.";

            if (behind > 0 && Session.HasRemote)
            {
                emptyCard.Add(Card("download", GitUi.Plural(behind, "update") + " from your team",
                    "Get them before you start so you're working on the newest files.",
                    GitUi.Button("Get latest", () => window.GetLatest(false), "primary", "download")));
            }
            else if (ahead > 0 && Session.HasRemote)
            {
                emptyCard.Add(Card("upload", GitUi.Plural(ahead, "commit") + " the team can't see yet",
                    "Push them so everyone gets your work.",
                    GitUi.Button("Push to team", window.Push, "primary", "upload")));
            }
            emptyCard.style.display = emptyCard.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static VisualElement Card(string icon, string title, string body, Button action)
        {
            var row = GitUi.Row("gfu-callout");
            var i = new GitIcon(icon, 18);
            i.AddToClassList("gfu-icon--accent");
            row.Add(i);
            var col = GitUi.Column("gfu-callout__text");
            col.Add(GitUi.Text(title, "gfu-callout__title"));
            col.Add(GitUi.Text(body, "gfu-callout__body"));
            row.Add(col);
            row.Add(action);
            return row;
        }

        // ---------------------------------------------------------------- actions

        private void Commit()
        {
            var repo = Session.Repository;
            if (repo == null || Session.IsBusy)
                return;
            var message = summary.value?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                FocusSummary();
                return;
            }
            var details = body.value?.Trim() ?? string.Empty;
            var selected = entries.Where(IsIncluded).ToList();
            if (selected.Count == 0)
                return;

            var lockedByOthers = selected.Where(e => e.LockedByOther).ToList();
            if (lockedByOthers.Count > 0 && !EditorUtility.DisplayDialog("Commit files locked by teammates?",
                    string.Join("\n", lockedByOthers.Select(e => "• " + e.DisplayName + " (locked by " + e.LockOwner + ")")) +
                    "\n\nThey may be editing these right now. Committing could overwrite their work when they push.",
                    "Commit anyway", "Cancel"))
                return;

            var files = selected.SelectMany(e => e.Paths).Distinct().ToList();
            if (!PreCommit.PreCommitGate.Run(Session.RepositoryPath, files, message, details))
                return;

            ITask task;
            var allStatus = Session.Changes;
            if (files.Count == allStatus.Count)
            {
                task = repo.CommitAllFiles(message, details);
            }
            else
            {
                task = repo.CommitFiles(files, message, details);
                // Files staged outside Unity but not selected here would be swept into the commit;
                // unstage them first.
                var stagedButUnchecked = allStatus.Where(x => x.Staged).Select(x => x.Path).Except(files).ToList();
                if (stagedButUnchecked.Count > 0)
                    task = Session.Manager.GitClient.Remove(stagedButUnchecked).Then(task);
            }

            var push = pushToggle.value && Session.HasRemote;
            var count = selected.Count;
            Session.Run("Committing…", task, (ok, ex) =>
            {
                if (!ok)
                {
                    window.ShowToast("Couldn't commit: " + GitSession.FriendlyError(ex), "error", sticky: true);
                    window.OnCommitFinished(false);
                    return;
                }

                summary.value = string.Empty;
                body.value = string.Empty;
                if (!push)
                {
                    window.ShowToast("Committed " + GitUi.Plural(count, "file") + ". Push when you're ready to share them.", "good");
                    window.OnCommitFinished(true);
                    return;
                }

                // Push as its own step so a failed push never looks like a failed commit.
                Session.Run("Pushing to the team…", repo.Push(), (pushed, pushError) =>
                {
                    if (pushed)
                        window.ShowToast("Committed and pushed " + GitUi.Plural(count, "file") + " to the team.", "good");
                    else
                        window.ShowToast("Committed, but couldn't push: " + GitSession.FriendlyError(pushError) + " Your work is safe on this computer.", "warn", sticky: true);
                    window.OnCommitFinished(true);
                });
            });
            UpdateCommitButton();
        }

        private void ToggleLock(ChangeEntry e)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            var path = e.Key.ToSPath();
            if (e.LockedByMe)
            {
                Session.Run("Unlocking…", repo.ReleaseLock(path, false), (ok, ex) =>
                {
                    window.ShowToast(ok ? "Unlocked " + e.DisplayName + "." : "Couldn't unlock: " + GitSession.FriendlyError(ex), ok ? "good" : "error");
                    Session.RefreshLocks();
                });
            }
            else
            {
                Session.Run("Locking…", repo.RequestLock(path), (ok, ex) =>
                {
                    window.ShowToast(ok ? "Locked " + e.DisplayName + ". Teammates will see it's yours." : "Couldn't lock: " + GitSession.FriendlyError(ex), ok ? "good" : "error");
                    Session.RefreshLocks();
                });
            }
        }

        private void Undo(ChangeEntry[] targets)
        {
            var repo = Session.Repository;
            if (repo == null || targets.Length == 0)
                return;
            var anyNew = targets.Any(t => t.Status == GitFileStatus.Untracked || t.Status == GitFileStatus.Added);
            var what = targets.Length == 1 ? targets[0].DisplayName : GitUi.Plural(targets.Length, "file");
            var message = "Your edits to " + what + " will be thrown away." + (anyNew ? " New files will be deleted." : string.Empty) + "\n\nThis can't be undone.";
            if (!EditorUtility.DisplayDialog("Undo your changes?", message, "Undo changes", "Cancel"))
                return;
            var statusEntries = targets.SelectMany(t => t.Entries).ToArray();
            Session.Run("Undoing changes…", repo.DiscardChanges(statusEntries), (ok, ex) =>
            {
                if (ok)
                {
                    AssetDatabase.Refresh();
                    window.ShowToast("Undid your changes to " + what + ".", "good");
                }
                else
                {
                    window.ShowToast("Couldn't undo: " + GitSession.FriendlyError(ex), "error", sticky: true);
                }
            });
        }

        private class ChangeItem
        {
            public bool IsHeader;
            public string GroupKey;
            public string GroupLabel;
            public AssetKind Kind;
            public List<ChangeEntry> GroupEntries;
            public ChangeEntry Entry;
        }

        /// <summary>A pooled list row that renders either a group header or a file.</summary>
        private class ChangeRow : VisualElement
        {
            public readonly Toggle Include;
            public readonly GitIcon Chevron;
            public readonly Button LockButton;
            public readonly Button UndoButton;
            private readonly Image icon;
            private readonly Label nameLabel;
            private readonly Label directory;
            private readonly Label size;
            private readonly VisualElement lockChip;
            private readonly GitIcon lockIcon;
            private readonly Label lockLabel;
            private readonly Label pill;
            private readonly Label groupCount;
            private readonly VisualElement actions;

            public ChangeRow()
            {
                AddToClassList("gfu-change-row");
                Chevron = new GitIcon("chevron-down", 14);
                Chevron.pickingMode = PickingMode.Position;
                Chevron.AddToClassList("gfu-change-row__chevron");
                Add(Chevron);
                Include = new Toggle();
                Include.AddToClassList("gfu-checkbox");
                Add(Include);
                icon = new Image { scaleMode = ScaleMode.ScaleToFit };
                icon.AddToClassList("gfu-change-row__icon");
                Add(icon);
                var text = GitUi.Row("gfu-change-row__text");
                nameLabel = GitUi.Text(string.Empty, "gfu-change-row__name");
                directory = GitUi.Text(string.Empty, "gfu-change-row__dir");
                groupCount = GitUi.Text(string.Empty, "gfu-change-row__count");
                text.Add(nameLabel);
                text.Add(groupCount);
                text.Add(directory);
                Add(text);
                size = GitUi.Text(string.Empty, "gfu-change-row__size gfu-hide-on-hover");
                Add(size);
                lockChip = GitUi.Row("gfu-lock-chip gfu-hide-on-hover");
                lockIcon = new GitIcon("lock", 13);
                lockChip.Add(lockIcon);
                lockLabel = GitUi.Text(string.Empty, "gfu-lock-chip__label");
                lockChip.Add(lockLabel);
                Add(lockChip);
                actions = GitUi.Row("gfu-row-actions");
                LockButton = GitUi.Button("Lock", null, "small", "lock", "Lock this file so nobody else edits it");
                UndoButton = GitUi.Button(null, null, "small", "undo", "Undo my changes to this file");
                actions.Add(LockButton);
                actions.Add(UndoButton);
                Add(actions);
                pill = GitUi.Pill(string.Empty, "edited");
                Add(pill);
            }

            public void BindHeader(string label, int count, int selected, bool isCollapsed, Texture kindIcon)
            {
                EnableInClassList("gfu-change-row--header", true);
                EnableInClassList("gfu-change-row--excluded", false);
                EnableInClassList("gfu-change-row--locked-other", false);
                Chevron.style.display = DisplayStyle.Flex;
                Chevron.IconName = isCollapsed ? "chevron-right" : "chevron-down";
                Include.SetValueWithoutNotify(selected > 0);
                Include.showMixedValue = selected > 0 && selected < count;
                Include.tooltip = "Include every file in this group";
                icon.image = kindIcon;
                nameLabel.text = label.ToUpperInvariant();
                groupCount.text = count.ToString();
                groupCount.style.display = DisplayStyle.Flex;
                directory.text = selected < count && selected > 0 ? selected + " selected" : string.Empty;
                size.EnableInClassList("gfu-hidden", true);
                lockChip.EnableInClassList("gfu-hidden", true);
                actions.EnableInClassList("gfu-hidden", true);
                pill.style.display = DisplayStyle.None;
                tooltip = null;
            }

            public void BindFile(ChangeEntry e, bool included, Texture fileIcon, bool canLock)
            {
                EnableInClassList("gfu-change-row--header", false);
                EnableInClassList("gfu-change-row--excluded", !included);
                EnableInClassList("gfu-change-row--locked-other", e.LockedByOther);
                Chevron.style.display = DisplayStyle.None;
                Include.showMixedValue = false;
                Include.SetValueWithoutNotify(included);
                Include.tooltip = included ? "Included in the commit" : "Left out of the commit";
                icon.image = fileIcon;
                nameLabel.text = e.DisplayName;
                groupCount.style.display = DisplayStyle.None;
                directory.text = e.Directory;
                tooltip = e.Key + (e.SettingsOnly ? "\nOnly the import settings (.meta) changed." : string.Empty);

                var showSize = e.SizeBytes > 0 && (e.Kind == AssetKind.Texture || e.Kind == AssetKind.Model || e.Kind == AssetKind.Audio || e.SizeBytes > 5 * 1024 * 1024);
                size.text = showSize ? GitUi.FileSize(e.SizeBytes) : string.Empty;
                size.EnableInClassList("gfu-hidden", !showSize);

                if (e.Lock.HasValue)
                {
                    lockChip.EnableInClassList("gfu-hidden", false);
                    lockChip.EnableInClassList("gfu-lock-chip--mine", e.LockedByMe);
                    lockChip.EnableInClassList("gfu-lock-chip--other", !e.LockedByMe);
                    lockLabel.text = e.LockedByMe ? "You" : e.LockOwner;
                    lockChip.tooltip = e.LockedByMe ? "You locked this file" : "Locked by " + e.LockOwner;
                }
                else
                {
                    lockChip.EnableInClassList("gfu-hidden", true);
                }

                actions.EnableInClassList("gfu-hidden", false);
                LockButton.style.display = canLock || e.LockedByMe ? DisplayStyle.Flex : DisplayStyle.None;
                GitUi.SetButtonText(LockButton, e.LockedByMe ? "Unlock" : "Lock");
                var lockGlyph = LockButton.Q<GitIcon>();
                if (lockGlyph != null)
                    lockGlyph.IconName = e.LockedByMe ? "unlock" : "lock";

                pill.style.display = DisplayStyle.Flex;
                GitUi.SetPill(pill, e.StatusLabel, e.StatusTone);
            }
        }
    }
}
