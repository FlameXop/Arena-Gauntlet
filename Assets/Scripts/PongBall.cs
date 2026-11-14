using UnityEngine;

public enum RallyPhase
{
    ServerReady,              // 1. Floats in front of Server until [X] or [Y] is pressed
    ServeGoingToServerTable,  // 2. Travels DOWN onto Server's own table side first
    CrossingToReceiver,       // 3. Bounces UP in a clear arc over the net to Receiver's table side
    ReceiverReadyToHit,       // 4. Bounces ONCE on Receiver's table and arcs through Receiver's BoxCollider zone!
    PointEnded                // 5. Point over -> Ball bounces naturally for 1.4s before next serve
}

public enum SpinCategory
{
    Flat,
    SideSpinLeft,
    SideSpinRight,
    CounterSpin,
    OffSpin
}

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class PongBall : MonoBehaviour
{
    [Header("Optional Target Centers (Auto-Found from Scene!)")]
    public Transform player1TargetCenter;
    public Transform player2TargetCenter;

    [Header("1. Visual Arc Heights (Fully Inspector Editable!)")]
    public float normalArcAboveNet = 0.60f;
    public float smashArcAboveNet = 0.34f;
    public float receiverBouncePopHeight = 0.48f;

    [Header("2. Flight Durations in Seconds (Fully Inspector Editable!)")]
    public float serveDownToOwnTableTime = 0.30f;
    public float normalCrossNetTime = 0.74f;
    public float smashCrossNetTime = 0.52f;
    public float receiverPopUpWindowTime = 0.75f;

    [Header("3. Spin Mechanic & Spin Meter HUD")]
    public bool showSpinMeterHUD = true;
    [Range(0.10f, 0.55f)] public float maxAirSpinCurve = 0.28f;
    [Range(0.05f, 0.40f)] public float maxBounceSpinKick = 0.20f;

    [Header("Debug")]
    public bool enableDebugLogs = true;

    public RallyPhase CurrentPhase { get; private set; } = RallyPhase.ServerReady;
    public bool IsPlayer1Serving { get; private set; } = true;
    public bool LastHitByPlayer1 { get; private set; } = true;
    public bool IsPlayer1Receiver => !LastHitByPlayer1;

    public float CurrentSpinMeter { get; private set; } = 0f;
    public SpinCategory CurrentSpinType { get; private set; } = SpinCategory.Flat;
    public string CurrentSpinLabel { get; private set; } = "FLAT (0%)";

    private Rigidbody rb;
    private Collider ballCollider;

    private RacketController p1RacketRef;
    private RacketController p2RacketRef;

    private Vector3 p1RacketHome;
    private Vector3 p2RacketHome;
    private Vector3 tableCenterPos;
    private Vector3 axisP1ToP2;
    private Vector3 axisLeftToRight;
    private float tableSurfaceY;
    private float netTopY;
    private float halfTableDist;

    private Vector3 flightStartPos;
    private Vector3 flightEndPos;
    private float flightPeakY;
    private float flightDuration;
    private float flightElapsed;
    private bool flightIsSmash;

    private Vector3 pendingServeReceiverSpot;
    private float pendingServeSpeedMult = 1f;
    private bool pendingServeIsSmash = false;

    private Vector3 postPointVelocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ballCollider = GetComponent<Collider>();

        rb.useGravity = false;
        rb.isKinematic = true;
        if (ballCollider != null) ballCollider.enabled = false;

        AutoFindSceneReferences();
    }

    private void Start()
    {
        AutoFindSceneReferences();

        RacketController[] rackets = FindObjectsByType<RacketController>(FindObjectsSortMode.None);
        if (rackets.Length >= 2)
        {
            p1RacketRef = rackets[0].isPlayer1 ? rackets[0] : rackets[1];
            p2RacketRef = !rackets[0].isPlayer1 ? rackets[0] : rackets[1];
            ConfigureFallbackTablePoints(p1RacketRef.transform.position, p2RacketRef.transform.position);
        }
    }

    private void AutoFindSceneReferences()
    {
        if (player1TargetCenter == null)
        {
            GameObject p1Obj = GameObject.Find("PlayerTargetCenter");
            if (p1Obj != null) player1TargetCenter = p1Obj.transform;
        }
        if (player2TargetCenter == null)
        {
            GameObject p2Obj = GameObject.Find("EnemyTargetCenter");
            if (p2Obj != null) player2TargetCenter = p2Obj.transform;
        }
    }

    public void ConfigureFallbackTablePoints(Vector3 p1Home, Vector3 p2Home)
    {
        AutoFindSceneReferences();

        p1RacketHome = p1Home;
        p2RacketHome = p2Home;

        Vector3 dir = p2Home - p1Home;
        dir.y = 0f;
        float totalRacketDist = dir.magnitude;
        axisP1ToP2 = totalRacketDist > 0.01f ? dir.normalized : Vector3.left;
        axisLeftToRight = Vector3.Cross(Vector3.up, axisP1ToP2).normalized;

        GameObject netObj = GameObject.Find("net");
        GameObject tableObj = GameObject.Find("table ping pong");

        if (player1TargetCenter != null && player2TargetCenter != null)
        {
            tableSurfaceY = (player1TargetCenter.position.y + player2TargetCenter.position.y) * 0.5f;
        }
        else if (netObj != null)
        {
            tableSurfaceY = netObj.transform.position.y - 0.05f;
        }
        else if (tableObj != null)
        {
            tableSurfaceY = tableObj.transform.position.y;
        }
        else
        {
            tableSurfaceY = Mathf.Min(p1Home.y, p2Home.y);
        }

        netTopY = tableSurfaceY + 0.18f;
        if (netObj != null && netObj.GetComponent<Renderer>() != null)
        {
            netTopY = Mathf.Max(tableSurfaceY + 0.15f, netObj.GetComponent<Renderer>().bounds.max.y);
        }

        tableCenterPos = (p1Home + p2Home) * 0.5f;
        if (netObj != null)
        {
            tableCenterPos.x = netObj.transform.position.x;
            tableCenterPos.z = netObj.transform.position.z;
        }
        tableCenterPos.y = tableSurfaceY;

        halfTableDist = Mathf.Max(0.6f, totalRacketDist * 0.24f);
    }

    public Vector3 GetPlayer1TableTarget()
    {
        Vector3 mathP1 = tableCenterPos - (axisP1ToP2 * halfTableDist);
        if (player1TargetCenter != null && player2TargetCenter != null)
        {
            float d1 = Vector3.Distance(p1RacketHome, player1TargetCenter.position);
            float d2 = Vector3.Distance(p1RacketHome, player2TargetCenter.position);
            Transform closerToP1 = d1 <= d2 ? player1TargetCenter : player2TargetCenter;
            return new Vector3(closerToP1.position.x, tableSurfaceY, closerToP1.position.z);
        }
        return mathP1;
    }

    public Vector3 GetPlayer2TableTarget()
    {
        Vector3 mathP2 = tableCenterPos + (axisP1ToP2 * halfTableDist);
        if (player1TargetCenter != null && player2TargetCenter != null)
        {
            float d1 = Vector3.Distance(p2RacketHome, player1TargetCenter.position);
            float d2 = Vector3.Distance(p2RacketHome, player2TargetCenter.position);
            Transform closerToP2 = d2 <= d1 ? player2TargetCenter : player1TargetCenter;
            return new Vector3(closerToP2.position.x, tableSurfaceY, closerToP2.position.z);
        }
        return mathP2;
    }

    public float GetTableSurfaceY() => tableSurfaceY;
    public float GetTableHalfDistance() => halfTableDist;
    public Vector3 GetTableCenter() => tableCenterPos;

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.CurrentState != GameState.Playing) return;

        if (CurrentPhase != RallyPhase.ServerReady && Mathf.Abs(CurrentSpinMeter) > 0.05f)
        {
            transform.Rotate(Vector3.up, CurrentSpinMeter * 720f * Time.deltaTime, Space.World);
        }

        if (CurrentPhase == RallyPhase.ServerReady) return;

        if (CurrentPhase == RallyPhase.PointEnded)
        {
            postPointVelocity.y -= 9.81f * Time.deltaTime;
            transform.position += postPointVelocity * Time.deltaTime;

            float distFromCenter = Vector2.Distance(
                new Vector2(transform.position.x, transform.position.z),
                new Vector2(tableCenterPos.x, tableCenterPos.z));

            if (distFromCenter < halfTableDist * 2.2f && transform.position.y <= tableSurfaceY + 0.03f && postPointVelocity.y < 0f)
            {
                transform.position = new Vector3(transform.position.x, tableSurfaceY + 0.03f, transform.position.z);
                postPointVelocity.y = Mathf.Abs(postPointVelocity.y) * 0.52f;
                postPointVelocity.x *= 0.72f;
                postPointVelocity.z *= 0.72f;

                if (AudioManager.Instance != null && postPointVelocity.y > 0.5f)
                {
                    AudioManager.Instance.PlayTableBounce();
                }
            }
            return;
        }

        flightElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(flightElapsed / Mathf.Max(0.05f, flightDuration));

        transform.position = EvaluateFlightPosition(t);

        if (CurrentPhase == RallyPhase.ReceiverReadyToHit)
        {
            Vector3 receiverHome = IsPlayer1Receiver ? p1RacketHome : p2RacketHome;
            Vector3 receiverForward = IsPlayer1Receiver ? axisP1ToP2 : -axisP1ToP2;

            float distInFrontOfReceiverHome = Vector3.Dot(transform.position - receiverHome, receiverForward);

            if (distInFrontOfReceiverHome < -0.18f)
            {
                TriggerReceiverFault(false);
                return;
            }
        }

        if (t >= 1f)
        {
            OnFlightSegmentCompleted();
        }
    }

    private Vector3 EvaluateFlightPosition(float t)
    {
        Vector3 posXZ = Vector3.Lerp(flightStartPos, flightEndPos, t);

        if (CurrentPhase == RallyPhase.CrossingToReceiver && Mathf.Abs(CurrentSpinMeter) > 0.01f)
        {
            float curveArc = 4f * t * (1f - t);
            posXZ += axisLeftToRight * (CurrentSpinMeter * maxAirSpinCurve * curveArc);
        }

        float y;
        if (CurrentPhase == RallyPhase.ServeGoingToServerTable)
        {
            y = Mathf.Lerp(flightStartPos.y, flightEndPos.y, t * t);
        }
        else
        {
            float baseLineY = Mathf.Lerp(flightStartPos.y, flightEndPos.y, t);
            float arcBoost = 4f * t * (1f - t) * Mathf.Max(0.08f, flightPeakY - (flightStartPos.y + flightEndPos.y) * 0.5f);
            y = baseLineY + arcBoost;
        }

        return new Vector3(posXZ.x, y, posXZ.z);
    }

    private void OnFlightSegmentCompleted()
    {
        if (CurrentPhase == RallyPhase.ServeGoingToServerTable)
        {
            TriggerServeFirstBounceOverNet();
            return;
        }

        if (CurrentPhase == RallyPhase.CrossingToReceiver)
        {
            TriggerReceiverTableBounce();
            return;
        }

        if (CurrentPhase == RallyPhase.ReceiverReadyToHit)
        {
            TriggerReceiverFault(true);
        }
    }

    private void TriggerReceiverFault(bool isDoubleBounce)
    {
        if (CurrentPhase == RallyPhase.PointEnded) return;

        if (isDoubleBounce && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayTableBounce();
        }

        string hitter = LastHitByPlayer1 ? "P1" : "P2/AI";
        string receiver = LastHitByPlayer1 ? "P2/AI" : "P1";

        Vector3 horizDir = (flightEndPos - flightStartPos) / Mathf.Max(0.1f, flightDuration);
        postPointVelocity = new Vector3(horizDir.x * 0.65f, isDoubleBounce ? 1.6f : -1.8f, horizDir.z * 0.65f);

        string reason = isDoubleBounce
            ? $"<color=#00FF88>POINT {hitter}!</color> ({receiver} missed return)"
            : $"<color=#00FF88>POINT {hitter}!</color> (Ball passed {receiver}'s racket)";

        EndPoint(LastHitByPlayer1, reason);
    }

    private void SetActiveSpinState(float normalizedSpin, SpinCategory category)
    {
        CurrentSpinMeter = Mathf.Clamp(normalizedSpin, -1f, 1f);
        CurrentSpinType = category;

        int pct = Mathf.RoundToInt(Mathf.Abs(CurrentSpinMeter) * 100f);
        string dirTag = CurrentSpinMeter < -0.08f ? "LEFT ◄" : (CurrentSpinMeter > 0.08f ? "► RIGHT" : "CENTER");

        switch (category)
        {
            case SpinCategory.CounterSpin:
                CurrentSpinLabel = $"COUNTER-SPIN {dirTag} ({pct}%)";
                break;
            case SpinCategory.OffSpin:
                CurrentSpinLabel = $"OFF-SPIN {dirTag} ({pct}%)";
                break;
            case SpinCategory.SideSpinLeft:
            case SpinCategory.SideSpinRight:
                CurrentSpinLabel = $"SIDE-SPIN {dirTag} ({pct}%)";
                break;
            default:
                CurrentSpinLabel = $"FLAT DRIVE ({pct}%)";
                break;
        }
    }

    public void SetupServe(bool isPlayer1Serve, Vector3 holdWorldPosition)
    {
        IsPlayer1Serving = isPlayer1Serve;
        CurrentPhase = RallyPhase.ServerReady;
        SetActiveSpinState(0f, SpinCategory.Flat);

        rb.isKinematic = true;
        if (ballCollider != null) ballCollider.enabled = false;

        transform.position = holdWorldPosition;
    }

    public void ExecuteServerDownwardHit(
        Vector3 ownTableBounceSpot,
        Vector3 receiverTableTarget,
        float speedMultiplier,
        bool isSmash,
        float spinNormalized,
        SpinCategory spinCategory,
        bool hitByP1)
    {
        CurrentPhase = RallyPhase.ServeGoingToServerTable;
        LastHitByPlayer1 = hitByP1;

        pendingServeReceiverSpot = new Vector3(receiverTableTarget.x, tableSurfaceY + 0.03f, receiverTableTarget.z);
        pendingServeSpeedMult = Mathf.Clamp(speedMultiplier, 0.85f, 1.25f);
        pendingServeIsSmash = isSmash;

        SetActiveSpinState(spinNormalized, spinCategory);

        flightStartPos = transform.position;
        flightEndPos = new Vector3(ownTableBounceSpot.x, tableSurfaceY + 0.03f, ownTableBounceSpot.z);
        flightPeakY = Mathf.Max(flightStartPos.y, flightEndPos.y);

        float serveTime = isSmash ? (serveDownToOwnTableTime * 0.78f) : serveDownToOwnTableTime;
        flightDuration = serveTime / pendingServeSpeedMult;
        flightElapsed = 0f;
        flightIsSmash = isSmash;

        // Play Racket Serve Hit SFX!
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayRacketHit(isSmash);
        }

        if (enableDebugLogs)
            Debug.Log($"<color=#00E5FF>[1. SERVE DOWN]</color> {(hitByP1 ? "P1" : "P2/AI")} served to own side {flightEndPos} -> Target {pendingServeReceiverSpot}");
    }

    private void TriggerServeFirstBounceOverNet()
    {
        // Play Table Bounce SFX on Server's own side!
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayTableBounce();
        }

        CurrentPhase = RallyPhase.CrossingToReceiver;

        flightStartPos = flightEndPos;
        flightEndPos = pendingServeReceiverSpot;

        float clearance = pendingServeIsSmash ? smashArcAboveNet : normalArcAboveNet;
        flightPeakY = netTopY + clearance;

        float baseTime = pendingServeIsSmash ? smashCrossNetTime : normalCrossNetTime;
        flightDuration = baseTime / pendingServeSpeedMult;
        flightElapsed = 0f;
        flightIsSmash = pendingServeIsSmash;
    }

    private void TriggerReceiverTableBounce()
    {
        // Play Table Bounce SFX on Receiver's side!
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayTableBounce();
        }

        CurrentPhase = RallyPhase.ReceiverReadyToHit;

        flightStartPos = flightEndPos;

        Vector3 receiverHome = IsPlayer1Receiver ? p1RacketHome : p2RacketHome;
        Vector3 dirToReceiver = IsPlayer1Receiver ? -axisP1ToP2 : axisP1ToP2;

        float distCenterToRacket = Mathf.Abs(Vector3.Dot(receiverHome - tableCenterPos, dirToReceiver));
        float targetReachDist = Mathf.Max(halfTableDist * 2.05f, distCenterToRacket + 0.22f);

        Vector3 lateralOffset = Vector3.Project(flightStartPos - tableCenterPos, axisLeftToRight);
        Vector3 spinBounceKick = axisLeftToRight * (CurrentSpinMeter * maxBounceSpinKick);

        Vector3 passThroughRacketSpot = tableCenterPos
            + (dirToReceiver * targetReachDist)
            + (lateralOffset * 1.05f)
            + spinBounceKick;

        passThroughRacketSpot.y = tableSurfaceY + 0.10f;

        flightEndPos = passThroughRacketSpot;
        flightPeakY = tableSurfaceY + receiverBouncePopHeight;
        flightDuration = flightIsSmash ? (receiverPopUpWindowTime * 0.85f) : receiverPopUpWindowTime;
        flightElapsed = 0f;
    }

    public void ExecuteReceiverRallyReturn(
        Vector3 opponentTableTarget,
        float speedMultiplier,
        bool isSmash,
        float spinNormalized,
        SpinCategory spinCategory,
        bool hitByP1,
        string shotLabel)
    {
        CurrentPhase = RallyPhase.CrossingToReceiver;
        LastHitByPlayer1 = hitByP1;

        SetActiveSpinState(spinNormalized, spinCategory);

        float clampedSpeed = Mathf.Clamp(speedMultiplier, 0.85f, 1.25f);

        flightStartPos = transform.position;
        flightEndPos = new Vector3(opponentTableTarget.x, tableSurfaceY + 0.03f, opponentTableTarget.z);

        float clearance = isSmash ? smashArcAboveNet : normalArcAboveNet;
        flightPeakY = Mathf.Max(flightStartPos.y + 0.12f, netTopY + clearance);

        float baseTime = isSmash ? smashCrossNetTime : normalCrossNetTime;
        flightDuration = baseTime / clampedSpeed;
        flightElapsed = 0f;
        flightIsSmash = isSmash;

        // Play Racket Rally Return Hit SFX!
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayRacketHit(isSmash);
        }

        if (enableDebugLogs)
            Debug.Log($"<color=#00E5FF>[RALLY RETURN - {shotLabel}]</color> {(hitByP1 ? "P1" : "P2/AI")} returned to {flightEndPos}!");
    }

    private void EndPoint(bool player1Scored, string banner)
    {
        if (CurrentPhase == RallyPhase.PointEnded) return;
        CurrentPhase = RallyPhase.PointEnded;

        if (GameManager.Instance != null)
        {
            GameManager.Instance.OnPointScored(player1Scored, banner);
        }
    }

    public void ResetBallToCenter()
    {
        CurrentPhase = RallyPhase.ServerReady;
        SetActiveSpinState(0f, SpinCategory.Flat);
        rb.isKinematic = true;
        if (ballCollider != null) ballCollider.enabled = false;
        transform.position = tableCenterPos + Vector3.up * 0.35f;
    }

    private void OnGUI()
    {
        if (!showSpinMeterHUD) return;
        if (GameManager.Instance != null && GameManager.Instance.CurrentState != GameState.Playing) return;

        float barWidth = 280f;
        float barHeight = 22f;
        float x = (Screen.width - barWidth) * 0.5f;
        float y = Screen.height - 68f;

        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.Box(new Rect(x - 10f, y - 26f, barWidth + 20f, barHeight + 36f), "");

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 13
        };
        GUI.color = CurrentSpinType == SpinCategory.CounterSpin ? new Color(1f, 0.45f, 0.2f)
                  : CurrentSpinType == SpinCategory.OffSpin ? new Color(0.75f, 0.4f, 1f)
                  : new Color(0f, 0.9f, 1f);
        GUI.Label(new Rect(x - 10f, y - 24f, barWidth + 20f, 22f), $"SPIN METER: {CurrentSpinLabel}", labelStyle);

        GUI.color = new Color(0.2f, 0.2f, 0.25f, 0.95f);
        GUI.DrawTexture(new Rect(x, y, barWidth, barHeight), Texture2D.whiteTexture);

        float centerX = x + (barWidth * 0.5f);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(centerX - 1f, y - 2f, 2f, barHeight + 4f), Texture2D.whiteTexture);

        float fillWidth = (barWidth * 0.5f) * Mathf.Abs(CurrentSpinMeter);
        if (fillWidth > 1f)
        {
            GUI.color = CurrentSpinMeter < 0f ? new Color(0f, 0.85f, 1f, 0.9f) : new Color(1f, 0.65f, 0f, 0.9f);
            float fillX = CurrentSpinMeter < 0f ? (centerX - fillWidth) : centerX;
            GUI.DrawTexture(new Rect(fillX, y + 3f, fillWidth, barHeight - 6f), Texture2D.whiteTexture);
        }

        GUI.color = Color.white;
    }
}