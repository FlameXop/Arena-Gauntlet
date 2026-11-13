using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class BootstrapLoader : MonoBehaviour
{
    [SerializeField] private Slider loadingSlider;
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private string gameSceneName = "SampleScene";
    [SerializeField] private bool loadSeparateScene = false;

    private void Awake()
    {
        // Force Loading Panel ON immediately before first frame renders
        if (loadingPanel != null) loadingPanel.SetActive(true);
        if (loadingSlider != null) loadingSlider.value = 0f;

        int nativeHz = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
#if UNITY_ANDROID || UNITY_IOS
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = nativeHz;
#else
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
#endif
    }

    private void Start()
    {
        StartCoroutine(WarmupAndLoad());
    }

    private IEnumerator WarmupAndLoad()
    {
        // Wait 2 frames so the Canvas actually renders the Loading Panel on screen first!
        yield return null;
        yield return null;

        if (loadingSlider != null) loadingSlider.value = 0.2f;
        yield return new WaitForSeconds(0.15f);

        // Warmup shaders after UI is visible
        Shader.WarmupAllShaders();

        if (loadSeparateScene)
        {
            AsyncOperation op = SceneManager.LoadSceneAsync(gameSceneName);
            op.allowSceneActivation = false;

            while (op.progress < 0.9f)
            {
                if (loadingSlider != null) loadingSlider.value = 0.3f + (op.progress * 0.7f);
                yield return null;
            }
            if (loadingSlider != null) loadingSlider.value = 1f;
            yield return new WaitForSeconds(0.25f);
            op.allowSceneActivation = true;
        }
        else
        {
            float t = 0.25f;
            while (t < 1f)
            {
                t += Time.deltaTime * 0.9f; // Smooth ~0.8s loading bar fill
                if (loadingSlider != null) loadingSlider.value = t;
                yield return null;
            }
            if (loadingPanel != null) loadingPanel.SetActive(false);
            GameManager.Instance.ChangeState(GameState.MainMenu);
        }
    }
}