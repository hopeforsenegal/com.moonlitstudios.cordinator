using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum EditorType { Symlink, HardCopy }
public static class CommandLineParams
{
    public static string Additional { get; } = "--additional";
    public static string Original { get; } = "-original";
    public static string OriginalProcessID { get; } = $"{Original} {Process.GetCurrentProcess().Id}";

    public static string AdditionalEditorParams { get; } = string.Join(" ", Additional, OriginalProcessID);
}
public static class MessageEndpoint
{
    public static string Playmode { get; } = Path.Combine(Paths.ProjectRootPath, nameof(PlayModeStateChange));
    public static string Scene { get; } = Path.Combine(Paths.ProjectRootPath, nameof(UnityEngine.SceneManagement.Scene));
}
public static class Messages
{
    public const string Edit = nameof(Edit);
    internal static string Play(string[] scriptingDefineSymbols) => $"{nameof(Play)}|{string.Join(";", scriptingDefineSymbols)}";
}
public static class Paths
{
    public static string ProjectPath { get; } = Application.dataPath.Replace("/Assets", "");
    public static string ProjectRootPath { get; } = Path.GetFullPath(Path.Combine(ProjectPath, ".."));
}
public static class EditorUserSettings
{
    public static EditorType Coordinator_EditorTypeOnCreate { get => (EditorType)EditorPrefs.GetInt(nameof(Coordinator_EditorTypeOnCreate), (int)EditorType.Symlink); set => EditorPrefs.SetInt(nameof(Coordinator_EditorTypeOnCreate), (int)value); }
    public static bool Coordinator_EditorCoordinatePlay { get => EditorPrefs.GetInt(nameof(Coordinator_EditorCoordinatePlay), 0) == 1; set => EditorPrefs.SetInt(nameof(Coordinator_EditorCoordinatePlay), value ? 1 : 0); }
}
public static class UntilExitSettings // SessionState is cleared when Unity exits. But survives domain reloads.
{
    public static string Coordinator_ParentProcessID { get => SessionState.GetString(nameof(Coordinator_ParentProcessID), string.Empty); set => SessionState.SetString(nameof(Coordinator_ParentProcessID), value); }
    public static string Coordinator_ProjectPathToChildProcessID { get => SessionState.GetString(nameof(Coordinator_ProjectPathToChildProcessID), string.Empty); set => SessionState.SetString(nameof(Coordinator_ProjectPathToChildProcessID), value); }
}
public struct PathToProcessId
{   // Format is 'long/project/path|1234124' and we store all of them seperated by ;
    public string path;
    public int processID;
    public const string Seperator = "|";
    public const string End = ";";

    public static string Join(params PathToProcessId[] pathToProcesIds)
    {
        var result = string.Empty;
        foreach (var p in pathToProcesIds) result += $"{p.path}{Seperator}{p.processID}{End}";
        return result;
    }
    public static PathToProcessId[] Split(string toParse)
    {
        var pathToProcessIdSplit = toParse.Split(End);
        var result = new List<PathToProcessId>();
        foreach (var p in pathToProcessIdSplit) {
            if (string.IsNullOrWhiteSpace(p)) continue;

            var split = p.Split(Seperator);
            if (int.TryParse(split[1], out var resultProcessId)) {
                result.Add(new PathToProcessId { path = split[0], processID = resultProcessId });
            } else {
                UnityEngine.Debug.LogWarning($"We failed to parse the {nameof(PathToProcessId)} on path '{split[0]}'");
            }
        }
        return result.ToArray();
    }
}
public struct EditorPaths
{
    public string Name;
    public string Path;
    public string Assets;
    public string ProjectSettings;
    public string Packages;

    public static EditorPaths PopulateEditorInfo(string path)
    {
        var pathByFolders = path.Split('/');
        return new EditorPaths
        {
            Path = path,
            Name = pathByFolders[pathByFolders.Length - 1],
            Assets = $"{path}/{nameof(Assets)}",
            ProjectSettings = $"{path}/{nameof(ProjectSettings)}",
            Packages = $"{path}/{nameof(Packages)}",
        };
    }
}

public static class Editors
{
    private static float RefreshInterval;
    private static readonly List<string> EndPointsToProcess = new List<string>();
    private static readonly SessionStateConvenientListInt Playmode = new SessionStateConvenientListInt(nameof(Playmode));

    [InitializeOnLoadMethod]
    public static void OnInitialize()
    {
        if (!IsAdditional()) {
            UnityEngine.Debug.Log("Is Original");
            var path = EditorSceneManager.GetActiveScene().path;
            if (!string.IsNullOrWhiteSpace(path)) {
                UnityEngine.Debug.Assert(!string.IsNullOrWhiteSpace(EditorSceneManager.GetActiveScene().name));
                SocketLayer.WriteMessage(MessageEndpoint.Scene, path);
            }
            EditorApplication.playModeStateChanged += OriginalCoordinatePlaymodeStateChanged; // Without being in a InitializeOnLoadMethod, the EnteredPlaymode event will get dropped in OriginalCoordinatePlaymodeStateChanged. We also cannot put that functionality in a EditorWindow/CoordinatorWindow
            EditorApplication.update += OriginalUpdate;
        } else {
            UnityEngine.Debug.Log("Is Additional");
            SocketLayer.OpenListenerOnFile(MessageEndpoint.Playmode);
            SocketLayer.OpenListenerOnFile(MessageEndpoint.Scene);
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++) {
                var arg = args[i];
                if (arg == CommandLineParams.Original) {
                    UntilExitSettings.Coordinator_ParentProcessID = args[i + 1];
                }
            }
            EditorApplication.update += AdditionalUpdate;
        }
    }

    private static void OriginalUpdate()
    {
        if (RefreshInterval > 0) {
            RefreshInterval -= Time.deltaTime;
        } else {
            RefreshInterval = .5f; // Refresh every half second
            ////////////////////////

            if (Playmode.Count() > 0) {
                var playmode = (PlayModeStateChange)Playmode.Dequeue();
                UnityEngine.Debug.Log($"Writing command '{playmode}'");
                switch (playmode) {
                    case PlayModeStateChange.EnteredPlayMode: SocketLayer.WriteMessage(MessageEndpoint.Playmode, Messages.Play(ProjectSettings.LoadInstance().scriptingDefineSymbols)); break;
                    case PlayModeStateChange.EnteredEditMode: SocketLayer.WriteMessage(MessageEndpoint.Playmode, Messages.Edit); break;
                }
            }
        }
    }

    private static void OriginalCoordinatePlaymodeStateChanged(PlayModeStateChange playmodeState)
    {
        if (playmodeState == PlayModeStateChange.EnteredPlayMode || playmodeState == PlayModeStateChange.EnteredEditMode) {
            Playmode.Queue((int)playmodeState);
        }
    }

    private static void AdditionalUpdate()
    {
        if (RefreshInterval > 0) {
            RefreshInterval -= Time.deltaTime;
        } else {
            RefreshInterval = .5f; // Refresh every half second

            if (int.TryParse(UntilExitSettings.Coordinator_ParentProcessID, out var processId)) {
                if (!IsProcessAlive(processId)) {
                    UnityEngine.Debug.Log($"The original '{UntilExitSettings.Coordinator_ParentProcessID}' closed so we should close ourselves");
                    Process.GetCurrentProcess().Kill();
                }
            }
        }

        foreach (var r in SocketLayer.ReceivedMessage) {
            var (endpoint, message) = (r.Key, r.Value);
            if (!string.IsNullOrWhiteSpace(message)) {
                EndPointsToProcess.Add(endpoint);
                UnityEngine.Debug.Log($"We consumed message '{message}'");
                if (endpoint == MessageEndpoint.Playmode) {
                    var split = message.Split("|");
                    if (split.Length == 2) {
                        UnityEngine.Debug.Log($"Doing asset database refresh. Updating Scripting Defines '{split[1]}'");
                        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Standalone), split[1]);
                        AssetDatabase.Refresh();
                    }

                    switch (split[0]) {
                        case nameof(Messages.Play): EditorApplication.isPlaying = true; break;
                        case nameof(Messages.Edit): EditorApplication.isPlaying = false; break;
                    }
                }
                if (endpoint == MessageEndpoint.Scene) {
                    if (SceneManager.GetActiveScene().path != message) {
                        if (Application.isPlaying) {
                            SceneManager.LoadScene(message);
                        } else {
                            EditorSceneManager.OpenScene(message);
                        }
                    }
                }
            }
        }
        foreach (var endpoint in EndPointsToProcess) {
            SocketLayer.ReceivedMessage[endpoint] = string.Empty;
        }
    }

    public static bool IsAdditional() => Environment.CommandLine.Contains(CommandLineParams.Additional);
    public static string[] GetEditorsAvailable() => new List<string>(Directory.EnumerateDirectories(Paths.ProjectRootPath)).ToArray();

    public static bool IsProcessAlive(int processId)
    {
        try { return !Process.GetProcessById(processId).HasExited; }
        catch (ArgumentException) { return false; } // this should suffice unless we throw ArgumentException for multiple reasons
    }

    public static void Symlink(string sourcePath, string destinationPath)
    {
        ExecuteBashCommandLine($"ln -s {sourcePath.Replace(" ", "\\ ")} {destinationPath.Replace(" ", "\\ ")}");
    }

    private static void ExecuteBashCommandLine(string command)
    {
        using (var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\"\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        }) {
            proc.Start();
            UntilExitSettings.Coordinator_ParentProcessID = proc.Id.ToString();
            proc.WaitForExit();

            if (!proc.StandardError.EndOfStream) {
                UnityEngine.Debug.LogError(proc.StandardError.ReadToEnd());
            }
        }
    }
}