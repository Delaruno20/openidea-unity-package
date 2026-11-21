using System;
using System.IO;
using System.Linq;
using Unity.CodeEditor;
using UnityEditor;
using UnityEngine;

namespace OpenIDEA.Unity
{
    /// OpenIDEA in Preferences › External Tools › External Script Editor, like Rider's package.
    [InitializeOnLoad]
    public class OpenIdeaCodeEditor : IExternalCodeEditor
    {
        static OpenIdeaCodeEditor()
        {
            var editor = new OpenIdeaCodeEditor();
            CodeEditor.Register(editor);
            if (IsCurrent)
                editor.Initialize(CodeEditor.CurrentEditorInstallation);
        }

        internal static bool IsCurrent => Discovery.IsOpenIdea(CodeEditor.CurrentEditorInstallation);

        internal static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        public CodeEditor.Installation[] Installations =>
            Discovery.Installations().Select(path => new CodeEditor.Installation { Name = "OpenIDEA", Path = path }).ToArray();

        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            if (Discovery.IsOpenIdea(editorPath))
            {
                installation = new CodeEditor.Installation { Name = "OpenIDEA", Path = editorPath };
                return true;
            }
            installation = default;
            return false;
        }

        public void Initialize(string editorInstallationPath)
        {
            BridgeServer.EnsureStarted();
            EditorApplication.delayCall += () => ProjectGeneration.Sync(false);
        }

        public void OnGUI()
        {
            EditorGUILayout.LabelField("Generate .csproj files for:");
            EditorGUI.indentLevel++;
            foreach (var source in OpenIdeaSettings.PackageSources)
            {
                var value = OpenIdeaSettings.GeneratesFor(source.Key);
                var next = EditorGUILayout.Toggle(source.Value, value);
                if (next != value) OpenIdeaSettings.SetGeneratesFor(source.Key, next);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();
            var refresh = EditorGUILayout.Toggle(
                new GUIContent("Refresh assets on save", "OpenIDEA asks Unity to refresh when you save a script, like Rider."),
                OpenIdeaSettings.RefreshOnSave);
            if (refresh != OpenIdeaSettings.RefreshOnSave) OpenIdeaSettings.RefreshOnSave = refresh;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Regenerate project files", GUILayout.Width(200)))
                    ProjectGeneration.Sync(true);
                GUILayout.Label(BridgeServer.Port > 0 ? $"OpenIDEA bridge: 127.0.0.1:{BridgeServer.Port}" : "OpenIDEA bridge: stopped",
                    EditorStyles.miniLabel);
            }
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            ProjectGeneration.SyncIfNeeded(addedFiles.Concat(deletedFiles).Concat(movedFiles).Concat(movedFromFiles).Concat(importedFiles));
        }

        public void SyncAll()
        {
            ProjectGeneration.Sync(true);
        }

        public bool OpenProject(string filePath = "", int line = -1, int column = -1)
        {
            if (!string.IsNullOrEmpty(filePath) && !ProjectGeneration.IsSupportedFile(filePath))
                return false;

            ProjectGeneration.Sync(false);
            var file = string.IsNullOrEmpty(filePath) ? null : Path.GetFullPath(filePath);
            return Discovery.Open(CodeEditor.CurrentEditorInstallation, ProjectRoot, file, line, column);
        }
    }
}
