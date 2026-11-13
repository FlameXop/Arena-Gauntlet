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

    [Header("2. Shot Speed Settings (Tuned Slower!)")]
    [Tooltip("Base speed multiplier (1.0 = exact PongBall timings)")]
    [Range(0.8f, 1.3f)] public float shotForce = 1.0f;
    [Tooltip("Smash uses PongBall's smashCrossNetTime (0.52s)")]
    [Range(1.0f, 1.3f)] public float smashMultiplier = 1.0f;

    [Header("3. References")]
    [SerializeField] private PongBall ball;
    [SerializeField] private Transform myTargetCenter;
    [SerializeField] private Transform opponentTargetCenter;
    public Transform opponentRacketTransform;

    [Header("4. Swing & Movement Settings")]
    [SerializeField] private float swingPushDistance = 0.45f;
    [SerializeField] private float swingDuration = 0.32f;
    [SerializeField] private float moveSpeed = 16f;
    [SerializeField] private float aiTrackSpeed = 11f;
    [SerializeField] private float maxTiltDegrees = 30f;

    private Gamepad assignedGamepad;
    private Vector3 homePosition;
    private Quaternion baseRotation;

    private Vector3 tableForwardAxis;
    private Vector3 tableRightAxis;

    // Auto-scaled to match your table mesh dimensions
    private float dynamicWidthLimit = 0.85f;
    private float safeTableBounceMargin = 0.68f;
    private float maxLateralReach = 0.58f;

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

        // Lock clean speed multipliers so PongBall's exact durations control X and Y speeds
        shotForce = 1.0f;
        smashMultiplier = 1.0f;
        swingPushDistance = 0.45f;
        swingDuration = 0.32f;
        moveSpeed = 16f;
        aiTrackSpeed = 11f;

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

            float halfLen = ball != null ? ball.GetTableHalfDistance() : (dirToOpponent.magnitude * 0.24f);
            dynamicWidthLimit = Mathf.Max(0.75f, halfLen * 0.82f);
            safeTableBounceMargin = dynamicWidthLimit * 0.82f; // Keeps random bounces well inside table edges
            maxLateralReach = Mathf.Max(0.48f, dynamicWidthLimit * 0.58f); // Player must move left/right to reach!
            return;
        }

        tableForwardAxis = isPlayer1 ? Vector3.left : Vector3.right;
        tableRightAxis = Vector3.Cross(Vector3.up, tableForwardAxis).normalized;
    }

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

        // 3. Human Player Input: Press [X] (Slower Normal) or [Y] (Faster Smash)
        if (controlType == RacketControlType.PlayerController && stateEntryIgnoreTimer <= 0f)
        {
            ShotType pressedShot = GetPlayerShotButton();
            if (pressedShot != ShotType.None)
            {
                StartForwardRacketSwing(pressedShot);
            }
        }

        // 4. Smart AI Behavior (Serves & Returns with random X or Y speed!)
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

    // =============================================================================================
    // REALISTIC POSITIONAL + RANDOM ZONE TARGETING
    // Example: If you are on the right edge, it starts from the point directly in front of you
    // on the right and picks a realistic spot between your lane and a random 4-5 unit spread inward/outward,
    // forcing the opponent to move left or right instead of standing still in the middle!
    // =============================================================================================
    private Vector3 CalculateRealisticReturnTarget(Vector3 oppBaseCenter)
    {
        // 1. World-space lateral position of this racket relative to the center line of the table
        float myLateralFromCenter = Vector3.Dot(transform.position - oppBaseCenter, tableRightAxis);
        myLateralFromCenter = Mathf.Clamp(myLateralFromCenter, -safeTableBounceMargin, safeTableBounceMargin);

        // 2. Define a realistic target window anchored around the point directly in front of the hitter
        // If you are near an edge, the window naturally expands toward the open side of the table
        // while keeping the point directly in front of you as one side of the range!
        float spreadRadius = safeTableBounceMargin * 0.72f; // Realistic 4-5 unit style spread zone

        float minBound;
        float maxBound;

        if (Mathf.Abs(myLateralFromCenter) > safeTableBounceMargin * 0.35f)
        {
            // Hitter is on the Left or Right side:
            // Pick any point between directly in front of the hitter and a realistic spread toward the middle/other side!
            float inwardTarget = myLateralFromCenter - Mathf.Sign(myLateralFromCenter) * spreadRadius * 1.15f;
            float outwardEdge = myLateralFromCenter + Mathf.Sign(myLateralFromCenter) * (spreadRadius * 0.25f);

            minBound = Mathf.Min(myLateralFromCenter, inwardTarget, outwardEdge);
            maxBound = Mathf.Max(myLateralFromCenter, inwardTarget, outwardEdge);
        }
        else
        {
            // Hitter is near the middle: spread left or right by spreadRadius so the receiver MUST move!
            minBound = myLateralFromCenter - spreadRadius;
            maxBound = myLateralFromCenter + spreadRadius;

            // Nudge away from dead-center (0) so rallies don't stay stuck middle-to-middle
            if (controlType == RacketControlType.AI && Random.value > 0.25f)
            {
                float sideSign = Random.value > 0.5f ? 1f : -1f;
                minBound = sideSign > 0f ? (spreadRadius * 0.30f) : -spreadRadius;
                maxBound = sideSign > 0f ? spreadRadius : (-spreadRadius * 0.30f);
            }
        }

        // Optional Right-Stick influence for human player
        if (controlType == RacketControlType.PlayerController && Mathf.Abs(currentTiltInput) > 0.15f)
        {
            float tiltShift = currentTiltInput * (spreadRadius * 0.45f);
            minBound += tiltShift;
            maxBound += tiltShift;
        }

        // Clamp strictly inside the table boundaries so normal shots never fly off the sides
        minBound = Mathf.Clamp(minBound, -safeTableBounceMargin, safeTableBounceMargin);
        maxBound = Mathf.Clamp(maxBound, -safeTableBounceMargin, safeTableBounceMargin);

        float chosenLateralOffset = Random.Range(minBound, maxBound);

        // 3. Add realistic depth variation (short vs. deep bounce on opponent's half)
        float depthVariation = Random.Range(-0.18f, 0.22f) * (ball != null ? ball.GetTableHalfDistance() : 0.8f);

        return oppBaseCenter
            + (tableRightAxis * chosenLateralOffset)
            + (tableForwardAxis * depthVariation);
    }

    private void TryExecuteStateMachineShot()
    {
        if (ball == null || hitCooldown > 0f) return;

        bool isSmash = (activeSwingShot == ShotType.SmashShot);

        // AI gets a slight natural speed variation (0.94x to 1.06x) on top of choosing X or Y
        float randomSpeedJitter = (controlType == RacketControlType.AI) ? Random.Range(0.94f, 1.06f) : 1.0f;
        float finalSpeedMult = (isSmash ? (shotForce * smashMultiplier) : shotForce) * randomSpeedJitter;

        Vector3 myBaseCenter = isPlayer1 ? ball.GetPlayer1TableTarget() : ball.GetPlayer2TableTarget();
        Vector3 oppBaseCenter = isPlayer1 ? ball.GetPlayer2TableTarget() : ball.GetPlayer1TableTarget();

        Vector3 receiverTargetPoint = CalculateRealisticReturnTarget(oppBaseCenter);
        Vector3 spin = Vector3.up * (-currentTiltInput * 25f);

        // =========================================================================
        // STATE A: SERVER STATE (Pressing [X] or [Y] hits ball DOWN onto own side first!)
        // =========================================================================
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            hasStruckBallThisSwing = true;
            hitCooldown = 0.22f;

            // Server's first bounce is right in front of where the Server is standing!
            float serverLat = Vector3.Dot(transform.position - myBaseCenter, tableRightAxis);
            serverLat = Mathf.Clamp(serverLat, -safeTableBounceMargin, safeTableBounceMargin);
            Vector3 ownBounceSpot = myBaseCenter + (tableRightAxis * serverLat);

            ball.ExecuteServerDownwardHit(ownBounceSpot, receiverTargetPoint, finalSpeedMult, isSmash, spin, isPlayer1);
            return;
        }

        // =========================================================================
        // STATE B: RECEIVER STATE (Must move left/right to the ball's lane to hit it!)
        // =========================================================================
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            // Check how far left/right the ball is from the player's racket
            float lateralDistToBall = Mathf.Abs(Vector3.Dot(ball.transform.position - transform.position, tableRightAxis));

            // Player MUST move toward the ball's left/right position (standing in middle won't hit wide shots!)
            if (lateralDistToBall <= maxLateralReach || controlType == RacketControlType.AI)
            {
                hasStruckBallThisSwing = true;
                hitCooldown = 0.22f;

                string label = isSmash ? "Y-FAST" : "X-SLOW";
                ball.ExecuteReceiverRallyReturn(receiverTargetPoint, finalSpeedMult, isSmash, spin, isPlayer1, label);
            }
        }
    }

    private void HandleSmartAIBehavior()
    {
        // 1. AI SERVER: Waits 0.70s, moves slightly left/right, and randomly serves with [X] Slow or [Y] Fast
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            aiServeTimer += Time.deltaTime;
            if (aiServeTimer >= 0.70f)
            {
                aiServeTimer = 0f;
                ShotType aiServeShot = (Random.value > 0.50f) ? ShotType.SmashShot : ShotType.NormalShot;
                StartForwardRacketSwing(aiServeShot);
            }
            return;
        }

        // 2. AI RECEIVER: Tracks ball and returns with random [X] Slow or [Y] Fast speed
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            float distAlongTable = Mathf.Abs(Vector3.Dot(ball.transform.position - homePosition, tableForwardAxis));
            float maxReach = Mathf.Max(1.1f, ball.GetTableHalfDistance() * 0.85f);

            if (hitCooldown <= 0f && activeSwingTimer <= 0f && distAlongTable <= maxReach)
            {
                // AI randomly mixes up X (Slower) and Y (Faster) returns!
                ShotType aiRallyShot = (Random.value > 0.45f) ? ShotType.SmashShot : ShotType.NormalShot;
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
                currentHorizontalOffset = Mathf.MoveTowards(currentHorizontalOffset, 0f, Time.deltaTime * 4f);
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
            if (pad.buttonWest.wasPressedThisFrame) return ShotType.NormalShot;  // [X] = Slower Normal Shot
            if (pad.buttonNorth.wasPressedThisFrame) return ShotType.SmashShot;  // [Y] = Faster Smash Shot
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