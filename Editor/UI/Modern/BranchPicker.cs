using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Editor.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    /// <summary>
    /// A popover under the branch button: current branch, your branches, the team's branches on
    /// GitHub, and a one-step "new branch from here".
    /// </summary>
    class BranchPicker
    {
        private readonly GitWindow window;
        private GitSession Session => window.Session;

        public VisualElement Root { get; }
        public bool IsOpen { get; private set; }

        private readonly VisualElement popover;
        private readonly TextField search;
        private readonly ScrollView list;
        private readonly VisualElement notice;
        private readonly Label noticeLabel;
        private readonly VisualElement footer;
        private readonly Button newBranchButton;
        private readonly VisualElement newBranchForm;
        private readonly TextField newBranchName;

        public BranchPicker(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Div("gfu-overlay");
            Root.style.display = DisplayStyle.None;

            var backdrop = GitUi.Div("gfu-overlay__backdrop");
            backdrop.RegisterCallback<PointerDownEvent>(_ => Close());
            Root.Add(backdrop);

            popover = GitUi.Column("gfu-popover gfu-branch-picker");
            search = GitUi.Field("Find a branch");
            search.AddToClassList("gfu-search");
            search.RegisterValueChangedCallback(_ => Rebuild());
            popover.Add(GitUi.Row("gfu-popover__search", search));

            list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("gfu-branch-picker__list");
            popover.Add(list);

            notice = GitUi.Row("gfu-branch-picker__notice");
            var info = new GitIcon("info", 14);
            info.AddToClassList("gfu-icon--accent");
            notice.Add(info);
            noticeLabel = GitUi.Text(string.Empty, "gfu-branch-picker__notice-text");
            notice.Add(noticeLabel);
            popover.Add(notice);

            footer = GitUi.Column("gfu-popover__footer");
            newBranchButton = GitUi.Button("New branch", ShowNewBranchForm, "secondary", "plus");
            newBranchButton.AddToClassList("gfu-btn--stretch");
            footer.Add(newBranchButton);
            newBranchForm = GitUi.Row("gfu-branch-picker__form");
            newBranchName = GitUi.Field("e.g. forest-lighting");
            newBranchName.AddToClassList("gfu-grow");
            newBranchName.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    CreateBranch();
                    evt.StopPropagation();
                }
            });
            newBranchForm.Add(newBranchName);
            newBranchForm.Add(GitUi.Button("Create", CreateBranch, "primary"));
            newBranchForm.style.display = DisplayStyle.None;
            footer.Add(newBranchForm);
            popover.Add(footer);

            Root.Add(popover);
        }

        public void Open(float left, float top)
        {
            IsOpen = true;
            popover.style.left = Mathf.Max(8, left);
            popover.style.top = top;
            Root.style.display = DisplayStyle.Flex;
            search.SetValueWithoutNotify(string.Empty);
            newBranchForm.style.display = DisplayStyle.None;
            newBranchButton.style.display = DisplayStyle.Flex;
            window.SetBranchButtonOpen(true);
            Session.Repository?.Refresh(CacheType.Branches);
            Rebuild();
            search.schedule.Execute(() => search.Focus()).StartingIn(30);
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsOpen = false;
            Root.style.display = DisplayStyle.None;
            window.SetBranchButtonOpen(false);
        }

        public void Refresh(GitDataKind kind)
        {
            if (IsOpen && (kind & (GitDataKind.Branches | GitDataKind.Branch | GitDataKind.Status)) != 0)
                Rebuild();
        }

        private void Rebuild()
        {
            list.Clear();
            var current = Session.BranchName;
            var query = search.value?.Trim();
            bool Match(string name) => string.IsNullOrEmpty(query) || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            var locals = Session.LocalBranches.Select(b => b.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            var localSet = new HashSet<string>(locals, StringComparer.OrdinalIgnoreCase);
            var trackedRemotes = new HashSet<string>(Session.LocalBranches.Select(b => b.Tracking).Where(t => !string.IsNullOrEmpty(t)), StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(current) && Match(current))
            {
                list.Add(GitUi.SectionLabel("Current"));
                list.Add(Item(current, current == "main" || current == "master" ? "Everyone's shared work" : null, true, null));
            }

            var yours = locals.Where(n => n != current && Match(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (yours.Count > 0)
            {
                list.Add(GitUi.SectionLabel("Your branches"));
                foreach (var name in yours)
                {
                    var branchName = name;
                    list.Add(Item(name, "On this computer", false, () => SwitchTo(branchName), menu =>
                    {
                        menu.AppendAction("Delete branch…", _ => Delete(branchName));
                    }));
                }
            }

            var team = Session.RemoteBranches
                .Select(b => b.Name)
                .Where(n => !string.IsNullOrEmpty(n) && !n.EndsWith("/HEAD", StringComparison.Ordinal))
                .Where(n => !trackedRemotes.Contains(n) && !localSet.Contains(LocalName(n)))
                .Where(n => Match(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (team.Count > 0)
            {
                list.Add(GitUi.SectionLabel("Team branches on GitHub"));
                foreach (var remote in team)
                {
                    var remoteName = remote;
                    list.Add(Item(LocalName(remote), "Get it from " + RemotePrefix(remote), false, () => CheckoutRemote(remoteName)));
                }
            }

            if (list.childCount == 0)
                list.Add(GitUi.Text(string.IsNullOrEmpty(query) ? "No branches yet." : "No branch matches \"" + query + "\".", "gfu-section-empty"));

            var changes = window.Session.Changes.Count > 0;
            notice.style.display = changes ? DisplayStyle.Flex : DisplayStyle.None;
            noticeLabel.text = "Your uncommitted changes come with you when you switch.";
            GitUi.SetButtonText(newBranchButton, "New branch from " + (string.IsNullOrEmpty(current) ? "here" : current));
        }

        private VisualElement Item(string name, string subtitle, bool isCurrent, Action onClick, Action<DropdownMenu> contextMenu = null)
        {
            var button = new Button(() =>
            {
                if (onClick != null)
                    onClick();
                else
                    Close();
            });
            button.AddToClassList("gfu-branch-item");
            button.EnableInClassList("gfu-branch-item--current", isCurrent);
            var icon = new GitIcon(isCurrent ? "check" : "branch", 14);
            if (isCurrent)
                icon.AddToClassList("gfu-icon--good");
            button.Add(icon);
            var col = GitUi.Column("gfu-branch-item__text");
            col.Add(GitUi.Text(name, "gfu-branch-item__name"));
            if (!string.IsNullOrEmpty(subtitle))
                col.Add(GitUi.Text(subtitle, "gfu-branch-item__sub"));
            button.Add(col);
            if (contextMenu != null)
                button.AddManipulator(new ContextualMenuManipulator(evt => contextMenu(evt.menu)));
            return button;
        }

        private static string LocalName(string remoteBranch)
        {
            var slash = remoteBranch.IndexOf('/');
            return slash >= 0 ? remoteBranch.Substring(slash + 1) : remoteBranch;
        }

        private static string RemotePrefix(string remoteBranch)
        {
            var slash = remoteBranch.IndexOf('/');
            return slash > 0 ? remoteBranch.Substring(0, slash) : "GitHub";
        }

        private void SwitchTo(string branch)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            Close();
            Run("Switching to " + branch + "…", repo.SwitchBranch(branch), "Switched to " + branch + ".");
        }

        private void CheckoutRemote(string remoteBranch)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            var local = LocalName(remoteBranch);
            Close();
            Run("Getting " + local + "…", repo.CreateBranch(local, remoteBranch).Then(repo.SwitchBranch(local)), "Switched to " + local + ".");
        }

        private void ShowNewBranchForm()
        {
            newBranchButton.style.display = DisplayStyle.None;
            newBranchForm.style.display = DisplayStyle.Flex;
            newBranchName.SetValueWithoutNotify(string.Empty);
            newBranchName.schedule.Execute(() => newBranchName.Focus()).StartingIn(30);
        }

        private void CreateBranch()
        {
            var repo = Session.Repository;
            var name = SanitizeBranchName(newBranchName.value);
            if (repo == null || string.IsNullOrEmpty(name))
                return;
            if (Session.LocalBranches.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                window.ShowToast("There's already a branch called " + name + ".", "warn");
                return;
            }
            var from = Session.BranchName;
            Close();
            Run("Creating " + name + "…", repo.CreateBranch(name, from).Then(repo.SwitchBranch(name)), "Created " + name + " from " + from + ". Push when you want the team to see it.");
        }

        private void Delete(string branch)
        {
            var repo = Session.Repository;
            if (repo == null)
                return;
            if (!EditorUtility.DisplayDialog("Delete " + branch + "?",
                    "This removes the branch from your computer. Branches already on GitHub stay there.",
                    "Delete", "Cancel"))
                return;
            Session.Run("Deleting " + branch + "…", repo.DeleteBranch(branch, false), (ok, ex) =>
            {
                if (ok)
                {
                    window.ShowToast("Deleted " + branch + ".", "good");
                    return;
                }
                if (EditorUtility.DisplayDialog("Delete anyway?",
                        branch + " has commits that aren't in any other branch. Deleting it throws that work away.",
                        "Delete anyway", "Keep it"))
                {
                    Session.Run("Deleting " + branch + "…", repo.DeleteBranch(branch, true), (forced, forcedError) =>
                        window.ShowToast(forced ? "Deleted " + branch + "." : "Couldn't delete: " + GitSession.FriendlyError(forcedError), forced ? "good" : "error"));
                }
            });
        }

        private void Run(string label, ITask task, string success)
        {
            var started = Session.Run(label, task, (ok, ex) =>
            {
                if (ok)
                {
                    AssetDatabase.Refresh();
                    window.ShowToast(success, "good");
                }
                else
                {
                    window.ShowToast("Couldn't switch branch: " + GitSession.FriendlyError(ex), "error", sticky: true);
                }
            });
            if (!started)
                window.ShowToast("Still busy with the last action. Try again in a moment.", "info");
        }

        private static string SanitizeBranchName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var chars = raw.Trim().Select(c => char.IsWhiteSpace(c) ? '-' : c)
                .Where(c => c != '~' && c != '^' && c != ':' && c != '?' && c != '*' && c != '[' && c != '\\')
                .ToArray();
            var name = new string(chars).Trim('/', '.', '-');
            while (name.Contains("--"))
                name = name.Replace("--", "-");
            while (name.Contains(".."))
                name = name.Replace("..", ".");
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}
