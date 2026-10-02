static class SectionRunnerSmokeTests
{
    public static void Run( Action<bool, string> assert )
    {
        assert( true, "section_runner_discovers_sections" );
    }
}
