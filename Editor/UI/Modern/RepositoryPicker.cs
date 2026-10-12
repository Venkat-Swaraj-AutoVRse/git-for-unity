using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    class RepositoryPicker
    {
        private readonly Action<string> select;
        private readonly Action clone;
        private readonly Action browse;
        private readonly Action rescan;
        private readonly VisualElement popover;
        private readonly TextField search;
        private readonly ScrollView list;
        private IReadOnlyList<RepositoryEntry> repositories;
        private string currentPath;
        private string selectedPath;

        public VisualElement Root { get; }
        public bool IsOpen { get; private set; }

        public RepositoryPicker(Action<string> select, Action clone, Action browse, Action rescan)
        {
            this.select = select;
            this.clone = clone;
            this.browse = browse;
            this.rescan = rescan;

            Root = GitUi.Div("gfu-overlay");
            Root.style.display = DisplayStyle.None;
            var backdrop = GitUi.Div("gfu-overlay__backdrop");
            backdrop.RegisterCallback<PointerDownEvent>(_ => Close());
            Root.Add(backdrop);

            popover = GitUi.Column("gfu-popover gfu-repository-picker");
            search = GitUi.Field("Find a repository");
            search.AddToClassList("gfu-search");
            search.RegisterValueChangedCallback(_ => Rebuild());
            popover.Add(GitUi.Row("gfu-popover__search", search));

            list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("gfu-branch-picker__list");
            popover.Add(list);

            var footer = GitUi.Column("gfu-popover__footer gfu-repository-picker__footer");
            footer.Add(GitUi.Button("Use project default", () => Choose(null), "ghost"));
            footer.Add(GitUi.Button("Clone repository", () => { Close(); clone(); }, "secondary", "repo"));
            footer.Add(GitUi.Button("Browse for a folder", () => { Close(); browse(); }, "secondary"));
            footer.Add(GitUi.Button("Look for repositories again", rescan, "ghost"));
            popover.Add(footer);
            Root.Add(popover);
        }

        public void Open(float left, float top, float availableHeight, IReadOnlyList<RepositoryEntry> entries, string current, string selected)
        {
            repositories = entries;
            currentPath = current;
            selectedPath = selected;
            popover.style.left = Mathf.Max(8, left);
            popover.style.top = top;
            popover.style.maxHeight = Mathf.Max(180, Mathf.Min(480, availableHeight - top - 8));
            search.SetValueWithoutNotify(string.Empty);
            Root.style.display = DisplayStyle.Flex;
            IsOpen = true;
            Rebuild();
            search.schedule.Execute(() => search.Focus()).StartingIn(30);
        }

        public void Refresh(IReadOnlyList<RepositoryEntry> entries)
        {
            repositories = entries;
            if (IsOpen)
                Rebuild();
        }

        public void Close()
        {
            IsOpen = false;
            Root.style.display = DisplayStyle.None;
        }

        private void Rebuild()
        {
            list.Clear();
            var query = search.value?.Trim();
            var entries = new List<RepositoryEntry>(repositories ?? Array.Empty<RepositoryEntry>());
            if (!string.IsNullOrEmpty(currentPath) && !entries.Any(e => SamePath(e.Path, currentPath)))
                entries.Add(new RepositoryEntry { Path = currentPath });
            if (!string.IsNullOrEmpty(selectedPath) && !entries.Any(e => SamePath(e.Path, selectedPath)))
                entries.Add(new RepositoryEntry { Path = selectedPath });

            bool Matches(RepositoryEntry entry) => string.IsNullOrEmpty(query) ||
                FolderName(entry.Path).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            var current = entries.FirstOrDefault(e => SamePath(e.Path, currentPath));
            if (!string.IsNullOrEmpty(current.Path) && Matches(current))
            {
                list.Add(GitUi.SectionLabel("Current"));
                list.Add(Item(current, true));
            }

            var others = entries.Where(e => !SamePath(e.Path, currentPath) && Matches(e))
                .OrderBy(e => FolderName(e.Path), StringComparer.OrdinalIgnoreCase).ToList();
            if (others.Count > 0)
            {
                list.Add(GitUi.SectionLabel("Repositories"));
                foreach (var entry in others)
                    list.Add(Item(entry, false));
            }

            if (list.childCount == 0)
                list.Add(GitUi.Text(string.IsNullOrEmpty(query) ? "No repositories found." : "No matching repositories.", "gfu-section-empty"));
        }

        private VisualElement Item(RepositoryEntry entry, bool isCurrent)
        {
            var path = entry.Path;
            var button = new Button(() => Choose(path));
            button.AddToClassList("gfu-branch-item");
            button.EnableInClassList("gfu-branch-item--current", isCurrent);
            var icon = new GitIcon(isCurrent ? "check" : "repo", 14);
            if (isCurrent)
                icon.AddToClassList("gfu-icon--good");
            button.Add(icon);
            button.Add(GitUi.Text(FolderName(path), "gfu-branch-item__name gfu-repository-picker__name"));
            return button;
        }

        private void Choose(string path)
        {
            Close();
            select(path);
        }

        private static bool SamePath(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(GitSession.Normalize(a).TrimEnd('/'), GitSession.Normalize(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        private static string FolderName(string path)
        {
            var name = Path.GetFileName(path?.TrimEnd('/', '\\'));
            return string.IsNullOrEmpty(name) ? path ?? "Repository" : name;
        }
    }
}
