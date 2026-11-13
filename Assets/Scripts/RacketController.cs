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
    [Range(0.8f, 1.3f)] public float shotForce = 1.0f;
    [Range(1.0f, 1.3f)] public float smashMultiplier = 1.0f;

    [Header("3. References & BoxCollider Hit Area (Auto-Found if Empty!)")]
    [SerializeField] private PongBall ball;
    [Tooltip("The BoxCollider on this racket used as the exact physical hit zone!")]
    public BoxCollider hitAreaBoxCollider;
    [Tooltip("Small forgiveness padding (in meters) around the BoxCollider bounds. Set to 0 for strict exact box size!")]
    [Range(0f, 0.35f)] public float hitBoxPadding = 0.06f;
    [SerializeField] private Transform myTargetCenter;
    [SerializeField] private Transform opponentTargetCenter;
    public Transform opponentRacketTransform;

    [Header("4. Swing, Reach & Beatable AI Settings (Fully Inspector Editable!)")]
    public float swingPushDistance = 0.42f;
    public float swingDuration = 0.28f;
    public float moveSpeed = 15.5f;
    [Tooltip("Edit this on 'enemy racket' in the Inspector! Lower = slower physical AI movement.")]
    public float aiTrackSpeed = 5.5f;
    [Tooltip("Edit this on 'enemy racket' in the Inspector! Higher = slower AI reaction before moving.")]
    public float aiReactionDelay = 0.36f;
    public float maxTiltDegrees = 30f;

    private Gamepad assignedGamepad;
    private Vector3 homePosition;
    private Quaternion baseRotation;

    private Vector3 tableForwardAxis;
    private Vector3 tableRightAxis;

    private float dynamicWidthLimit = 0.85f;
    private float safeTableBounceMargin = 0.66f;

    private float currentHorizontalOffset = 0f;
    private float currentTiltInput = 0f;

    private float activeSwingTimer = 0f;
    private ShotType activeSwingShot = ShotType.NormalShot;
    private bool hasStruckBallThisSwing = false;

    private float hitCooldown = 0f;
    private float stateEntryIgnoreTimer = 0f;
    private float aiServeTimer = 0f;

    // AI Humanization & Imperfection state
    private float aiCurrentReactionTimer = 0f;
    private float aiTrackingErrorOffset = 0f;
    private RallyPhase lastObservedPhase = RallyPhase.ServerReady;

    public Vector3 HomePosition => homePosition;

    [ContextMenu("Apply 5.5 Speed & 0.36 Delay Defaults")]
    public void ApplyRecommendedDefaults()
    {
        shotForce = 1.0f;
        smashMultiplier = 1.0f;
        swingPushDistance = 0.42f;
        swingDuration = 0.28f;
        moveSpeed = 15.5f;
        aiTrackSpeed = 5.5f;
        aiReactionDelay = 0.36f;
        maxTiltDegrees = 30f;
        hitBoxPadding = 0.06f;
    }

    private void Awake()
    {
        homePosition = transform.position;
        baseRotation = transform.rotation;

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

        // Automatically find the BoxCollider you placed on player racket / enemy racket
        if (hitAreaBoxCollider == null)
        {
            hitAreaBoxCollider = GetComponent<BoxCollider>();
            if (hitAreaBoxCollider == null)
            {
                hitAreaBoxCollider = GetComponentInChildren<BoxCollider>(true);
            }
        }

        // Make sure the racket's BoxCollider is a trigger so it doesn't cause weird physics ricochets,
        // while keeping its exact 3D bounds active for our hit detection!
        if (hitAreaBoxCollider != null)
        {
            hitAreaBoxCollider.enabled = true;
            hitAreaBoxCollider.isTrigger = true;
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
            dynamicWidthLimit = Mathf.Max(0.75f, halfLen * 0.85f);
            safeTableBounceMargin = dynamicWidthLimit * 0.80f;
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
        aiCurrentReactionTimer = 0f;
        aiTrackingErrorOffset = 0f;
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

        // Detect when a new shot starts flying toward AI so AI uses your Inspector aiReactionDelay
        if (ball.CurrentPhase != lastObservedPhase)
        {
            if (ball.CurrentPhase == RallyPhase.CrossingToReceiver && ball.IsPlayer1Receiver == isPlayer1)
            {
                aiCurrentReactionTimer = aiReactionDelay;

                // AI occasionally misjudges heavy spin or fast shots
                float spinPenalty = Mathf.Abs(ball.CurrentSpinMeter) * 0.18f;
                aiTrackingErrorOffset = Random.Range(-0.18f - spinPenalty, 0.18f + spinPenalty);
            }
            lastObservedPhase = ball.CurrentPhase;
        }

        // 1. Calculate target horizontal movement FIRST
        UpdateHorizontalMovement();

        // 2. Calculate forward swing push
        float currentForwardPush = 0f;
        if (activeSwingTimer > 0f)
        {
            activeSwingTimer -= Time.deltaTime;
            float normalizedTime = 1f - Mathf.Clamp01(activeSwingTimer / swingDuration);
            currentForwardPush = Mathf.Sin(normalizedTime * Mathf.PI) * swingPushDistance;
        }

        // 3. ACTUALLY MOVE THE RACKET TRANSFORM BEFORE CHECKING BOXCOLLIDER HIT!
        // For AI, we move directly at aiTrackSpeed so the racket's physical position and BoxCollider
        // genuinely reflect where the AI has reached—no fake teleporting or hitting from afar!
        Vector3 targetWorldPos = homePosition
            + (tableRightAxis * currentHorizontalOffset)
            + (tableForwardAxis * currentForwardPush);

        if (controlType == RacketControlType.AI)
        {
            transform.position = targetWorldPos;
        }
        else
        {
            transform.position = Vector3.Lerp(transform.position, targetWorldPos, moveSpeed * Time.deltaTime);
        }

        // Sync physics transform so BoxCollider.bounds is 100% up-to-date this exact frame!
        Physics.SyncTransforms();

        // 4. Tilt racket angle with Right Stick
        UpdateManualTilt();

        // 5. STATE: ServerReady -> Ball floats in front of Server's racket until [X] or [Y] is pressed
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            ball.SetupServe(isPlayer1, GetBallHoldPosition());
        }

        // 6. Human Player Input: Press [X] (Slower Normal) or [Y] (Faster Smash)
        if (controlType == RacketControlType.PlayerController && stateEntryIgnoreTimer <= 0f)
        {
            ShotType pressedShot = GetPlayerShotButton();
            if (pressedShot != ShotType.None)
            {
                StartForwardRacketSwing(pressedShot);
            }
        }

        // 7. Beatable AI Behavior (Can ONLY swing and hit if its BoxCollider physically reaches the ball!)
        if (controlType == RacketControlType.AI && stateEntryIgnoreTimer <= 0f)
        {
            HandleSmartAIBehavior();
        }

        // 8. Check if an active swing's BoxCollider is currently touching the ball
        if (activeSwingTimer > 0f && !hasStruckBallThisSwing && hitCooldown <= 0f)
        {
            TryExecuteStateMachineShot();
        }
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
    // STRICT BOXCOLLIDER HIT AREA CHECK (Applies equally to BOTH Player and AI!)
    // Checks:
    // 1. Has the ball passed behind the front face of the racket/BoxCollider? If so -> FALSE!
    // 2. Is the ball physically inside the BoxCollider bounds you placed on the racket?
    //    If the AI (or Player) hasn't physically moved the BoxCollider to the ball -> FALSE!
    // =============================================================================================
    private bool IsBallInsideRacketBoxCollider()
    {
        if (ball == null) return false;

        Vector3 ballPos = ball.transform.position;

        if (hitAreaBoxCollider != null)
        {
            // 1. Convert ball world position into the BoxCollider's local space so even rotated/tilted
            //    BoxColliders are checked with 100% precision!
            Vector3 localBallPos = hitAreaBoxCollider.transform.InverseTransformPoint(ballPos) - hitAreaBoxCollider.center;
            Vector3 halfExtents = (hitAreaBoxCollider.size * 0.5f);

            // Convert world padding into local scale padding
            Vector3 lossy = hitAreaBoxCollider.transform.lossyScale;
            float padX = hitBoxPadding / Mathf.Max(0.01f, Mathf.Abs(lossy.x));
            float padY = (hitBoxPadding + 0.15f) / Mathf.Max(0.01f, Mathf.Abs(lossy.y));
            float padZ = hitBoxPadding / Mathf.Max(0.01f, Mathf.Abs(lossy.z));

            bool insideLocalBox =
                Mathf.Abs(localBallPos.x) <= (halfExtents.x + padX) &&
                Mathf.Abs(localBallPos.y) <= (halfExtents.y + padY) &&
                Mathf.Abs(localBallPos.z) <= (halfExtents.z + padZ);

            if (!insideLocalBox)
            {
                return false;
            }

            // 2. Strict Front-Face Law: Even if inside the box, once the ball's center passes behind
            //    the center/back plane of the BoxCollider along tableForwardAxis, it's too late!
            Vector3 boxWorldCenter = hitAreaBoxCollider.bounds.center;
            float signedForwardDist = Vector3.Dot(ballPos - boxWorldCenter, tableForwardAxis);
            if (signedForwardDist < -0.05f)
            {
                return false;
            }

            return true;
        }

        // Fallback ONLY if someone deleted the BoxCollider component off the racket
        Vector3 toBall = ballPos - transform.position;
        float forwardDist = Vector3.Dot(toBall, tableForwardAxis);
        float lateralDist = Mathf.Abs(Vector3.Dot(toBall, tableRightAxis));
        return (forwardDist >= -0.02f && forwardDist <= 0.55f && lateralDist <= 0.35f);
    }

    private void CalculateShotSpin(out float newSpinMeter, out SpinCategory category)
    {
        float incomingSpin = ball != null ? ball.CurrentSpinMeter : 0f;
        float randomRoll = Random.Range(0f, 1f);
        float randomDirection = Random.value > 0.5f ? 1f : -1f;

        if (Mathf.Abs(incomingSpin) > 0.25f && randomRoll < 0.38f)
        {
            category = SpinCategory.CounterSpin;
            newSpinMeter = Mathf.Clamp(-incomingSpin * Random.Range(1.05f, 1.35f), -1f, 1f);
        }
        else if (Mathf.Abs(incomingSpin) > 0.20f && randomRoll < 0.65f)
        {
            category = SpinCategory.OffSpin;
            newSpinMeter = Mathf.Clamp(incomingSpin * 0.35f + (randomDirection * Random.Range(0.15f, 0.40f)), -1f, 1f);
        }
        else
        {
            float baseSpin = (controlType == RacketControlType.PlayerController && Mathf.Abs(currentTiltInput) > 0.15f)
                ? currentTiltInput
                : (randomDirection * Random.Range(0.35f, 0.90f));

            newSpinMeter = Mathf.Clamp(baseSpin + Random.Range(-0.20f, 0.20f), -1f, 1f);
            category = newSpinMeter < 0f ? SpinCategory.SideSpinLeft : SpinCategory.SideSpinRight;
        }
    }

    private Vector3 CalculateRealisticReturnTarget(Vector3 oppBaseCenter)
    {
        float myLateralFromCenter = Vector3.Dot(transform.position - oppBaseCenter, tableRightAxis);
        myLateralFromCenter = Mathf.Clamp(myLateralFromCenter, -safeTableBounceMargin, safeTableBounceMargin);

        float spreadRadius = safeTableBounceMargin * 0.72f;

        float minBound;
        float maxBound;

        if (Mathf.Abs(myLateralFromCenter) > safeTableBounceMargin * 0.35f)
        {
            float inwardTarget = myLateralFromCenter - Mathf.Sign(myLateralFromCenter) * spreadRadius * 1.15f;
            float outwardEdge = myLateralFromCenter + Mathf.Sign(myLateralFromCenter) * (spreadRadius * 0.25f);

            minBound = Mathf.Min(myLateralFromCenter, inwardTarget, outwardEdge);
            maxBound = Mathf.Max(myLateralFromCenter, inwardTarget, outwardEdge);
        }
        else
        {
            minBound = myLateralFromCenter - spreadRadius;
            maxBound = myLateralFromCenter + spreadRadius;

            if (controlType == RacketControlType.AI && Random.value > 0.25f)
            {
                float sideSign = Random.value > 0.5f ? 1f : -1f;
                minBound = sideSign > 0f ? (spreadRadius * 0.28f) : -spreadRadius;
                maxBound = sideSign > 0f ? spreadRadius : (-spreadRadius * 0.28f);
            }
        }

        if (controlType == RacketControlType.PlayerController && Mathf.Abs(currentTiltInput) > 0.15f)
        {
            float tiltShift = currentTiltInput * (spreadRadius * 0.45f);
            minBound += tiltShift;
            maxBound += tiltShift;
        }

        minBound = Mathf.Clamp(minBound, -safeTableBounceMargin, safeTableBounceMargin);
        maxBound = Mathf.Clamp(maxBound, -safeTableBounceMargin, safeTableBounceMargin);

        float chosenLateralOffset = Random.Range(minBound, maxBound);
        float depthVariation = Random.Range(-0.18f, 0.22f) * (ball != null ? ball.GetTableHalfDistance() : 0.8f);

        return oppBaseCenter
            + (tableRightAxis * chosenLateralOffset)
            + (tableForwardAxis * depthVariation);
    }

    private void TryExecuteStateMachineShot()
    {
        if (ball == null || hitCooldown > 0f) return;

        bool isSmash = (activeSwingShot == ShotType.SmashShot);
        float randomSpeedJitter = (controlType == RacketControlType.AI) ? Random.Range(0.92f, 1.06f) : 1.0f;
        float finalSpeedMult = (isSmash ? (shotForce * smashMultiplier) : shotForce) * randomSpeedJitter;

        Vector3 myBaseCenter = isPlayer1 ? ball.GetPlayer1TableTarget() : ball.GetPlayer2TableTarget();
        Vector3 oppBaseCenter = isPlayer1 ? ball.GetPlayer2TableTarget() : ball.GetPlayer1TableTarget();

        Vector3 receiverTargetPoint = CalculateRealisticReturnTarget(oppBaseCenter);
        CalculateShotSpin(out float shotSpin, out SpinCategory spinCat);

        // STATE A: SERVER STATE
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            hasStruckBallThisSwing = true;
            hitCooldown = 0.22f;

            float serverLat = Vector3.Dot(transform.position - myBaseCenter, tableRightAxis);
            serverLat = Mathf.Clamp(serverLat, -safeTableBounceMargin, safeTableBounceMargin);
            Vector3 ownBounceSpot = myBaseCenter + (tableRightAxis * serverLat);

            ball.ExecuteServerDownwardHit(ownBounceSpot, receiverTargetPoint, finalSpeedMult, isSmash, shotSpin, spinCat, isPlayer1);
            return;
        }

        // STATE B: RECEIVER STATE -> MUST BE PHYSICALLY INSIDE THE RACKET'S BOX COLLIDER!
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            if (IsBallInsideRacketBoxCollider())
            {
                hasStruckBallThisSwing = true;
                hitCooldown = 0.22f;

                string label = isSmash ? "Y-FAST" : "X-SLOW";
                ball.ExecuteReceiverRallyReturn(receiverTargetPoint, finalSpeedMult, isSmash, shotSpin, spinCat, isPlayer1, label);
            }
        }
    }

    private void HandleSmartAIBehavior()
    {
        // 1. AI Serve
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

        // 2. AI Return: AI can ONLY trigger a return if the ball has physically entered its BoxCollider!
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            if (hitCooldown <= 0f && activeSwingTimer <= 0f && IsBallInsideRacketBoxCollider())
            {
                ShotType aiRallyShot = (Random.value > 0.48f) ? ShotType.SmashShot : ShotType.NormalShot;
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

            // When ball is flying away from AI, return slowly toward center
            if (ball.IsPlayer1Receiver != isPlayer1)
            {
                currentHorizontalOffset = Mathf.MoveTowards(currentHorizontalOffset, 0f, (aiTrackSpeed * 0.40f) * Time.deltaTime);
                return;
            }

            // AI waits for aiReactionDelay before it starts physically moving toward the ball!
            if (aiCurrentReactionTimer > 0f)
            {
                aiCurrentReactionTimer -= Time.deltaTime;
                return;
            }

            // AI physically moves its racket toward the ball at aiTrackSpeed
            Vector3 offsetFromHome = ball.transform.position - homePosition;
            float ballRightDist = Vector3.Dot(offsetFromHome, tableRightAxis) + aiTrackingErrorOffset;
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
            if (pad.buttonWest.wasPressedThisFrame) return ShotType.NormalShot;
            if (pad.buttonNorth.wasPressedThisFrame) return ShotType.SmashShot;
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

    // Draws the exact BoxCollider Hit Zone in the Scene View so you can see & size it visually!
    private void OnDrawGizmosSelected()
    {
        BoxCollider col = hitAreaBoxCollider != null ? hitAreaBoxCollider : GetComponentInChildren<BoxCollider>();
        if (col != null)
        {
            Gizmos.color = new Color(0f, 1f, 0.4f, 0.85f);
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = col.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(col.center, col.size);
            Gizmos.matrix = oldMatrix;
        }
    }
}