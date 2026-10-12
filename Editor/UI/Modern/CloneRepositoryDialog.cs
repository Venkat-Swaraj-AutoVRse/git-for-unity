using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    class CloneRepositoryDialog
    {
        private readonly GitWindow window;
        private GitSession Session => window.Session;
        private readonly VisualElement dialog;
        private readonly TextField urlField;
        private readonly TextField parentField;
        private readonly TextField nameField;
        private readonly Button cloneButton;
        private string suggestedName;

        public VisualElement Root { get; }
        public bool IsOpen { get; private set; }

        public CloneRepositoryDialog(GitWindow window)
        {
            this.window = window;
            Root = GitUi.Div("gfu-overlay");
            Root.style.display = DisplayStyle.None;
            var backdrop = GitUi.Div("gfu-overlay__backdrop");
            backdrop.RegisterCallback<PointerDownEvent>(_ => Close());
            Root.Add(backdrop);

            dialog = GitUi.Column("gfu-popover gfu-clone-dialog");
            dialog.Add(GitUi.Text("Clone a repository", "gfu-clone-dialog__title"));
            dialog.Add(GitUi.Text("Enter a Git URL and choose where to create its folder.", "gfu-clone-dialog__hint"));
            dialog.Add(GitUi.Text("Repository URL", "gfu-caption"));
            urlField = GitUi.Field("https://github.com/team/project.git");
            urlField.RegisterValueChangedCallback(evt =>
            {
                var inferred = InferName(evt.newValue);
                if (string.IsNullOrEmpty(nameField.value) || nameField.value == suggestedName)
                    nameField.SetValueWithoutNotify(inferred);
                suggestedName = inferred;
                UpdateCloneButton();
            });
            dialog.Add(urlField);

            dialog.Add(GitUi.Text("Parent folder", "gfu-caption"));
            var parentRow = GitUi.Row("gfu-clone-dialog__path");
            parentField = GitUi.Field("Choose a folder");
            parentField.RegisterValueChangedCallback(_ => UpdateCloneButton());
            parentRow.Add(parentField);
            parentRow.Add(GitUi.Button("Browse", Browse, "secondary"));
            dialog.Add(parentRow);

            dialog.Add(GitUi.Text("New repository folder", "gfu-caption"));
            nameField = GitUi.Field("project");
            nameField.RegisterValueChangedCallback(_ => UpdateCloneButton());
            dialog.Add(nameField);

            var actions = GitUi.Row("gfu-clone-dialog__actions");
            actions.Add(GitUi.Button("Cancel", Close, "ghost"));
            cloneButton = GitUi.Button("Clone", Clone, "primary", "repo");
            actions.Add(cloneButton);
            dialog.Add(actions);
            Root.Add(dialog);
        }

        public void Open()
        {
            var project = Session.ProjectPath;
            if (string.IsNullOrEmpty(parentField.value) && !string.IsNullOrEmpty(project))
                parentField.SetValueWithoutNotify(Path.GetDirectoryName(project.TrimEnd('/', '\\')));
            dialog.style.left = Mathf.Max(8, (window.position.width - 420) / 2);
            dialog.style.top = 48;
            Root.style.display = DisplayStyle.Flex;
            IsOpen = true;
            UpdateCloneButton();
            urlField.schedule.Execute(() => urlField.Focus()).StartingIn(30);
        }

        public void Close()
        {
            IsOpen = false;
            Root.style.display = DisplayStyle.None;
        }

        private void Browse()
        {
            var selected = EditorUtility.OpenFolderPanel("Choose parent folder for clone", parentField.value ?? Session.ProjectPath, string.Empty);
            if (!string.IsNullOrEmpty(selected))
                parentField.value = selected;
        }

        private void UpdateCloneButton()
        {
            cloneButton.SetEnabled(!string.IsNullOrWhiteSpace(urlField.value) &&
                !string.IsNullOrWhiteSpace(parentField.value) && !string.IsNullOrWhiteSpace(nameField.value) &&
                !Session.IsOperationRunning);
        }

        private void Clone()
        {
            var url = urlField.value?.Trim();
            var parent = parentField.value?.Trim();
            var name = nameField.value?.Trim();
            if (string.IsNullOrEmpty(url) || url.Any(char.IsControl) || string.IsNullOrEmpty(parent) ||
                string.IsNullOrEmpty(name) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("/") || name.Contains("\\"))
            {
                window.ShowToast("Enter a valid Git URL and a new folder name.", "warn");
                return;
            }

            string destination;
            try
            {
                parent = Path.GetFullPath(parent);
                destination = Path.GetFullPath(Path.Combine(parent, name));
            }
            catch (Exception ex)
            {
                window.ShowToast("Invalid destination: " + ex.Message, "warn");
                return;
            }
            if (!Directory.Exists(parent) || Directory.Exists(destination) || File.Exists(destination))
            {
                window.ShowToast("Choose an existing parent and a folder name that does not exist yet.", "warn");
                return;
            }

            var manager = Session.Manager;
            if (manager == null)
                return;
            var started = Session.Run("Cloning " + name + "…", manager.GitClient.Clone(url, destination, parent), (ok, ex) =>
            {
                UpdateCloneButton();
                if (!ok)
                {
                    window.ShowToast("Clone failed: " + GitSession.FriendlyError(ex), "error", sticky: true);
                    return;
                }
                try
                {
                    if (!Directory.Exists(Path.Combine(destination, ".git")) && !File.Exists(Path.Combine(destination, ".git")))
                        throw new InvalidOperationException("The clone did not create a Git repository.");
                    RepositorySwitcher.Switch(destination);
                    var project = Session.ProjectPath;
                    if (!string.IsNullOrEmpty(project) && destination.StartsWith(project.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        AssetDatabase.Refresh();
                    Close();
                    window.ShowToast("Cloned " + name + ".", "good");
                }
                catch (Exception switchError)
                {
                    window.ShowToast("Cloned, but could not open it: " + switchError.Message, "error", sticky: true);
                }
            });
            if (started)
                UpdateCloneButton();
            else
                window.ShowToast("Still busy with the last action. Try again in a moment.", "info");
        }

        private static string InferName(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;
            var tail = url.Trim().TrimEnd('/', '\\');
            tail = tail.Substring(Math.Max(tail.LastIndexOf('/'), tail.LastIndexOf(':')) + 1);
            if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                tail = tail.Substring(0, tail.Length - 4);
            return tail;
        }
    }
}
