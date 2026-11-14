using UnityEngine;

public class AudioManager : Singleton<AudioManager>
{
    [Header("1. Audio Clips (Drag Your Downloaded Sounds Here!)")]
    [Tooltip("Menu background music (Plays after Loading screen, stops when Match starts!)")]
    public AudioClip backgroundMusicClip;
    [Tooltip("Sound played when Player or AI serves or returns the ball")]
    public AudioClip racketHitClip;
    [Tooltip("Sound played every time the ball bounces on the table")]
    public AudioClip tableBounceClip;

    [Header("2. Volume Settings")]
    [Range(0f, 1f)] public float bgmVolume = 0.35f;
    [Range(0f, 1f)] public float sfxVolume = 0.85f;

    private AudioSource bgmSource;
    private AudioSource sfxSource;

    protected override void Awake()
    {
        base.Awake();
        SetupAudioSources();
    }

    private void SetupAudioSources()
    {
        AudioSource[] existingSources = GetComponents<AudioSource>();

        bgmSource = existingSources.Length > 0 ? existingSources[0] : gameObject.AddComponent<AudioSource>();
        sfxSource = existingSources.Length > 1 ? existingSources[1] : gameObject.AddComponent<AudioSource>();

        // Configure Menu Background Music Source
        bgmSource.loop = true;
        bgmSource.playOnAwake = false;
        bgmSource.spatialBlend = 0f;
        bgmSource.volume = bgmVolume;

        // Configure Table Tennis SFX Source (Hits & Bounces)
        sfxSource.loop = false;
        sfxSource.playOnAwake = false;
        sfxSource.spatialBlend = 0f;
        sfxSource.volume = sfxVolume;
    }

    // Plays menu music on MainMenu, ModeSelect, and ControllerSelect
    public void StartBackgroundMusic()
    {
        if (bgmSource == null) SetupAudioSources();
        if (backgroundMusicClip == null) return;

        bgmSource.volume = bgmVolume;
        if (bgmSource.clip != backgroundMusicClip)
        {
            bgmSource.clip = backgroundMusicClip;
        }

        if (!bgmSource.isPlaying)
        {
            bgmSource.Play();
        }
    }

    // Stops the background music as soon as the player enters the match (GameState.Playing)
    public void StopBackgroundMusic()
    {
        if (bgmSource != null && bgmSource.isPlaying)
        {
            bgmSource.Stop();
        }
    }

    public void ToggleMuteMusic()
    {
        if (bgmSource != null)
        {
            bgmSource.mute = !bgmSource.mute;
        }
    }

    // Plays when Player or AI hits the ball (Serve or Rally Return)
    public void PlayRacketHit(bool isSmash = false)
    {
        if (sfxSource == null || racketHitClip == null) return;

        sfxSource.pitch = isSmash ? Random.Range(1.06f, 1.15f) : Random.Range(0.95f, 1.04f);
        float vol = isSmash ? Mathf.Clamp01(sfxVolume * 1.15f) : sfxVolume;
        sfxSource.PlayOneShot(racketHitClip, vol);
    }

    // Plays every time the ball bounces on the table surface
    public void PlayTableBounce()
    {
        if (sfxSource == null || tableBounceClip == null) return;

        sfxSource.pitch = Random.Range(0.96f, 1.06f);
        sfxSource.PlayOneShot(tableBounceClip, sfxVolume * 0.9f);
    }
}