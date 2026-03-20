using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace AutoPlayMod.Core;

/// <summary>
/// Displays agent status on screen: "[Agent]" when active, "Vibing" when waiting for LLM.
/// Creates a Godot Label overlay at the top of the screen.
/// </summary>
public class AgentStatusOverlay
{
    private Label? _label;
    private bool _isActive;
    private bool _isThinking;

    /// <summary>Set to true when auto-play is enabled in agent mode.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            UpdateLabel();
        }
    }

    /// <summary>Set to true when waiting for LLM response.</summary>
    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            _isThinking = value;
            UpdateLabel();
        }
    }

    /// <summary>
    /// Initialize the overlay. Call once after the game scene tree is ready.
    /// </summary>
    public void Initialize()
    {
        if (_label != null) return;

        _label = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(0, 4),
            Size = new Vector2(200, 30),
        };

        // Style
        _label.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.5f)); // green
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _label.AddThemeFontSizeOverride("font_size", 14);
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);

        // Add as CanvasLayer so it's always on top
        var canvas = new CanvasLayer { Layer = 100 };
        canvas.AddChild(_label);

        var game = NGame.Instance;
        if (game != null)
        {
            game.AddChild(canvas);
        }

        UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_label == null) return;

        if (!_isActive)
        {
            _label.Text = "";
            _label.Visible = false;
        }
        else if (_isThinking)
        {
            _label.Text = "🤖 Vibing...";
            _label.Visible = true;
            _label.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.2f)); // yellow
        }
        else
        {
            _label.Text = "🤖 Agent";
            _label.Visible = true;
            _label.AddThemeColorOverride("font_color", new Color(0.3f, 1f, 0.5f)); // green
        }
    }
}
