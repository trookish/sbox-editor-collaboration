using System;
using System.IO;
using Sandbox;
using Sandbox.Internal;

namespace TeamCreate;

/// <summary>
/// This computer's shared play choice (Core/SharedPlaySetting.cs), kept in <c>.collaboration/shared-play.txt</c> next to the tunnel preference: one person's
/// choice, ignored by Git and by the project manifest. OFF until the checkbox is ticked; a file that cannot be read keeps it off and says so in the log.
/// A host reads it when it starts hosting, a guest when it connects, so changing it applies to the next session.
/// </summary>
public static class SharedPlayPreference
{
    private static bool loaded;

    private static bool enabled;

    private static string PathOnDisk => Path.Combine( SceneAdapter.Root, ".collaboration", SharedPlaySetting.FileName );

    public static bool Enabled
    {
        get
        {
            if ( !loaded )
            {
                loaded = true;
                string problem = null;
                try
                {
                    string path = PathOnDisk;
                    enabled = SharedPlaySetting.Parse( File.Exists( path ) ? File.ReadAllText( path ) : null, out problem );
                }
                catch ( Exception ex ) when ( ex is IOException || ex is UnauthorizedAccessException )
                {
                    enabled = false;
                    problem = "The shared play preference in .collaboration/" + SharedPlaySetting.FileName + " could not be read (" + ex.Message + "), so shared play stays off.";
                }
                if ( problem != null )
                {
                    GlobalGameNamespace.Log.Error( "Shared play: " + CollaborationDiagnostics.Bounded( problem ) );
                }
            }
            return enabled;
        }
        set
        {
            string path = PathOnDisk;
            Directory.CreateDirectory( Path.GetDirectoryName( path ) );
            File.WriteAllText( path, SharedPlaySetting.Format( value ) );
            enabled = value;
            loaded = true;
        }
    }
}
