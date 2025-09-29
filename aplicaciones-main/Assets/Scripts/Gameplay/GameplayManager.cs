using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.SceneManagement;

public class GameplayManager : MonoBehaviour
{
    public static GameplayManager Instance;

    [Header("Audio")]
    public AudioSource musicSource;

    [Header("Spawner")]
    public NoteSpawner spawner;
    
    [Header("Managers")]
    public ScoreManager scoreManager;
    public InputManager inputManager;
    
    [Header("Game State")]
    public bool isGameActive = false;
    public bool isPaused = false;
    public float songLength = 0f;
    
    [Header("Hit Detection")]
    public float hitWindow = 0.1f;
    public float perfectWindow = 0.05f;
    public float greatWindow = 0.08f;
    
    public ChartParser.ChartData chartData;
    public List<NoteData> selectedNotes;
    
    // Events
    public System.Action<NoteData, HitAccuracy> OnNoteHit;
    public System.Action<NoteData> OnNoteMissed;
    public System.Action OnSongFinished;
    
    // Active notes for hit detection
    private List<NoteData> activeNotes = new List<NoteData>();
    private Dictionary<int, Note> spawnedNotes = new Dictionary<int, Note>();

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    void Start()
    {
        StartCoroutine(InitializeGameplay());
    }
    
    IEnumerator InitializeGameplay()
    {
        LoadChartSection();
        yield return StartCoroutine(LoadAudio());
        
        // Wait a moment for everything to initialize
        yield return new WaitForSeconds(0.5f);
        
        StartGameplay();
    }
    
    void StartGameplay()
    {
        isGameActive = true;
        if (musicSource.clip != null)
        {
            songLength = musicSource.clip.length;
        }
        
        // Subscribe to events
        if (spawner != null)
        {
            spawner.OnNoteSpawned += RegisterActiveNote;
        }
    }

    IEnumerator LoadAudio()
    {
        string songFolder = Path.Combine(Application.streamingAssetsPath, "Songs", GameManager.Instance.selectedSongPath);
        string audioPath = Path.Combine(songFolder, "song.ogg");

        if (!File.Exists(audioPath))
        {
            Debug.LogError("❌ Audio no encontrado: " + audioPath);
            yield break;
        }

        UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + audioPath, AudioType.OGGVORBIS);
        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("❌ Error al cargar audio: " + www.error);
        }
        else
        {
            musicSource.clip = DownloadHandlerAudioClip.GetContent(www);
            musicSource.volume = 0.4f;
            musicSource.Play();
        }
    }


    void LoadChartSection()
    {
        string songFolder = Path.Combine(Application.streamingAssetsPath, "Songs", GameManager.Instance.selectedSongPath);
        string chartPath = Path.Combine(songFolder, "notes.chart");

        if (!File.Exists(chartPath))
        {
            Debug.LogError("❌ Chart no encontrado: " + chartPath);
            return;
        }

        // Parse the chart file using the new ChartParser
        chartData = ChartParser.ParseChartFile(chartPath);
        
        if (chartData == null)
        {
            Debug.LogError("❌ Error al parsear el chart: " + chartPath);
            return;
        }

        // Get notes for the selected difficulty
        selectedNotes = ChartParser.GetNotesForDifficulty(chartData, GameManager.Instance.selectedDifficulty);
        
        if (selectedNotes == null || selectedNotes.Count == 0)
        {
            Debug.LogError($"❌ No se encontraron notas para la dificultad: {GameManager.Instance.selectedDifficulty}");
            return;
        }
        
        Debug.Log($"🎯 Chart parseado exitosamente: {chartData.songName} - {selectedNotes.Count} notas encontradas");
    }

    public float GetSongTime()
    {
        if (musicSource != null && musicSource.clip != null && musicSource.isPlaying)
            return musicSource.time;
        else
            return 0f;
    }
    
    void Update()
    {
        if (!isGameActive || isPaused) return;
        
        // Check for missed notes
        CheckMissedNotes();
        
        // Check if song is finished
        if (musicSource != null && !musicSource.isPlaying && GetSongTime() > 0)
        {
            EndSong();
        }
    }
    
    void CheckMissedNotes()
    {
        float currentTime = GetSongTime();
        
        for (int i = activeNotes.Count - 1; i >= 0; i--)
        {
            NoteData note = activeNotes[i];
            
            if (note.ShouldBeMissed(currentTime, hitWindow + 0.05f))
            {
                note.MarkAsMissed();
                OnNoteMissed?.Invoke(note);
                scoreManager?.RegisterMiss();
                activeNotes.RemoveAt(i);
                
                Debug.Log($"❌ Nota perdida en lane {note.laneIndex} - Tiempo: {currentTime:F2}");
            }
        }
    }
    
    public void RegisterActiveNote(NoteData noteData, Note noteObject)
    {
        if (!activeNotes.Contains(noteData))
        {
            activeNotes.Add(noteData);
            spawnedNotes[noteData.GetHashCode()] = noteObject;
            
            // Subscribe to note destruction to clean up references
            if (noteObject != null)
            {
                noteObject.OnNoteDestroyed += OnNoteObjectDestroyed;
            }
        }
    }
    
    void OnNoteObjectDestroyed(Note destroyedNote)
    {
        if (destroyedNote == null) return;
        
        // Find and remove the corresponding note data safely
        NoteData noteToRemove = null;
        int noteHashCode = 0;
        
        for (int i = activeNotes.Count - 1; i >= 0; i--)
        {
            if (i < activeNotes.Count && spawnedNotes.TryGetValue(activeNotes[i].GetHashCode(), out Note noteObject) && noteObject == destroyedNote)
            {
                noteToRemove = activeNotes[i];
                noteHashCode = noteToRemove.GetHashCode();
                break;
            }
        }
        
        // Remove safely
        if (noteToRemove != null)
        {
            activeNotes.Remove(noteToRemove);
            spawnedNotes.Remove(noteHashCode);
        }
        
        // Unsubscribe from the event
        destroyedNote.OnNoteDestroyed -= OnNoteObjectDestroyed;
    }
    
    public bool TryHitNote(int laneIndex, out HitAccuracy accuracy)
    {
        accuracy = HitAccuracy.Miss;
        float currentTime = GetSongTime();
        
        // Find the closest hittable note in the specified lane
        NoteData closestNote = null;
        float closestDistance = float.MaxValue;
        
        foreach (NoteData note in activeNotes)
        {
            if (note.laneIndex == laneIndex && note.CanBeHit(currentTime, hitWindow))
            {
                float distance = Mathf.Abs(currentTime - note.time);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestNote = note;
                }
            }
        }
        
        if (closestNote != null)
        {
            // Determine accuracy based on timing
            if (closestDistance <= perfectWindow)
                accuracy = HitAccuracy.Perfect;
            else if (closestDistance <= greatWindow)
                accuracy = HitAccuracy.Great;
            else
                accuracy = HitAccuracy.Good;
            
            closestNote.MarkAsHit(accuracy);
            OnNoteHit?.Invoke(closestNote, accuracy);
            scoreManager?.RegisterHit(accuracy);
            
            // Remove from active notes immediately
            activeNotes.Remove(closestNote);
            
            // Destroy the visual note immediately when hit in hitzone
            if (spawnedNotes.TryGetValue(closestNote.GetHashCode(), out Note noteObject))
            {
                if (noteObject != null)
                {
                    noteObject.Hit(); // This will trigger immediate destruction
                }
                spawnedNotes.Remove(closestNote.GetHashCode());
            }
            
            Debug.Log($"✅ Nota acertada en lane {laneIndex} - Precisión: {accuracy}");
            return true;
        }
        
        // No note to hit - register as a miss
        scoreManager?.RegisterMiss();
        Debug.Log($"❌ Fallo en lane {laneIndex} - Sin nota disponible");
        return false;
    }
    
    public void PauseGame()
    {
        isPaused = true;
        if (musicSource.isPlaying)
            musicSource.Pause();
        Time.timeScale = 0f;
    }
    
    public void ResumeGame()
    {
        isPaused = false;
        if (musicSource.clip != null)
            musicSource.UnPause();
        Time.timeScale = 1f;
    }
    
    public void EndSong()
    {
        isGameActive = false;
        OnSongFinished?.Invoke();
        
        Debug.Log($"🎵 Canción terminada - Puntuación final: {scoreManager?.score}");
        
        // Create game results and go to PostGameplay scene
        CreateGameResults();
    }
    
    // Public method to start gameplay with test notes
    public void StartTestGameplay()
    {
        isGameActive = true;
        
        // Subscribe to events
        if (spawner != null)
        {
            spawner.OnNoteSpawned += RegisterActiveNote;
        }
        
        Debug.Log("🎮 Test gameplay initialized");
    }
    
    void CreateGameResults()
    {
        if (scoreManager == null)
        {
            Debug.LogError("ScoreManager not found - cannot create game results");
            StartCoroutine(ReturnToMainMenu());
            return;
        }
        
        // Get song information
        string songName = chartData?.songName ?? "Unknown Song";
        string artist = chartData?.artist ?? "Unknown Artist";
        string difficulty = GameManager.Instance?.selectedDifficulty ?? "Unknown";
        
        // Get performance data
        int finalScore = scoreManager.score;
        float accuracy = scoreManager.GetAccuracyPercentage();
        float completion = CalculateCompletionPercentage();
        
        // Get hit statistics
        int perfectHits = scoreManager.GetHitCount(HitAccuracy.Perfect);
        int greatHits = scoreManager.GetHitCount(HitAccuracy.Great);
        int goodHits = scoreManager.GetHitCount(HitAccuracy.Good);
        int missedHits = scoreManager.GetMissCount();
        int totalNotes = selectedNotes?.Count ?? 0;
        
        // Create results data
        GameResultsData.CreateResults(
            songName,
            artist,
            difficulty,
            finalScore,
            accuracy,
            completion,
            perfectHits,
            greatHits,
            goodHits,
            missedHits,
            totalNotes
        );
        
        Debug.Log($"🎯 Game results created: {songName} - Score: {finalScore}, Accuracy: {accuracy:F1}%");
        
        // Load PostGameplay scene
        SceneManager.LoadScene("PostGameplay");
    }
    
    float CalculateCompletionPercentage()
    {
        if (selectedNotes == null || selectedNotes.Count == 0)
            return 0f;
            
        if (scoreManager == null)
            return 0f;
        
        int totalHits = scoreManager.GetHitCount(HitAccuracy.Perfect) + 
                       scoreManager.GetHitCount(HitAccuracy.Great) + 
                       scoreManager.GetHitCount(HitAccuracy.Good);
        
        return ((float)totalHits / selectedNotes.Count) * 100f;
    }
    
    IEnumerator ReturnToMainMenu()
    {
        yield return new WaitForSeconds(3f);
        SceneManager.LoadScene("MainMenu");
    }
    
    void OnDestroy()
    {
        if (spawner != null)
        {
            spawner.OnNoteSpawned -= RegisterActiveNote;
        }
        
        // Clean up note object event subscriptions
        foreach (var noteObject in spawnedNotes.Values)
        {
            if (noteObject != null)
            {
                noteObject.OnNoteDestroyed -= OnNoteObjectDestroyed;
            }
        }
    }
}
