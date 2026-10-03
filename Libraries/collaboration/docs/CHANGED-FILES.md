# Files changed in this session

The library never runs Git. After a session, the host and each member must commit what changed themselves. The **Files changed in this
session** card (Session tab of the Collaboration dock) lists the project files that were changed on this computer, so nothing is missed.

## What is listed

One row per project-relative path, `<kind> <path> (<who>)`, in ordinal path order:

| Kind | Meaning |
|---|---|
| Added | The file did not exist at the start of the session. |
| Changed | The file existed and its content changed. |
| Deleted | The file was removed. |
| Received from host | The host's version was written here by file synchronization. |
| Code received | A code file was written here by an accepted code transfer. |

The latest event for a path decides the peer, time and digest. Kinds merge so the row stays true to the start of the session: Added then
Changed stays Added; Added then Deleted removes the row (nothing to commit); Changed then Deleted is Deleted; Deleted then Added is Changed.

Recorded by:

- the host, when it writes or deletes a file for a member (peer = the member's name);
- a member, for files received from the host (peer = "the host") and for its own additions, edits and deletions seen by its file poll (peer = "you");
- a scene save while connected (peer = "you"; the path comes from the engine in lower case, so its case can differ from the file on disk).

The card shows the first 50 rows and the count, `N files changed. Commit them through Git when you are done.` At most 5,000 rows are kept; the
least recently touched row is dropped and counted ("N older changes are not shown."). **Copy list** copies every kept row to the clipboard
(`EditorUtility.Clipboard.Copy`). **Open recovery folder** (`EditorUtility.OpenFolder`) appears once `.collaboration/recovery` exists; it holds
the copy made before any file was replaced or deleted.

## Persistence

The log is written atomically to `.collaboration/session-changes.json` at most once every 5 seconds while it changes, and when the session is
left. The next session reads only the count from that file (limit 1 MiB, malformed files ignored) to show `Previous session: N files changed.`
while nothing has changed yet. Sessions are never merged. The file is not part of the project to commit: `.collaboration` is local state.

## Limits

- Files changed by the host's own editor, outside any synchronized event, are not recorded: the host runs no file poll of its own.
- A project file changed outside the Assets folder is listed only when it arrived through a code transfer.
- Nothing here starts Git or any other process; a source test enforces it.
