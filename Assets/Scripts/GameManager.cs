using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

public enum GameState { Loading, MainMenu, ModeSelect, ControllerSelect, Playing, GameOver }
public enum GameMode { SinglePlayerAI, LocalPvP }

public class GameManager : Singleton<GameManager>
{
    private const int POINTS_TO_WIN = 11;

    [Header("State & Mode")]
    public GameState CurrentState { get; private set; } = GameState.Loading;
    public GameMode CurrentMode { get; private set; } = GameMode.SinglePlayerAI;

    [Header("Cameras & Split Screen")]
    [SerializeField] private GameObject menuVirtualCam;
    [SerializeField] private GameObject player1VirtualCam;
    [SerializeField] private Camera mainCameraP1;
    [SerializeField] private Camera splitScreenCamP2;

    [Header("UI Panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject modeSelectPanel;
    [SerializeField] private GameObject sideSelectPanel;
    [SerializeField] private GameObject hudPanel;

    [Header("Controller Menu First-Selected Buttons (Drag First Button of Each Menu Here)")]
    [SerializeField] private GameObject mainMenuFirstButton;
    [SerializeField] private GameObject modeSelectFirstButton;

    [Header("Side Selection UI Buttons")]
    [SerializeField] private Button player1SideButton;
    [SerializeField] private Button player2SideButton;
    [SerializeField] private TMP_Text player1ButtonText;
    [SerializeField] private TMP_Text player2ButtonText;

    [Header("HUD UI Texts")]
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private TMP_Text controlsGuideText;

    [Header("Gameplay References")]
    [SerializeField] private PongBall ball;
    [SerializeField] private RacketController player1Racket;
    [SerializeField] private RacketController player2Racket;
    [SerializeField] private AudioSource bgmSource;

    [Header("Point Reset Delay (So ball doesn't vanish instantly!)")]
    [SerializeField] private float pointResetDelay = 1.4f;

    private int p1Score = 0;
    private int p2Score = 0;
    private bool p1Joined = false;
    private bool p2Joined = false;
    private Gamepad p1Gamepad = null;
    private Gamepad p2Gamepad = null;
    private Coroutine pointTransitionRoutine;

    protected override void Awake()
    {
        base.Awake();
        if (splitScreenCamP2 != null) splitScreenCamP2.gameObject.SetActive(false);
        if (mainMenuPanel) mainMenuPanel.SetActive(false);
        if (modeSelectPanel) modeSelectPanel.SetActive(false);
        if (sideSelectPanel) sideSelectPanel.SetActive(false);
        if (hudPanel) hudPanel.SetActive(false);

        if (player1Racket != null && player2Racket != null)
        {
            player1Racket.isPlayer1 = true;
            player2Racket.isPlayer1 = false;
            player1Racket.opponentRacketTransform = player2Racket.transform;
            player2Racket.opponentRacketTransform = player1Racket.transform;

            if (ball != null)
            {
                ball.ConfigureFallbackTablePoints(player1Racket.transform.position, player2Racket.transform.position);
            }
        }

        SetupControllerHUD();
    }

    private void SetupControllerHUD()
    {
        if (controlsGuideText != null)
        {
            controlsGuideText.text =
                "<b><color=#00E5FF>XBOX CONTROLLER</color></b>\n" +
                "<b>Move Racket:</b> Left Stick\n" +
                "<b>Aim & Spin:</b> Right Stick\n" +
                "<b>1. Toss Serve:</b> RB / LB (or A)\n" +
                "<b>2. [X]</b> Normal Hit  |  <b>[Y]</b> Fast Smash";
        }
    }

    private void Update()
    {
        if (CurrentState == GameState.MainMenu || CurrentState == GameState.ModeSelect)
        {
            EnsureControllerMenuSelection();

            if (CurrentState == GameState.ModeSelect && Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)
            {
                ChangeState(GameState.MainMenu);
            }
        }
        else if (CurrentState == GameState.ControllerSelect)
        {
            PollControllersForSideSelect();
            if (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame)
            {
                ChangeState(GameState.ModeSelect);
            }
        }
    }

    private void EnsureControllerMenuSelection()
    {
        if (EventSystem.current == null) return;
        if (EventSystem.current.currentSelectedGameObject == null || !EventSystem.current.currentSelectedGameObject.activeInHierarchy)
        {
            if (CurrentState == GameState.MainMenu) SelectUIButton(mainMenuFirstButton, mainMenuPanel);
            else if (CurrentState == GameState.ModeSelect) SelectUIButton(modeSelectFirstButton, modeSelectPanel);
        }
    }

    private void SelectUIButton(GameObject preferredBtn, GameObject fallbackPanel)
    {
        if (EventSystem.current == null) return;
        EventSystem.current.SetSelectedGameObject(null);

        if (preferredBtn != null && preferredBtn.activeInHierarchy)
        {
            EventSystem.current.SetSelectedGameObject(preferredBtn);
            return;
        }
        if (fallbackPanel != null)
        {
            Button firstBtn = fallbackPanel.GetComponentInChildren<Button>(false);
            if (firstBtn != null) EventSystem.current.SetSelectedGameObject(firstBtn.gameObject);
        }
    }

    public void ChangeState(GameState newState)
    {
        if (pointTransitionRoutine != null)
        {
            StopCoroutine(pointTransitionRoutine);
            pointTransitionRoutine = null;
        }

        CurrentState = newState;

        if (mainMenuPanel) mainMenuPanel.SetActive(newState == GameState.MainMenu);
        if (modeSelectPanel) modeSelectPanel.SetActive(newState == GameState.ModeSelect);
        if (sideSelectPanel) sideSelectPanel.SetActive(newState == GameState.ControllerSelect);
        if (hudPanel) hudPanel.SetActive(newState == GameState.Playing || newState == GameState.GameOver);

        switch (newState)
        {
            case GameState.MainMenu:
                ResetSplitScreen();
                if (menuVirtualCam) menuVirtualCam.SetActive(true);
                if (player1VirtualCam) player1VirtualCam.SetActive(false);
                SelectUIButton(mainMenuFirstButton, mainMenuPanel);
                break;

            case GameState.ModeSelect:
                SelectUIButton(modeSelectFirstButton, modeSelectPanel);
                break;

            case GameState.ControllerSelect:
                ResetSideSelection();
                if (player1SideButton != null) SelectUIButton(player1SideButton.gameObject, sideSelectPanel);
                break;

            case GameState.Playing:
                if (menuVirtualCam) menuVirtualCam.SetActive(false);
                if (player1VirtualCam) player1VirtualCam.SetActive(true);

                if (CurrentMode == GameMode.LocalPvP) EnableSplitScreen();
                else ResetSplitScreen();

                SetupNextPointServe();
                break;

            case GameState.GameOver:
                StartCoroutine(GameOverRoutine());
                break;
        }
    }

    // --- UI BUTTON HOOKS ---
    public void OnPlayButtonPressed() => ChangeState(GameState.ModeSelect);
    public void OnToggleMusicPressed() { if (bgmSource != null) bgmSource.mute = !bgmSource.mute; }
    public void OnExitButtonPressed() => Application.Quit();

    public void OnSelectAIMode()
    {
        CurrentMode = GameMode.SinglePlayerAI;
        Gamepad pad = Gamepad.current != null ? Gamepad.current : (Gamepad.all.Count > 0 ? Gamepad.all[0] : null);

        player1Racket.isPlayer1 = true;
        player2Racket.isPlayer1 = false;

        player1Racket.ConfigureControl(RacketControlType.PlayerController, mainCameraP1, pad);
        player2Racket.ConfigureControl(RacketControlType.AI, null, null);

        p1Score = 0; p2Score = 0;
        UpdateScoreUI();
        ChangeState(GameState.Playing);
    }

    public void OnSelectPvPMode()
    {
        CurrentMode = GameMode.LocalPvP;
        ChangeState(GameState.ControllerSelect);
    }

    private void ResetSideSelection()
    {
        p1Joined = false;
        p2Joined = false;
        p1Gamepad = null;
        p2Gamepad = null;

        if (player1ButtonText) player1ButtonText.text = "Player 1\n<size=60%>Press [A] on Controller 1</size>";
        if (player2ButtonText) player2ButtonText.text = "Player 2\n<size=60%>Press [A] on Controller 2</size>";
    }

    public void OnPlayer1SideButtonClicked()
    {
        if (p1Joined) return;
        Gamepad pad = Gamepad.current != null && Gamepad.current != p2Gamepad ? Gamepad.current : null;
        LockInPlayer1(pad, pad != null ? $"Controller ({pad.displayName})" : "Controller 1");
    }

    public void OnPlayer2SideButtonClicked()
    {
        if (p2Joined) return;
        Gamepad pad = Gamepad.current != null && Gamepad.current != p1Gamepad ? Gamepad.current : null;
        LockInPlayer2(pad, pad != null ? $"Controller ({pad.displayName})" : "Controller 2");
    }

    private void PollControllersForSideSelect()
    {
        foreach (Gamepad pad in Gamepad.all)
        {
            if (pad.buttonSouth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame)
            {
                if (!p1Joined && pad != p2Gamepad)
                {
                    LockInPlayer1(pad, "Xbox/Pad 1");
                    return;
                }
                else if (!p2Joined && pad != p1Gamepad)
                {
                    LockInPlayer2(pad, "Xbox/Pad 2");
                    return;
                }
            }
        }
    }

    private void LockInPlayer1(Gamepad pad, string label)
    {
        p1Joined = true;
        p1Gamepad = pad;
        player1Racket.isPlayer1 = true;
        player1Racket.ConfigureControl(RacketControlType.PlayerController, mainCameraP1, pad);
        if (player1ButtonText) player1ButtonText.text = $"<color=#00FF88>P1 READY</color>\n<size=55%>{label}</size>";
        CheckBothPlayersReady();
    }

    private void LockInPlayer2(Gamepad pad, string label)
    {
        p2Joined = true;
        p2Gamepad = pad;
        player2Racket.isPlayer1 = false;
        player2Racket.ConfigureControl(RacketControlType.PlayerController, splitScreenCamP2, pad);
        if (player2ButtonText) player2ButtonText.text = $"<color=#00FF88>P2 READY</color>\n<size=55%>{label}</size>";
        CheckBothPlayersReady();
    }

    private void CheckBothPlayersReady()
    {
        if (p1Joined && p2Joined)
        {
            p1Score = 0; p2Score = 0;
            UpdateScoreUI();
            Invoke(nameof(BeginPvPMatch), 0.4f);
        }
    }

    private void BeginPvPMatch() => ChangeState(GameState.Playing);

    private bool IsPlayer1TurnToServe()
    {
        int totalPoints = p1Score + p2Score;
        if (p1Score >= 10 && p2Score >= 10) return (totalPoints % 2) == 0;
        return ((totalPoints / 2) % 2) == 0;
    }

    private void SetupNextPointServe()
    {
        player1Racket.ResetRacketToHome();
        player2Racket.ResetRacketToHome();

        bool isP1Serve = IsPlayer1TurnToServe();
        RacketController serverRacket = isP1Serve ? player1Racket : player2Racket;
        ball.SetupServe(isP1Serve, serverRacket.GetBallHoldPosition());
        UpdateScoreUI();
    }

    // Supports an optional feedback banner (e.g., "NET FAULT!", "OUT OF BOUNDS!") and waits before resetting!
    public void OnPointScored(bool player1Scored, string reasonBanner = "")
    {
        if (CurrentState != GameState.Playing || pointTransitionRoutine != null) return;

        if (player1Scored) p1Score++;
        else p2Score++;

        UpdateScoreUI(reasonBanner);

        if ((p1Score >= POINTS_TO_WIN || p2Score >= POINTS_TO_WIN) && Mathf.Abs(p1Score - p2Score) >= 2)
        {
            ChangeState(GameState.GameOver);
        }
        else
        {
            pointTransitionRoutine = StartCoroutine(DelayedNextServeRoutine());
        }
    }

    private IEnumerator DelayedNextServeRoutine()
    {
        yield return new WaitForSeconds(pointResetDelay);
        pointTransitionRoutine = null;
        if (CurrentState == GameState.Playing)
        {
            SetupNextPointServe();
        }
    }

    private IEnumerator GameOverRoutine()
    {
        yield return new WaitForSeconds(0.8f);
        ball.ResetBallToCenter();
        string winner = p1Score > p2Score ? "PLAYER 1 WINS!" : (CurrentMode == GameMode.SinglePlayerAI ? "AI WINS!" : "PLAYER 2 WINS!");
        if (scoreText != null) scoreText.text = $"<color=#FFD700>{winner}</color>  ({p1Score} - {p2Score})";
        yield return new WaitForSeconds(3.5f);
        ChangeState(GameState.MainMenu);
    }

    private void UpdateScoreUI(string feedbackBanner = "")
    {
        if (scoreText == null) return;

        string p2Label = CurrentMode == GameMode.SinglePlayerAI ? "AI" : "P2";
        string statusTag = "";

        if (p1Score >= 10 && p2Score >= 10)
        {
            if (p1Score == p2Score) statusTag = "  <color=#FFAA00>[DEUCE]</color>";
            else if (p1Score > p2Score) statusTag = "  <color=#00FF88>[ADV P1]</color>";
            else statusTag = $"  <color=#00FF88>[ADV {p2Label}]</color>";
        }

        string serveIndicatorP1 = IsPlayer1TurnToServe() ? "● " : "";
        string serveIndicatorP2 = !IsPlayer1TurnToServe() ? " ●" : "";

        string mainScoreLine = $"{serveIndicatorP1}P1  <b>{p1Score} : {p2Score}</b>  {p2Label}{serveIndicatorP2}{statusTag}";

        if (!string.IsNullOrEmpty(feedbackBanner))
        {
            scoreText.text = $"{mainScoreLine}\n<size=65%>{feedbackBanner}</size>";
        }
        else
        {
            scoreText.text = mainScoreLine;
        }
    }

    private void EnableSplitScreen()
    {
        if (mainCameraP1 != null) mainCameraP1.rect = new Rect(0f, 0f, 0.5f, 1f);
        if (splitScreenCamP2 != null)
        {
            splitScreenCamP2.gameObject.SetActive(true);
            splitScreenCamP2.rect = new Rect(0.5f, 0f, 0.5f, 1f);
        }
    }

    private void ResetSplitScreen()
    {
        if (mainCameraP1 != null) mainCameraP1.rect = new Rect(0f, 0f, 1f, 1f);
        if (splitScreenCamP2 != null) splitScreenCamP2.gameObject.SetActive(false);
    }
}