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
    [Tooltip("Small forgiveness padding (in meters) around the BoxCollider X/Z bounds.")]
    [Range(0f, 0.40f)] public float hitBoxPadding = 0.12f;
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
        hitBoxPadding = 0.12f;
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

        if (hitAreaBoxCollider == null)
        {
            hitAreaBoxCollider = GetComponent<BoxCollider>();
            if (hitAreaBoxCollider == null)
            {
                hitAreaBoxCollider = GetComponentInChildren<BoxCollider>(true);
            }
        }

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

                // Small human-like tracking imperfection
                float spinPenalty = Mathf.Abs(ball.CurrentSpinMeter) * 0.12f;
                aiTrackingErrorOffset = Random.Range(-0.12f - spinPenalty, 0.12f + spinPenalty);
            }
            lastObservedPhase = ball.CurrentPhase;
        }

        // 1. Update left/right movement offset
        UpdateHorizontalMovement();

        // 2. Calculate forward swing push
        float currentForwardPush = 0f;
        if (activeSwingTimer > 0f)
        {
            activeSwingTimer -= Time.deltaTime;
            float normalizedTime = 1f - Mathf.Clamp01(activeSwingTimer / swingDuration);
            currentForwardPush = Mathf.Sin(normalizedTime * Mathf.PI) * swingPushDistance;
        }

        // 3. Move racket transform physically
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

        Physics.SyncTransforms();

        // 4. Tilt racket angle
        UpdateManualTilt();

        // 5. STATE: ServerReady -> Ball floats in front of Server's racket
        if (ball.CurrentPhase == RallyPhase.ServerReady && ball.IsPlayer1Serving == isPlayer1)
        {
            ball.SetupServe(isPlayer1, GetBallHoldPosition());
        }

        // 6. Human Player Input
        if (controlType == RacketControlType.PlayerController && stateEntryIgnoreTimer <= 0f)
        {
            ShotType pressedShot = GetPlayerShotButton();
            if (pressedShot != ShotType.None)
            {
                StartForwardRacketSwing(pressedShot);
            }
        }

        // 7. Beatable AI Behavior
        if (controlType == RacketControlType.AI && stateEntryIgnoreTimer <= 0f)
        {
            HandleSmartAIBehavior();
        }

        // 8. Check if an active swing's BoxCollider is touching the ball
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
    // WORLD-SPACE BOXCOLLIDER HIT CHECK (Works even if you moved the BoxCollider above the table!)
    // Optional parameter 'extraForwardReach' lets the AI know when the ball is close enough in front
    // of the BoxCollider to START swinging forward so its BoxCollider pushes into the ball!
    // =============================================================================================
    private bool IsBallInsideRacketBoxCollider(float extraForwardReach = 0f)
    {
        if (ball == null) return false;

        Vector3 ballPos = ball.transform.position;

        if (hitAreaBoxCollider != null)
        {
            Bounds worldBounds = hitAreaBoxCollider.bounds;
            Vector3 boxCenter = worldBounds.center;

            // Measure distance from BoxCollider center along the table's Forward and Right axes
            Vector3 diff = ballPos - boxCenter;
            float forwardDist = Vector3.Dot(diff, tableForwardAxis);
            float lateralDist = Mathf.Abs(Vector3.Dot(diff, tableRightAxis));
            float verticalDist = Mathf.Abs(diff.y);

            // Project world bounds extents onto the table axes so rotation never breaks it
            float boxHalfForward =
                Mathf.Abs(Vector3.Dot(Vector3.right * worldBounds.extents.x, tableForwardAxis)) +
                Mathf.Abs(Vector3.Dot(Vector3.forward * worldBounds.extents.z, tableForwardAxis));

            float boxHalfLateral =
                Mathf.Abs(Vector3.Dot(Vector3.right * worldBounds.extents.x, tableRightAxis)) +
                Mathf.Abs(Vector3.Dot(Vector3.forward * worldBounds.extents.z, tableRightAxis));

            // 1. Strict Front-Face Law: Once the ball passes behind the back of the BoxCollider, it's missed!
            if (forwardDist < -(boxHalfForward + 0.05f))
            {
                return false;
            }

            // 2. Must be within the forward reach of the BoxCollider
            if (forwardDist > (boxHalfForward + hitBoxPadding + extraForwardReach))
            {
                return false;
            }

            // 3. Must be physically inside the left/right width of the BoxCollider!
            //    If the AI (or Player) hasn't physically moved the BoxCollider to the ball's lane -> FALSE!
            if (lateralDist > (boxHalfLateral + hitBoxPadding))
            {
                return false;
            }

            // 4. Generous vertical tolerance so moving the BoxCollider higher above the table still catches low bounces
            if (verticalDist > (worldBounds.extents.y + 0.65f))
            {
                return false;
            }

            return true;
        }

        // Fallback if BoxCollider is missing
        Vector3 toBall = ballPos - transform.position;
        float fDist = Vector3.Dot(toBall, tableForwardAxis);
        float lDist = Mathf.Abs(Vector3.Dot(toBall, tableRightAxis));
        return (fDist >= -0.05f && fDist <= (0.55f + extraForwardReach) && lDist <= 0.40f);
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

    // Automatically determines which table target is THIS racket's side and which is the OPPONENT'S side
    // by physical distance, so even if PlayerTargetCenter/EnemyTargetCenter are swapped, AI NEVER hits its own side!
    private void GetVerifiedTableSides(out Vector3 mySideCenter, out Vector3 oppSideCenter)
    {
        Vector3 t1 = ball.GetPlayer1TableTarget();
        Vector3 t2 = ball.GetPlayer2TableTarget();

        float distToT1 = Vector3.Distance(homePosition, t1);
        float distToT2 = Vector3.Distance(homePosition, t2);

        if (distToT1 <= distToT2)
        {
            mySideCenter = t1;
            oppSideCenter = t2;
        }
        else
        {
            mySideCenter = t2;
            oppSideCenter = t1;
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

        GetVerifiedTableSides(out Vector3 myBaseCenter, out Vector3 oppBaseCenter);

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

        // STATE B: RECEIVER STATE -> Must be physically inside the BoxCollider (or within active swing push reach)!
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            if (IsBallInsideRacketBoxCollider(swingPushDistance * 0.65f))
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

        // 2. AI Return: If the AI has physically moved its BoxCollider into the ball's lateral lane
        //    and the ball is entering the AI's forward swing reach, the AI swings forward to hit it!
        if (ball.CurrentPhase == RallyPhase.ReceiverReadyToHit && ball.IsPlayer1Receiver == isPlayer1)
        {
            if (hitCooldown <= 0f && activeSwingTimer <= 0f && IsBallInsideRacketBoxCollider(swingPushDistance))
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

            if (ball.IsPlayer1Receiver != isPlayer1)
            {
                currentHorizontalOffset = Mathf.MoveTowards(currentHorizontalOffset, 0f, (aiTrackSpeed * 0.40f) * Time.deltaTime);
                return;
            }

            if (aiCurrentReactionTimer > 0f)
            {
                aiCurrentReactionTimer -= Time.deltaTime;
                return;
            }

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
            return Gamepad.all.Count > index ? Gamepad.all[index] : null;
        }

        return Gamepad.current;
    }

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