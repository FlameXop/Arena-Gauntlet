using UnityEngine;

public enum RallyPhase
{
    ServerReady,              // 1. Floats in front of Server until [X] or [Y] is pressed
    ServeGoingToServerTable,  // 2. Travels DOWN onto Server's own table side first
    CrossingToReceiver,       // 3. Bounces UP in a clear arc over the net to Receiver's table side
    ReceiverReadyToHit,       // 4. Bounces ONCE on Receiver's table and arcs toward Receiver's paddle
    PointEnded                // 5. Point over -> Ball bounces naturally for 1.4s before next serve
}

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class PongBall : MonoBehaviour
{
    [Header("Optional Target Centers (Auto-Found from Scene!)")]
    public Transform player1TargetCenter;
    public Transform player2TargetCenter;

    [Header("1. Visual Arc Heights (Guaranteed Net Clearance!)")]
    [Tooltip("Arc height above the net for [X] Normal Shot")]
    public float normalArcAboveNet = 0.60f;
    [Tooltip("Arc height above the net for [Y] Smash Shot")]
    public float smashArcAboveNet = 0.34f;
    [Tooltip("How high the ball pops up after bouncing on Receiver's side")]
    public float receiverBouncePopHeight = 0.48f;

    [Header("2. Flight Durations (Seconds) - Higher = Slower")]
    [Tooltip("Time for serve to hit server's own side")]
    public float serveDownToOwnTableTime = 0.30f;
    [Tooltip("[X] Normal Shot: Slower, comfortable rally speed")]
    public float normalCrossNetTime = 0.74f;
    [Tooltip("[Y] Smash Shot: Equal to the previous [X] speed!")]
    public float smashCrossNetTime = 0.52f;
    [Tooltip("Time window after bounce for Receiver to move and hit")]
    public float receiverPopUpWindowTime = 0.78f;

    [Header("Debug")]
    public bool enableDebugLogs = true;

    public RallyPhase CurrentPhase { get; private set; } = RallyPhase.ServerReady;
    public bool IsPlayer1Serving { get; private set; } = true;
    public bool LastHitByPlayer1 { get; private set; } = true;
    public bool IsPlayer1Receiver => !LastHitByPlayer1;

    private Rigidbody rb;
    private Collider ballCollider;

    // Exact Table Geometry cached from Scene
    private Vector3 p1RacketHome;
    private Vector3 p2RacketHome;
    private Vector3 tableCenterPos;
    private Vector3 axisP1ToP2;
    private Vector3 axisLeftToRight;
    private float tableSurfaceY;
    private float netTopY;
    private float halfTableDist;

    // Active Flight Segment variables
    private Vector3 flightStartPos;
    private Vector3 flightEndPos;
    private float flightPeakY;
    private float flightDuration;
    private float flightElapsed;
    private bool flightIsSmash;

    // Saved target for 2nd half of Serve (after bouncing on Server's own table)
    private Vector3 pendingServeReceiverSpot;
    private float pendingServeSpeedMult = 1f;
    private bool pendingServeIsSmash = false;

    // Post-point natural bounce velocity
    private Vector3 postPointVelocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ballCollider = GetComponent<Collider>();

        // Force-apply the new slower speeds ([Y] = old [X] speed, [X] = slower)
        normalArcAboveNet = 0.60f;
        smashArcAboveNet = 0.34f;
        receiverBouncePopHeight = 0.48f;

        serveDownToOwnTableTime = 0.30f;
        normalCrossNetTime = 0.74f;      // Slower X shot
        smashCrossNetTime = 0.52f;       // Y shot is now the old X speed!
        receiverPopUpWindowTime = 0.78f; // Plenty of time to move left/right to the ball

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
            RacketController p1 = rackets[0].isPlayer1 ? rackets[0] : rackets[1];
            RacketController p2 = !rackets[0].isPlayer1 ? rackets[0] : rackets[1];
            ConfigureFallbackTablePoints(p1.transform.position, p2.transform.position);
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
        if (player1TargetCenter != null)
        {
            return new Vector3(player1TargetCenter.position.x, tableSurfaceY, player1TargetCenter.position.z);
        }
        return tableCenterPos - (axisP1ToP2 * halfTableDist);
    }

    public Vector3 GetPlayer2TableTarget()
    {
        if (player2TargetCenter != null)
        {
            return new Vector3(player2TargetCenter.position.x, tableSurfaceY, player2TargetCenter.position.z);
        }
        return tableCenterPos + (axisP1ToP2 * halfTableDist);
    }

    public float GetTableSurfaceY() => tableSurfaceY;
    public float GetTableHalfDistance() => halfTableDist;
    public Vector3 GetTableCenter() => tableCenterPos;

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.CurrentState != GameState.Playing) return;

        // PHASE 1: ServerReady -> Ball floats in front of Server's paddle
        if (CurrentPhase == RallyPhase.ServerReady)
        {
            return;
        }

        // PHASE 5: PointEnded -> Ball bounces/rolls naturally for 1.4s until next serve
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
                postPointVelocity.y = Mathf.Abs(postPointVelocity.y) * 0.55f;
                postPointVelocity.x *= 0.75f;
                postPointVelocity.z *= 0.75f;
            }
            return;
        }

        // ADVANCE ACTIVE PARABOLIC FLIGHT SEGMENT
        flightElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(flightElapsed / Mathf.Max(0.05f, flightDuration));

        transform.position = EvaluateFlightPosition(t);

        if (t >= 1f)
        {
            OnFlightSegmentCompleted();
        }
    }

    private Vector3 EvaluateFlightPosition(float t)
    {
        Vector3 posXZ = Vector3.Lerp(flightStartPos, flightEndPos, t);

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
        // 1. Serve bounced on Server's own table -> Arc over the net
        if (CurrentPhase == RallyPhase.ServeGoingToServerTable)
        {
            TriggerServeFirstBounceOverNet();
            return;
        }

        // 2. Crossed net and landed on Receiver's table -> Pop up along that lane toward Receiver's baseline
        if (CurrentPhase == RallyPhase.CrossingToReceiver)
        {
            TriggerReceiverTableBounce();
            return;
        }

        // 3. Receiver failed to reach & hit the ball before the pop-up arc finished -> Point to Hitter!
        if (CurrentPhase == RallyPhase.ReceiverReadyToHit)
        {
            string hitter = LastHitByPlayer1 ? "P1" : "P2/AI";
            string receiver = LastHitByPlayer1 ? "P2/AI" : "P1";

            Vector3 horizDir = (flightEndPos - flightStartPos) / Mathf.Max(0.1f, flightDuration);
            postPointVelocity = new Vector3(horizDir.x * 0.65f, -2.0f, horizDir.z * 0.65f);

            EndPoint(LastHitByPlayer1, $"<color=#00FF88>POINT {hitter}!</color> ({receiver} missed return)");
        }
    }

    // STATE 1: Hold ball in front of Server
    public void SetupServe(bool isPlayer1Serve, Vector3 holdWorldPosition)
    {
        IsPlayer1Serving = isPlayer1Serve;
        CurrentPhase = RallyPhase.ServerReady;

        rb.isKinematic = true;
        if (ballCollider != null) ballCollider.enabled = false;

        transform.position = holdWorldPosition;
    }

    // STATE 1 -> STATE 2 (SERVE): Server hits ball DOWN onto their own side first
    public void ExecuteServerDownwardHit(
        Vector3 ownTableBounceSpot,
        Vector3 receiverTableTarget,
        float speedMultiplier,
        bool isSmash,
        Vector3 spinVector,
        bool hitByP1)
    {
        CurrentPhase = RallyPhase.ServeGoingToServerTable;
        LastHitByPlayer1 = hitByP1;

        pendingServeReceiverSpot = new Vector3(receiverTableTarget.x, tableSurfaceY + 0.03f, receiverTableTarget.z);
        pendingServeSpeedMult = Mathf.Clamp(speedMultiplier, 0.85f, 1.25f);
        pendingServeIsSmash = isSmash;

        flightStartPos = transform.position;
        flightEndPos = new Vector3(ownTableBounceSpot.x, tableSurfaceY + 0.03f, ownTableBounceSpot.z);
        flightPeakY = Mathf.Max(flightStartPos.y, flightEndPos.y);

        // If Y Smash serve, slightly faster; if X Normal serve, slower
        float serveTime = isSmash ? (serveDownToOwnTableTime * 0.78f) : serveDownToOwnTableTime;
        flightDuration = serveTime / pendingServeSpeedMult;
        flightElapsed = 0f;
        flightIsSmash = isSmash;

        if (enableDebugLogs)
            Debug.Log($"<color=#00E5FF>[1. SERVE DOWN]</color> {(hitByP1 ? "P1" : "P2/AI")} served to own side: {flightEndPos}");
    }

    // STATE 2 -> STATE 3 (1ST SERVE BOUNCE): Bounces off Server's side -> High arc over net!
    private void TriggerServeFirstBounceOverNet()
    {
        CurrentPhase = RallyPhase.CrossingToReceiver;

        flightStartPos = flightEndPos;
        flightEndPos = pendingServeReceiverSpot;

        float clearance = pendingServeIsSmash ? smashArcAboveNet : normalArcAboveNet;
        flightPeakY = netTopY + clearance;

        float baseTime = pendingServeIsSmash ? smashCrossNetTime : normalCrossNetTime;
        flightDuration = baseTime / pendingServeSpeedMult;
        flightElapsed = 0f;
        flightIsSmash = pendingServeIsSmash;

        if (enableDebugLogs)
            Debug.Log($"<color=#55FF55>[2. SERVE BOUNCED]</color> Crossing net to {flightEndPos}");
    }

    // STATE 3 -> STATE 4 (BOUNCE ON RECEIVER'S TABLE): Pops up along the bounce lane so Receiver must move to it!
    private void TriggerReceiverTableBounce()
    {
        CurrentPhase = RallyPhase.ReceiverReadyToHit;

        flightStartPos = flightEndPos;

        Vector3 dirToReceiver = IsPlayer1Receiver ? -axisP1ToP2 : axisP1ToP2;
        Vector3 lateralOffset = Vector3.Project(flightStartPos - tableCenterPos, axisLeftToRight);

        // Carries slight outward angle (1.12x) after the bounce like a real ping pong ball!
        Vector3 strikeLinePos = tableCenterPos
            + (dirToReceiver * (halfTableDist * 1.95f))
            + (lateralOffset * 1.12f);

        strikeLinePos.y = tableSurfaceY + 0.12f;

        flightEndPos = strikeLinePos;
        flightPeakY = tableSurfaceY + receiverBouncePopHeight;
        flightDuration = flightIsSmash ? (receiverPopUpWindowTime * 0.85f) : receiverPopUpWindowTime;
        flightElapsed = 0f;

        if (enableDebugLogs)
            Debug.Log($"<color=#00FF88>[3. BOUNCED ON RECEIVER TABLE]</color> Receiver must move to lane: {strikeLinePos}");
    }

    // STATE 4 -> STATE 3 (RALLY RETURN): Receiver hits ball over the net to the calculated target zone!
    public void ExecuteReceiverRallyReturn(
        Vector3 opponentTableTarget,
        float speedMultiplier,
        bool isSmash,
        Vector3 spinVector,
        bool hitByP1,
        string shotLabel)
    {
        CurrentPhase = RallyPhase.CrossingToReceiver;
        LastHitByPlayer1 = hitByP1;

        float clampedSpeed = Mathf.Clamp(speedMultiplier, 0.85f, 1.25f);

        flightStartPos = transform.position;
        flightEndPos = new Vector3(opponentTableTarget.x, tableSurfaceY + 0.03f, opponentTableTarget.z);

        float clearance = isSmash ? smashArcAboveNet : normalArcAboveNet;
        flightPeakY = Mathf.Max(flightStartPos.y + 0.12f, netTopY + clearance);

        float baseTime = isSmash ? smashCrossNetTime : normalCrossNetTime;
        flightDuration = baseTime / clampedSpeed;
        flightElapsed = 0f;
        flightIsSmash = isSmash;

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
        rb.isKinematic = true;
        if (ballCollider != null) ballCollider.enabled = false;
        transform.position = tableCenterPos + Vector3.up * 0.35f;
    }
}