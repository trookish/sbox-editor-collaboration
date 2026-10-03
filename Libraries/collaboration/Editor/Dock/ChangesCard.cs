using System;
using System.IO;
using System.Linq;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The "Files changed in this session" card. The library never runs Git, so this card is how the host and each member learn which project
/// files to commit after working together: it lists what file synchronization, a code transfer or a scene save changed on this computer,
/// from the session's <see cref="SessionChangeLog"/>. It shows the first 50 rows and says how many more exist; "Copy list" puts every row on
/// the clipboard (EditorUtility.Clipboard.Copy), and "Open recovery folder" opens the folder that holds the copies made before a file was
/// replaced or deleted (EditorUtility.OpenFolder), shown only once that folder exists.
/// </summary>
public sealed class ChangesCard : DockCard
{
    private const int ListedRows = 50;

    private readonly Label summary;

    private readonly Label list;

    private readonly Label more;

    private readonly Label previous;

    private readonly Button copy;

    private readonly Button recovery;

    private SessionChangeLog shown;

    private int shownRevision = -1;

    private int? shownPrevious = -1;

    private DateTime nextFolderCheck;

    protected override bool FieldsAssigned => summary != null && list != null && more != null && previous != null && copy != null && recovery != null;

    public ChangesCard(Widget parent)
        : base(parent, "Files changed in this session")
    {
        summary = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        list = Layout.Add(new Label("", this)
        {
            WordWrap = true
        });
        more = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        previous = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        copy = Layout.Add(new Button("Copy list", this));
        copy.ToolTip = "Copies every changed file, one per line, so it can be pasted into a commit message or a note. Nothing is committed for you.";
        copy.Clicked = () => DockCard.Run(() => EditorUtility.Clipboard.Copy(CollaborationSession.Current.Changes.CopyText()));
        recovery = Layout.Add(new Button("Open recovery folder", this));
        recovery.ToolTip = "Opens the folder that holds the copy of each file this session replaced or deleted, in case a change has to be undone.";
        recovery.Clicked = () => DockCard.Run(() => EditorUtility.OpenFolder(RecoveryFolder()));
        copy.Visible = false;
        recovery.Visible = false;
    }

    private static string RecoveryFolder()
    {
        return Path.Combine(SceneAdapter.Root, ".collaboration", "recovery");
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        SessionChangeLog log = current.Changes;
        int? earlier = current.PreviousSessionChanges;
        bool changed = !ReferenceEquals(shown, log) || shownRevision != log.Revision || shownPrevious != earlier;
        if (changed)
        {
            shown = log;
            shownRevision = log.Revision;
            shownPrevious = earlier;
            var rows = log.Rows();
            DockCard.SetText(summary, SessionChangeLog.SummaryLine(log.TotalChanged));
            DockCard.SetText(list, string.Join("\n", rows.Take(ListedRows).Select(SessionChangeLog.Describe)));
            list.Visible = rows.Count > 0;
            int hidden = Math.Max(0, rows.Count - ListedRows);
            string text = hidden > 0 ? $"{hidden} more {(hidden == 1 ? "file is" : "files are")} not listed here; Copy list includes every file." : "";
            if (log.OlderNotShown > 0)
            {
                text = (text.Length > 0 ? text + " " : "") + SessionChangeLog.OlderLine(log.OlderNotShown);
            }
            DockCard.SetText(more, text);
            more.Visible = text.Length > 0;
            // Only said while this session has not changed anything yet: once it has, the card is about the current session.
            DockCard.SetText(previous, earlier.HasValue && earlier.Value > 0 && log.TotalChanged == 0 ? SessionChangeLog.PreviousLine(earlier.Value) : "");
            previous.Visible = previous.Text.Length > 0;
            copy.Visible = rows.Count > 0;
        }
        // The folder appears when the first file is replaced; looking at the disk every frame would be wasteful, so twice a second is enough.
        bool folderCheck = DateTime.UtcNow >= nextFolderCheck;
        if (folderCheck)
        {
            nextFolderCheck = DateTime.UtcNow.AddMilliseconds(500);
        }
        bool hasFolder = folderCheck ? Directory.Exists(RecoveryFolder()) : recovery.Visible;
        bool folderChanged = recovery.Visible != hasFolder;
        if (folderChanged)
        {
            recovery.Visible = hasFolder;
        }
        if (changed || folderChanged)
        {
            AdjustSize();
            Reflow();
        }
    }
}
