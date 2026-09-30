using System;

namespace TeamCreate;

/// <summary>
/// One translucent sphere in the stack that makes a collaborator's marker. Offsets are in units of the body
/// radius along the viewer's right, up and toward-camera axes, so the highlight always sits toward the same
/// side of the screen however the scene view is turned.
/// </summary>
public readonly record struct SphereLayer(float RadiusScale, float Alpha, float OffsetRight, float OffsetUp, float OffsetToward, float Tone);

/// <summary>An RGB triple in 0..1, kept engine-free so the palette can be pinned by tests.</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb Lerp(Rgb from, Rgb to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Rgb(from.R + (to.R - from.R) * t, from.G + (to.G - from.G) * t, from.B + (to.B - from.B) * t);
    }
}

/// <summary>
/// The look of a collaborator's floating marker, as numbers. It lives in Core so the proportions, colours and
/// distance behaviour are unit-tested; the editor only turns these numbers into Gizmo draw calls.
/// </summary>
public static class PresenceStyle
{
    public static readonly Rgb Deep = new Rgb(0.04f, 0.20f, 0.68f);

    public static readonly Rgb Base = new Rgb(0.14f, 0.48f, 1.00f);

    public static readonly Rgb Light = new Rgb(0.55f, 0.80f, 1.00f);

    public static readonly Rgb Glint = new Rgb(0.93f, 0.97f, 1.00f);

    public static readonly Rgb Playing = new Rgb(0.30f, 0.90f, 0.52f);

    public const float MinRadius = 10f;

    public const float MaxRadius = 120f;

    // The sphere keeps roughly one size on screen, so a collaborator across a large scene is still a visible
    // blue dot rather than a speck. Clamped at both ends: close up it must not swallow the view, and far away
    // it must not become larger than the objects it sits among.
    public const float RadiusPerUnitDistance = 0.045f;

    public const float BobPeriodSeconds = 3.2f;

    public const float BobAmplitude = 0.12f;

    public const float FullOpacityDistance = 1200f;

    public const float FarDistance = 6000f;

    public const float FarOpacity = 0.35f;

    public const float AvatarSizeScale = 1.15f;

    public const float AvatarHeightScale = 2.25f;

    public const float NameHeightScale = 3.55f;

    public const float StatusDepthScale = 1.85f;

    // Halo shells first, then the body, then the lit face, then the glint: back to front, because the
    // markers are drawn over the scene and the later layers must land on top of the earlier ones.
    public static readonly SphereLayer[] Layers =
    {
        new SphereLayer(1.90f, 0.05f, 0f, 0f, 0f, 0.30f),
        new SphereLayer(1.50f, 0.10f, 0f, 0f, 0f, 0.30f),
        new SphereLayer(1.22f, 0.22f, 0f, 0f, 0f, 0.40f),
        new SphereLayer(1.00f, 1.00f, 0f, 0f, 0f, 0.00f),
        new SphereLayer(0.90f, 1.00f, -0.07f, 0.09f, 0.10f, 0.32f),
        new SphereLayer(0.66f, 1.00f, -0.16f, 0.22f, 0.24f, 0.58f),
        new SphereLayer(0.26f, 0.95f, -0.34f, 0.40f, 0.62f, 1.00f)
    };

    public static float Radius(float distance)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
        {
            return MinRadius;
        }
        return Math.Clamp(distance * RadiusPerUnitDistance, MinRadius, MaxRadius);
    }

    /// <summary>A slow vertical drift that makes the marker read as floating rather than pinned to the camera position.</summary>
    public static float Bob(double seconds, float radius)
    {
        return (float)Math.Sin(seconds * (2.0 * Math.PI / BobPeriodSeconds)) * BobAmplitude * radius;
    }

    /// <summary>Editing and playing pulse the body slightly so activity is visible without reading the status line.</summary>
    public static float Pulse(string state, double seconds)
    {
        switch (state)
        {
            case "editing":
                return 1f + 0.05f * (float)(0.5 + 0.5 * Math.Sin(seconds * 2.0 * Math.PI * 1.6));
            case "playing":
                return 1f + 0.08f * (float)(0.5 + 0.5 * Math.Sin(seconds * 2.0 * Math.PI * 0.9));
            case "saving":
                return 1f + 0.04f * (float)(0.5 + 0.5 * Math.Sin(seconds * 2.0 * Math.PI * 3.0));
            default:
                return 1f;
        }
    }

    /// <summary>Opacity of the text and badge by distance: fully readable near, dimmed but never invisible far away.</summary>
    public static float Fade(float distance)
    {
        if (float.IsNaN(distance) || distance <= FullOpacityDistance)
        {
            return 1f;
        }
        if (distance >= FarDistance)
        {
            return FarOpacity;
        }
        float t = (distance - FullOpacityDistance) / (FarDistance - FullOpacityDistance);
        return 1f - t * (1f - FarOpacity);
    }

    public static Rgb LayerColor(SphereLayer layer, bool playing)
    {
        // Tone 0 is the deep body, 1 is the glint. A player's sphere warms toward green at the lit face so the
        // state reads at a glance, but the body stays blue: it is still the same collaborator.
        Rgb body = Rgb.Lerp(Deep, Base, layer.Tone * 2f);
        Rgb lit = Rgb.Lerp(Base, Light, (layer.Tone - 0.5f) * 2f);
        Rgb color = layer.Tone < 0.5f ? body : lit;
        if (layer.Tone >= 0.95f)
        {
            color = Glint;
        }
        return playing ? Rgb.Lerp(color, Playing, 0.35f * layer.Tone) : color;
    }

    public static Rgb StateColor(string state)
    {
        switch (state)
        {
            case "playing":
                return Playing;
            case "editing":
                return Light;
            case "saving":
                return Glint;
            default:
                return new Rgb(0.72f, 0.78f, 0.88f);
        }
    }

    /// <summary>
    /// The badge behind the profile picture is a ring wide enough to cover the corners of the square avatar
    /// sprite, which turns a square texture into a circular one without a mask. The picture's corner sits
    /// 0.7071 of its side from the centre, so the ring must reach at least that far.
    /// </summary>
    public static (float Inner, float Outer) BadgeRing(float avatarSize)
    {
        float inner = avatarSize * 0.5f;
        float outer = avatarSize * 0.5f * 1.45f;
        return (inner, outer);
    }

    /// <summary>
    /// The dark disc behind the picture exists only so a marker with no picture yet (still loading, or a peer
    /// with no avatar) shows a filled badge instead of an empty ring. With a picture present it must not be drawn:
    /// the engine batches sprites and solid shapes separately, and the disc landed on top of the picture and
    /// washed it blue in the live editor.
    /// </summary>
    public static bool NeedsBackdrop(bool hasAvatar)
    {
        return !hasAvatar;
    }

    public static string Truncate(string name, int max)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "(unknown name)";
        }
        string trimmed = name.Trim();
        return trimmed.Length <= max ? trimmed : trimmed.Substring(0, Math.Max(1, max - 1)) + "…";
    }
}
