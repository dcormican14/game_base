using Godot;
using GameBase.Core;
using GameBase.Items;

namespace GameBase.UI;

/// <summary>
/// Covers the screen with a progress bar until the level is playable, then
/// fades out.
///
/// Waits on whatever in the scene implements <see cref="ILoadProgress"/> -- the
/// chunk streamer, in the planet level. It owns the SCREEN only: the UI state,
/// the menus that must not open mid-load, and the cursor. Holding and placing
/// the player is the spawn's job, so the two never fight over the body.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    /// <summary>Seconds the finished screen takes to fade away.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float FadeSeconds { get; set; } = 0.4f;

    /// <summary>Text shown above the bar.</summary>
    [Export] public string Message { get; set; } = "Building world";

    /// <summary>
    /// Roughly how long a load takes, in seconds. Only paces the bar between
    /// real milestones so it moves from the first frame; release always waits
    /// on the level itself.
    /// </summary>
    [Export(PropertyHint.Range, "0.5,60,0.5")] public float ExpectedSeconds { get; set; } = 15f;

    private ILoadProgress _load;
    private PauseMenu _pauseMenu;
    private InventoryHud _inventory;

    private ProgressBar _bar;
    private Control _root;
    private float _fade = -1f;
    private float _waited;
    private bool _released;

    public override void _Ready()
    {
        _root = GetNode<Control>("%Root");
        _bar = GetNode<ProgressBar>("%Bar");
        GetNode<Label>("%Label").Text = Message;
        ApplyPalette();

        Node scene = GetTree().CurrentScene ?? GetParent();
        _load = NodeSearch.FindImplementing<ILoadProgress>(scene);

        // Not playable yet: the base state is Loading, which keeps gameplay
        // from acting on input behind the screen.
        UiStateService.Instance?.Reset(UiState.Loading);

        // Pausing mid-load would stop the very work being waited on.
        _pauseMenu = NodeSearch.FindByType<PauseMenu>(scene);
        _inventory = NodeSearch.FindByType<InventoryHud>(scene);
        SetMenusSuspended(true);

        if (_load == null || _load.IsReady)
            CallDeferred(MethodName.Release);
        else
            _load.BecameReady += Release;
    }

    public override void _ExitTree()
    {
        if (_load != null)
            _load.BecameReady -= Release;

        // Never leave the menus suspended if this screen is torn down mid-load.
        SetMenusSuspended(false);
    }

    public override void _Process(double delta)
    {
        if (!_released)
        {
            _waited += (float)delta;
            float paced = 1f - Mathf.Exp(-_waited / ExpectedSeconds);
            float real = _load?.Progress ?? 1f;

            _bar.Value = Mathf.Clamp(Mathf.Max(real, paced), 0f, 0.99f) * 100.0;

            // Re-asserted every frame: the player captures the mouse in its
            // own _Ready, and which runs first depends on scene order.
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
                Input.MouseMode = Input.MouseModeEnum.Visible;

            return;
        }

        if (_fade < 0f)
            return;

        _fade += (float)delta;
        float alpha = FadeSeconds <= 0f ? 0f : Mathf.Clamp(1f - _fade / FadeSeconds, 0f, 1f);
        _root.Modulate = new Color(1f, 1f, 1f, alpha);

        if (alpha <= 0f)
            QueueFree();
    }

    private void Release()
    {
        if (_released)
            return;

        _released = true;
        _bar.Value = 100.0;

        UiStateService.Instance?.Reset(UiState.Gameplay);
        SetMenusSuspended(false);

        if (Input.MouseMode == Input.MouseModeEnum.Visible)
            Input.MouseMode = Input.MouseModeEnum.Captured;

        _fade = 0f;
    }

    private void SetMenusSuspended(bool suspended)
    {
        if (_pauseMenu != null && IsInstanceValid(_pauseMenu))
            _pauseMenu.Suspended = suspended;

        if (_inventory != null && IsInstanceValid(_inventory))
            _inventory.Suspended = suspended;
    }

    private void ApplyPalette()
    {
        GetNode<ColorRect>("%Root/Background").Color = Palette.Void;
        GetNode<Label>("%Label").AddThemeColorOverride("font_color", Palette.Cream);

        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Palette.Plum });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Palette.Gold });
        _bar.AddThemeColorOverride("font_color", Palette.Cream);
    }
}
