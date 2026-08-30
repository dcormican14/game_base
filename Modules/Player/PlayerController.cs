using Godot;
using GameBase.Core;

namespace GameBase.Player;

/// <summary>
/// CharacterBody3D player with mouse-look, sprinting, jumping, crouching and
/// sliding. The camera rides a SpringArm3D so it never clips through geometry;
/// first- vs third-person comes from SettingsService.ThirdPerson (falling back
/// to ThirdPersonFallback), as does mouse sensitivity.
///
/// All tuning values and the input action names are exported, so the rig drops
/// into another project and remaps without touching code. IsCrouching and
/// IsSliding are public for PlayerAnimator to pose against.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
    [ExportGroup("Movement")]
    [Export] public float WalkSpeed { get; set; } = 3.6f;
    [Export] public float SprintSpeed { get; set; } = 6.5f;
    [Export] public float JumpVelocity { get; set; } = 5f;
    [Export] public float Gravity { get; set; } = 14f;
    [Export] public float GroundAcceleration { get; set; } = 12f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float AirControl { get; set; } = 0.35f;

    [ExportGroup("Crouch & Slide")]
    [Export] public float CrouchSpeed { get; set; } = 2f;
    [Export] public float StandCapsuleHeight { get; set; } = 1.8f;
    [Export] public float CrouchCapsuleHeight { get; set; } = 1.1f;
    [Export] public float CrouchCameraHeight { get; set; } = 1f;
    [Export] public float SlideCameraHeight { get; set; } = 0.75f;
    /// <summary>Camera height (and pose) transition speed for crouch/slide.</summary>
    [Export] public float CrouchTransitionSpeed { get; set; } = 10f;
    /// <summary>Minimum horizontal speed at which pressing crouch starts a slide.</summary>
    [Export] public float SlideMinSpeed { get; set; } = 5f;
    /// <summary>Speed granted at slide start (never slower than current speed).</summary>
    [Export] public float SlideBoostSpeed { get; set; } = 8.5f;
    /// <summary>Slide deceleration in m/s².</summary>
    [Export] public float SlideFriction { get; set; } = 4.5f;
    /// <summary>The slide ends when it decays below this speed.</summary>
    [Export] public float SlideEndSpeed { get; set; } = 3f;
    /// <summary>How quickly the slide direction steers toward movement input.</summary>
    [Export] public float SlideSteerStrength { get; set; } = 2f;

    [ExportGroup("Camera")]
    /// <summary>Camera distance used in third-person mode. The first/third-person
    /// choice itself comes from SettingsService.ThirdPerson when the autoload is
    /// present, otherwise from ThirdPersonFallback.</summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float CameraDistance { get; set; } = 4f;
    [Export] public bool ThirdPersonFallback { get; set; } = true;
    [Export] public float MinPitchDegrees { get; set; } = -80f;
    [Export] public float MaxPitchDegrees { get; set; } = 80f;
    [Export] public float FallbackMouseSensitivity { get; set; } = 0.15f;
    [Export] public bool HideBodyInFirstPerson { get; set; } = true;
    [Export] public bool CaptureMouseOnReady { get; set; } = true;

    [ExportGroup("Input Actions")]
    [Export] public StringName MoveForwardAction { get; set; } = "move_forward";
    [Export] public StringName MoveBackAction { get; set; } = "move_back";
    [Export] public StringName MoveLeftAction { get; set; } = "move_left";
    [Export] public StringName MoveRightAction { get; set; } = "move_right";
    [Export] public StringName JumpAction { get; set; } = "jump";
    [Export] public StringName SprintAction { get; set; } = "sprint";
    [Export] public StringName CrouchAction { get; set; } = "crouch";

    /// <summary>True while crouched (including during a slide).</summary>
    public bool IsCrouching { get; private set; }

    /// <summary>True while sliding.</summary>
    public bool IsSliding { get; private set; }

    private Node3D _cameraPivot;
    private SpringArm3D _springArm;
    private Node3D _characterRig;
    private CollisionShape3D _collisionShape;
    private CapsuleShape3D _capsule;
    private CapsuleShape3D _standTestShape;
    private float _pitchDegrees;
    private float _standCameraHeight;
    private Vector3 _slideDirection;
    private float _slideSpeed;

    public override void _Ready()
    {
        _cameraPivot = GetNode<Node3D>("%CameraPivot");
        _springArm = GetNode<SpringArm3D>("%SpringArm");
        _characterRig = GetNode<Node3D>("%CharacterRig");
        _collisionShape = GetNode<CollisionShape3D>("%CollisionShape3D");
        _standCameraHeight = _cameraPivot.Position.Y;

        // Give this body its own capsule instance so crouching never resizes a
        // shape shared with other instances of the scene.
        _capsule = _collisionShape.Shape?.Duplicate() as CapsuleShape3D;
        if (_capsule != null)
            _collisionShape.Shape = _capsule;
        else
            GD.PushWarning("PlayerController: collision shape is not a capsule — crouching will not resize it.");
        UpdateCapsule();

        _springArm.AddExcludedObject(GetRid());

        ApplyCameraMode();
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged += ApplyCameraMode;

        if (CaptureMouseOnReady)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _ExitTree()
    {
        if (SettingsService.Instance != null)
            SettingsService.Instance.SettingsChanged -= ApplyCameraMode;
    }

    private void ApplyCameraMode()
    {
        bool thirdPerson = SettingsService.Instance?.ThirdPerson ?? ThirdPersonFallback;
        _springArm.SpringLength = thirdPerson ? CameraDistance : 0f;
        _characterRig.Visible = thirdPerson || !HideBodyInFirstPerson;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseMotion motion || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        float sensitivity = SettingsService.Instance?.MouseSensitivity ?? FallbackMouseSensitivity;

        RotateY(Mathf.DegToRad(-motion.Relative.X * sensitivity));
        _pitchDegrees = Mathf.Clamp(
            _pitchDegrees - motion.Relative.Y * sensitivity,
            MinPitchDegrees,
            MaxPitchDegrees);
        _cameraPivot.RotationDegrees = new Vector3(_pitchDegrees, 0, 0);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector3 velocity = Velocity;

        Vector2 input = Input.GetVector(MoveLeftAction, MoveRightAction, MoveForwardAction, MoveBackAction);
        Vector3 direction = Transform.Basis * new Vector3(input.X, 0, input.Y);
        float horizontalSpeed = new Vector2(velocity.X, velocity.Z).Length();

        if (!IsOnFloor())
        {
            velocity.Y -= Gravity * dt;
        }
        else if (Input.IsActionJustPressed(JumpAction) && (!IsCrouching && !IsSliding || CanStand()))
        {
            velocity.Y = JumpVelocity;
            if (IsSliding)
                EndSlide(keepCrouched: false);
            else
                SetCrouching(false);
        }

        if (IsSliding)
        {
            UpdateSlide(ref velocity, direction, dt);
        }
        else
        {
            if (Input.IsActionJustPressed(CrouchAction) && horizontalSpeed >= SlideMinSpeed)
            {
                StartSlide(velocity);
                UpdateSlide(ref velocity, direction, dt);
            }
            else
            {
                SetCrouching(Input.IsActionPressed(CrouchAction) || (IsCrouching && !CanStand()));

                // Crouch limits speed only on the ground — mid-air it never
                // bleeds momentum.
                float targetSpeed = IsCrouching && IsOnFloor() ? CrouchSpeed
                    : Input.IsActionPressed(SprintAction) ? SprintSpeed
                    : WalkSpeed;
                Vector3 targetHorizontal = direction * targetSpeed;

                float acceleration = IsOnFloor() ? GroundAcceleration : GroundAcceleration * AirControl;
                float weight = 1f - Mathf.Exp(-acceleration * dt);
                velocity.X = Mathf.Lerp(velocity.X, targetHorizontal.X, weight);
                velocity.Z = Mathf.Lerp(velocity.Z, targetHorizontal.Z, weight);
            }
        }

        Velocity = velocity;
        MoveAndSlide();
        UpdateCameraHeight(dt);
    }

    // ----------------------------------------------------------- crouch/slide

    private void StartSlide(Vector3 velocity)
    {
        IsSliding = true;
        IsCrouching = true;
        UpdateCapsule();

        var horizontal = new Vector3(velocity.X, 0, velocity.Z);
        _slideDirection = horizontal.LengthSquared() > 0.01f ? horizontal.Normalized() : -Transform.Basis.Z;
        // The boost is a ground move; a slide started mid-air just carries
        // the momentum it already has.
        _slideSpeed = IsOnFloor() ? Mathf.Max(horizontal.Length(), SlideBoostSpeed) : horizontal.Length();
    }

    private void UpdateSlide(ref Vector3 velocity, Vector3 inputDirection, float dt)
    {
        // Friction only applies while touching the ground; an airborne slide
        // keeps its speed and continues when it lands.
        if (IsOnFloor())
            _slideSpeed = Mathf.Max(0f, _slideSpeed - SlideFriction * dt);

        if (inputDirection.LengthSquared() > 0.01f)
        {
            float steer = Mathf.Clamp(SlideSteerStrength * (IsOnFloor() ? 1f : 0.4f) * dt, 0f, 1f);
            _slideDirection = _slideDirection.Slerp(inputDirection.Normalized(), steer).Normalized();
        }

        velocity.X = _slideDirection.X * _slideSpeed;
        velocity.Z = _slideDirection.Z * _slideSpeed;

        if (!Input.IsActionPressed(CrouchAction) || _slideSpeed <= SlideEndSpeed)
            EndSlide(keepCrouched: Input.IsActionPressed(CrouchAction));
    }

    private void EndSlide(bool keepCrouched)
    {
        if (!IsSliding)
            return;
        IsSliding = false;
        IsCrouching = keepCrouched || !CanStand();
        UpdateCapsule();
    }

    private void SetCrouching(bool crouch)
    {
        if (crouch == IsCrouching)
            return;
        if (!crouch && !CanStand())
            return;
        IsCrouching = crouch;
        UpdateCapsule();
    }

    private void UpdateCapsule()
    {
        if (_capsule == null)
            return;
        float height = IsCrouching || IsSliding ? CrouchCapsuleHeight : StandCapsuleHeight;
        _capsule.Height = height;
        _collisionShape.Position = new Vector3(0, height * 0.5f, 0);
    }

    /// <summary>Whether the full standing capsule fits at the current position.</summary>
    private bool CanStand()
    {
        if (_capsule == null)
            return true;

        _standTestShape ??= new CapsuleShape3D();
        _standTestShape.Radius = Mathf.Max(0.05f, _capsule.Radius - 0.03f);
        _standTestShape.Height = StandCapsuleHeight - 0.05f;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _standTestShape,
            Transform = new Transform3D(Basis.Identity, GlobalPosition + Vector3.Up * (StandCapsuleHeight * 0.5f + 0.03f)),
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
            CollisionMask = CollisionMask,
        };

        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0;
    }

    private void UpdateCameraHeight(float dt)
    {
        float targetY = IsSliding ? SlideCameraHeight
            : IsCrouching ? CrouchCameraHeight
            : _standCameraHeight;
        float weight = 1f - Mathf.Exp(-CrouchTransitionSpeed * dt);
        Vector3 position = _cameraPivot.Position;
        position.Y = Mathf.Lerp(position.Y, targetY, weight);
        _cameraPivot.Position = position;
    }
}
