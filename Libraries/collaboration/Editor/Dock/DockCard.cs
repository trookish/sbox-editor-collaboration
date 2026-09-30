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
            Paint.SetBrushAndPen(DockTheme.CardColor, Color.Transparent, 0f);
            Paint.DrawRect(LocalRect, 6f);
            return false;
        };
        Title = Layout.Add(new Label(title, this)
        {
            Color = DockTheme.MutedColor
        });
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
