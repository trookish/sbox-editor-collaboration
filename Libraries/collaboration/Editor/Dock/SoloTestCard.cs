using System;
using System.Linq;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The "Try it alone" card. In a real project it offers one button, which copies the project into a disposable folder, opens that copy in a second
/// editor window, and runs the solo test there by itself. In a disposable copy it shows the test's checklist, live. It never runs the test in a real
/// project, because the test relies on an identity the host only accepts inside a copy.
/// </summary>
public sealed class SoloTestCard : DockCard
{
    private readonly Button run;

    private readonly Button stop;

    private readonly Button openReport;

    private readonly Label notice;

    private readonly Label summary;

    private readonly Widget list;

    private string lastSignature;

    private string status;

    private bool statusIsError;

    protected override bool FieldsAssigned => run != null && stop != null && notice != null && summary != null && list != null && openReport != null;

    public SoloTestCard(Widget parent)
        : base(parent, "Try it alone")
    {
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        run = Layout.Add(new Button.Primary("Run solo test", this));
        run.ToolTip = "Copies this project to a disposable folder, opens the copy in a second editor window, and runs a test there in which a scripted collaborator joins, edits and leaves. Your project is never changed.";
        run.Clicked = () => DockCard.Run(RunClicked);
        stop = Layout.Add(new Button("Stop the test", this));
        stop.ToolTip = "Ends the test: the collaborator leaves, the object it made is removed, and the session ends.";
        stop.Clicked = () => DockCard.Run(SoloTestRunner.Stop);
        summary = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        list = Layout.Add(new Widget(this));
        list.Layout = Layout.Column();
        list.Layout.Spacing = 2f;
        openReport = Layout.Add(new Button("Open the last report", this));
        openReport.ToolTip = "The last run's results as a file, one line per check.";
        openReport.Clicked = () => DockCard.Run(OpenReport);
        stop.Visible = false;
        openReport.Visible = false;
        list.Visible = false;
    }

    private static string ProjectRoot()
    {
        try
        {
            return SceneAdapter.Root;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RunClicked()
    {
        status = null;
        statusIsError = false;
        if (SoloTestRunner.IsSoloCopy)
        {
            SoloTestRunner.Start();
            return;
        }
        LaunchCopy();
    }

    private void LaunchCopy()
    {
        string root = ProjectRoot();
        CollaborationSession session = CollaborationSession.Current;
        if (session.Busy)
        {
            Say("Leave the current collaboration session before running the solo test.", true);
            return;
        }
        if (SceneEditorSession.All.Any((SceneEditorSession x) => !(x is GameEditorSession) && x.HasUnsavedChanges))
        {
            Say("Save your scene first, so the copy is made from what you see.", true);
            return;
        }
        string memory = SoloCopy.MemoryAdvice(SoloTestLauncher.AvailableMemoryBytes());
        if (memory != null)
        {
            Say(memory, true);
            return;
        }
        string existing = SoloCopy.CopyRoot(root);
        if (SoloTestLauncher.IsRunning(existing))
        {
            Say("The solo test window is already open. Switch to it.", false);
            return;
        }
        Say("Copying the project…", false);
        SoloCopyResult copy = SoloCopy.Build(root, autorun: true);
        if (!copy.Ok)
        {
            Say(copy.Error, true);
            return;
        }
        string error = SoloTestLauncher.Launch(copy.Path);
        if (error != null)
        {
            Say(error, true);
            return;
        }
        Say("Opened a solo test window (" + copy.Files + " files copied). It runs the test by itself in about a minute; watch that window.", false);
    }

    private void OpenReport()
    {
        string root = ProjectRoot();
        string path = System.IO.Path.Combine(SoloCopy.CopyRoot(root), ".collaboration", SoloTestRunner.ReportFile);
        if (!System.IO.File.Exists(path))
        {
            path = System.IO.Path.Combine(root, ".collaboration", SoloTestRunner.ReportFile);
        }
        if (System.IO.File.Exists(path))
        {
            EditorUtility.OpenFile(path);
        }
    }

    private void Say(string text, bool error)
    {
        status = text;
        statusIsError = error;
    }

    public void Refresh()
    {
        string root = ProjectRoot();
        bool inCopy = SoloTestRunner.IsSoloCopy;
        bool active = SoloTestRunner.Active;
        string lastRun = inCopy ? SoloTestRunner.LastRunSummary(root) : SoloTestRunner.LastRunSummary(SoloCopy.CopyRoot(root ?? string.Empty));
        string signature = inCopy + "|" + active + "|" + SoloTestRunner.Headline + "|" + status + "|" + statusIsError + "|" + lastRun + "|" + string.Join(";", SoloTestRunner.Steps.Select((SoloStep s) => s.State + s.Detail));
        if (string.Equals(signature, lastSignature, StringComparison.Ordinal))
        {
            return;
        }
        lastSignature = signature;

        string intro;
        if (inCopy)
        {
            intro = active
                ? "Running in this solo test copy. The blue sphere is a scripted collaborator."
                : "This is a disposable solo test copy. Nothing here is your real project.";
        }
        else
        {
            intro = "See two people editing together with just this editor. The test runs in a disposable copy of the project, so your scenes are never touched.";
        }
        DockCard.SetText(notice, status ?? intro);
        notice.Color = statusIsError ? DockTheme.BlockedColor : DockTheme.MutedColor;
        DockTheme.SetText(run, active ? "Running…" : (inCopy ? "Run the test again" : "Run solo test"));
        run.Enabled = !active;
        stop.Visible = active;
        bool haveList = SoloTestRunner.Steps.Count > 0;
        list.Visible = haveList;
        DockCard.SetText(summary, haveList ? SoloTestRunner.Headline : (lastRun != null ? "Last run: " + lastRun : string.Empty));
        summary.Visible = haveList || lastRun != null;
        openReport.Visible = !inCopy && lastRun != null;
        RebuildList();
    }

    private void RebuildList()
    {
        list.Layout.Clear(deleteWidgets: true);
        foreach (SoloStep step in SoloTestRunner.Steps)
        {
            Color color = step.State == SoloStepState.Passed ? DockTheme.OkColor : step.State == SoloStepState.Failed ? DockTheme.BlockedColor : step.State == SoloStepState.Running ? Theme.Text : DockTheme.MutedColor;
            string text = SoloReport.Symbol(step.State) + "  " + step.Title;
            if (!string.IsNullOrWhiteSpace(step.Detail) && (step.State == SoloStepState.Failed || step.State == SoloStepState.Running || step.State == SoloStepState.Skipped))
            {
                text += "\n      " + step.Detail;
            }
            list.Layout.Add(new Label(text, list)
            {
                WordWrap = true,
                Color = color,
                ToolTip = step.Detail ?? step.Title
            });
        }
    }
}
