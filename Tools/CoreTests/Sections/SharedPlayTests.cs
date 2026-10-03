using TeamCreate;

/// <summary>
/// Shared play mode: one person plays, the others watch a mirrored, non-executing copy and fly through it. The sections are partial files so each
/// piece (roster, whitelist, sampler, relay, camera, fence) is added and committed on its own. Assertion names start with <c>play_</c>.
/// </summary>
static partial class SharedPlayTests
{
    public static void Run( Action<bool, string> assert )
    {
        Roster( assert );
    }
}
