using System;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// Draws one collaborator as a floating blue sphere with their profile picture and name above it. Every call is
/// a <c>Gizmo.Draw</c> primitive inside the caller's scope: nothing here creates, edits or serializes a scene
/// object, so a marker can never end up in a saved scene. The proportions, colours and distance behaviour come
/// from <see cref="PresenceStyle"/>, which has unit tests; this class only turns those numbers into draw calls.
/// </summary>
public static class PresenceMarker
{
    public sealed class Subject
    {
        public Vector3 Position;

        public Rotation ViewRotation;

        public float? FieldOfView;

        public string Name;

        public string State;

        public string StatusLine;

        public Texture Avatar;

        public bool SameScene;

        public bool Emphasized;

        public bool ShowFrustum;

        public Frustum? Frustum;
    }

    private static readonly DateTime Epoch = DateTime.UtcNow;

    private static double Seconds => (DateTime.UtcNow - Epoch).TotalSeconds;

    private static Color ToColor(Rgb rgb, float alpha)
    {
        return new Color(rgb.R, rgb.G, rgb.B, alpha);
    }

    public static void Draw(Subject subject)
    {
        Vector3 cameraPosition = Gizmo.Camera?.Position ?? Vector3.Zero;
        Rotation cameraRotation = Gizmo.Camera?.Rotation ?? Rotation.Identity;
        float distance = (subject.Position - cameraPosition).Length;
        float fade = PresenceStyle.Fade(distance);
        bool playing = string.Equals(subject.State, "playing", StringComparison.Ordinal);
        double now = Seconds;
        float radius = PresenceStyle.Radius(distance) * (subject.Emphasized ? 1.08f : 1f) * PresenceStyle.Pulse(subject.State, now);
        Vector3 center = subject.Position + Vector3.Up * PresenceStyle.Bob(now, radius);

        // Camera-space axes: the light comes from the upper left of the screen and the glint faces the viewer,
        // whichever way the scene view is turned.
        Vector3 right = cameraRotation.Right;
        Vector3 up = cameraRotation.Up;
        Vector3 toward = -cameraRotation.Forward;

        // Drawn over the scene on purpose. A collaborator behind a wall is still worth knowing about, and with
        // depth off the later layers are guaranteed to land on top of the earlier ones.
        Gizmo.Draw.IgnoreDepth = true;
        Gizmo.Draw.LineThickness = subject.Emphasized ? 3f : 1f;

        if (subject.ShowFrustum && subject.Frustum.HasValue)
        {
            Gizmo.Draw.Color = ToColor(PresenceStyle.Base, 0.55f * fade);
            Gizmo.Draw.LineFrustum(subject.Frustum.Value);
        }
        else
        {
            Gizmo.Draw.Color = ToColor(PresenceStyle.Base, 0.7f * fade);
            Gizmo.Draw.Line(subject.Position, subject.Position + subject.ViewRotation.Forward * (radius * 6f));
        }
        Gizmo.Draw.LineThickness = 1f;

        foreach (SphereLayer layer in PresenceStyle.Layers)
        {
            Vector3 layerCenter = center + right * (layer.OffsetRight * radius) + up * (layer.OffsetUp * radius) + toward * (layer.OffsetToward * radius);
            Gizmo.Draw.Color = ToColor(PresenceStyle.LayerColor(layer, playing), layer.Alpha * (layer.Alpha < 1f && layer.Tone < 0.5f ? fade : 1f));
            Gizmo.Draw.SolidSphere(layerCenter, radius * layer.RadiusScale, 14, 14);
        }

        float avatarSize = radius * PresenceStyle.AvatarSizeScale;
        Vector3 avatarCenter = center + Vector3.Up * (radius * PresenceStyle.AvatarHeightScale);
        DrawBadge(subject.Avatar, avatarCenter, avatarSize, cameraRotation, fade, playing);

        string name = PresenceStyle.Truncate(subject.Name, 24);
        Gizmo.Draw.Color = ToColor(PresenceStyle.Glint, fade);
        Gizmo.Draw.Text(name, new Transform(center + Vector3.Up * (radius * PresenceStyle.NameHeightScale)), "Inter", 20f * NameScale(distance));

        if (distance <= PresenceStyle.FarDistance && !string.IsNullOrWhiteSpace(subject.StatusLine))
        {
            Gizmo.Draw.Color = ToColor(PresenceStyle.StateColor(subject.State), fade);
            Gizmo.Draw.Text(subject.StatusLine, new Transform(center - Vector3.Up * (radius * PresenceStyle.StatusDepthScale)), "Inter", 14f * NameScale(distance));
        }
        Gizmo.Draw.IgnoreDepth = false;
    }

    // Text is sized in screen terms, so it needs no growth with distance, but it does shrink a little far away
    // to keep a crowded scene readable, the same trade the previous nameplate made.
    private static float NameScale(float distance)
    {
        return Math.Clamp(1f - (distance - 800f) / 8000f, 0.7f, 1f);
    }

    private static void DrawBadge(Texture avatar, Vector3 center, float size, Rotation cameraRotation, float fade, bool playing)
    {
        (float inner, float outer) = PresenceStyle.BadgeRing(size);
        // The ring lives in the plane of its scope, so the scope is turned to face the camera. Both the ring and
        // the picture sit in that plane, and the ring's width is what rounds the picture's square corners.
        Rotation facing = Rotation.LookAt(-cameraRotation.Forward, cameraRotation.Up);
        using (Gizmo.Scope("badge", center, facing, 1f))
        {
            Gizmo.Draw.IgnoreDepth = true;
            if (PresenceStyle.NeedsBackdrop(avatar != null))
            {
                Gizmo.Draw.Color = ToColor(PresenceStyle.Deep, 0.9f * fade);
                Gizmo.Draw.SolidCircle(Vector3.Zero, inner, 0f, 360f, 24);
            }
            if (avatar != null)
            {
                Gizmo.Draw.Color = new Color(1f, 1f, 1f, fade);
                Gizmo.Draw.Sprite(Vector3.Zero, new Vector2(size, size), avatar, true);
            }
            Gizmo.Draw.Color = ToColor(playing ? PresenceStyle.Playing : PresenceStyle.Base, fade);
            Gizmo.Draw.SolidRing(Vector3.Zero, inner * 0.98f, outer, 0f, 360f, 32);
            Gizmo.Draw.IgnoreDepth = false;
        }
    }
}
