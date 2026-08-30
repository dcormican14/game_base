using Godot;

namespace GameBase.Player;

/// <summary>
/// Animates a humanoid character rig under a CharacterBody3D, in one of two
/// modes chosen at startup:
///
/// 1. Clip mode — used when an AnimationPlayer under this node has clips named
///    for locomotion states ("idle", "walk", "run", "sprint", "jump", "fall",
///    "crouch", "slide"). Those are cross-faded by movement state, so dropping
///    in real (e.g. Mixamo) animations takes over automatically.
///
/// 2. Procedural mode — the fallback for an unanimated model such as a T-pose
///    Mixamo dummy. Legs run on a phase-locked gait oscillator: both share one
///    clock half a cycle apart (so they cannot sync into a gallop), with a duty
///    factor that shrinks with speed to give double support at a walk and a
///    flight phase at a run. Each step predicts its landing spot from velocity
///    and raycasts it onto the ground; planted feet are world-locked while
///    two-bone IK solves each leg to reach them. Sideways travel is a shuffle
///    (lead foot out, trail foot closing) rather than a stride. The torso,
///    arms, body yaw and head aim are layered on top of that, driven by the
///    feet's measured positions so the upper body always agrees with them.
///
/// Bones are found by Mixamo-style name suffixes ("Hips", "LeftUpLeg", ...)
/// regardless of prefix; rotations are computed in model space from the rest
/// pose (model faces +Z, the Mixamo convention).
/// </summary>
public partial class PlayerAnimator : Node3D
{
    [Export] public NodePath BodyPath { get; set; } = "..";

    [ExportGroup("Locomotion Reference")]
    /// <summary>Speed treated as a full walk (amplitudes saturate here).</summary>
    [Export] public float WalkSpeedReference { get; set; } = 3.6f;
    [Export] public float SprintSpeedReference { get; set; } = 6.5f;
    /// <summary>Extra step length gained at full run — sprinting takes longer
    /// steps instead of only faster ones.</summary>
    [Export] public float StrideWarpRun { get; set; } = 0.6f;

    [ExportGroup("Procedural Stepping")]
    /// <summary>Length of one step at walking speed, in meters. Together with
    /// speed this sets the gait cycle (cadence = speed / step length).</summary>
    [Export] public float StepLength { get; set; } = 0.85f;
    /// <summary>Stance fraction of the gait cycle at a walk. Above 0.5 the
    /// feet overlap on the ground (double support — the trailing foot).</summary>
    [Export(PropertyHint.Range, "0.2,0.9,0.01")] public float DutyFactorWalk { get; set; } = 0.55f;
    /// <summary>Stance fraction at a full run. Below 0.5 both feet are briefly
    /// airborne between steps — the flight phase of a real run.</summary>
    [Export(PropertyHint.Range, "0.2,0.9,0.01")] public float DutyFactorRun { get; set; } = 0.32f;
    /// <summary>Fraction of stance time the foot lands ahead of its home spot
    /// (0.5 ≈ symmetric stance: land ahead, lift behind).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float StepAnticipation { get; set; } = 0.5f;
    /// <summary>Fraction of the natural stance width each foot always keeps
    /// from the center line, so feet never crisscross when strafing.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float StanceWidthKeep { get; set; } = 0.5f;
    /// <summary>Lateral travel covered by the LEAD foot (the one on the side
    /// of travel) as a fraction of the sideways offset — it steps out first.</summary>
    [Export(PropertyHint.Range, "0,1.5,0.05")] public float ShuffleLeadScale { get; set; } = 0.95f;
    /// <summary>Lateral travel covered by the TRAIL foot, which only closes up
    /// underneath the body rather than crossing over.</summary>
    [Export(PropertyHint.Range, "0,1.5,0.05")] public float ShuffleTrailScale { get; set; } = 0.55f;
    /// <summary>Hard cap on how far a foot may land to either side of its home
    /// spot, in meters.</summary>
    [Export] public float MaxLateralStep { get; set; } = 0.55f;
    /// <summary>Peak height of the swing arc at a walk, in meters.</summary>
    [Export] public float StepHeight { get; set; } = 0.16f;
    /// <summary>Extra swing-arc height multiplier at full run (drives the knees up).</summary>
    [Export] public float StepHeightRunBoost { get; set; } = 1.1f;
    /// <summary>Maximum distance a foot may land from its home position.</summary>
    [Export] public float MaxStepReach { get; set; } = 1.5f;
    /// <summary>Standing still, a foot further than this from home takes a repositioning step.</summary>
    [Export] public float IdleStepDistance { get; set; } = 0.25f;
    [Export] public float IdleStepDuration { get; set; } = 0.35f;
    [Export] public float MinStepDuration { get; set; } = 0.15f;
    [Export] public float MaxStepDuration { get; set; } = 0.55f;
    /// <summary>Toe-down pitch of the foot mid-swing.</summary>
    [Export] public float FootSwingPitchDegrees { get; set; } = 12f;
    /// <summary>Minimum height a swinging foot keeps above the ground directly
    /// beneath it (sampled per frame), so steps clear ramps and ledges.</summary>
    [Export] public float SwingGroundClearance { get; set; } = 0.09f;
    [Export(PropertyHint.Layers3DPhysics)] public uint GroundMask { get; set; } = 1;
    /// <summary>Total length of the ground-probing ray around each foot target.</summary>
    [Export] public float GroundRayLength { get; set; } = 4f;

    [ExportGroup("Ground Pose")]
    [Export] public float WalkSwingDegrees { get; set; } = 26f;
    [Export] public float RunExtraSwingDegrees { get; set; } = 26f;
    /// <summary>How far arms hang down from the T-pose when idle/walking.</summary>
    [Export] public float ArmDownDegrees { get; set; } = 68f;
    /// <summary>Arms tuck closer to the body at full run.</summary>
    [Export] public float RunArmDownDegrees { get; set; } = 84f;
    /// <summary>Upper arms carried slightly forward at full run.</summary>
    [Export] public float RunArmForwardDegrees { get; set; } = 12f;
    [Export] public float ArmSwingScale { get; set; } = 0.8f;
    [Export] public float ElbowBendIdleDegrees { get; set; } = 15f;
    [Export] public float ElbowBendWalkDegrees { get; set; } = 35f;
    /// <summary>Elbow bend at full run (runner's ~90° arm carry).</summary>
    [Export] public float ElbowBendRunDegrees { get; set; } = 105f;
    /// <summary>Extra elbow flex as the arm swings forward.</summary>
    [Export] public float ElbowSwingDegrees { get; set; } = 20f;
    [Export] public float TorsoLeanDegrees { get; set; } = 4f;
    [Export] public float RunLeanExtraDegrees { get; set; } = 8f;
    /// <summary>Counter-rotation of the torso against the hips while moving.</summary>
    [Export] public float TorsoTwistDegrees { get; set; } = 7f;
    /// <summary>Hip bob as a fraction of the hip rest height.</summary>
    [Export] public float HipBobFraction { get; set; } = 0.035f;
    [Export] public float HipSwayDegrees { get; set; } = 3f;

    [ExportGroup("Air Pose")]
    [Export] public float AirFrontThighDegrees { get; set; } = 40f;
    [Export] public float AirFrontKneeDegrees { get; set; } = 70f;
    [Export] public float AirBackThighDegrees { get; set; } = -18f;
    [Export] public float AirBackKneeDegrees { get; set; } = 35f;
    [Export] public float AirArmDownDegrees { get; set; } = 50f;
    [Export] public float AirElbowDegrees { get; set; } = 45f;
    [Export] public float AirLeanDegrees { get; set; } = 8f;

    [ExportGroup("Crouch Pose")]
    /// <summary>Forward fold of the torso while crouched — a hunched stance
    /// keeps the character low without folding the legs to their limit.</summary>
    [Export] public float CrouchLeanDegrees { get; set; } = 34f;
    /// <summary>Extra hunch distributed up the spine (rounded upper back).</summary>
    [Export] public float CrouchSpineCurlDegrees { get; set; } = 16f;
    /// <summary>Head lift countering the hunch, so the character still looks ahead.</summary>
    [Export] public float CrouchHeadLiftDegrees { get; set; } = 26f;
    /// <summary>How far the hips drop while crouched, as a fraction of hip rest
    /// height. Kept modest — the hunch supplies most of the height loss, and
    /// dropping the pelvis too far leaves the IK no room and folds the legs.</summary>
    [Export] public float CrouchHipDropFraction { get; set; } = 0.16f;
    /// <summary>Crouched steps are shorter than upright ones by this factor.</summary>
    [Export(PropertyHint.Range, "0.2,1,0.05")] public float CrouchStepScale { get; set; } = 0.55f;

    [ExportGroup("Slide Pose")]
    [Export] public float SlideFrontThighDegrees { get; set; } = 65f;
    [Export] public float SlideFrontKneeDegrees { get; set; } = 15f;
    [Export] public float SlideBackThighDegrees { get; set; } = 20f;
    [Export] public float SlideBackKneeDegrees { get; set; } = 95f;
    /// <summary>Torso leans backward while sliding.</summary>
    [Export] public float SlideLeanBackDegrees { get; set; } = 22f;
    [Export] public float SlideArmDownDegrees { get; set; } = 55f;
    [Export] public float SlideElbowDegrees { get; set; } = 35f;
    [Export] public float SlideHipDropFraction { get; set; } = 0.5f;

    [ExportGroup("Body Orientation")]
    /// <summary>How far the body turns toward its movement direction while
    /// running, as a fraction of the full angle. Partial, so the character
    /// still faces roughly where the camera looks — a sideways run reads as
    /// an angled run rather than a sidestep.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RunTurnTowardMovement { get; set; } = 0.6f;
    /// <summary>Hard cap on that turn, in degrees.</summary>
    [Export] public float MaxBodyTurnDegrees { get; set; } = 55f;
    /// <summary>Fraction of the turn applied while walking (0 = face forward).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float WalkTurnScale { get; set; } = 0.35f;
    /// <summary>Fraction applied while crouched — a crouch-walk stays square
    /// to the camera so strafing reads as the shuffle it is.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float CrouchTurnScale { get; set; }
    [Export] public float BodyTurnSpeed { get; set; } = 8f;

    [ExportGroup("Head Aim")]
    /// <summary>Head tracks where the camera is looking, so the model shows
    /// the player's aim. Set the camera (or any node whose -Z is the look
    /// direction); defaults to the first Camera3D found on the player.</summary>
    [Export] public NodePath AimSourcePath { get; set; } = "";
    [Export(PropertyHint.Range, "0,1,0.05")] public float HeadAimWeight { get; set; } = 0.85f;
    /// <summary>Share of the aim the neck contributes, spreading the turn.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float NeckAimShare { get; set; } = 0.35f;
    [Export] public float MaxHeadYawDegrees { get; set; } = 70f;
    /// <summary>Maximum upward tilt of the head/neck (looking up).</summary>
    [Export] public float MaxHeadPitchUpDegrees { get; set; } = 50f;
    /// <summary>Maximum downward tilt (looking down). Necks bend further down
    /// than up, so this is the larger of the two limits.</summary>
    [Export] public float MaxHeadPitchDownDegrees { get; set; } = 65f;
    [Export] public float HeadAimSpeed { get; set; } = 9f;

    [ExportGroup("Responsiveness")]
    /// <summary>Degrees of torso lean per m/s² of horizontal acceleration —
    /// leans into speed-ups, brakes, and turns for a sense of weight.</summary>
    [Export] public float AccelLeanDegrees { get; set; } = 0.7f;
    [Export] public float AccelLeanMaxDegrees { get; set; } = 10f;
    /// <summary>Smoothing rate for the acceleration signal.</summary>
    [Export] public float AccelSmoothing { get; set; } = 6f;

    [ExportGroup("Blending & Idle")]
    /// <summary>Blend frequency into/out of crouch and slide poses
    /// (critically damped spring, so transitions keep their momentum).</summary>
    [Export] public float PoseBlendSpeed { get; set; } = 10f;
    [Export] public float AirBlendSpeed { get; set; } = 6f;
    [Export] public float IdleBreathDegrees { get; set; } = 1.5f;
    [Export] public float IdleBreathPeriod { get; set; } = 4f;
    /// <summary>Cross-fade time between clips in clip mode.</summary>
    [Export] public float ClipBlendTime { get; set; } = 0.25f;

    private struct BoneRef
    {
        public bool Valid;
        public int Index;
        public int ParentIndex;
        public Basis GlobalRest;
        public Basis ParentRestInverse;
        public Vector3 RestPosition;
    }

    /// <summary>Critically damped spring — a light-weight take on
    /// inertialization: transitions carry velocity instead of snapping onto a
    /// new exponential curve.</summary>
    private struct DampedValue
    {
        public float Value;
        public float Velocity;

        public void Step(float target, float frequency, float delta)
        {
            delta = Mathf.Min(delta, 1f / 30f); // keep the integrator stable on hitches
            Velocity += (target - Value) * frequency * frequency * delta - 2f * frequency * Velocity * delta;
            Value += Velocity * delta;
        }
    }

    /// <summary>Per-leg IK chain and footstep state.</summary>
    private class Leg
    {
        public BoneRef Thigh, Shin, Foot;
        public float ThighLength, ShinLength;      // model units
        public Vector3 ThighRestDir, ShinRestDir;  // model space
        public float AnkleRestHeight;              // model units
        public Vector3 HomeModel;                  // foot rest position projected to the model ground plane

        public float SideBodyRight;                // +1 right leg, -1 left leg (body space)

        public bool Swinging;
        public float SwingProgress;
        public float StepDuration = 0.3f;
        public float StanceTime;                   // predicted upcoming stance duration
        public Vector3 SwingStartWorld;
        public Vector3 PlantedWorld;               // sole contact point
        public Vector3 GroundNormal = Vector3.Up;
        public Vector3 CurrentWorld;               // resolved sole position this frame
        public bool HasTarget;                     // LastTarget* have been resolved
        public Vector3 LastTargetWorld;            // latest predicted landing spot
        public Vector3 LastTargetNormal = Vector3.Up;
    }

    private CharacterBody3D _body;
    private PlayerController _controller;
    private Skeleton3D _skeleton;
    private AnimationPlayer _animationPlayer;
    private bool _useClips;

    private BoneRef _hips, _spine, _spine1, _spine2, _neck, _head;
    private Node3D _aimSource;
    private float _headYaw, _headPitch;
    private BoneRef _armL, _armR, _forearmL, _forearmR;
    private Leg _legL, _legR;
    private bool _feetInitialized;

    private float _time;
    private float _gaitPhase; // shared oscillator, 0..1; legs sit half a cycle apart
    private float _bodyYaw;   // current model yaw offset from the body's facing, radians
    private float _restYaw;   // the rig's authored yaw (Mixamo models are turned 180°)
    private DampedValue _airSpring;
    private DampedValue _crouchSpring;
    private DampedValue _slideSpring;
    private DampedValue _reachDropSpring;
    private Vector3 _previousPlanarVelocity;
    private Vector3 _smoothedAccel;
    private string _currentClip = "";
    private string _idleClip, _walkClip, _runClip, _sprintClip, _jumpClip, _fallClip;
    private string _crouchIdleClip, _crouchWalkClip, _slideClip;

    public override void _Ready()
    {
        _body = GetNodeOrNull<CharacterBody3D>(BodyPath);
        _controller = _body as PlayerController;
        if (_body == null)
            GD.PushWarning("PlayerAnimator: no CharacterBody3D at BodyPath — animating as stationary.");

        _skeleton = FindDescendant<Skeleton3D>(this);
        _animationPlayer = FindDescendant<AnimationPlayer>(this);

        _restYaw = Rotation.Y;

        _aimSource = !AimSourcePath.IsEmpty ? GetNodeOrNull<Node3D>(AimSourcePath) : null;
        _aimSource ??= _body != null ? FindDescendant<Camera3D>(_body) : null;

        _useClips = ResolveClips();
        if (!_useClips && _skeleton != null)
            CacheBones();

        if (_skeleton == null && !_useClips)
        {
            GD.PushWarning("PlayerAnimator: no Skeleton3D or AnimationPlayer found under the rig — disabled.");
            SetPhysicsProcess(false);
        }
    }

    // Runs in physics so ground raycasts are always safe and in step with the
    // controller's velocity.
    public override void _PhysicsProcess(double delta)
    {
        Vector3 velocity = _body?.Velocity ?? Vector3.Zero;
        bool grounded = _body?.IsOnFloor() ?? true;
        float horizontalSpeed = new Vector2(velocity.X, velocity.Z).Length();

        if (_useClips)
            UpdateClips(horizontalSpeed, grounded, velocity.Y);
        else
            UpdateProcedural((float)delta, horizontalSpeed, grounded);
    }

    // --------------------------------------------------------------- clip mode

    private bool ResolveClips()
    {
        if (_animationPlayer == null)
            return false;

        string Find(params string[] keywords)
        {
            foreach (string keyword in keywords)
            {
                foreach (string name in _animationPlayer.GetAnimationList())
                {
                    string lower = name.ToLowerInvariant();
                    if (lower.Contains(keyword) && !lower.Contains("t-pose") && !lower.Contains("tpose"))
                        return name;
                }
            }

            return null;
        }

        _idleClip = Find("idle");
        _walkClip = Find("walk");
        _runClip = Find("run", "jog");
        _sprintClip = Find("sprint") ?? _runClip;
        _jumpClip = Find("jump");
        _fallClip = Find("fall") ?? _jumpClip;
        _crouchIdleClip = Find("crouch_idle", "crouchidle") ?? Find("crouch");
        _crouchWalkClip = Find("crouch_walk", "crouchwalk", "sneak") ?? _crouchIdleClip;
        _slideClip = Find("slide") ?? _crouchIdleClip;

        // Only use clip mode when actual locomotion clips exist (a bare
        // imported T-pose take does not count).
        return _walkClip != null || _runClip != null || _idleClip != null;
    }

    private void UpdateClips(float horizontalSpeed, bool grounded, float verticalVelocity)
    {
        float runThreshold = (WalkSpeedReference + SprintSpeedReference) * 0.5f;
        bool sliding = _controller?.IsSliding ?? false;
        bool crouching = _controller?.IsCrouching ?? false;

        string clip;
        if (!grounded)
            clip = verticalVelocity > 0.5f ? _jumpClip : _fallClip;
        else if (sliding)
            clip = _slideClip;
        else if (crouching)
            clip = horizontalSpeed < 0.3f ? _crouchIdleClip : _crouchWalkClip;
        else if (horizontalSpeed < 0.3f)
            clip = _idleClip;
        else if (horizontalSpeed > SprintSpeedReference - 0.5f)
            clip = _sprintClip;
        else if (horizontalSpeed > runThreshold)
            clip = _runClip ?? _walkClip;
        else
            clip = _walkClip ?? _runClip;

        clip ??= _idleClip ?? _walkClip ?? _runClip;
        if (clip == null || clip == _currentClip)
            return;

        _currentClip = clip;
        _animationPlayer.Play(clip, ClipBlendTime);
    }

    // --------------------------------------------------------- procedural mode

    private void UpdateProcedural(float delta, float horizontalSpeed, bool grounded)
    {
        if (_skeleton == null)
            return;

        _time += delta;

        float walk01 = Mathf.Clamp(horizontalSpeed / Mathf.Max(0.1f, WalkSpeedReference), 0f, 1f);
        float run01 = Mathf.Clamp((horizontalSpeed - WalkSpeedReference) / Mathf.Max(0.1f, SprintSpeedReference - WalkSpeedReference), 0f, 1f);

        bool sliding = _controller?.IsSliding ?? false;
        bool crouching = !sliding && (_controller?.IsCrouching ?? false);

        _airSpring.Step(grounded ? 0f : 1f, AirBlendSpeed, delta);
        _crouchSpring.Step(crouching ? 1f : 0f, PoseBlendSpeed, delta);
        _slideSpring.Step(sliding ? 1f : 0f, PoseBlendSpeed, delta);
        float slide = Mathf.Clamp(_slideSpring.Value, 0f, 1f);
        float crouch = Mathf.Clamp(_crouchSpring.Value, 0f, 1f);
        // A slide owns the pose even while airborne (sliding off an edge).
        float air = Mathf.Clamp(_airSpring.Value, 0f, 1f) * (1f - slide);

        Vector3 planarVelocity = _body != null ? new Vector3(_body.Velocity.X, 0, _body.Velocity.Z) : Vector3.Zero;

        // Phase-locked gait: both legs run on one oscillator, half a cycle
        // apart, with a duty factor (stance fraction) that shrinks with speed
        // — double support at a walk, a flight phase at a run. The fixed phase
        // offset makes it structurally impossible for the feet to sync up.
        UpdateBodyYaw(delta, planarVelocity, horizontalSpeed, walk01, run01, crouch, slide);

        bool canStep = grounded && !sliding;
        UpdateFeet(delta, planarVelocity, canStep, horizontalSpeed, run01, crouch);

        // Gait signals measured from what the feet are actually doing: the
        // forwardness of each leg drives torso and arm motion, so the upper
        // body stays in sync with the planner in every movement direction.
        // Measured in the rig's frame so arm swing stays in sync when the body
        // is yawed toward its movement direction.
        Vector3 forwardDir = Planar(_skeleton.GlobalBasis.Z);
        forwardDir = forwardDir.LengthSquared() > 0.0001f ? forwardDir.Normalized() : Vector3.Forward;
        float halfStride = Mathf.Max(0.25f, StepLength * (1f + StrideWarpRun * run01) * 0.6f);
        float forwardnessL = LegForwardness(_legL, forwardDir, halfStride);
        float forwardnessR = LegForwardness(_legR, forwardDir, halfStride);
        float gaitSin = (forwardnessL - forwardnessR) * 0.5f;

        // Horizontal acceleration, smoothed and expressed in model space, for
        // weight shifts: lean into speed-ups/brakes, roll into turns.
        Vector3 rawAccel = delta > 0.0001f ? (planarVelocity - _previousPlanarVelocity) / delta : Vector3.Zero;
        _previousPlanarVelocity = planarVelocity;
        _smoothedAccel = _smoothedAccel.Lerp(rawAccel, 1f - Mathf.Exp(-AccelSmoothing * delta));

        float accelLean = 0f;
        float accelRoll = 0f;
        if (_body != null)
        {
            Vector3 localAccel = _body.GlobalBasis.Inverse() * _smoothedAccel;
            float maxLean = Mathf.DegToRad(AccelLeanMaxDegrees);
            // Body forward is -Z and the rig is turned 180°, so model forward
            // accel is -localAccel.Z and model-side accel is -localAccel.X.
            accelLean = Mathf.Clamp(Mathf.DegToRad(AccelLeanDegrees) * -localAccel.Z, -maxLean, maxLean) * (1f - air) * (1f - slide);
            accelRoll = Mathf.Clamp(Mathf.DegToRad(AccelLeanDegrees) * localAccel.X, -maxLean, maxLean) * (1f - air) * (1f - slide);
        }

        // ------------------------------------------------------------- torso

        float breath = Mathf.DegToRad(IdleBreathDegrees) * Mathf.Sin(_time * Mathf.Tau / Mathf.Max(0.5f, IdleBreathPeriod)) * (1f - walk01);
        float lean = Mathf.DegToRad(TorsoLeanDegrees) * walk01 + Mathf.DegToRad(RunLeanExtraDegrees) * run01;
        lean = Mathf.Lerp(lean, Mathf.DegToRad(CrouchLeanDegrees), crouch);
        lean = Mathf.Lerp(lean, -Mathf.DegToRad(SlideLeanBackDegrees), slide);
        lean = Mathf.Lerp(lean, Mathf.DegToRad(AirLeanDegrees), air);
        lean += accelLean;
        float sway = Mathf.DegToRad(HipSwayDegrees) * gaitSin * walk01 * (1f - air) * (1f - slide) + accelRoll;
        float twist = Mathf.DegToRad(TorsoTwistDegrees) * gaitSin * Mathf.Clamp(walk01 + run01 * 0.5f, 0f, 1.5f)
            * (1f - air) * (1f - slide) * (1f - crouch * 0.5f);

        // Crouching hunches the back: the fold is spread up the spine rather
        // than tipping the whole torso as one rigid piece, and the head lifts
        // to keep looking ahead.
        float curl = Mathf.DegToRad(CrouchSpineCurlDegrees) * crouch;
        float headLift = Mathf.DegToRad(CrouchHeadLiftDegrees) * crouch;

        ApplyModelRotation(_hips, new Basis(Vector3.Right, lean * 0.4f) * new Basis(Vector3.Back, sway));
        ApplyModelRotation(_spine, new Basis(Vector3.Up, twist * 0.5f) * new Basis(Vector3.Right, lean * 0.3f + breath * 0.5f + curl * 0.3f));
        ApplyModelRotation(_spine1, new Basis(Vector3.Up, twist) * new Basis(Vector3.Right, lean * 0.2f + breath + curl * 0.4f));
        ApplyPitch(_spine2, lean * 0.1f + breath * 0.5f + curl * 0.3f);
        ApplyHeadAim(delta, -lean * 0.5f - headLift);

        if (_hips.Valid)
        {
            float hipHeight = _hips.RestPosition.Length();
            // Pelvis rises as the stance leg passes under the body mid-swing —
            // the bounce is synced to the actual steps, stronger at a run.
            float swingLift = 0f;
            if (_legL != null && _legL.Swinging)
                swingLift = Mathf.Sin(Mathf.Pi * _legL.SwingProgress);
            if (_legR != null && _legR.Swinging)
                swingLift = Mathf.Max(swingLift, Mathf.Sin(Mathf.Pi * _legR.SwingProgress));
            float bob = HipBobFraction * hipHeight * (walk01 + run01) * swingLift * (1f - air) * (1f - slide);

            // Adaptive pelvis: lower the hips just enough that both feet can
            // actually reach their spots — at stride extremes a straight leg
            // cannot touch the ground, and without this the feet hover
            // (tiptoeing). This also produces the natural gait bounce: down in
            // double support, up mid-stance.
            // The crouch already lowers the pelvis, so the reach allowance
            // shrinks with it — otherwise the two stack and fold the legs up.
            Transform3D toModel = _skeleton.GlobalTransform.AffineInverse();
            float reachLimit = hipHeight * 0.35f * (1f - 0.7f * crouch);
            float reachDrop = Mathf.Max(LegReachDrop(_legL, toModel), LegReachDrop(_legR, toModel));
            _reachDropSpring.Step(Mathf.Min(reachDrop, reachLimit), PoseBlendSpeed, delta);
            float grounding = Mathf.Clamp(_reachDropSpring.Value, 0f, reachLimit) * (1f - air) * (1f - slide);

            float drop = hipHeight * (CrouchHipDropFraction * crouch + SlideHipDropFraction * slide) * (1f - air);
            _skeleton.SetBonePosePosition(_hips.Index, _hips.RestPosition + Vector3.Up * (bob - drop - grounding));
        }

        // -------------------------------------------------------------- arms

        float armDownDeg = Mathf.Lerp(ArmDownDegrees, RunArmDownDegrees, run01);
        armDownDeg = Mathf.Lerp(armDownDeg, SlideArmDownDegrees, slide);
        float armDown = Mathf.DegToRad(Mathf.Lerp(armDownDeg, AirArmDownDegrees, air));

        float armSwingAmp = (Mathf.DegToRad(WalkSwingDegrees) * walk01 + Mathf.DegToRad(RunExtraSwingDegrees) * run01)
            * ArmSwingScale * (1f - 0.45f * crouch) * (1f - slide);
        float armForward = -Mathf.DegToRad(RunArmForwardDegrees) * run01;
        armForward = Mathf.Lerp(armForward, Mathf.DegToRad(12f), slide);
        // Each arm opposes its own side's leg (leg forward -> arm back).
        float armSwingL = armForward + armSwingAmp * forwardnessL;
        float armSwingR = armForward + armSwingAmp * forwardnessR;
        armSwingL = Mathf.Lerp(armSwingL, -Mathf.DegToRad(15f), air);
        armSwingR = Mathf.Lerp(armSwingR, -Mathf.DegToRad(15f), air);

        float elbowDeg = Mathf.Lerp(ElbowBendIdleDegrees, ElbowBendWalkDegrees, walk01)
            + (ElbowBendRunDegrees - ElbowBendWalkDegrees) * run01;
        elbowDeg = Mathf.Lerp(elbowDeg, SlideElbowDegrees, slide);
        elbowDeg = Mathf.Lerp(elbowDeg, AirElbowDegrees, air);

        // Extra flex while the arm swings forward (its leg swings back).
        float elbowDrive = Mathf.DegToRad(ElbowSwingDegrees) * (walk01 * 0.3f + run01) * (1f - slide) * (1f - air);
        float elbowL = Mathf.DegToRad(elbowDeg) + elbowDrive * Mathf.Max(0f, -forwardnessL);
        float elbowR = Mathf.DegToRad(elbowDeg) + elbowDrive * Mathf.Max(0f, -forwardnessR);

        ApplyArm(_armL, armSwingL, armDown);
        ApplyArm(_armR, armSwingR, armDown);
        ApplyElbow(_forearmL, elbowL);
        ApplyElbow(_forearmR, elbowR);

        // -------------------------------------------------------------- legs

        float legPoseWeight = Mathf.Clamp(Mathf.Max(air, slide), 0f, 1f);
        float airShare = air + slide > 0.001f ? air / (air + slide) : 0f;

        SolveLeg(_legL, legPoseWeight,
            poseThigh: -Mathf.DegToRad(Mathf.Lerp(SlideFrontThighDegrees, AirFrontThighDegrees, airShare)),
            poseKnee: Mathf.DegToRad(Mathf.Lerp(SlideFrontKneeDegrees, AirFrontKneeDegrees, airShare)));
        SolveLeg(_legR, legPoseWeight,
            poseThigh: -Mathf.DegToRad(Mathf.Lerp(SlideBackThighDegrees, AirBackThighDegrees, airShare)),
            poseKnee: Mathf.DegToRad(Mathf.Lerp(SlideBackKneeDegrees, AirBackKneeDegrees, airShare)));
    }

    /// <summary>
    /// Points the head where the player is aiming (the camera's look
    /// direction), lerped so it turns smoothly, and split between neck and
    /// head so the turn is spread rather than snapping the skull around.
    /// <paramref name="basePitch"/> is the gait/crouch pitch the head would
    /// otherwise carry; it survives as the resting pose when there is no aim
    /// source or the aim weight is dialed down.
    /// </summary>
    private void ApplyHeadAim(float delta, float basePitch)
    {
        float targetYaw = 0f;
        float targetPitch = 0f;

        if (_aimSource != null && IsInstanceValid(_aimSource) && _head.Valid)
        {
            // The aim direction expressed in the rig's own frame, so the head
            // compensates for the body's yaw toward its movement direction.
            Vector3 look = _skeleton.GlobalBasis.Inverse() * (-_aimSource.GlobalBasis.Z);
            if (look.LengthSquared() > 0.0001f)
            {
                look = look.Normalized();
                // Model faces +Z. A rotation of t about +Y carries +Z toward
                // -X, so aiming at a point with model-space X needs yaw
                // atan2(X, Z) -- negating it mirrors the turn. Pitch is
                // positive looking up and is applied about -X below.
                targetYaw = Mathf.Atan2(look.X, look.Z);
                targetPitch = Mathf.Asin(Mathf.Clamp(look.Y, -1f, 1f));

                float maxYaw = Mathf.DegToRad(MaxHeadYawDegrees);
                targetYaw = Mathf.Clamp(targetYaw, -maxYaw, maxYaw) * HeadAimWeight;
                targetPitch = Mathf.Clamp(
                    targetPitch,
                    -Mathf.DegToRad(MaxHeadPitchDownDegrees),
                    Mathf.DegToRad(MaxHeadPitchUpDegrees)) * HeadAimWeight;
            }
        }

        float weight = 1f - Mathf.Exp(-HeadAimSpeed * delta);
        _headYaw = Mathf.LerpAngle(_headYaw, targetYaw, weight);
        _headPitch = Mathf.Lerp(_headPitch, targetPitch, weight);

        // Spread the turn: the neck takes a share, the head the remainder.
        float neckShare = NeckAimShare;
        ApplyModelRotation(_neck,
            new Basis(Vector3.Up, _headYaw * neckShare) * new Basis(Vector3.Right, -_headPitch * neckShare));
        ApplyModelRotation(_head,
            new Basis(Vector3.Up, _headYaw * (1f - neckShare))
            * new Basis(Vector3.Right, basePitch - _headPitch * (1f - neckShare)));
    }

    /// <summary>
    /// Turns the model partway toward its movement direction. The body node
    /// (and camera) keep facing where the player aims; only the rig yaws, so
    /// a sideways sprint reads as an angled run while still looking toward
    /// the center of the screen. Feet are planned in the body frame, so this
    /// stays a purely visual rotation.
    /// </summary>
    private void UpdateBodyYaw(float delta, Vector3 planarVelocity, float speed,
        float walk01, float run01, float crouch, float slide)
    {
        float target = 0f;
        if (_body != null && speed > 0.5f)
        {
            Vector3 forward = Planar(-_body.GlobalBasis.Z);
            Vector3 move = Planar(planarVelocity);
            if (forward.LengthSquared() > 0.0001f && move.LengthSquared() > 0.0001f)
            {
                forward = forward.Normalized();
                move = move.Normalized();
                // Signed angle from facing to movement, about world up.
                float angle = Mathf.Atan2(forward.Cross(move).Dot(Vector3.Up), forward.Dot(move));

                // Never turn to face away: moving backward, the character keeps
                // facing forward and walks in reverse. Folding the angle into
                // the front half also flips the back-diagonals, so backing to
                // the right angles the body to the right while retreating.
                // (A slide is exempt — it genuinely travels feet-first.)
                if (slide < 0.5f && Mathf.Abs(angle) > Mathf.Pi * 0.5f)
                    angle = Mathf.Pi * Mathf.Sign(angle) - angle;

                float scale = Mathf.Lerp(WalkTurnScale, RunTurnTowardMovement, run01) * Mathf.Max(walk01, run01);
                scale = Mathf.Lerp(scale, CrouchTurnScale, crouch);
                scale = Mathf.Lerp(scale, 1f, slide); // a slide travels feet-first
                float max = Mathf.DegToRad(MaxBodyTurnDegrees);
                target = Mathf.Clamp(angle * scale, -max, max);
            }
        }

        _bodyYaw = Mathf.LerpAngle(_bodyYaw, target, 1f - Mathf.Exp(-BodyTurnSpeed * delta));
        Rotation = new Vector3(Rotation.X, _restYaw + _bodyYaw, Rotation.Z);
    }

    // ------------------------------------------------------- footstep planner

    private void UpdateFeet(float delta, Vector3 planarVelocity, bool canStep, float speed, float run01, float crouch)
    {
        if (_legL == null || _legR == null)
            return;

        if (!_feetInitialized)
        {
            PlantAtHome(_legL);
            PlantAtHome(_legR);
            _feetInitialized = true;
        }

        if (!canStep)
        {
            // No ground to plant on: keep the stored spots tracking the ground
            // under each home so the landing catch-step starts sensibly.
            TrackHomeGround(_legL);
            TrackHomeGround(_legR);
            _legL.Swinging = false;
            _legR.Swinging = false;
        }
        else if (speed > 0.5f)
        {
            // Moving: advance the shared oscillator. Each leg reads its own
            // phase (offset by half a cycle) and is in stance below the duty
            // factor, swinging above it.
            float stepLength = StepLength * (1f + StrideWarpRun * run01) * Mathf.Lerp(1f, CrouchStepScale, crouch);
            float cycleTime = stepLength * 2f / speed;
            float duty = Mathf.Lerp(DutyFactorWalk, DutyFactorRun, run01);
            _gaitPhase = Mathf.PosMod(_gaitPhase + delta / cycleTime, 1f);

            float stanceTime = duty * cycleTime;
            float swingTime = Mathf.Clamp((1f - duty) * cycleTime, MinStepDuration, MaxStepDuration);

            UpdatePhasedLeg(_legL, _gaitPhase, duty, swingTime, stanceTime);
            UpdatePhasedLeg(_legR, Mathf.PosMod(_gaitPhase + 0.5f, 1f), duty, swingTime, stanceTime);
        }
        else
        {
            // Idle: let any leftover swing finish on a timer, and take
            // distance-based repositioning steps (covers turning in place).
            FinishSwingByTime(_legL, delta);
            FinishSwingByTime(_legR, delta);

            if (!_legL.Swinging && !_legR.Swinging)
            {
                float errorL = Planar(_legL.PlantedWorld - HomeWorld(_legL)).Length();
                float errorR = Planar(_legR.PlantedWorld - HomeWorld(_legR)).Length();
                if (errorL > IdleStepDistance && errorL >= errorR)
                    StartStep(_legL, IdleStepDuration);
                else if (errorR > IdleStepDistance)
                    StartStep(_legR, IdleStepDuration);
            }
        }

        ResolveFootPosition(_legL, planarVelocity, run01);
        ResolveFootPosition(_legR, planarVelocity, run01);
    }

    /// <summary>Stance/swing state from the leg's slice of the gait phase.</summary>
    private void UpdatePhasedLeg(Leg leg, float legPhase, float duty, float swingTime, float stanceTime)
    {
        bool shouldSwing = legPhase >= duty;
        if (shouldSwing)
        {
            if (!leg.Swinging)
            {
                leg.Swinging = true;
                leg.SwingStartWorld = leg.CurrentWorld;
            }

            leg.StepDuration = swingTime;
            leg.StanceTime = stanceTime;
            // Progress comes straight from the phase, so the two legs can
            // never drift relative to each other.
            leg.SwingProgress = Mathf.Clamp((legPhase - duty) / Mathf.Max(0.001f, 1f - duty), 0f, 1f);
        }
        else if (leg.Swinging)
        {
            PlantFoot(leg);
        }
    }

    private void FinishSwingByTime(Leg leg, float delta)
    {
        if (!leg.Swinging)
            return;
        leg.SwingProgress += delta / Mathf.Max(0.05f, leg.StepDuration);
        if (leg.SwingProgress >= 1f)
            PlantFoot(leg);
    }

    private void StartStep(Leg leg, float duration)
    {
        leg.Swinging = true;
        leg.SwingProgress = 0f;
        leg.StepDuration = Mathf.Max(0.05f, duration);
        leg.StanceTime = 0f;
        leg.SwingStartWorld = leg.CurrentWorld;
    }

    private void PlantFoot(Leg leg)
    {
        leg.Swinging = false;
        leg.PlantedWorld = leg.HasTarget ? leg.LastTargetWorld : ProbeGround(leg.CurrentWorld).position;
        leg.GroundNormal = leg.LastTargetNormal;
        leg.CurrentWorld = leg.PlantedWorld;
    }

    private void ResolveFootPosition(Leg leg, Vector3 planarVelocity, float run01)
    {
        if (!leg.Swinging)
        {
            // Planted: world-locked.
            leg.CurrentWorld = leg.PlantedWorld;
            return;
        }

        (Vector3 target, Vector3 normal) = PredictStepTarget(leg, planarVelocity);
        leg.LastTargetWorld = target;
        leg.LastTargetNormal = normal;
        leg.HasTarget = true;

        float t = leg.SwingProgress;
        float ease = t * t * t * (t * (6f * t - 15f) + 10f); // smootherstep: soft lift-off and touch-down
        Vector3 position = leg.SwingStartWorld.Lerp(target, ease);
        position += Vector3.Up * (StepHeight * (1f + StepHeightRunBoost * run01) * Mathf.Sin(Mathf.Pi * t));

        // Terrain-aware clearance: the straight-line arc can cut into an
        // uphill slope, so sample the ground under the foot and keep the
        // sole above it (tapering to zero so the foot can still land).
        (Vector3 groundBelow, _) = ProbeGround(position);
        float clearance = SwingGroundClearance * Mathf.Sin(Mathf.Pi * Mathf.Min(t * 1.25f, 1f));
        position.Y = Mathf.Max(position.Y, groundBelow.Y + clearance);

        leg.CurrentWorld = position;
        leg.GroundNormal = leg.GroundNormal.Slerp(normal, 0.3f).Normalized();
    }

    /// <summary>
    /// Predicted landing spot: home position led by velocity (direction
    /// agnostic — forward, strafe, or backpedal), clamped to reach, then
    /// snapped to the ground with a raycast.
    /// </summary>
    private (Vector3 position, Vector3 normal) PredictStepTarget(Leg leg, Vector3 planarVelocity)
    {
        // Land where home WILL be at touch-down (remaining swing time), plus a
        // fraction of the upcoming stance so the foot lands ahead and lifts
        // behind — symmetric stance, no lurching.
        float remaining = Mathf.Max(0f, 1f - leg.SwingProgress) * leg.StepDuration;
        Vector3 home = HomeWorld(leg);
        Vector3 offset = Planar(planarVelocity) * (remaining + StepAnticipation * leg.StanceTime);

        // Sideways travel is a shuffle, not a stride: the foot on the side of
        // travel leads out, and the other closes up underneath the body
        // instead of crossing over. Applying different scales per leg is what
        // makes it read as a human sidestep rather than a wide split.
        if (_body != null)
        {
            Vector3 right = RigRight();
            if (right.LengthSquared() > 0.0001f)
            {
                float lateral = offset.Dot(right);
                bool isLead = lateral * leg.SideBodyRight > 0f;
                float wanted = Mathf.Clamp(
                    lateral * (isLead ? ShuffleLeadScale : ShuffleTrailScale),
                    -MaxLateralStep,
                    MaxLateralStep);
                offset += right * (wanted - lateral);
            }
        }

        if (offset.Length() > MaxStepReach)
            offset = offset.Normalized() * MaxStepReach;

        return ProbeGround(ClampToOwnSide(leg, home + offset));
    }

    /// <summary>
    /// Keeps a foot target on its own side of the body's center line (by at
    /// least StanceWidthKeep of the natural stance width) so the legs never
    /// crisscross, whatever the movement direction.
    /// </summary>
    private Vector3 ClampToOwnSide(Leg leg, Vector3 target)
    {
        if (_body == null || _legL == null || _legR == null)
            return target;

        Vector3 right = RigRight();
        if (right.LengthSquared() < 0.0001f)
            return target;

        Vector3 center = (HomeWorld(_legL) + HomeWorld(_legR)) * 0.5f;
        float natural = Mathf.Abs(Planar(HomeWorld(leg) - center).Dot(right));
        float keep = natural * StanceWidthKeep;
        float lateral = Planar(target - center).Dot(right);
        float clamped = leg.SideBodyRight > 0f ? Mathf.Max(lateral, keep) : Mathf.Min(lateral, -keep);
        return target + right * (clamped - lateral);
    }

    private (Vector3 position, Vector3 normal) ProbeGround(Vector3 around)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space != null)
        {
            var query = PhysicsRayQueryParameters3D.Create(
                around + Vector3.Up * (GroundRayLength * 0.5f),
                around - Vector3.Up * (GroundRayLength * 0.5f),
                GroundMask);
            if (_body != null)
                query.Exclude = new Godot.Collections.Array<Rid> { _body.GetRid() };

            var hit = space.IntersectRay(query);
            if (hit.Count > 0)
                return ((Vector3)hit["position"], ((Vector3)hit["normal"]).Normalized());
        }

        return (around, Vector3.Up);
    }

    private void PlantAtHome(Leg leg)
    {
        (leg.PlantedWorld, leg.GroundNormal) = ProbeGround(HomeWorld(leg));
        leg.CurrentWorld = leg.PlantedWorld;
        leg.Swinging = false;
    }

    private void TrackHomeGround(Leg leg)
    {
        (leg.PlantedWorld, leg.GroundNormal) = ProbeGround(HomeWorld(leg));
        leg.CurrentWorld = leg.PlantedWorld;
    }

    private Vector3 HomeWorld(Leg leg)
    {
        return _skeleton.GlobalTransform * leg.HomeModel;
    }

    /// <summary>The visible rig's right axis (world, planar and normalized).
    /// Stance geometry follows the yawed model, so lateral step reasoning must
    /// use the rig's frame rather than the body's.</summary>
    private Vector3 RigRight()
    {
        // Model X is body -X (the rig is turned 180°), matching Leg.SideBodyRight.
        Vector3 right = Planar(-_skeleton.GlobalBasis.X);
        return right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Zero;
    }

    private float LegForwardness(Leg leg, Vector3 forwardDir, float halfStride)
    {
        if (leg == null)
            return 0f;
        return Mathf.Clamp(Planar(leg.CurrentWorld - HomeWorld(leg)).Dot(forwardDir) / halfStride, -1f, 1f);
    }

    private static Vector3 Planar(Vector3 v)
    {
        v.Y = 0f;
        return v;
    }

    // ------------------------------------------------------------- leg solver

    /// <summary>
    /// Drives one leg: analytic two-bone IK toward the planner's ankle target,
    /// blended (by quaternion slerp) with the authored air/slide pose angles.
    /// </summary>
    private void SolveLeg(Leg leg, float poseWeight, float poseThigh, float poseKnee)
    {
        if (leg == null || !leg.Thigh.Valid || !leg.Shin.Valid)
            return;

        Transform3D toModel = _skeleton.GlobalTransform.AffineInverse();

        // Hip joint position honors the pelvis pose set this frame; the
        // thigh's own rotation does not affect its origin.
        Vector3 hip = _skeleton.GetBoneGlobalPose(leg.Thigh.Index).Origin;

        // Ankle target: sole contact plus ankle height, into model space.
        Vector3 ankleWorld = leg.CurrentWorld + Vector3.Up * WorldAnkleHeight(leg);
        Vector3 target = toModel * ankleWorld;

        Vector3 diff = target - hip;
        float maxLen = (leg.ThighLength + leg.ShinLength) * 0.999f;
        float minLen = Mathf.Abs(leg.ThighLength - leg.ShinLength) + 0.001f;
        float distance = Mathf.Clamp(diff.Length(), minLen, maxLen);
        Vector3 direction = diff.LengthSquared() > 0.000001f ? diff.Normalized() : Vector3.Down;

        // Law of cosines; the knee bends toward the model's forward (+Z) pole.
        float cosThigh = (leg.ThighLength * leg.ThighLength + distance * distance - leg.ShinLength * leg.ShinLength)
            / (2f * leg.ThighLength * distance);
        float thighAngle = Mathf.Acos(Mathf.Clamp(cosThigh, -1f, 1f));

        Vector3 bendAxis = direction.Cross(Vector3.Back);
        if (bendAxis.LengthSquared() < 0.000001f)
            bendAxis = Vector3.Right;
        bendAxis = bendAxis.Normalized();

        Vector3 thighDir = direction.Rotated(bendAxis, thighAngle);
        Vector3 knee = hip + thighDir * leg.ThighLength;
        Vector3 shinDir = (target - knee).LengthSquared() > 0.000001f ? (target - knee).Normalized() : thighDir;

        Quaternion ikThigh = ShortArc(leg.ThighRestDir, thighDir) * leg.Thigh.GlobalRest.GetRotationQuaternion();
        Quaternion ikShin = ShortArc(leg.ShinRestDir, shinDir) * leg.Shin.GlobalRest.GetRotationQuaternion();

        // Authored pose fallback (air/slide): simple sagittal angles.
        if (poseWeight > 0.001f)
        {
            Quaternion poseThighQ = (new Basis(Vector3.Right, poseThigh) * leg.Thigh.GlobalRest).GetRotationQuaternion();
            Quaternion poseShinQ = (new Basis(Vector3.Right, poseThigh + poseKnee) * leg.Shin.GlobalRest).GetRotationQuaternion();
            ikThigh = ikThigh.Slerp(poseThighQ, poseWeight);
            ikShin = ikShin.Slerp(poseShinQ, poseWeight);
        }

        ApplyGlobalRotation(leg.Thigh, ikThigh);
        ApplyGlobalRotation(leg.Shin, ikShin);

        // Foot: level in rest, aligned to the sampled ground normal, with a
        // little toe-down mid-swing.
        if (leg.Foot.Valid)
        {
            Vector3 normalModel = (toModel.Basis * leg.GroundNormal).Normalized();
            float swingPitch = leg.Swinging
                ? Mathf.DegToRad(FootSwingPitchDegrees) * Mathf.Sin(Mathf.Pi * leg.SwingProgress)
                : 0f;
            Quaternion footQ = ShortArc(Vector3.Up, normalModel)
                * new Basis(Vector3.Right, swingPitch).GetRotationQuaternion()
                * leg.Foot.GlobalRest.GetRotationQuaternion();
            if (poseWeight > 0.001f)
            {
                Quaternion posePitch = (new Basis(Vector3.Right, -(poseThigh + poseKnee) * 0.4f) * leg.Foot.GlobalRest).GetRotationQuaternion();
                footQ = footQ.Slerp(posePitch, poseWeight);
            }

            ApplyGlobalRotation(leg.Foot, footQ);
        }
    }

    /// <summary>How much the pelvis must lower (model units) for this leg's
    /// ankle target to be within reach of a nearly straight leg.</summary>
    private float LegReachDrop(Leg leg, Transform3D toModel)
    {
        if (leg == null || !leg.Thigh.Valid)
            return 0f;

        Vector3 ankleModel = toModel * (leg.CurrentWorld + Vector3.Up * WorldAnkleHeight(leg));
        Vector3 thighOrigin = _skeleton.GetBoneGlobalRest(leg.Thigh.Index).Origin;
        var planar = new Vector2(ankleModel.X - thighOrigin.X, ankleModel.Z - thighOrigin.Z);
        float reach = (leg.ThighLength + leg.ShinLength) * 0.97f;
        float vertical = Mathf.Sqrt(Mathf.Max(0.0001f, reach * reach - planar.LengthSquared()));
        return Mathf.Max(0f, thighOrigin.Y - ankleModel.Y - vertical);
    }

    private float WorldAnkleHeight(Leg leg)
    {
        return (_skeleton.GlobalBasis * (Vector3.Up * leg.AnkleRestHeight)).Length();
    }

    private static Quaternion ShortArc(Vector3 from, Vector3 to)
    {
        from = from.Normalized();
        to = to.Normalized();
        float dot = from.Dot(to);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            Vector3 axis = from.Cross(Vector3.Right);
            if (axis.LengthSquared() < 0.000001f)
                axis = from.Cross(Vector3.Up);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }

        return new Quaternion(from, to);
    }

    /// <summary>Sets a bone's local pose so its model-space orientation equals
    /// <paramref name="desiredGlobal"/>, using the parent's LIVE pose (exact
    /// even when ancestors are animated — required for IK accuracy).</summary>
    private void ApplyGlobalRotation(BoneRef bone, Quaternion desiredGlobal)
    {
        Basis parent = bone.ParentIndex >= 0
            ? _skeleton.GetBoneGlobalPose(bone.ParentIndex).Basis.Orthonormalized()
            : Basis.Identity;
        _skeleton.SetBonePoseRotation(bone.Index, (parent.Inverse() * new Basis(desiredGlobal)).GetRotationQuaternion());
    }

    // ------------------------------------------------- torso & arm primitives

    /// <summary>Rotation about the model-space X axis (sagittal swing/bend).</summary>
    private void ApplyPitch(BoneRef bone, float angle)
    {
        ApplyModelRotation(bone, new Basis(Vector3.Right, angle));
    }

    /// <summary>Arm pose: lowered toward the body about model Z (side-aware), then swung about model X.</summary>
    private void ApplyArm(BoneRef bone, float swing, float down)
    {
        if (!bone.Valid)
            return;
        float side = Mathf.Sign(_skeleton.GetBoneGlobalRest(bone.Index).Origin.X);
        if (side == 0f)
            side = 1f;
        ApplyModelRotation(bone, new Basis(Vector3.Right, swing) * new Basis(Vector3.Back, -side * down));
    }

    /// <summary>
    /// Elbow flexion. In the rest T-pose the elbow's hinge axis is vertical
    /// (bending sweeps the hand forward); because model deltas compose down
    /// the chain, the axis travels with the lowered/swinging upper arm, so a
    /// positive <paramref name="flex"/> always bends the elbow naturally.
    /// </summary>
    private void ApplyElbow(BoneRef bone, float flex)
    {
        if (!bone.Valid)
            return;
        float side = Mathf.Sign(_skeleton.GetBoneGlobalRest(bone.Index).Origin.X);
        if (side == 0f)
            side = 1f;
        ApplyModelRotation(bone, new Basis(Vector3.Up, -side * flex));
    }

    /// <summary>
    /// Sets a bone's pose so its model-space orientation equals
    /// <paramref name="modelDelta"/> applied on top of the rest pose,
    /// independent of the skeleton's bone-local axis conventions.
    /// </summary>
    private void ApplyModelRotation(BoneRef bone, Basis modelDelta)
    {
        if (!bone.Valid)
            return;
        Basis local = bone.ParentRestInverse * modelDelta * bone.GlobalRest;
        _skeleton.SetBonePoseRotation(bone.Index, local.GetRotationQuaternion());
    }

    // ------------------------------------------------------------------- setup

    private void CacheBones()
    {
        _hips = FindBone("Hips");
        _spine = FindBone("Spine");
        _spine1 = FindBone("Spine1");
        _spine2 = FindBone("Spine2");
        _neck = FindBone("Neck");
        _head = FindBone("Head");
        _armL = FindBone("LeftArm");
        _armR = FindBone("RightArm");
        _forearmL = FindBone("LeftForeArm");
        _forearmR = FindBone("RightForeArm");

        _legL = BuildLeg("LeftUpLeg", "LeftLeg", "LeftFoot");
        _legR = BuildLeg("RightUpLeg", "RightLeg", "RightFoot");

        if (!_hips.Valid || _legL == null || _legR == null)
            GD.PushWarning("PlayerAnimator: core bones not found — is this a Mixamo-style humanoid skeleton?");
    }

    private Leg BuildLeg(string thighSuffix, string shinSuffix, string footSuffix)
    {
        BoneRef thigh = FindBone(thighSuffix);
        BoneRef shin = FindBone(shinSuffix);
        BoneRef foot = FindBone(footSuffix);
        if (!thigh.Valid || !shin.Valid || !foot.Valid)
            return null;

        Vector3 hipRest = _skeleton.GetBoneGlobalRest(thigh.Index).Origin;
        Vector3 kneeRest = _skeleton.GetBoneGlobalRest(shin.Index).Origin;
        Vector3 ankleRest = _skeleton.GetBoneGlobalRest(foot.Index).Origin;

        return new Leg
        {
            Thigh = thigh,
            Shin = shin,
            Foot = foot,
            ThighLength = (kneeRest - hipRest).Length(),
            ShinLength = (ankleRest - kneeRest).Length(),
            ThighRestDir = (kneeRest - hipRest).Normalized(),
            ShinRestDir = (ankleRest - kneeRest).Normalized(),
            AnkleRestHeight = ankleRest.Y,
            HomeModel = new Vector3(ankleRest.X, 0f, ankleRest.Z),
            // Model X is body -X (the rig is turned 180°).
            SideBodyRight = ankleRest.X != 0f ? -Mathf.Sign(ankleRest.X) : 1f,
        };
    }

    private BoneRef FindBone(string suffix)
    {
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            if (!_skeleton.GetBoneName(i).EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                continue;

            int parent = _skeleton.GetBoneParent(i);
            return new BoneRef
            {
                Valid = true,
                Index = i,
                ParentIndex = parent,
                GlobalRest = _skeleton.GetBoneGlobalRest(i).Basis.Orthonormalized(),
                ParentRestInverse = (parent >= 0 ? _skeleton.GetBoneGlobalRest(parent).Basis.Orthonormalized() : Basis.Identity).Inverse(),
                RestPosition = _skeleton.GetBoneRest(i).Origin,
            };
        }

        return default;
    }

    private static T FindDescendant<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T typed)
                return typed;
            T found = FindDescendant<T>(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
