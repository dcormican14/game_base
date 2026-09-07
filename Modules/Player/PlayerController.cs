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
    [Export(PropertyHint.Range, "0,10,0.1")] public float CameraDistance { get; set; } = 1.5f;
    [Export] public bool ThirdPersonFallback { get; set; } = true;
    /// <summary>Over-the-shoulder offset in third person. Moving the camera
    /// RIGHT (+X) puts the character on the LEFT of frame; a small value keeps
    /// the framing tight, with the crosshair just past the shoulder rather
    /// than far out in open space. Negative mirrors to the other shoulder.</summary>
    [Export(PropertyHint.Range, "-2,2,0.05")] public float ShoulderOffset { get; set; } = 0.2f;
    /// <summary>Camera height offset applied with the shoulder offset.</summary>
    [Export(PropertyHint.Range, "-1,1,0.05")] public float ShoulderHeight { get; set; } = 0.15f;
    /// <summary>Look limits, in degrees: -89 is straight down and +89 is
    /// straight up, a full vertical sweep. Stopping just short of the poles
    /// avoids the gimbal flip that happens exactly at +/-90.</summary>
    [Export(PropertyHint.Range, "-89,0,0.5")] public float MinPitchDegrees { get; set; } = -89f;
    [Export(PropertyHint.Range, "0,89,0.5")] public float MaxPitchDegrees { get; set; } = 89f;
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

    [ExportGroup("Sandbox")]
    /// <summary>Double-tap jump to toggle free-fly inspection mode: no
    /// gravity, no collision, WASD plus jump/crouch for up and down.</summary>
    [Export] public bool AllowSandboxToggle { get; set; } = true;

    /// <summary>How quickly the second tap must follow the first, in seconds.
    /// Long enough to be comfortable, short enough that two deliberate jumps
    /// in a row do not trip it.</summary>
    [Export(PropertyHint.Range, "0.1,1,0.05")] public float SandboxDoubleTapWindow { get; set; } = 0.3f;

    [Export] public float SandboxSpeed { get; set; } = 8f;
    [Export] public float SandboxSprintSpeed { get; set; } = 20f;
    /// <summary>How fast free-fly reaches its target velocity. Higher is
    /// snappier; the damping keeps it from feeling like ice.</summary>
    [Export] public float SandboxAcceleration { get; set; } = 14f;

    /// <summary>True while crouched (including during a slide).</summary>
    public bool IsCrouching { get; private set; }

    /// <summary>True while sliding.</summary>
    public bool IsSliding { get; private set; }

    /// <summary>True in free-fly inspection mode: no gravity, no collision.
    /// Read by the perf overlay so the mode is always visible on screen.</summary>
    public bool IsSandbox { get; private set; }

    /// <summary>Raised when sandbox mode turns on or off.</summary>
    public event System.Action<bool> SandboxChanged;

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
    private float _lastJumpPressTime = float.NegativeInfinity;

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
        // Sandbox forces first person for its duration: a third-person spring
        // arm would shove the camera back out of any block you fly into, and
        // the character model would fill the view from the inside.
        bool thirdPerson = !IsSandbox && (SettingsService.Instance?.ThirdPerson ?? ThirdPersonFallback);
        _springArm.SpringLength = thirdPerson ? CameraDistance : 0f;
        _springArm.Position = thirdPerson
            ? new Vector3(ShoulderOffset, ShoulderHeight, 0f)
            : Vector3.Zero;
        _characterRig.Visible = thirdPerson || (!IsSandbox && !HideBodyInFirstPerson);
    }

    /// <summary>
    /// Which way is down, at the player's feet.
    ///
    /// Defaults to world -Y, so a level with no planet in it behaves exactly as
    /// it did. A planet level installs a <see cref="RadialGravity"/> and every
    /// axis below follows from it.
    /// </summary>
    public IGravityField GravityField { get; set; } = new FlatGravity();

    /// <summary>The current down direction, sampled once per physics step.</summary>
    private Vector3 _down = Vector3.Down;

    /// <summary>
    /// Turns the body so its own up axis matches the local up.
    ///
    /// This is what makes a planet walkable rather than a boulder to slide off.
    /// The controller works in ITS OWN basis -- input is pushed along the body's
    /// forward and right, gravity along its down -- so re-aligning the body is
    /// the only place the sphere has to be accounted for. Everything downstream
    /// is the flat-world arithmetic it always was.
    ///
    /// The yaw is preserved by rotating the existing basis onto the new up by
    /// the shortest arc, rather than rebuilding it from scratch: building a
    /// fresh basis each frame would snap the player's facing to an arbitrary
    /// reference direction every time they crossed a pole.
    /// </summary>
    private void AlignToGravity()
    {
        Vector3 up = -_down;
        Vector3 currentUp = GlobalTransform.Basis.Y;

        Vector3 axis = currentUp.Cross(up);
        float sin = axis.Length();
        float cos = currentUp.Dot(up);

        Basis basis;
        if (sin < 0.000001f)
        {
            // Already aligned, or exactly inverted. Inverted cannot be reached
            // by walking -- it would need the player to pass through the centre
            // -- so treating it as aligned is safe and avoids a divide by zero.
            if (cos > 0f)
                return;

            basis = GlobalTransform.Basis;
        }
        else
        {
            basis = new Basis(axis / sin, Mathf.Atan2(sin, cos)) * GlobalTransform.Basis;
        }

        GlobalTransform = new Transform3D(basis.Orthonormalized(), GlobalPosition);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseMotion motion || Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        float sensitivity = SettingsService.Instance?.MouseSensitivity ?? FallbackMouseSensitivity;

        // Yaw about the body's OWN up, not the world's. On the far side of a
        // planet world +Y is straight down, and yawing about it would roll the
        // camera instead of turning it.
        RotateObjectLocal(Vector3.Up, Mathf.DegToRad(-motion.Relative.X * sensitivity));
        _pitchDegrees = Mathf.Clamp(
            _pitchDegrees - motion.Relative.Y * sensitivity,
            MinPitchDegrees,
            MaxPitchDegrees);
        _cameraPivot.RotationDegrees = new Vector3(_pitchDegrees, 0, 0);
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // WHICH WAY IS DOWN, asked once and used for everything below.
        //
        // On a planet this changes as the player walks, so it cannot be the
        // constant -Y the rest of this method was written against. Aligning the
        // body to it first is what lets the movement code keep working in the
        // body's own axes without ever knowing it is on a sphere.
        _down = GravityField.DownAt(GlobalPosition);
        UpDirection = -_down;
        AlignToGravity();

        Vector3 up = -_down;
        Vector3 velocity = Velocity;

        Vector2 input = Input.GetVector(MoveLeftAction, MoveRightAction, MoveForwardAction, MoveBackAction);
        Vector3 direction = Transform.Basis * new Vector3(input.X, 0, input.Y);

        // Velocity split along the local frame rather than the world axes: the
        // part along gravity, and the part across it.
        float verticalSpeed = velocity.Dot(up);
        Vector3 horizontal = velocity - up * verticalSpeed;
        float horizontalSpeed = horizontal.Length();

        // The double-tap is read BEFORE the jump below consumes the press, but
        // the first tap still jumps normally — only the second one within the
        // window toggles, so ordinary jumping is untouched.
        if (Input.IsActionJustPressed(JumpAction))
            NoteJumpPress();

        if (IsSandbox)
        {
            FreeFly(direction, dt);
            return;
        }

        if (!IsOnFloor())
        {
            verticalSpeed -= Gravity * dt;
        }
        else if (Input.IsActionJustPressed(JumpAction) && (!IsCrouching && !IsSliding || CanStand()))
        {
            verticalSpeed = JumpVelocity;
            if (IsSliding)
                EndSlide(keepCrouched: false);
            else
                SetCrouching(false);
        }

        // Reassembled so the slide code below, which works on a whole velocity,
        // sees the vertical change made above.
        velocity = horizontal + up * verticalSpeed;

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

                // Steered ACROSS gravity rather than across world XZ. The
                // direction already comes from the body basis, which is aligned
                // to local up, but projecting again keeps a walk from gaining a
                // vertical component when the camera is pitched.
                Vector3 wish = direction - up * direction.Dot(up);
                Vector3 targetHorizontal = wish.LengthSquared() > 0.000001f
                    ? wish.Normalized() * targetSpeed
                    : Vector3.Zero;

                float acceleration = IsOnFloor() ? GroundAcceleration : GroundAcceleration * AirControl;
                float weight = 1f - Mathf.Exp(-acceleration * dt);

                float along = velocity.Dot(up);
                Vector3 across = velocity - up * along;
                velocity = across.Lerp(targetHorizontal, weight) + up * along;
            }
        }

        Velocity = velocity;
        MoveAndSlide();
        UpdateCameraHeight(dt);
    }

    // -------------------------------------------------------------- sandbox

    /// <summary>
    /// Records a jump press and toggles sandbox mode when two land inside
    /// <see cref="SandboxDoubleTapWindow"/>. The window is cleared on the
    /// toggle so a third tap starts a fresh pair rather than immediately
    /// flipping back.
    /// </summary>
    private void NoteJumpPress()
    {
        if (!AllowSandboxToggle)
            return;

        float now = (float)Time.GetTicksMsec() / 1000f;
        if (now - _lastJumpPressTime <= SandboxDoubleTapWindow)
        {
            _lastJumpPressTime = float.NegativeInfinity;
            SetSandbox(!IsSandbox);
            return;
        }

        _lastJumpPressTime = now;
    }

    /// <summary>Enters or leaves free-fly inspection mode.</summary>
    public void SetSandbox(bool sandbox)
    {
        if (sandbox == IsSandbox)
            return;

        IsSandbox = sandbox;

        if (sandbox)
        {
            // Stand up first: the crouch capsule and slide state make no sense
            // while flying, and leaving them set would resize the body oddly
            // on the way back out.
            IsSliding = false;
            IsCrouching = false;
            UpdateCapsule();
            Velocity = Vector3.Zero;
        }

        // Collision is switched off wholesale rather than just skipping
        // MoveAndSlide: other bodies (and the block editor's placement test)
        // query this body, and they should all agree it is intangible while
        // inspecting.
        SetCollisionLayerValue(1, !sandbox);
        if (_collisionShape != null)
            _collisionShape.Disabled = sandbox;

        // The spring arm pulls the third-person camera out of anything solid,
        // which would shove the view around the moment you fly into a block,
        // and the character model would fill the view from the inside. Going
        // first person for the duration is what actually lets you sit inside
        // the geometry and look at it; ApplyCameraMode restores the player's
        // real preference on the way out.
        ApplyCameraMode();

        SandboxChanged?.Invoke(sandbox);
    }

    /// <summary>
    /// Free-fly movement: horizontal from the look direction, vertical from
    /// jump/crouch, with no gravity and no ground contact.
    ///
    /// Position is written directly rather than through MoveAndSlide, because
    /// MoveAndSlide always resolves collisions — it is what would stop the
    /// camera entering a block. Disabling the collision shape alone is not
    /// enough to pass through geometry.
    /// </summary>
    private void FreeFly(Vector3 direction, float dt)
    {
        // Fly along where the camera looks, so pushing forward while looking
        // up climbs — the natural way to inspect something overhead.
        Vector2 input = Input.GetVector(MoveLeftAction, MoveRightAction, MoveForwardAction, MoveBackAction);
        Basis look = GlobalTransform.Basis * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(_pitchDegrees));
        Vector3 wish = look * new Vector3(input.X, 0, input.Y);

        // Away from and toward the planet, not world up and down -- otherwise
        // ascending on the far side of a globe flies you into it.
        Vector3 flyUp = -GravityField.DownAt(GlobalPosition);
        if (Input.IsActionPressed(JumpAction))
            wish += flyUp;
        if (Input.IsActionPressed(CrouchAction))
            wish -= flyUp;

        if (wish.LengthSquared() > 1f)
            wish = wish.Normalized();

        float speed = Input.IsActionPressed(SprintAction) ? SandboxSprintSpeed : SandboxSpeed;
        float weight = 1f - Mathf.Exp(-SandboxAcceleration * dt);
        Velocity = Velocity.Lerp(wish * speed, weight);

        GlobalPosition += Velocity * dt;
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
            // Oriented and offset along LOCAL up: the capsule stands away from
            // the planet's centre, so testing along world +Y would sweep
            // sideways through the crust anywhere but the north pole.
            Transform = new Transform3D(
                GlobalTransform.Basis.Orthonormalized(),
                GlobalPosition + GlobalTransform.Basis.Y.Normalized()
                    * (StandCapsuleHeight * 0.5f + 0.03f)),
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
