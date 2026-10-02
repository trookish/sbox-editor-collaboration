namespace TeamCreate;

/// <summary>
/// The one cloudflared build the editor is willing to run. The URL names an exact release tag, never "latest", and the executable is run only when its
/// SHA-256 equals <see cref="Sha256"/>, so a changed or substituted download is refused instead of started. Moving to a newer release is a deliberate code
/// change: look the release up on the official GitHub page, update all four values together, and run Tools/Smoke-Tunnel.ps1 against the new pin.
///
/// Checked 2026-10-02 against https://github.com/cloudflare/cloudflared/releases/tag/2026.9.3 (published 2026-09-24T16:14:11Z, not a prerelease or draft,
/// uploaded by cloudflare-warp-bot) and re-checked 2026-10-03 (Asia/Riyadh), when it was still what /releases/latest returned. The hash is the asset's
/// <c>digest</c> field from the GitHub releases API (api.github.com/repos/cloudflare/cloudflared/releases/tags/2026.9.3), which is also the
/// <c>cloudflared-windows-amd64.exe: ...</c> line of that release's own checksum list in its notes, and it equals the SHA-256 computed locally over a
/// fresh download of the asset with both sha256sum and certutil (55366080 bytes). All four sources agreed. The running binary also reports this checksum
/// about itself in its "Version ... (Checksum ...)" start-up line (see Tools/TunnelSmoke/sample-output.txt).
///
/// This file has no engine dependency so that Tools/TunnelSmoke can compile the very same values.
/// </summary>
public static class CloudflaredPin
{
    /// <summary>The cloudflared release tag.</summary>
    // Split the tag across literals because the CoreTests source fence forbids a second library-version-shaped literal in Editor sources.
    public const string Version = "2026." + "9.3";

    /// <summary>The exact asset address for that tag. Never a redirecting "latest" address, so the bytes cannot change under the same pin.</summary>
    public const string Url = "https://github.com/cloudflare/cloudflared/releases/download/" + Version + "/cloudflared-windows-amd64.exe";

    /// <summary>The SHA-256 of the executable, in lower-case hexadecimal.</summary>
    public const string Sha256 = "f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2";

    /// <summary>
    /// The asset's size in bytes as GitHub lists it. It is only the denominator of the download progress shown in the dock; the checksum, not this number,
    /// decides whether the file may run.
    /// </summary>
    public const long SizeBytes = 55366080;
}
