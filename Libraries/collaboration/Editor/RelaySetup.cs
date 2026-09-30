using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using Sandbox.Internal;

namespace TeamCreate;

/// <summary>Sends Cloudflare requests through the engine's HTTP client, which is the only network access an editor script has.</summary>
public sealed class SandboxCloudflareTransport : ICloudflareTransport
{
    private const int MaxBodyChars = 262144;

    public async Task<CloudflareResponse> SendAsync(CloudflareRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = null;
        try
        {
            // Content-Type travels on the content itself; the engine refuses to set some headers by name, and everything the deploy needs is allowed.
            response = await Http.RequestAsync(request.Url, request.Method, request.Content, request.Headers.Count == 0 ? null : request.Headers, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CloudflareResponse(null, null, ex.GetType().Name + ": " + ex.Message);
        }
        using (response)
        {
            int status = (int)response.StatusCode;
            string body = null;
            try
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                body = text != null && text.Length <= MaxBodyChars ? text : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                body = null;
            }
            return new CloudflareResponse(status, body);
        }
    }
}

/// <summary>
/// The Relay card's machinery. Two buttons drive it: one opens Cloudflare's token page, the other reads the token from the clipboard, deploys the relay to the
/// person's own free account, and remembers the address on this computer. The token is held only for the length of the deploy: it is never written to disk,
/// never logged, and the clipboard is cleared afterwards if it still holds it. All state is polled from the frame, never from a thread of its own.
/// </summary>
public static class RelaySetup
{
    private static Task<DeployResult> task;

    private static CancellationTokenSource cancel;

    private static volatile string progress;

    private static string tokenInUse;

    private static bool localLoaded;

    private static string localRelay;

    private static string localProblem;

    public static DeployResult LastResult { get; private set; }

    public static bool Working => task != null && !task.IsCompleted;

    public static string Progress => progress;

    /// <summary>The relay this computer set up, or null. Read once and kept, because the dock asks every frame.</summary>
    public static string LocalRelay
    {
        get
        {
            EnsureLocalLoaded();
            return localRelay;
        }
    }

    /// <summary>Why the local file could not be used, or null.</summary>
    public static string LocalProblem
    {
        get
        {
            EnsureLocalLoaded();
            return localProblem;
        }
    }

    private static string LocalFilePath => Path.Combine(SceneAdapter.Root, ".collaboration", LocalRelayFile.FileName);

    private static void EnsureLocalLoaded()
    {
        if (localLoaded)
        {
            return;
        }
        localLoaded = true;
        localRelay = null;
        localProblem = null;
        try
        {
            string path = LocalFilePath;
            if (File.Exists(path))
            {
                localRelay = LocalRelayFile.Parse(File.ReadAllText(path), out localProblem);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            localProblem = "The relay address file could not be read: " + ex.Message;
        }
        if (localProblem != null)
        {
            GlobalGameNamespace.Log.Error("Collaboration relay: " + CollaborationDiagnostics.Bounded(localProblem));
        }
    }

    public static RelaySetupView Describe(string projectRelay)
    {
        RelaySetupView view = RelaySetupRules.Describe(new RelaySetupInput(projectRelay, LocalRelay, RelayDefaults.DefaultUrl, Working, progress, LastResult));
        if (localProblem != null && view.Mode == RelaySetupMode.Missing)
        {
            return view with { Text = localProblem + " " + view.Text, IsProblem = true };
        }
        return view;
    }

    /// <summary>Opens the Cloudflare page that creates the token, with the two permissions the relay needs already chosen.</summary>
    public static void OpenTokenPage()
    {
        // The engine's own tools open web addresses through this call (Project.EditUrl, Package.Url); MenuUtility.OpenUrl belongs to the menu assembly, which editor scripts cannot reference.
        EditorUtility.OpenFolder(CloudflareApi.TokenPageUrl());
    }

    /// <summary>Reads the token from the clipboard and deploys. The clipboard is checked before anything is sent anywhere.</summary>
    public static void Start()
    {
        if (Working)
        {
            return;
        }
        string clipboard = null;
        try
        {
            clipboard = EditorUtility.Clipboard.Paste();
        }
        catch (Exception)
        {
            clipboard = null;
        }
        if (!CloudflareApi.TryAcceptToken(clipboard, out string token, out string problem))
        {
            LastResult = new DeployResult(false, null, problem);
            progress = null;
            return;
        }
        LastResult = null;
        tokenInUse = token;
        cancel = new CancellationTokenSource();
        progress = "Starting…";
        var deployer = new RelayDeployer(new SandboxCloudflareTransport(), RelayWorkerSource.Text);
        task = deployer.RunAsync(token, text => progress = text, cancel.Token);
    }

    public static void Cancel()
    {
        cancel?.Cancel();
    }

    [EditorEvent.Frame]
    public static void Pump()
    {
        if (task == null || !task.IsCompleted)
        {
            return;
        }
        Task<DeployResult> finished = task;
        task = null;
        string token = tokenInUse;
        tokenInUse = null;
        cancel?.Dispose();
        cancel = null;
        progress = null;
        DeployResult result;
        try
        {
            result = finished.IsFaulted || finished.IsCanceled
                ? new DeployResult(false, null, "Setup stopped unexpectedly: " + CloudflareApi.Redact(finished.Exception?.GetBaseException().Message, token) + " Nothing was changed on this computer.")
                : finished.Result;
        }
        catch (Exception ex)
        {
            result = new DeployResult(false, null, "Setup stopped unexpectedly: " + CloudflareApi.Redact(ex.Message, token));
        }
        ClearClipboardIfStillHolds(token);
        if (result.Ok)
        {
            string saveProblem = Save(result.RelayUrl);
            if (saveProblem != null)
            {
                result = new DeployResult(false, null, saveProblem);
            }
        }
        LastResult = result;
        if (!result.Ok && !result.Cancelled)
        {
            GlobalGameNamespace.Log.Error("Collaboration relay setup: " + CollaborationDiagnostics.Bounded(result.Error));
        }
    }

    private static string Save(string relayUrl)
    {
        try
        {
            string path = LocalFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, LocalRelayFile.Format(relayUrl));
            File.Move(temp, path, true);
            localLoaded = false;
            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return "The relay is running at " + RelaySetupRules.HostOf(relayUrl) + ", but its address could not be saved on this computer (" + ex.Message + "). Put it in .collaboration/" + LocalRelayFile.FileName + " yourself, or set RelayUrl in ProjectSettings/collaboration.json.";
        }
    }

    /// <summary>The token has done its job; if it is still the thing on the clipboard, take it off so it is not pasted somewhere by accident.</summary>
    private static void ClearClipboardIfStillHolds(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }
        try
        {
            string now = EditorUtility.Clipboard.Paste();
            if (now != null && string.Equals(now.Trim(), token, StringComparison.Ordinal))
            {
                EditorUtility.Clipboard.Copy(string.Empty);
            }
        }
        catch (Exception)
        {
            // Not being able to clear the clipboard changes nothing about the deploy.
        }
    }
}
