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

    // Prevents the [A] button press from ModeSelect from bleeding into ControllerSelect on the same frame!
    private float stateInputCooldown = 0f;

    protected override void Awake()
    {
        base.Awake();
        // 1. Unlock FPS & match the 144Hz monitor's native refresh rate in builds!
        QualitySettings.vSyncCount = 1; // Syncs cleanly to 144Hz without screen tearing
        Application.targetFrameRate = 144;
        Screen.SetResolution(1920, 1080, FullScreenMode.FullScreenWindow, Screen.currentResolution.refreshRateRatio);
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
                "<b>[X]</b> Normal Hit  |  <b>[Y]</b> Fast Smash";
        }
    }

    private void Update()
    {
        if (stateInputCooldown > 0f)
        {
            stateInputCooldown -= Time.unscaledDeltaTime;
            return;
        }

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
            // Keep UI EventSystem deselected in ControllerSelect so pressing [A] NEVER clicks a hidden UI button!
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }

            PollControllersForSideSelect();

            // Press [B] (buttonEast) to go back to Mode Select
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
        stateInputCooldown = 0.20f; // 0.2s buffer so button presses don't carry over between screens!

        // =========================================================================
        // BACKGROUND MUSIC CONTROL:
        // - Plays automatically after Loading screen (MainMenu, ModeSelect, ControllerSelect)
        // - STOPS immediately when the match starts (GameState.Playing)!
        // =========================================================================
        if (newState == GameState.MainMenu || newState == GameState.ModeSelect || newState == GameState.ControllerSelect)
        {
            if (AudioManager.Instance != null) AudioManager.Instance.StartBackgroundMusic();
            if (bgmSource != null && !bgmSource.isPlaying) bgmSource.Play();
        }
        else if (newState == GameState.Playing)
        {
            if (AudioManager.Instance != null) AudioManager.Instance.StopBackgroundMusic();
            if (bgmSource != null && bgmSource.isPlaying) bgmSource.Stop();
        }

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
                CurrentMode = GameMode.LocalPvP;
                ResetSideSelection();
                // Deselect UI buttons so controller [A] only registers in PollControllersForSideSelect()!
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
                break;

            case GameState.Playing:
                if (menuVirtualCam) menuVirtualCam.SetActive(false);
                if (player1VirtualCam) player1VirtualCam.SetActive(true);

                if (CurrentMode == GameMode.LocalPvP)
                {
                    EnableSplitScreen();
                    // Force both rackets to PlayerController in Local PvP!
                    player1Racket.isPlayer1 = true;
                    player2Racket.isPlayer1 = false;
                    player1Racket.ConfigureControl(RacketControlType.PlayerController, mainCameraP1, p1Gamepad);
                    player2Racket.ConfigureControl(RacketControlType.PlayerController, splitScreenCamP2, p2Gamepad);
                }
                else
                {
                    ResetSplitScreen();
                }

                SetupNextPointServe();
                break;

            case GameState.GameOver:
                StartCoroutine(GameOverRoutine());
                break;
        }
    }

    // --- UI BUTTON HOOKS ---
    public void OnPlayButtonPressed() => ChangeState(GameState.ModeSelect);

    public void OnToggleMusicPressed()
    {
        if (AudioManager.Instance != null) AudioManager.Instance.ToggleMuteMusic();
        if (bgmSource != null) bgmSource.mute = !bgmSource.mute;
    }

    public void OnExitButtonPressed() => Application.Quit();

    public void OnSelectAIMode()
    {
        // Ignore accidental UI click if we are already in ControllerSelect!
        if (CurrentState == GameState.ControllerSelect) return;

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

    // Allows clicking with Mouse as well
    public void OnPlayer1SideButtonClicked()
    {
        if (CurrentState != GameState.ControllerSelect || p1Joined) return;
        Gamepad pad = Gamepad.current != null && Gamepad.current != p2Gamepad ? Gamepad.current : null;
        LockInPlayer1(pad, pad != null ? $"Controller 1" : "Player 1 Ready");
    }

    public void OnPlayer2SideButtonClicked()
    {
        if (CurrentState != GameState.ControllerSelect || p2Joined) return;
        Gamepad pad = Gamepad.current != null && Gamepad.current != p1Gamepad ? Gamepad.current : null;
        LockInPlayer2(pad, pad != null ? $"Controller 2" : "Player 2 Ready");
    }

    private void PollControllersForSideSelect()
    {
        // 1. Check connected Gamepads
        foreach (Gamepad pad in Gamepad.all)
        {
            // Press [A] on a controller to lock in P1 (or P2 if using a 2nd controller)
            if (pad.buttonSouth.wasPressedThisFrame)
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

            // SOLO TEST SHORTCUT ON CONTROLLER:
            // If P1 is already ready, press [X] or [Y] on Controller 1 to force-join Player 2!
            if (p1Joined && !p2Joined && (pad.buttonWest.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame))
            {
                LockInPlayer2(null, "Keyboard / Test P2");
                return;
            }

            // Once both are ready, press [START] on controller to launch Split-Screen
            if (p1Joined && p2Joined && pad.startButton.wasPressedThisFrame)
            {
                BeginPvPMatch();
                return;
            }
        }

        // 2. Check Keyboard so you can join P2 and start Split-Screen without a 2nd controller!
        if (Keyboard.current != null)
        {
            bool keyPressed = Keyboard.current.spaceKey.wasPressedThisFrame ||
                              Keyboard.current.enterKey.wasPressedThisFrame ||
                              Keyboard.current.numpadEnterKey.wasPressedThisFrame;

            if (keyPressed)
            {
                if (!p1Joined)
                {
                    LockInPlayer1(null, "Keyboard P1");
                    return;
                }
                else if (!p2Joined)
                {
                    // First press when P1 is ready -> Locks in Player 2!
                    LockInPlayer2(null, "Keyboard P2");
                    return;
                }
                else
                {
                    // Second press when both P1 & P2 are ready -> Starts the Split-Screen match!
                    BeginPvPMatch();
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

        if (player1ButtonText)
        {
            player1ButtonText.text = p2Joined
                ? $"<color=#00FF88>P1 READY</color>\n<size=55%>Press [START] to Play!</size>"
                : $"<color=#00FF88>P1 READY</color>\n<size=55%>{label}</size>";
        }
        UpdateStartPromptOnButtons();
    }

    private void LockInPlayer2(Gamepad pad, string label)
    {
        p2Joined = true;
        p2Gamepad = pad;
        player2Racket.isPlayer1 = false;
        player2Racket.ConfigureControl(RacketControlType.PlayerController, splitScreenCamP2, pad);

        if (player2ButtonText)
        {
            player2ButtonText.text = $"<color=#00FF88>P2 READY</color>\n<size=55%>{label}</size>";
        }
        UpdateStartPromptOnButtons();
    }

    private void UpdateStartPromptOnButtons()
    {
        if (p1Joined && p2Joined)
        {
            if (player1ButtonText) player1ButtonText.text = "<color=#00FF88>P1 READY</color>\n<size=55%>Press [START] to Begin!</size>";
            if (player2ButtonText) player2ButtonText.text = "<color=#00FF88>P2 READY</color>\n<size=55%>Press [START] to Begin!</size>";
        }
    }

    public void BeginPvPMatch()
    {
        CurrentMode = GameMode.LocalPvP;
        p1Score = 0;
        p2Score = 0;
        UpdateScoreUI();
        ChangeState(GameState.Playing);
    }

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