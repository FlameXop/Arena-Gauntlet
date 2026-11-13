using UnityEngine;
using UnityEngine.InputSystem;

public enum RacketControlType
{
    PlayerController,
    AI
}

public enum ShotType { None, NormalShot, SmashShot }

public class RacketController : MonoBehaviour
{
    public bool isPlayer1 = true;
    public RacketControlType controlType = RacketControlType.PlayerController;

    [Header("1. Ball Hold Position In Front of Racket")]
    public Transform ballSpawnPoint;
    [SerializeField] private float safeSpawnForwardDistance = 0.35f;
    [SerializeField] private float safeSpawnUpOffset = 0.25f;

    [Header("2. Shot Speed Settings")]
    [Range(0.8f, 1.5f)] public float shotForce = 1.05f;
    [Range(1.1f, 1.5f)] public float smashMultiplier = 1.25f;

    [Header("3. References")]
    [SerializeField] private PongBall ball;
    [SerializeField] private Transform myTargetCenter;
    [SerializeField] private Transform opponentTargetCenter;
    public Transform opponentRacketTransform;

    [Header("4. Swing & Movement Settings")]
    [SerializeField] private float swingPushDistance = 0.45f;
    [SerializeField] private float swingDuration = 0.28f;
    [SerializeField] private float moveSpeed = 18f;
    [SerializeField] private float aiTrackSpeed = 15f;
    [SerializeField] private float maxTiltDegrees = 30f;

    private Gamepad assignedGamepad;
    private Vector3 homePosition;
    private Quaternion baseRotation;

    private Vector3 tableForwardAxis;
    private Vector3 tableRightAxis;

    private float dynamicWidthLimit = 0.85f;
    private float dynamicSpreadWidth = 0.45f;

    private float currentHorizontalOffset = 0f;
    private float currentTiltInput = 0f;

    private float activeSwingTimer = 0f;
    private ShotType activeSwingShot = ShotType.NormalShot;
    private bool hasStruckBallThisSwing = false;

    private float hitCooldown = 0f;
    private float stateEntryIgnoreTimer = 0f;
    private float aiServeTimer = 0f;

    public Vector3 HomePosition => homePosition;

    private void Awake()
    {
        homePosition = transform.position;
        baseRotation = transform.rotation;

        shotForce = 1.05f;
        smashMultiplier = 1.25f;
        swingPushDistance = 0.45f;
        swingDuration = 0.28f;
        moveSpeed = 18f;
        aiTrackSpeed = 15f;

        AutoFindMissingReferences();
        ComputeTableAxes();
    }

    private void Start()
    {
        AutoFindMissingReferences();
        ComputeTableAxes();
    }

    private void AutoFindMissingReferences()
    {
        if (ball == null)
        {
            ball = FindFirstObjectByType<PongBall>();
        }

        if (opponentRacketTransform == null)
        {
            RacketController[] allRackets = FindObjectsByType<RacketController>(FindObjectsSortMode.None);
            foreach (RacketController r in allRackets)
            {
                if (r != this)
                {
                    opponentRacketTransform = r.transform;
                    break;
                }
            }
        }
    }

    public void ComputeTableAxes()
    {
        Vector3 dirToOpponent = Vector3.zero;

        if (opponentRacketTransform != null)
        {
            dirToOpponent = opponentRacketTransform.position - homePosition;
        }
        else if (ball != null)
        {
            Vector3 center = (ball.GetPlayer1TableTarget() + ball.GetPlayer2TableTarget()) * 0.5f;
            dirToOpponent = center - homePosition;
        }

        dirToOpponent.y = 0f;
        if (dirToOpponent.sqrMagnitude > 0.001f)
        {
            tableForwardAxis = dirToOpponent.normalized;
            tableRightAxis = Vector3.Cross(Vector3.up, tableForwardAxis).normalized;

            // Automatically scale paddle movement width & aim spread to match the table size in your scene
            float halfLen = ball != null ? ball.GetTableHalfDistance() : (dirToOpponent.magnitude * 0.24f);
            dynamicWidthLimit = Mathf.Max(0.65f, halfLen * 0.75f);
            dynamicSpreadWidth = Mathf.Max(0.35f, halfLen * 0.42f);
            return;
        }

        tableForwardAxis = isPlayer1 ? Vector3.left : Vector3.right;
        tableRightAxis = Vector3.Cross(Vector3.up, tableForwardAxis).normalized;
    }

    // Holds the ball visibly in front of the Server's paddle above the table surface
    public Vector3 GetBallHoldPosition()
    {
        ComputeTableAxes();

        Vector3 basePos = homePosition + (tableRightAxis * currentHorizontalOffset);
        float surfaceY = ball != null ? ball.GetTableSurfaceY() : homePosition.y;
        float holdY = Mathf.Max(homePosition.y + 0.1f, surfaceY + safeSpawnUpOffset);

        Vector3 holdPos = basePos + (tableForwardAxis * safeSpawnForwardDistance);
        holdPos.y = holdY;
        return holdPos;
    }

    public void ResetRacketToHome()
    {
        AutoFindMissingReferences();
        ComputeTableAxes();

        currentHorizontalOffset = 0f;
        currentTiltInput = 0f;
        activeSwingTimer = 0f;
        activeSwingShot = ShotType.NormalShot;
        hasStruckBallThisSwing = false;
        hitCooldown = 0.08f;
        stateEntryIgnoreTimer = 0.15f;
        aiServeTimer = 0f;
        transform.position = homePosition;
        transform.rotation = baseRotation;
    }

    public void ConfigureControl(RacketControlType type, Camera cam, Gamepad pad = null)
    {
        controlType = type;
        assignedGamepad = pad;
        ResetRacketToHome();
    }

    private void Update()
    {
        if (ball == null) AutoFindMissingReferences();
        if (ball == null) return;
        if (GameManager.Instance != null && GameManager.Instance.CurrentState != GameState.Playing) return;

        ComputeTableAxes();

        hitCooldown -= Time.deltaTime;
        stateEntryIgnoreTimer -= Time.deltaTime;

        // 1. Move racket Left & Right
        UpdateHorizontalMovement();

        // 2. STATE: ServerReady -> Ball floats in front of Server's racket until [X] or [Y] is pressed
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            ball.SetupServe(isPlayer1, GetBallHoldPosition());
        }

        // 3. Human Player Input: Press [X] (Normal) or [Y] (Smash)
        if (controlType == RacketControlType.PlayerController && stateEntryIgnoreTimer <= 0f)
        {
            ShotType pressedShot = GetPlayerShotButton();
            if (pressedShot != ShotType.None)
            {
                StartForwardRacketSwing(pressedShot);
            }
        }

        // 4. Smart AI Behavior (Serves & Returns)
        if (controlType == RacketControlType.AI && stateEntryIgnoreTimer <= 0f)
        {
            HandleSmartAIBehavior();
        }

        // 5. Animate forward swing & execute hit
        float currentForwardPush = 0f;
        if (activeSwingTimer > 0f)
        {
            activeSwingTimer -= Time.deltaTime;
            float normalizedTime = 1f - Mathf.Clamp01(activeSwingTimer / swingDuration);
            currentForwardPush = Mathf.Sin(normalizedTime * Mathf.PI) * swingPushDistance;

            if (!hasStruckBallThisSwing && hitCooldown <= 0f)
            {
                TryExecuteStateMachineShot();
            }
        }

        Vector3 targetWorldPos = homePosition
            + (tableRightAxis * currentHorizontalOffset)
            + (tableForwardAxis * currentForwardPush);

        transform.position = Vector3.Lerp(transform.position, targetWorldPos, moveSpeed * Time.deltaTime);

        // 6. Tilt racket angle with Right Stick
        UpdateManualTilt();
    }

    private void StartForwardRacketSwing(ShotType shot)
    {
        activeSwingTimer = swingDuration;
        activeSwingShot = shot;
        hasStruckBallThisSwing = false;

        if (hitCooldown <= 0f)
        {
            TryExecuteStateMachineShot();
        }
    }

    private void TryExecuteStateMachineShot()
    {
        if (ball == null || hitCooldown > 0f) return;

        bool isSmash = (activeSwingShot == ShotType.SmashShot);
        float finalSpeedMult = isSmash ? (shotForce * smashMultiplier) : shotForce;

        // Calculate aim target on opponent's half of the table
        float aimSpread = (controlType == RacketControlType.AI)
            ? Random.Range(-dynamicSpreadWidth * 0.75f, dynamicSpreadWidth * 0.75f)
            : Mathf.Clamp((currentTiltInput * 0.75f + (currentHorizontalOffset / dynamicWidthLimit) * 0.25f) * dynamicSpreadWidth, -dynamicSpreadWidth, dynamicSpreadWidth);

        Vector3 myBaseCenter = isPlayer1 ? ball.GetPlayer1TableTarget() : ball.GetPlayer2TableTarget();
        Vector3 oppBaseCenter = isPlayer1 ? ball.GetPlayer2TableTarget() : ball.GetPlayer1TableTarget();

        Vector3 receiverTargetPoint = oppBaseCenter + (tableRightAxis * aimSpread);
        Vector3 spin = Vector3.up * (-currentTiltInput * 25f);

        // =========================================================================
        // STATE A: SERVER STATE (Pressing [X] or [Y] hits ball DOWN onto own side first!)
        // =========================================================================
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            hasStruckBallThisSwing = true;
            hitCooldown = 0.20f;

            Vector3 ownBounceSpot = myBaseCenter + (tableRightAxis * (currentHorizontalOffset * 0.35f));
            ball.ExecuteServerDownwardHit(ownBounceSpot, receiverTargetPoint, finalSpeedMult, isSmash, spin, isPlayer1);
            return;
        }

        // =========================================================================
        // STATE B: RECEIVER STATE (Once ball bounces on Receiver's side, hit it back over net!)
        // =========================================================================
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            hasStruckBallThisSwing = true;
            hitCooldown = 0.20f;

            string label = isSmash ? "Y-SMASH" : "X-NORMAL";
            ball.ExecuteReceiverRallyReturn(receiverTargetPoint, finalSpeedMult, isSmash, spin, isPlayer1, label);
        }
    }

    private void HandleSmartAIBehavior()
    {
        // 1. AI SERVER: Waits 0.65s so you see the ball ready, then serves down onto AI's own side
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            aiServeTimer += Time.deltaTime;
            if (aiServeTimer >= 0.65f)
            {
                aiServeTimer = 0f;
                ShotType aiServeShot = (Random.value > 0.45f) ? ShotType.SmashShot : ShotType.NormalShot;
                StartForwardRacketSwing(aiServeShot);
            }
            return;
        }

        // 2. AI RECEIVER: Once ball bounces on AI's table and glides near AI's paddle, AI returns it
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            float distAlongTable = Mathf.Abs(Vector3.Dot(ball.transform.position - homePosition, tableForwardAxis));
            float maxReach = Mathf.Max(1.2f, ball.GetTableHalfDistance() * 0.85f);

            if (hitCooldown <= 0f && activeSwingTimer <= 0f && distAlongTable <= maxReach)
            {
                ShotType aiRallyShot = (Random.value > 0.50f) ? ShotType.SmashShot : ShotType.NormalShot;
                StartForwardRacketSwing(aiRallyShot);
            }
        }
    }

    private void UpdateHorizontalMovement()
    {
        if (controlType == RacketControlType.AI)
        {
            if (ball.CurrentPhase == RallyPhase.ServerReady)
            {
                currentHorizontalOffset = Mathf.MoveTowards(currentHorizontalOffset, 0f, Time.deltaTime * 5f);
                return;
            }

            Vector3 offsetFromHome = ball.transform.position - homePosition;
            float ballRightDist = Vector3.Dot(offsetFromHome, tableRightAxis);
            float targetOffset = Mathf.Clamp(ballRightDist, -dynamicWidthLimit, dynamicWidthLimit);
            currentHorizontalOffset = Mathf.MoveTowards(currentHorizontalOffset, targetOffset, aiTrackSpeed * Time.deltaTime);
            return;
        }

        float inputX = 0f;
        Gamepad pad = GetActiveGamepad();
        if (pad != null)
        {
            float stickX = pad.leftStick.ReadValue().x;
            float dpadX = pad.dpad.ReadValue().x;
            inputX = Mathf.Clamp(stickX + dpadX, -1f, 1f);
        }
        else if (Keyboard.current != null)
        {
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputX -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputX += 1f;
        }

        currentHorizontalOffset = inputX * dynamicWidthLimit;
    }

    private void UpdateManualTilt()
    {
        float targetTilt = 0f;

        if (controlType == RacketControlType.PlayerController)
        {
            Gamepad pad = GetActiveGamepad();
            if (pad != null)
            {
                float rStickX = pad.rightStick.ReadValue().x;
                float triggerDiff = pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
                targetTilt = Mathf.Clamp(rStickX + triggerDiff, -1f, 1f);
            }
            else if (Keyboard.current != null)
            {
                if (Keyboard.current.qKey.isPressed) targetTilt -= 1f;
                if (Keyboard.current.eKey.isPressed) targetTilt += 1f;
            }
        }

        currentTiltInput = Mathf.Lerp(currentTiltInput, targetTilt, Time.deltaTime * 12f);
        Quaternion tiltRotation = Quaternion.AngleAxis(-currentTiltInput * maxTiltDegrees, tableForwardAxis);
        transform.rotation = Quaternion.Lerp(transform.rotation, tiltRotation * baseRotation, Time.deltaTime * 15f);
    }

    private ShotType GetPlayerShotButton()
    {
        Gamepad pad = GetActiveGamepad();
        if (pad != null)
        {
            if (pad.buttonWest.wasPressedThisFrame) return ShotType.NormalShot;  // [X] = Normal Shot
            if (pad.buttonNorth.wasPressedThisFrame) return ShotType.SmashShot;  // [Y] = Fast Smash
        }

        if (Keyboard.current != null)
        {
            if (Keyboard.current.xKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame)
                return ShotType.NormalShot;
            if (Keyboard.current.yKey.wasPressedThisFrame)
                return ShotType.SmashShot;
        }

        return ShotType.None;
    }

    private Gamepad GetActiveGamepad()
    {
        if (assignedGamepad != null) return assignedGamepad;
        if (GameManager.Instance != null && GameManager.Instance.CurrentMode == GameMode.LocalPvP)
        {
            int index = isPlayer1 ? 0 : 1;
            if (Gamepad.all.Count > index) return Gamepad.all[index];
        }
        return Gamepad.current;
    }
}