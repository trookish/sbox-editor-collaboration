using System;
using Editor;

namespace TeamCreate;

public abstract class DockCard : Widget
{
    protected Label Title { get; }

    public bool Complete => Title != null && FieldsAssigned;

    protected abstract bool FieldsAssigned { get; }

    protected DockCard(Widget parent, string title)
        : base(parent)
    {
        Layout = Layout.Column();
        Layout.Margin = 8f;
        Layout.Spacing = 4f;
        OnPaintOverride = () =>
        {
            // A hairline border, so each section reads as its own block on any theme, light or dark.
            Paint.SetBrushAndPen(DockTheme.CardColor, Theme.Border.WithAlpha(0.6f), 1f);
            Paint.DrawRect(LocalRect, 6f);
            return false;
        };
        Title = Layout.Add(new Label(title, this)
        {
            Color = Theme.Text.WithAlpha(0.9f)
        });
        // The section's name is a heading, not one more line of text.
        Title.SetStyles("font-weight: 600;");
    }

    protected static void SetText(Label label, string text)
    {
        DockTheme.SetText(label, text);
    }

    public Vector2 WantedSize()
    {
        return SizeHint();
    }

    public float MinimumNeeded()
    {
        return MinimumSizeHint().y;
    }

    public void Reflow()
    {
        DockTheme.InvalidateLayout(this);
        UpdateGeometry();
    }

    protected override void OnResize()
    {
        base.OnResize();
        DockTheme.InvalidateLayout(this);
    }

    protected static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            CollaborationSession.Current.ReportAction(error);
        }
    }
}
