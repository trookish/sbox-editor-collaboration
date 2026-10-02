using TeamCreate;

/// <summary>
/// Finds every static class in Sections/ named "*Tests" that exposes
/// <c>public static void Run( Action&lt;bool, string&gt; assert )</c> and runs it with the shared Assert.
/// New test sections are separate files, so parallel workers never edit the same file.
/// </summary>
static class SectionRunner
{
    public static void RunAll( Action<bool, string> assert )
    {
        var runners = typeof( SectionRunner ).Assembly.GetTypes()
            .Where( t => t.IsAbstract && t.IsSealed && t.Name.EndsWith( "Tests", StringComparison.Ordinal ) )
            .Select( t => (Type: t, Run: t.GetMethod( "Run", new[] { typeof( Action<bool, string> ) } )) )
            .Where( x => x.Run != null && x.Run.IsStatic )
            .OrderBy( x => x.Type.FullName, StringComparer.Ordinal );
        foreach ( var runner in runners )
        {
            Console.WriteLine( "SECTION: " + runner.Type.Name );
            runner.Run.Invoke( null, new object[] { assert } );
        }
    }
}
